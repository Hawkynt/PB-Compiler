using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Ir;

/// <summary>
/// Lowers a bound program into the IR in clang-style alloca/load/store form: every
/// scalar variable gets an entry-block alloca, reads/writes become load/store, control
/// flow becomes explicit blocks and branches. A later mem2reg pass promotes the
/// allocas to SSA. <see cref="TryLowerModule"/> lowers the whole program - the main
/// body as <c>@main</c> plus each user SUB/FUNCTION whose signature and body fit the
/// supported subset (scalar BYVAL parameters, scalar/void returns, direct calls).
/// Anything outside the subset (BYREF/array/string parameters, arrays, UDTs, GOTO,
/// SELECT, I/O, intrinsics) makes that function decline, so the IR is only built for
/// code it models exactly.
/// </summary>
public sealed class IrLowering {

  private readonly SemanticModel _model;
  private readonly IReadOnlyDictionary<ProcedureSymbol, IrFunction>? _procMap;
  private readonly Dictionary<VariableSymbol, IrValue> _addr = new(ReferenceEqualityComparer.Instance);
  private readonly Stack<LoopContext> _loops = new();
  private readonly ConstantFolder _folder;

  private IrFunction _fn = null!;
  private IrBasicBlock _entry = null!;
  private int _entryAllocaCount;
  private IrBuilder _b = null!;
  private int _seq;
  private VariableSymbol? _resultVar;

  private readonly record struct LoopContext(ExitKind Kind, IrBasicBlock Exit, IrBasicBlock Continue);

  private IrLowering(SemanticModel model, IReadOnlyDictionary<ProcedureSymbol, IrFunction>? procMap) {
    this._model = model;
    this._procMap = procMap;
    this._folder = new ConstantFolder(model.Equates);
  }

  /// <summary>Lowers just the main body into an <c>@main</c> function (no procedures), or null if unsupported.</summary>
  public static IrFunction? TryLowerMainBody(SemanticModel model) {
    try {
      var lowering = new IrLowering(model, null);
      var fn = new IrFunction("main", IrType.Void);
      lowering.LowerBodyInto(fn, model.MainBody, null);
      return fn;
    } catch (IrLoweringException) {
      return null;
    }
  }

  /// <summary>Lowers the whole program into a module; declines (null) only if the main body is unsupported.</summary>
  public static IrModule? TryLowerModule(SemanticModel model) {
    var module = new IrModule(model.FileName);
    var procMap = new Dictionary<ProcedureSymbol, IrFunction>(ReferenceEqualityComparer.Instance);

    foreach (var proc in model.Procedures.Values)
      if (!proc.IsExternal && TrySignature(proc, out var irfn)) {
        procMap[proc] = irfn!;
        module.AddFunction(irfn!);
      }

    var main = new IrFunction("main", IrType.Void);
    module.AddFunction(main);
    try {
      new IrLowering(model, procMap).LowerBodyInto(main, model.MainBody, null);
    } catch (IrLoweringException) {
      return null;
    }

    foreach (var (proc, irfn) in procMap) {
      try {
        new IrLowering(model, procMap).LowerProcedure(proc, irfn);
      } catch (IrLoweringException) {
        irfn.ClearBody();                              // leave it a declaration; callers can still call it
      }
    }
    return module;
  }

  /// <summary>Builds an IR signature for a procedure, or false if it is outside the supported subset.</summary>
  private static bool TrySignature(ProcedureSymbol proc, out IrFunction? fn) {
    fn = null;
    var ret = IrType.Void;
    if (proc.IsFunction) {
      if (proc.ReturnType is null || !IrTypeMapper.TryMap(proc.ReturnType, out ret))
        return false;
    }
    var args = new List<IrArgument>();
    foreach (var p in proc.Parameters) {
      if (p.Seg || p.Optional || !IrTypeMapper.TryMap(p.Type, out var pty))
        return false;                                  // scalar parameters only (SEG/CDECL-optional excluded)
      args.Add(new IrArgument(p.ByVal ? pty : IrType.Ptr, args.Count, p.Name));  // BYREF parameters arrive as pointers
    }
    fn = new IrFunction(proc.Name, ret, args);
    return true;
  }

  private void LowerProcedure(ProcedureSymbol proc, IrFunction fn) {
    this._resultVar = proc.IsFunction ? proc.Variables.GetValueOrDefault(proc.Name) : null;
    this.LowerBodyInto(fn, proc.Body!, proc);
  }

  private void LowerBodyInto(IrFunction fn, IReadOnlyList<Statement> body, ProcedureSymbol? proc) {
    this._fn = fn;
    this._entry = fn.CreateBlock("entry");
    this._b = new IrBuilder(this._entry);

    // bind parameters: BYVAL copies the argument into a mutable local slot; BYREF
    // takes the incoming pointer as the variable's address (reads/writes go through it)
    if (proc is not null)
      for (var i = 0; i < proc.Parameters.Count; ++i) {
        var p = proc.Parameters[i];
        if (p.ByVal)
          this._b.Store(fn.Parameters[i], this.SlotFor(p));
        else
          this._addr[p] = fn.Parameters[i];
      }

    this.LowerStatements(body);
    if (!this.Terminated)
      this.ReturnFromFunction();
  }

  // ---- helpers -------------------------------------------------------------

  private bool Terminated => this._b.Block!.Terminator is not null;

  private IrBasicBlock NewBlock(string hint) => this._fn.CreateBlock($"{hint}{this._seq++}");

  private void ReturnFromFunction() {
    if (this._resultVar is not null)
      this._b.Ret(this._b.Load(IrTypeMapper.Map(this._resultVar.Type), this.SlotFor(this._resultVar)));
    else
      this._b.Ret();
  }

  private IrValue SlotFor(VariableSymbol symbol) {
    if (this._addr.TryGetValue(symbol, out var existing))
      return existing;
    IrAlloca alloca;
    if (symbol.Type is ArrayType arr) {
      if (arr.IsDynamic || !IrTypeMapper.TryMap(arr.Element, out var elem))
        throw new IrLoweringException("dynamic or non-scalar array");
      alloca = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(elem) { Count = arr.ElementCount, Name = symbol.Name });
    } else {
      alloca = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(IrTypeMapper.Map(symbol.Type)) { Name = symbol.Name });
    }
    this._addr[symbol] = alloca;
    return alloca;
  }

  /// <summary>The address of one static-array element, by row-major flattening of the index list.</summary>
  private (IrValue Address, PbType Element) ElementAddress(CallOrIndexExpr expr) {
    if (!this._model.VariableBindings.TryGetValue(expr, out var symbol) || symbol.Type is not ArrayType arr)
      throw new IrLoweringException($"not a static array element: {expr.Name}");
    if (arr.IsDynamic || arr.StaticBounds is not { } bounds || bounds.Count != expr.Arguments.Count)
      throw new IrLoweringException("dynamic array or rank mismatch");
    var basePtr = this.SlotFor(symbol);

    IrValue? flat = null;
    for (var k = 0; k < bounds.Count; ++k) {
      var idx = this.Coerce(this.LowerExpr(expr.Arguments[k]), this._model.TypeOf(expr.Arguments[k]), PbType.Long);
      var rel = this._b.Sub(idx, new IrConstantInt(IrType.I32, bounds[k].Lower));
      var size = bounds[k].Upper - bounds[k].Lower + 1;
      flat = flat is null ? rel : this._b.Add(this._b.Mul(flat, new IrConstantInt(IrType.I32, size)), rel);
    }

    var byteOffset = this._b.Mul(flat!, new IrConstantInt(IrType.I32, arr.Element.Size));
    return (this._b.Gep(basePtr, byteOffset), arr.Element);
  }

  private VariableSymbol SymbolOf(Expression target) =>
    target is NameExpr && this._model.VariableBindings.TryGetValue(target, out var s)
      ? s
      : throw new IrLoweringException("non-scalar or unbound storage reference");

  // ---- statements ----------------------------------------------------------

  private void LowerStatements(IReadOnlyList<Statement> statements) {
    foreach (var statement in statements) {
      if (this.Terminated)
        return;
      this.LowerStatement(statement);
    }
  }

  private void LowerStatement(Statement statement) {
    switch (statement) {
      case AssignStmt a: this.LowerAssign(a); break;
      case IncrDecrStmt id: this.LowerIncrDecr(id); break;
      case IfStmt i: this.LowerIf(i); break;
      case ForStmt f: this.LowerFor(f); break;
      case DoLoopStmt d: this.LowerDo(d); break;
      case ExitStmt e: this.LowerExit(e); break;
      case IterateStmt it: this.LowerIterate(it); break;
      case CallStmt c: this.LowerCallStatement(c); break;
      case SelectStmt s: this.LowerSelect(s); break;
      case DimStmt d: this.LowerDim(d); break;
      default: throw new IrLoweringException($"unsupported statement: {statement.GetType().Name}");
    }
  }

  private void LowerAssign(AssignStmt a) {
    if (a.Target is CallOrIndexExpr indexed && this._model.VariableBindings.TryGetValue(indexed, out var arrSym) && arrSym.Type is ArrayType) {
      var (address, element) = this.ElementAddress(indexed);
      this._b.Store(this.Coerce(this.LowerExpr(a.Value), this._model.TypeOf(a.Value), element), address);
      return;
    }
    var symbol = this.SymbolOf(a.Target);
    var slot = this.SlotFor(symbol);
    var value = this.Coerce(this.LowerExpr(a.Value), this._model.TypeOf(a.Value), symbol.Type);
    this._b.Store(value, slot);
  }

  private void LowerDim(DimStmt d) {
    if (d.AtAddress is not null || d.Class != ArrayClass.Default)
      throw new IrLoweringException("DIM AT / non-default array class");
    // a DIM is just a declaration here; storage is allocated lazily on first use
  }

  private void LowerIncrDecr(IncrDecrStmt id) {
    var symbol = this.SymbolOf(id.Target);
    var slot = this.SlotFor(symbol);
    var ty = IrTypeMapper.Map(symbol.Type);
    if (ty.IsFloat)
      throw new IrLoweringException("INCR/DECR on float");
    var current = this._b.Load(ty, slot);
    var amount = id.Amount is null
      ? new IrConstantInt(ty, 1)
      : this.Coerce(this.LowerExpr(id.Amount), this._model.TypeOf(id.Amount), symbol.Type);
    this._b.Store(this._b.Binary(id.Increment ? IrBinaryOp.Add : IrBinaryOp.Sub, current, amount), slot);
  }

  private void LowerIf(IfStmt stmt) {
    var endif = this.NewBlock("if.end");
    var clauses = new List<(Expression Cond, IReadOnlyList<Statement> Body)> { (stmt.Condition, stmt.Then) };
    clauses.AddRange(stmt.ElseIfs);

    foreach (var (cond, body) in clauses) {
      var then = this.NewBlock("if.then");
      var next = this.NewBlock("if.next");
      this._b.CondBr(this.LowerCondition(cond), then, next);
      this._b.Position(then);
      this.LowerStatements(body);
      if (!this.Terminated)
        this._b.Br(endif);
      this._b.Position(next);
    }

    if (stmt.Else is { } elseBody)
      this.LowerStatements(elseBody);
    if (!this.Terminated)
      this._b.Br(endif);
    this._b.Position(endif);
  }

  private void LowerFor(ForStmt f) {
    var symbol = this.SymbolOf(f.Variable);
    var ty = IrTypeMapper.Map(symbol.Type);
    if (!ty.IsInteger)
      throw new IrLoweringException("FOR over a non-integer counter");
    var signed = ((ScalarType)symbol.Type).Signed;
    var step = this.ConstStep(f.Step);
    var slot = this.SlotFor(symbol);
    var limitSlot = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(ty) { Name = symbol.Name + ".limit" });

    this._b.Store(this.Coerce(this.LowerExpr(f.From), this._model.TypeOf(f.From), symbol.Type), slot);
    this._b.Store(this.Coerce(this.LowerExpr(f.To), this._model.TypeOf(f.To), symbol.Type), limitSlot);

    var header = this.NewBlock("for.head");
    var body = this.NewBlock("for.body");
    var inc = this.NewBlock("for.inc");
    var exit = this.NewBlock("for.exit");
    this._b.Br(header);

    this._b.Position(header);
    var i = this._b.Load(ty, slot);
    var limit = this._b.Load(ty, limitSlot);
    var pred = step > 0 ? (signed ? IrCmpPred.Sle : IrCmpPred.Ule) : (signed ? IrCmpPred.Sge : IrCmpPred.Uge);
    this._b.CondBr(this._b.Cmp(pred, i, limit), body, exit);

    this._b.Position(body);
    this._loops.Push(new LoopContext(ExitKind.For, exit, inc));
    this.LowerStatements(f.Body);
    this._loops.Pop();
    if (!this.Terminated)
      this._b.Br(inc);

    this._b.Position(inc);
    var iv = this._b.Load(ty, slot);
    this._b.Store(this._b.Binary(IrBinaryOp.Add, iv, new IrConstantInt(ty, step)), slot);
    this._b.Br(header);

    this._b.Position(exit);
  }

  private void LowerDo(DoLoopStmt d) {
    var header = this.NewBlock("do.head");
    var body = this.NewBlock("do.body");
    var latch = this.NewBlock("do.latch");
    var exit = this.NewBlock("do.exit");
    this._b.Br(header);

    this._b.Position(header);
    if (d.PreTest != LoopTestKind.None) {
      var c = this.LowerCondition(d.PreCondition!);
      if (d.PreTest == LoopTestKind.While)
        this._b.CondBr(c, body, exit);
      else
        this._b.CondBr(c, exit, body);
    } else {
      this._b.Br(body);
    }

    this._b.Position(body);
    this._loops.Push(new LoopContext(ExitKind.Do, exit, latch));
    this.LowerStatements(d.Body);
    this._loops.Pop();
    if (!this.Terminated)
      this._b.Br(latch);

    this._b.Position(latch);
    if (d.PostTest != LoopTestKind.None) {
      var c = this.LowerCondition(d.PostCondition!);
      if (d.PostTest == LoopTestKind.While)
        this._b.CondBr(c, header, exit);
      else
        this._b.CondBr(c, exit, header);
    } else {
      this._b.Br(header);
    }

    this._b.Position(exit);
  }

  private void LowerExit(ExitStmt e) {
    switch (e.Kind) {
      case ExitKind.Sub or ExitKind.Function or ExitKind.Def:
        this.ReturnFromFunction();
        return;
      default:
        foreach (var loop in this._loops)
          if (loop.Kind == e.Kind || e.Kind is ExitKind.Loop) {
            this._b.Br(loop.Exit);
            return;
          }
        throw new IrLoweringException($"EXIT {e.Kind} outside a matching loop");
    }
  }

  private void LowerIterate(IterateStmt it) {
    foreach (var loop in this._loops)
      if (loop.Kind == it.Kind || it.Kind is ExitKind.Loop) {
        this._b.Br(loop.Continue);
        return;
      }
    throw new IrLoweringException($"ITERATE {it.Kind} outside a matching loop");
  }

  private void LowerCallStatement(CallStmt c) {
    if (this._procMap is null || !this._model.CallBindings.TryGetValue(c, out var proc) || !this._procMap.TryGetValue(proc, out var callee))
      throw new IrLoweringException($"call to unsupported procedure {c.Name}");
    this.EmitCall(callee, proc, c.Arguments);
  }

  private void LowerSelect(SelectStmt s) {
    var subject = this.LowerExpr(s.Subject);
    var subjectPb = this._model.TypeOf(s.Subject);
    if (subjectPb is not ScalarType)
      throw new IrLoweringException("SELECT CASE on a non-scalar subject");

    var endsel = this.NewBlock("sel.end");
    CaseArm? elseArm = null;
    var arms = new List<CaseArm>();
    foreach (var arm in s.Arms) {
      if (arm.Selectors.Count == 0)
        elseArm = arm;                               // CASE ELSE
      else
        arms.Add(arm);
    }

    foreach (var arm in arms) {
      var body = this.NewBlock("sel.case");
      var next = this.NewBlock("sel.next");
      IrValue? cond = null;
      foreach (var selector in arm.Selectors) {
        var test = this.SelectorTest(subject, subjectPb, selector);
        cond = cond is null ? test : this._b.Or(cond, test);
      }
      this._b.CondBr(cond!, body, next);
      this._b.Position(body);
      this.LowerStatements(arm.Body);
      if (!this.Terminated)
        this._b.Br(endsel);
      this._b.Position(next);
    }

    if (elseArm is not null)
      this.LowerStatements(elseArm.Body);
    if (!this.Terminated)
      this._b.Br(endsel);
    this._b.Position(endsel);
  }

  private IrValue SelectorTest(IrValue subject, PbType subjectPb, CaseSelector selector) {
    if (selector.IsComparison is { } cmp)
      return this.CompareToValue(subject, subjectPb, cmp, selector.Value!);
    if (selector.RangeUpper is { } upper) {
      var atLeast = this.CompareToValue(subject, subjectPb, CaseComparison.GreaterEqual, selector.Value!);
      var atMost = this.CompareToValue(subject, subjectPb, CaseComparison.LessEqual, upper);
      return this._b.And(atLeast, atMost);
    }
    if (selector.Value is { } value)
      return this.CompareToValue(subject, subjectPb, CaseComparison.Equal, value);
    throw new IrLoweringException("empty CASE selector");
  }

  private IrValue CompareToValue(IrValue subject, PbType subjectPb, CaseComparison op, Expression rightExpr) {
    var rightPb = this._model.TypeOf(rightExpr);
    var (cmpPb, isFloat, signed) = CommonCompareType(subjectPb, rightPb);
    var l = this.Coerce(subject, subjectPb, cmpPb);
    var r = this.Coerce(this.LowerExpr(rightExpr), rightPb, cmpPb);
    var pred = op switch {
      CaseComparison.Equal => isFloat ? IrCmpPred.Foeq : IrCmpPred.Eq,
      CaseComparison.NotEqual => isFloat ? IrCmpPred.Fone : IrCmpPred.Ne,
      CaseComparison.Less => isFloat ? IrCmpPred.Folt : signed ? IrCmpPred.Slt : IrCmpPred.Ult,
      CaseComparison.LessEqual => isFloat ? IrCmpPred.Fole : signed ? IrCmpPred.Sle : IrCmpPred.Ule,
      CaseComparison.Greater => isFloat ? IrCmpPred.Fogt : signed ? IrCmpPred.Sgt : IrCmpPred.Ugt,
      CaseComparison.GreaterEqual => isFloat ? IrCmpPred.Foge : signed ? IrCmpPred.Sge : IrCmpPred.Uge,
      _ => throw new IrLoweringException($"case comparison {op}"),
    };
    return this._b.Cmp(pred, l, r);
  }

  private long ConstStep(Expression? step) {
    if (step is null)
      return 1;
    if (this._folder.TryFold(step) is { Integer: { } n } && n != 0)
      return n;
    throw new IrLoweringException("FOR STEP must be a non-zero compile-time constant in this lowering");
  }

  // ---- conditions & expressions -------------------------------------------

  private IrValue LowerCondition(Expression expr) {
    var value = this.LowerExpr(expr);
    if (this._model.TypeOf(expr) is ScalarType { IsFloat: true })
      return this._b.Cmp(IrCmpPred.Fone, value, new IrConstantFloat(value.Type, 0.0));
    return this._b.Cmp(IrCmpPred.Ne, value, new IrConstantInt(value.Type, 0));
  }

  private IrValue LowerExpr(Expression expr) {
    switch (expr) {
      case IntegerLiteralExpr lit:
        return new IrConstantInt(IrTypeMapper.Map(this._model.TypeOf(lit)), lit.Value);
      case FloatLiteralExpr lit:
        return new IrConstantFloat(IrTypeMapper.Map(this._model.TypeOf(lit)), lit.Value);
      case NamedConstantExpr nc:
        return this.LowerNamedConstant(nc);
      case NameExpr name:
        return this.LowerNameRead(name);
      case UnaryExpr u:
        return this.LowerUnary(u);
      case BinaryExpr b:
        return this.LowerBinary(b);
      case CallOrIndexExpr indexed when this._model.VariableBindings.TryGetValue(indexed, out var s) && s.Type is ArrayType:
        var (address, element) = this.ElementAddress(indexed);
        return this._b.Load(IrTypeMapper.Map(element), address);
      case CallOrIndexExpr call:
        return this.LowerCallExpr(call);
      default:
        throw new IrLoweringException($"unsupported expression: {expr.GetType().Name}");
    }
  }

  private IrValue LowerNamedConstant(NamedConstantExpr nc) {
    if (!this._model.Equates.TryGetValue(nc.Name, out var value))
      throw new IrLoweringException($"unknown equate {nc.Name}");
    var ty = IrTypeMapper.Map(this._model.TypeOf(nc));
    if (value.Integer is { } n)
      return new IrConstantInt(ty, n);
    if (value.Float is { } f)
      return new IrConstantFloat(ty, f);
    throw new IrLoweringException("non-numeric equate");
  }

  private IrValue LowerNameRead(NameExpr name) {
    if (!this._model.VariableBindings.TryGetValue(name, out var symbol))
      throw new IrLoweringException($"unbound name {name.Name}");
    return this._b.Load(IrTypeMapper.Map(symbol.Type), this.SlotFor(symbol));
  }

  private IrValue LowerCallExpr(CallOrIndexExpr call) {
    if (this._procMap is null || !this._model.CallBindings.TryGetValue(call, out var proc) || !this._procMap.TryGetValue(proc, out var callee))
      throw new IrLoweringException($"unsupported call/index {call.Name}");   // array index / intrinsic
    if (!proc.IsFunction)
      throw new IrLoweringException("SUB used in expression position");
    return this.EmitCall(callee, proc, call.Arguments);
  }

  private IrValue EmitCall(IrFunction callee, ProcedureSymbol proc, IReadOnlyList<Expression> arguments) {
    if (arguments.Count != proc.Parameters.Count)
      throw new IrLoweringException("argument count mismatch (optional/CDECL not modelled)");
    var args = new List<IrValue>(arguments.Count);
    for (var i = 0; i < arguments.Count; ++i) {
      var p = proc.Parameters[i];
      args.Add(p.ByVal
        ? this.Coerce(this.LowerExpr(arguments[i]), this._model.TypeOf(arguments[i]), p.Type)
        : this.AddressOfArgument(arguments[i], p.Type));
    }
    return this._b.Call(callee.ReturnType, callee, args);
  }

  /// <summary>A pointer to a BYREF argument: the variable's own slot when the type matches, else a temp copy.</summary>
  private IrValue AddressOfArgument(Expression arg, PbType paramType) {
    if (arg is NameExpr && this._model.VariableBindings.TryGetValue(arg, out var sym) && sym.Type.Equals(paramType))
      return this.SlotFor(sym);
    if (arg is CallOrIndexExpr indexed && this._model.VariableBindings.TryGetValue(indexed, out var arrSym) && arrSym.Type is ArrayType) {
      var (address, element) = this.ElementAddress(indexed);
      if (element.Equals(paramType))
        return address;
    }
    // a constant / expression / type-mismatched lvalue: materialize a temporary
    var temp = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(IrTypeMapper.Map(paramType)) { Name = "byref.tmp" });
    this._b.Store(this.Coerce(this.LowerExpr(arg), this._model.TypeOf(arg), paramType), temp);
    return temp;
  }

  private IrValue LowerUnary(UnaryExpr u) {
    var pb = this._model.TypeOf(u);
    var ty = IrTypeMapper.Map(pb);
    var operand = this.Coerce(this.LowerExpr(u.Operand), this._model.TypeOf(u.Operand), pb);
    return u.Op switch {
      UnaryOp.Negate when ty.IsFloat => this._b.Binary(IrBinaryOp.FSub, new IrConstantFloat(ty, 0.0), operand),
      UnaryOp.Negate => this._b.Binary(IrBinaryOp.Sub, new IrConstantInt(ty, 0), operand),
      UnaryOp.Not => this._b.Xor(operand, new IrConstantInt(ty, -1)),
      _ => throw new IrLoweringException($"unary {u.Op}"),
    };
  }

  private IrValue LowerBinary(BinaryExpr expr) {
    var leftPb = this._model.TypeOf(expr.Left);
    var rightPb = this._model.TypeOf(expr.Right);
    var resultPb = this._model.TypeOf(expr);
    return expr.Op switch {
      BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.Greater
        or BinaryOp.LessEqual or BinaryOp.GreaterEqual => this.LowerComparison(expr, leftPb, rightPb, resultPb),
      _ => this.LowerArithmetic(expr, leftPb, rightPb, resultPb),
    };
  }

  private IrValue LowerArithmetic(BinaryExpr expr, PbType leftPb, PbType rightPb, PbType resultPb) {
    var resultTy = IrTypeMapper.Map(resultPb);
    var signed = resultPb is ScalarType { Signed: true };
    var l = this.Coerce(this.LowerExpr(expr.Left), leftPb, resultPb);
    var r = this.Coerce(this.LowerExpr(expr.Right), rightPb, resultPb);

    switch (expr.Op) {
      case BinaryOp.Eqv:
        return this._b.Xor(this._b.Xor(l, r), new IrConstantInt(resultTy, -1));
      case BinaryOp.Imp:
        return this._b.Or(this._b.Xor(l, new IrConstantInt(resultTy, -1)), r);
    }

    var op = expr.Op switch {
      BinaryOp.Add => resultTy.IsFloat ? IrBinaryOp.FAdd : IrBinaryOp.Add,
      BinaryOp.Subtract => resultTy.IsFloat ? IrBinaryOp.FSub : IrBinaryOp.Sub,
      BinaryOp.Multiply => resultTy.IsFloat ? IrBinaryOp.FMul : IrBinaryOp.Mul,
      BinaryOp.Divide => IrBinaryOp.FDiv,
      BinaryOp.IntegerDivide => signed ? IrBinaryOp.SDiv : IrBinaryOp.UDiv,
      BinaryOp.Modulo => signed ? IrBinaryOp.SRem : IrBinaryOp.URem,
      BinaryOp.And => IrBinaryOp.And,
      BinaryOp.Or => IrBinaryOp.Or,
      BinaryOp.Xor => IrBinaryOp.Xor,
      _ => throw new IrLoweringException($"unsupported binary op {expr.Op}"),
    };
    return this._b.Binary(op, l, r);
  }

  private IrValue LowerComparison(BinaryExpr expr, PbType leftPb, PbType rightPb, PbType resultPb) {
    var (cmpPb, isFloat, signed) = CommonCompareType(leftPb, rightPb);
    var l = this.Coerce(this.LowerExpr(expr.Left), leftPb, cmpPb);
    var r = this.Coerce(this.LowerExpr(expr.Right), rightPb, cmpPb);
    var pred = expr.Op switch {
      BinaryOp.Equal => isFloat ? IrCmpPred.Foeq : IrCmpPred.Eq,
      BinaryOp.NotEqual => isFloat ? IrCmpPred.Fone : IrCmpPred.Ne,
      BinaryOp.Less => isFloat ? IrCmpPred.Folt : signed ? IrCmpPred.Slt : IrCmpPred.Ult,
      BinaryOp.LessEqual => isFloat ? IrCmpPred.Fole : signed ? IrCmpPred.Sle : IrCmpPred.Ule,
      BinaryOp.Greater => isFloat ? IrCmpPred.Fogt : signed ? IrCmpPred.Sgt : IrCmpPred.Ugt,
      BinaryOp.GreaterEqual => isFloat ? IrCmpPred.Foge : signed ? IrCmpPred.Sge : IrCmpPred.Uge,
      _ => throw new IrLoweringException($"comparison {expr.Op}"),
    };
    return this._b.SExt(this._b.Cmp(pred, l, r), IrTypeMapper.Map(resultPb));
  }

  private static (PbType Type, bool IsFloat, bool Signed) CommonCompareType(PbType a, PbType b) {
    if (a is not ScalarType sa || b is not ScalarType sb)
      throw new IrLoweringException("comparison of non-scalar operands");
    if (sa.IsFloat || sb.IsFloat) {
      var bytes = Math.Max(sa.IsFloat ? sa.ByteSize : 8, sb.IsFloat ? sb.ByteSize : 8);
      return (new ScalarType(ScalarKind.Double, bytes, true, true), true, true);
    }
    var width = Math.Max(sa.ByteSize, sb.ByteSize);
    var signed = sa.Signed || sb.Signed;
    return (new ScalarType(ScalarKind.Long, width, signed, false), false, signed);
  }

  private IrValue Coerce(IrValue value, PbType from, PbType to) {
    if (from is not ScalarType sf || to is not ScalarType st)
      throw new IrLoweringException("coercion between non-scalar types");
    var toTy = IrTypeMapper.Map(to);
    if (value.Type.Equals(toTy))
      return value;

    if (!sf.IsFloat && !st.IsFloat) {
      if (toTy.Bits > value.Type.Bits)
        return this._b.Cast(sf.Signed ? IrCastOp.SExt : IrCastOp.ZExt, value, toTy);
      if (toTy.Bits < value.Type.Bits)
        return this._b.Trunc(value, toTy);
      return value;
    }
    if (sf.IsFloat && st.IsFloat)
      return this._b.Cast(toTy.Bits > value.Type.Bits ? IrCastOp.FPExt : IrCastOp.FPTrunc, value, toTy);
    if (!sf.IsFloat && st.IsFloat)
      return this._b.Cast(sf.Signed ? IrCastOp.SIToFP : IrCastOp.UIToFP, value, toTy);
    return this._b.Cast(st.Signed ? IrCastOp.FPToSI : IrCastOp.FPToUI, value, toTy);
  }
}
