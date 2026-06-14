using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Ir;

/// <summary>
/// Lowers a bound program (the main body, for now) into the IR in clang-style
/// alloca/load/store form: every scalar variable gets an <c>alloca</c> in the entry
/// block, reads become <c>load</c>s and writes <c>store</c>s, control flow becomes
/// explicit blocks and branches. This form is trivially correct; a later mem2reg pass
/// promotes the allocas to SSA registers and phis. Anything outside the supported
/// subset (strings, arrays, UDTs, calls, GOTO, SELECT, I/O, intrinsics) makes the
/// lowering decline (return null) so the IR path is only ever built for code it models faithfully.
/// </summary>
public sealed class IrLowering {

  private readonly SemanticModel _model;
  private readonly Dictionary<VariableSymbol, IrAlloca> _slots = new(ReferenceEqualityComparer.Instance);
  private readonly Stack<LoopContext> _loops = new();
  private readonly ConstantFolder _folder;

  private IrFunction _fn = null!;
  private IrBasicBlock _entry = null!;
  private int _entryAllocaCount;
  private IrBuilder _b = null!;
  private int _seq;

  private readonly record struct LoopContext(ExitKind Kind, IrBasicBlock Exit, IrBasicBlock Continue);

  private IrLowering(SemanticModel model) {
    this._model = model;
    this._folder = new ConstantFolder(model.Equates);
  }

  /// <summary>Lowers the program's main body into an <c>@main</c> function, or returns null if it uses an unsupported construct.</summary>
  public static IrFunction? TryLowerMainBody(SemanticModel model) {
    try {
      return new IrLowering(model).LowerMain();
    } catch (IrLoweringException) {
      return null;
    }
  }

  private IrFunction LowerMain() {
    this._fn = new IrFunction("main", IrType.Void);
    this._entry = this._fn.CreateBlock("entry");
    this._b = new IrBuilder(this._entry);
    this.LowerStatements(this._model.MainBody);
    if (!this.Terminated)
      this._b.Ret();
    return this._fn;
  }

  // ---- helpers -------------------------------------------------------------

  private bool Terminated => this._b.Block!.Terminator is not null;

  private IrBasicBlock NewBlock(string hint) => this._fn.CreateBlock($"{hint}{this._seq++}");

  private IrAlloca SlotFor(Expression target) {
    if (target is not NameExpr || !this._model.VariableBindings.TryGetValue(target, out var symbol))
      throw new IrLoweringException("non-scalar or unbound assignment target");
    return this.SlotFor(symbol);
  }

  private IrAlloca SlotFor(VariableSymbol symbol) {
    if (this._slots.TryGetValue(symbol, out var existing))
      return existing;
    if (symbol.IsArray)
      throw new IrLoweringException("array variable");
    var ir = IrTypeMapper.Map(symbol.Type);
    var alloca = new IrAlloca(ir) { Name = symbol.Name };
    this._entry.InsertAt(this._entryAllocaCount++, alloca);
    this._slots[symbol] = alloca;
    return alloca;
  }

  // ---- statements ----------------------------------------------------------

  private void LowerStatements(IReadOnlyList<Statement> statements) {
    foreach (var statement in statements) {
      if (this.Terminated)
        return;                                      // dead code after an unconditional branch
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
      default: throw new IrLoweringException($"unsupported statement: {statement.GetType().Name}");
    }
  }

  private void LowerAssign(AssignStmt a) {
    var slot = this.SlotFor(a.Target);
    var symbol = this._model.VariableBindings[a.Target];
    var value = this.LowerExpr(a.Value);
    value = this.Coerce(value, this._model.TypeOf(a.Value), symbol.Type);
    this._b.Store(value, slot);
  }

  private void LowerIncrDecr(IncrDecrStmt id) {
    var slot = this.SlotFor(id.Target);
    var symbol = this._model.VariableBindings[id.Target];
    var ty = IrTypeMapper.Map(symbol.Type);
    if (ty.IsFloat)
      throw new IrLoweringException("INCR/DECR on float");
    var current = this._b.Load(ty, slot);
    var amount = id.Amount is null
      ? new IrConstantInt(ty, 1)
      : this.Coerce(this.LowerExpr(id.Amount), this._model.TypeOf(id.Amount), symbol.Type);
    var updated = this._b.Binary(id.Increment ? IrBinaryOp.Add : IrBinaryOp.Sub, current, amount);
    this._b.Store(updated, slot);
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
    if (f.Variable is not NameExpr || !this._model.VariableBindings.TryGetValue(f.Variable, out var symbol))
      throw new IrLoweringException("FOR counter is not a scalar variable");
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
        this._b.CondBr(c, exit, body);               // UNTIL: leave once the condition holds
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
        this._b.CondBr(c, exit, header);             // LOOP UNTIL: leave once it holds
    } else {
      this._b.Br(header);
    }

    this._b.Position(exit);
  }

  private void LowerExit(ExitStmt e) {
    foreach (var loop in this._loops)
      if (loop.Kind == e.Kind || e.Kind is ExitKind.Loop) {
        this._b.Br(loop.Exit);
        return;
      }
    throw new IrLoweringException($"EXIT {e.Kind} outside a matching loop");
  }

  private void LowerIterate(IterateStmt it) {
    foreach (var loop in this._loops)
      if (loop.Kind == it.Kind || it.Kind is ExitKind.Loop) {
        this._b.Br(loop.Continue);
        return;
      }
    throw new IrLoweringException($"ITERATE {it.Kind} outside a matching loop");
  }

  private long ConstStep(Expression? step) {
    if (step is null)
      return 1;
    var folded = this._folder.TryFold(step);
    if (folded is { Integer: { } n } && n != 0)
      return n;
    throw new IrLoweringException("FOR STEP must be a non-zero compile-time constant in this lowering");
  }

  // ---- conditions & expressions -------------------------------------------

  /// <summary>Lowers a BASIC truth test to an i1 (any nonzero value is true).</summary>
  private IrValue LowerCondition(Expression expr) {
    var value = this.LowerExpr(expr);
    var pb = this._model.TypeOf(expr);
    if (pb is ScalarType { IsFloat: true })
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
      throw new IrLoweringException($"unbound name {name.Name} (parameterless call?)");
    var slot = this.SlotFor(symbol);
    return this._b.Load(IrTypeMapper.Map(symbol.Type), slot);
  }

  private IrValue LowerUnary(UnaryExpr u) {
    var operand = this.LowerExpr(u.Operand);
    var pb = this._model.TypeOf(u);
    var ty = IrTypeMapper.Map(pb);
    operand = this.Coerce(operand, this._model.TypeOf(u.Operand), pb);
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

    switch (expr.Op) {
      case BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.Greater
        or BinaryOp.LessEqual or BinaryOp.GreaterEqual:
        return this.LowerComparison(expr, leftPb, rightPb, resultPb);
      default:
        return this.LowerArithmetic(expr, leftPb, rightPb, resultPb);
    }
  }

  private IrValue LowerArithmetic(BinaryExpr expr, PbType leftPb, PbType rightPb, PbType resultPb) {
    var resultTy = IrTypeMapper.Map(resultPb);
    var signed = resultPb is ScalarType { Signed: true };
    var l = this.Coerce(this.LowerExpr(expr.Left), leftPb, resultPb);
    var r = this.Coerce(this.LowerExpr(expr.Right), rightPb, resultPb);

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
      BinaryOp.Eqv => IrBinaryOp.Xor,    // handled specially below
      BinaryOp.Imp => IrBinaryOp.Or,     // handled specially below
      _ => throw new IrLoweringException($"unsupported binary op {expr.Op}"),
    };

    switch (expr.Op) {
      case BinaryOp.Eqv: {
        var xor = this._b.Xor(l, r);
        return this._b.Xor(xor, new IrConstantInt(resultTy, -1));
      }
      case BinaryOp.Imp: {
        var notL = this._b.Xor(l, new IrConstantInt(resultTy, -1));
        return this._b.Or(notL, r);
      }
      default:
        return this._b.Binary(op, l, r);
    }
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
    var i1 = this._b.Cmp(pred, l, r);
    // a BASIC relational yields the INTEGER -1 (true) / 0 (false): sext i1 gives exactly that
    return this._b.SExt(i1, IrTypeMapper.Map(resultPb));
  }

  /// <summary>The type two operands are compared in (the wider type; float wins; signed if either is signed).</summary>
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

  /// <summary>Inserts the conversion needed to bring <paramref name="value"/> from one PB type to another.</summary>
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
      return value;                                   // same width, signedness is not a representation change
    }
    if (sf.IsFloat && st.IsFloat)
      return this._b.Cast(toTy.Bits > value.Type.Bits ? IrCastOp.FPExt : IrCastOp.FPTrunc, value, toTy);
    if (!sf.IsFloat && st.IsFloat)
      return this._b.Cast(sf.Signed ? IrCastOp.SIToFP : IrCastOp.UIToFP, value, toTy);
    return this._b.Cast(st.Signed ? IrCastOp.FPToSI : IrCastOp.FPToUI, value, toTy);
  }
}
