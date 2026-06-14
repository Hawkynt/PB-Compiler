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
  private readonly IrModule? _module;
  private readonly Dictionary<VariableSymbol, IrValue> _addr = new(ReferenceEqualityComparer.Instance);
  private readonly Stack<LoopContext> _loops = new();
  private readonly Dictionary<string, IrBasicBlock> _labels = new(StringComparer.OrdinalIgnoreCase);
  private readonly ConstantFolder _folder;

  private IrFunction _fn = null!;
  private IrBasicBlock _entry = null!;
  private int _entryAllocaCount;
  private IrBuilder _b = null!;
  private int _seq;
  private VariableSymbol? _resultVar;
  private bool _isMain;

  // GOSUB/RETURN: a fixed-depth return-id stack (PB allows nested GOSUB) plus a single
  // dispatch block that pops the top id and switches back to the matching continuation.
  private const int GosubStackDepth = 64;
  private IrValue? _gosubStack;
  private IrValue? _gosubSp;
  private IrBasicBlock? _gosubDispatch;
  private IrSwitch? _gosubSwitch;
  private readonly List<(int Id, IrBasicBlock Cont)> _gosubConts = new();
  private int _gosubSeq;

  // DATA/READ/RESTORE: every DATA item program-wide is packed into one byte blob - each
  // item is a 2-byte little-endian length followed by its raw bytes. A module-global i32
  // cursor holds the current byte offset; READ decodes the item there and advances.
  private DataLayout? _dataLayout;
  private readonly record struct DataLayout(byte[] Blob, IReadOnlyDictionary<string, int> LabelOffsets);

  private readonly record struct LoopContext(ExitKind Kind, IrBasicBlock Exit, IrBasicBlock Continue);

  private IrLowering(SemanticModel model, IReadOnlyDictionary<ProcedureSymbol, IrFunction>? procMap, IrModule? module) {
    this._model = model;
    this._procMap = procMap;
    this._module = module;
    this._folder = new ConstantFolder(model.Equates);
  }

  /// <summary>Lowers just the main body into an <c>@main</c> function (no procedures), or null if unsupported.</summary>
  public static IrFunction? TryLowerMainBody(SemanticModel model) {
    try {
      var lowering = new IrLowering(model, null, null);
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
      new IrLowering(model, procMap, module).LowerBodyInto(main, model.MainBody, null);
    } catch (IrLoweringException) {
      return null;
    }

    foreach (var (proc, irfn) in procMap) {
      try {
        new IrLowering(model, procMap, module).LowerProcedure(proc, irfn);
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
    this._isMain = proc is null;
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

    // pre-create a block for every label so forward GOTOs have a target
    foreach (var label in CollectLabels(body))
      this._labels[label] = this._fn.CreateBlock("lbl." + label);

    if (ContainsGosub(body))
      this.SetupGosub();

    this.LowerStatements(body);

    if (this._gosubSwitch is not null)              // wire each GOSUB's continuation into the shared dispatch
      foreach (var (id, cont) in this._gosubConts)
        this._gosubSwitch.AddCase(id, cont);

    if (!this.Terminated)
      this.ReturnFromFunction();
  }

  /// <summary>Allocates the return-id stack used by GOSUB to record its call sites.</summary>
  private void SetupGosub() {
    this._gosubStack = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(IrType.I32) { Count = GosubStackDepth, Name = "gosub.stack" });
    this._gosubSp = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(IrType.I32) { Name = "gosub.sp" });
    this._b.Store(new IrConstantInt(IrType.I32, 0), this._gosubSp);   // builder is positioned at entry here
  }

  /// <summary>
  /// Builds (once) the shared dispatch block a plain RETURN branches to: it pops the top
  /// return id and switches back to the matching continuation. Built lazily so a program
  /// that only uses <c>RETURN &lt;label&gt;</c> never gets an unreachable dispatch block.
  /// </summary>
  private IrBasicBlock EnsureGosubDispatch() {
    if (this._gosubDispatch is not null)
      return this._gosubDispatch;
    this._gosubDispatch = this._fn.CreateBlock("gosub.dispatch");
    var noReturn = this._fn.CreateBlock("gosub.noreturn");            // RETURN with an empty stack: unreachable in well-formed code
    var saved = this._b.Block!;
    this._b.Position(this._gosubDispatch);
    var sp = this._b.Load(IrType.I32, this._gosubSp!);
    var top = this._b.Sub(sp, new IrConstantInt(IrType.I32, 1));
    this._b.Store(top, this._gosubSp!);                               // pop
    var id = this._b.Load(IrType.I32, this._b.Gep(this._gosubStack!, top, IrType.I32));
    this._gosubSwitch = this._b.Switch(id, noReturn);
    this._b.Position(noReturn);
    this._b.Unreachable();
    this._b.Position(saved);
    return this._gosubDispatch;
  }

  private static IEnumerable<string> CollectLabels(IReadOnlyList<Statement> statements) {
    foreach (var s in statements)
      switch (s) {
        case LabelStmt l: yield return l.Name; break;
        case IfStmt i:
          foreach (var n in CollectLabels(i.Then)) yield return n;
          foreach (var (_, body) in i.ElseIfs)
            foreach (var n in CollectLabels(body)) yield return n;
          if (i.Else is { } e)
            foreach (var n in CollectLabels(e)) yield return n;
          break;
        case ForStmt f:
          foreach (var n in CollectLabels(f.Body)) yield return n;
          break;
        case DoLoopStmt d:
          foreach (var n in CollectLabels(d.Body)) yield return n;
          break;
        case SelectStmt sel:
          foreach (var arm in sel.Arms)
            foreach (var n in CollectLabels(arm.Body)) yield return n;
          break;
      }
  }

  private static bool ContainsGosub(IReadOnlyList<Statement> statements) {
    foreach (var s in statements)
      switch (s) {
        case GosubStmt: return true;
        case IfStmt i when ContainsGosub(i.Then) || i.ElseIfs.Any(e => ContainsGosub(e.Body)) || (i.Else is { } el && ContainsGosub(el)):
          return true;
        case ForStmt f when ContainsGosub(f.Body): return true;
        case DoLoopStmt d when ContainsGosub(d.Body): return true;
        case SelectStmt sel when sel.Arms.Any(a => ContainsGosub(a.Body)): return true;
      }
    return false;
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
    if (symbol.Type is StringType) {
      alloca = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(IrType.Ptr) { Name = symbol.Name });  // holds a string handle
    } else if (symbol.Type is ArrayType arr) {
      if (arr.IsDynamic)
        throw new IrLoweringException("dynamic array");
      IrType elem;
      if (arr.Element is StringType)
        elem = IrType.Ptr;                              // an array of string handles
      else if (!IrTypeMapper.TryMap(arr.Element, out elem))
        throw new IrLoweringException("non-scalar array element");
      alloca = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(elem) { Count = arr.ElementCount, Name = symbol.Name });
    } else {
      alloca = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(IrTypeMapper.Map(symbol.Type)) { Name = symbol.Name });
    }
    this._addr[symbol] = alloca;
    return alloca;
  }

  /// <summary>The address of one array element, by row-major flattening of the index list.</summary>
  private (IrValue Address, PbType Element) ElementAddress(CallOrIndexExpr expr) {
    if (!this._model.VariableBindings.TryGetValue(expr, out var symbol) || symbol.Type is not ArrayType arr)
      throw new IrLoweringException($"not an array element: {expr.Name}");
    if (arr.IsDynamic)
      return this.DynamicElementAddress(expr, symbol, arr);
    if (arr.StaticBounds is not { } bounds || bounds.Count != expr.Arguments.Count)
      throw new IrLoweringException("rank mismatch");
    var basePtr = this.SlotFor(symbol);

    IrValue? flat = null;
    for (var k = 0; k < bounds.Count; ++k) {
      var idx = this.Coerce(this.LowerExpr(expr.Arguments[k]), this._model.TypeOf(expr.Arguments[k]), PbType.Long);
      var rel = this._b.Sub(idx, new IrConstantInt(IrType.I32, bounds[k].Lower));
      var size = bounds[k].Upper - bounds[k].Lower + 1;
      flat = flat is null ? rel : this._b.Add(this._b.Mul(flat, new IrConstantInt(IrType.I32, size)), rel);
    }

    if (arr.Element is StringType)
      return (this._b.Gep(basePtr, flat!, IrType.Ptr), arr.Element);   // ptr-element stride is target-dependent: typed GEP
    var byteOffset = this._b.Mul(flat!, new IrConstantInt(IrType.I32, arr.Element.Size));
    return (this._b.Gep(basePtr, byteOffset), arr.Element);
  }

  // A dynamic array is a runtime-allocated buffer plus a bound descriptor: the data
  // pointer and, per dimension, the lower bound and size each live in their own
  // promotable scalar slot. Sizes feed row-major flattening and the allocation count.
  private readonly record struct DynArr(IrValue Data, IrValue[] Lo, IrValue[] Size);
  private readonly Dictionary<VariableSymbol, DynArr> _dynArrays = new(ReferenceEqualityComparer.Instance);

  private DynArr DynDescriptor(VariableSymbol symbol, int rank) {
    if (this._dynArrays.TryGetValue(symbol, out var existing))
      return existing;
    var data = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(IrType.Ptr) { Name = symbol.Name + ".data" });
    var lo = new IrValue[rank];
    var size = new IrValue[rank];
    for (var k = 0; k < rank; ++k) {
      lo[k] = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(IrType.I32) { Name = $"{symbol.Name}.lo{k}" });
      size[k] = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(IrType.I32) { Name = $"{symbol.Name}.size{k}" });
    }
    var descriptor = new DynArr(data, lo, size);
    this._dynArrays[symbol] = descriptor;
    return descriptor;
  }

  /// <summary>The address of one element of a runtime-allocated dynamic array (row-major flattening).</summary>
  private (IrValue Address, PbType Element) DynamicElementAddress(CallOrIndexExpr expr, VariableSymbol symbol, ArrayType arr) {
    if (expr.Arguments.Count != arr.Rank)
      throw new IrLoweringException("dynamic array rank mismatch");
    var descriptor = this.DynDescriptor(symbol, arr.Rank);
    var data = this._b.Load(IrType.Ptr, descriptor.Data);

    IrValue? flat = null;
    for (var k = 0; k < arr.Rank; ++k) {
      var idx = this.Coerce(this.LowerExpr(expr.Arguments[k]), this._model.TypeOf(expr.Arguments[k]), PbType.Long);
      var rel = this._b.Sub(idx, this._b.Load(IrType.I32, descriptor.Lo[k]));
      flat = flat is null ? rel : this._b.Add(this._b.Mul(flat, this._b.Load(IrType.I32, descriptor.Size[k])), rel);
    }

    if (arr.Element is StringType)
      return (this._b.Gep(data, flat!, IrType.Ptr), arr.Element);
    return (this._b.Gep(data, this._b.Mul(flat!, new IrConstantInt(IrType.I32, arr.Element.Size))), arr.Element);
  }

  private VariableSymbol SymbolOf(Expression target) =>
    target is NameExpr && this._model.VariableBindings.TryGetValue(target, out var s)
      ? s
      : throw new IrLoweringException("non-scalar or unbound storage reference");

  // ---- statements ----------------------------------------------------------

  private void LowerStatements(IReadOnlyList<Statement> statements) {
    foreach (var statement in statements) {
      if (this.Terminated && statement is not LabelStmt)
        continue;                                      // code unreachable until the next label
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
      case RedimStmt rdm: this.LowerRedim(rdm); break;
      case EraseStmt er: this.LowerErase(er); break;
      case SwapStmt sw: this.LowerSwap(sw); break;
      case LabelStmt l: this.LowerLabel(l); break;
      case GotoStmt g: this.LowerGoto(g); break;
      case GosubStmt gs: this.LowerGosub(gs); break;
      case ReturnStmt rs: this.LowerReturn(rs); break;
      case OnGotoStmt og: this.LowerOnGoto(og); break;
      case PrintStmt pr: this.LowerPrint(pr); break;
      case InputStmt inp: this.LowerInput(inp); break;
      case OpenStmt op: this.LowerOpen(op); break;
      case CloseStmt cl: this.LowerClose(cl); break;
      case DataStmt: break;                          // DATA is gathered once into a module blob; the statement itself emits nothing
      case ReadStmt rd: this.LowerRead(rd); break;
      case RestoreStmt rs: this.LowerRestore(rs); break;
      case EndStmt: this.LowerEnd(); break;
      default: throw new IrLoweringException($"unsupported statement: {statement.GetType().Name}");
    }
  }

  private void LowerAssign(AssignStmt a) {
    if (a.Target is NameExpr && this._model.VariableBindings.TryGetValue(a.Target, out var strSym) && strSym.Type is StringType) {
      this._b.Store(this.LowerStringExpr(a.Value), this.SlotFor(strSym));   // strings are immutable handles
      return;
    }
    if (a.Target is CallOrIndexExpr indexed && this._model.VariableBindings.TryGetValue(indexed, out var arrSym) && arrSym.Type is ArrayType) {
      var (address, element) = this.ElementAddress(indexed);
      if (element is StringType) {
        this._b.Store(this.LowerStringExpr(a.Value), address);   // a string array element holds an immutable handle
        return;
      }
      this._b.Store(this.Coerce(this.LowerExpr(a.Value), this._model.TypeOf(a.Value), element), address);
      return;
    }
    var symbol = this.SymbolOf(a.Target);
    var slot = this.SlotFor(symbol);
    var value = this.Coerce(this.LowerExpr(a.Value), this._model.TypeOf(a.Value), symbol.Type);
    this._b.Store(value, slot);
  }

  private void LowerSwap(SwapStmt sw) {
    var (leftAddr, leftType) = this.LValue(sw.Left);
    var (rightAddr, rightType) = this.LValue(sw.Right);
    if (!leftType.Equals(rightType))
      throw new IrLoweringException("SWAP of differently-typed operands");
    var ty = IrTypeMapper.Map(leftType);
    var leftVal = this._b.Load(ty, leftAddr);
    var rightVal = this._b.Load(ty, rightAddr);
    this._b.Store(rightVal, leftAddr);
    this._b.Store(leftVal, rightAddr);
  }

  /// <summary>The storage address and element type of a scalar lvalue (a variable or a static-array element).</summary>
  private (IrValue Address, PbType Type) LValue(Expression e) {
    if (e is NameExpr && this._model.VariableBindings.TryGetValue(e, out var sym) && sym.Type is ScalarType)
      return (this.SlotFor(sym), sym.Type);
    if (e is CallOrIndexExpr ci && this._model.VariableBindings.TryGetValue(ci, out var arr) && arr.Type is ArrayType)
      return this.ElementAddress(ci);
    throw new IrLoweringException("unsupported lvalue");
  }

  private void LowerPrint(PrintStmt p) {
    if (this._module is null)
      throw new IrLoweringException("PRINT requires whole-module lowering");
    if (p.IsLPrint || p.UsingFormat is not null)
      throw new IrLoweringException("LPRINT / PRINT USING");
    var file = p.FileNumber is { } fn ? this.FileNum(fn) : null;

    foreach (var item in p.Items) {
      if (item.Value is not { } expr)
        continue;
      if (expr is StringLiteralExpr lit) {
        var bytes = System.Text.Encoding.ASCII.GetBytes(lit.Value);
        var global = this._module.AddStringConstant(bytes);
        this.EmitIo(file, "print", "str", IrType.Void, [IrType.Ptr, IrType.I32], global, new IrConstantInt(IrType.I32, bytes.Length));
        continue;
      }
      if (this._model.TypeOf(expr) is StringType) {
        this.EmitIo(file, "print", "strvar", IrType.Void, [IrType.Ptr], this.LowerStringExpr(expr));
        continue;
      }
      if (this._model.TypeOf(expr) is not ScalarType s)
        throw new IrLoweringException("PRINT of a non-numeric, non-literal item");
      var (suffix, ty) = NumericSuffix(s);
      this.EmitIo(file, "print", suffix, IrType.Void, [ty], this.Coerce(this.LowerExpr(expr), s, s));
    }

    if (p.Items.Count == 0 || p.Items[^1].Separator == PrintSeparator.Newline)
      this.EmitIo(file, "print", "nl", IrType.Void, []);
  }

  private void LowerInput(InputStmt input) {
    if (this._module is null)
      throw new IrLoweringException("INPUT requires whole-module lowering");
    var file = input.FileNumber is { } fn ? this.FileNum(fn) : null;

    if (input.Prompt is { } prompt && file is null) {
      var bytes = System.Text.Encoding.ASCII.GetBytes(prompt);
      var global = this._module.AddStringConstant(bytes);
      this.EmitIo(null, "print", "str", IrType.Void, [IrType.Ptr, IrType.I32], global, new IrConstantInt(IrType.I32, bytes.Length));
    }

    foreach (var target in input.Targets) {
      if (target is NameExpr && this._model.VariableBindings.TryGetValue(target, out var strSym) && strSym.Type is StringType) {
        this._b.Store(this.EmitIo(file, "input", input.IsLineInput ? "line" : "str", IrType.Ptr, []), this.SlotFor(strSym));
        continue;
      }
      var (addr, type) = this.LValue(target);
      if (type is not ScalarType s)
        throw new IrLoweringException("INPUT into a non-scalar target");
      var (suffix, ty) = NumericSuffix(s);
      this._b.Store(this.EmitIo(file, "input", suffix, ty, []), addr);
    }
  }

  private void LowerOpen(OpenStmt o) {
    if (this._module is null)
      throw new IrLoweringException("OPEN requires whole-module lowering");
    this._b.Call(IrType.Void, this.RuntimeFn("rt_file_open", IrType.Void, IrType.I32, IrType.Ptr, IrType.I32),
      this.FileNum(o.FileNumber), this.LowerStringExpr(o.FileName), new IrConstantInt(IrType.I32, (int)o.Mode));
  }

  private void LowerClose(CloseStmt c) {
    if (this._module is null)
      throw new IrLoweringException("CLOSE requires whole-module lowering");
    if (c.FileNumbers.Count == 0) {
      this._b.Call(IrType.Void, this.RuntimeFn("rt_file_close_all", IrType.Void));
      return;
    }
    foreach (var fn in c.FileNumbers)
      this._b.Call(IrType.Void, this.RuntimeFn("rt_file_close", IrType.Void, IrType.I32), this.FileNum(fn));
  }

  /// <summary>Lowers a file-number expression (unwrapping the #n marker) to an i32.</summary>
  private IrValue FileNum(Expression e) {
    var inner = e is FileNumberExpr f ? f.Number : e;
    return this.Coerce(this.LowerExpr(inner), this._model.TypeOf(inner), PbType.Long);
  }

  /// <summary>Emits a console (rt_*) or file (rt_f*, file number first) I/O runtime call.</summary>
  private IrValue EmitIo(IrValue? file, string op, string suffix, IrType returnType, IrType[] argTypes, params IrValue[] args) {
    var name = file is null ? $"rt_{op}_{suffix}" : $"rt_f{op}_{suffix}";
    var types = file is null ? argTypes : [IrType.I32, .. argTypes];
    var callArgs = file is null ? args : [file, .. args];
    return this._b.Call(returnType, this.RuntimeFn(name, returnType, types), callArgs);
  }

  private static (string Suffix, IrType Type) NumericSuffix(ScalarType s) {
    if (s.IsFloat)
      return s.ByteSize switch { 4 => ("single", IrType.F32), 8 => ("double", IrType.F64), _ => ("ext", IrType.F80) };
    var bits = s.ByteSize * 8;
    return ($"{(s.Signed ? "i" : "u")}{bits}", IrType.Integer(bits));
  }

  /// <summary>Finds or declares an external runtime function by name and signature.</summary>
  private IrFunction RuntimeFn(string name, IrType returnType, params IrType[] paramTypes) {
    if (this._module is null)
      throw new IrLoweringException("runtime calls require whole-module lowering");
    if (this._module.FindFunction(name) is { } existing)
      return existing;
    var args = paramTypes.Select((t, i) => new IrArgument(t, i)).ToList();
    return this._module.AddFunction(new IrFunction(name, returnType, args));   // a declaration (no body)
  }

  /// <summary>Lowers a string-typed expression to a runtime string handle (an opaque pointer).</summary>
  private IrValue LowerStringExpr(Expression expr) {
    if (this._module is null)
      throw new IrLoweringException("strings require whole-module lowering");
    switch (expr) {
      case StringLiteralExpr lit: {
        var bytes = System.Text.Encoding.ASCII.GetBytes(lit.Value);
        var global = this._module!.AddStringConstant(bytes);
        return this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_const", IrType.Ptr, IrType.Ptr, IrType.I32), global, new IrConstantInt(IrType.I32, bytes.Length));
      }
      case NameExpr when this._model.VariableBindings.TryGetValue(expr, out var sym) && sym.Type is StringType:
        return this._b.Load(IrType.Ptr, this.SlotFor(sym));
      case BinaryExpr { Op: BinaryOp.Concat } cat:
        return this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_concat", IrType.Ptr, IrType.Ptr, IrType.Ptr),
          this.LowerStringExpr(cat.Left), this.LowerStringExpr(cat.Right));
      case CallOrIndexExpr arrayRead when this._model.VariableBindings.TryGetValue(arrayRead, out var arr) && arr.Type is ArrayType { Element: StringType }:
        return this._b.Load(IrType.Ptr, this.ElementAddress(arrayRead).Address);
      case CallOrIndexExpr ci when this._model.IntrinsicBindings.TryGetValue(ci, out var info):
        return this.LowerStringIntrinsic(ci, info.Name);
      default:
        throw new IrLoweringException($"unsupported string expression: {expr.GetType().Name}");
    }
  }

  private void LowerLabel(LabelStmt label) {
    var block = this._labels[label.Name];
    if (!this.Terminated)
      this._b.Br(block);                               // fall through into the label
    this._b.Position(block);
  }

  private void LowerGoto(GotoStmt g) {
    if (!this._labels.TryGetValue(g.Target, out var target))
      throw new IrLoweringException($"GOTO to unknown label {g.Target}");
    this._b.Br(target);
  }

  private void LowerGosub(GosubStmt g) {
    if (this._gosubSp is null)
      throw new IrLoweringException("GOSUB without return-stack setup");
    if (!this._labels.TryGetValue(g.Target, out var target))
      throw new IrLoweringException($"GOSUB to unknown label {g.Target}");
    var id = ++this._gosubSeq;
    var sp = this._b.Load(IrType.I32, this._gosubSp);
    this._b.Store(new IrConstantInt(IrType.I32, id), this._b.Gep(this._gosubStack!, sp, IrType.I32));  // push the return id
    this._b.Store(this._b.Add(sp, new IrConstantInt(IrType.I32, 1)), this._gosubSp);
    this._b.Br(target);
    var cont = this.NewBlock("gosub.cont");            // RETURN dispatches back here
    this._gosubConts.Add((id, cont));
    this._b.Position(cont);
  }

  private void LowerReturn(ReturnStmt r) {
    if (this._gosubSp is null)
      throw new IrLoweringException("RETURN without a matching GOSUB");
    if (r.Target is { } label) {                       // RETURN <label>: pop the id, jump to the explicit label
      var sp = this._b.Load(IrType.I32, this._gosubSp);
      this._b.Store(this._b.Sub(sp, new IrConstantInt(IrType.I32, 1)), this._gosubSp);
      if (!this._labels.TryGetValue(label, out var target))
        throw new IrLoweringException($"RETURN to unknown label {label}");
      this._b.Br(target);
      return;
    }
    this._b.Br(this.EnsureGosubDispatch());            // plain RETURN: dispatch back to the caller's continuation
  }

  private void LowerOnGoto(OnGotoStmt o) {
    if (o.IsGosub)
      throw new IrLoweringException("ON ... GOSUB");
    if (this._model.TypeOf(o.Selector) is not ScalarType { IsFloat: false })
      throw new IrLoweringException("ON GOTO with a non-integer selector");
    var selector = this.LowerExpr(o.Selector);
    var fallthrough = this.NewBlock("on.next");        // out-of-range selector falls through (PB semantics)
    var sw = this._b.Switch(selector, fallthrough);
    for (var k = 0; k < o.Targets.Count; ++k) {
      if (!this._labels.TryGetValue(o.Targets[k], out var target))
        throw new IrLoweringException($"ON GOTO to unknown label {o.Targets[k]}");
      sw.AddCase(k + 1, target);                       // selector is 1-based
    }
    this._b.Position(fallthrough);
  }

  // ---- DATA / READ / RESTORE ----------------------------------------------

  /// <summary>Packs every program-wide DATA item into one length-prefixed blob (computed once).</summary>
  private DataLayout GetDataLayout() {
    if (this._dataLayout is { } cached)
      return cached;
    var blob = new List<byte>();
    var labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    GatherData(this._model.MainBody, blob, labels);
    var layout = new DataLayout(blob.ToArray(), labels);
    this._dataLayout = layout;
    return layout;
  }

  private static void GatherData(IReadOnlyList<Statement> statements, List<byte> blob, Dictionary<string, int> labels) {
    foreach (var s in statements)
      switch (s) {
        case LabelStmt l:
          labels[l.Name] = blob.Count;                 // RESTORE <label> rewinds to the first DATA item at/after the label
          break;
        case DataStmt d:
          foreach (var item in d.Items) {
            var bytes = System.Text.Encoding.ASCII.GetBytes(item);
            if (bytes.Length > 0xFFFF)
              throw new IrLoweringException("DATA item exceeds 64KB");
            blob.Add((byte)(bytes.Length & 0xFF));
            blob.Add((byte)((bytes.Length >> 8) & 0xFF));
            blob.AddRange(bytes);
          }
          break;
        case IfStmt i:
          GatherData(i.Then, blob, labels);
          foreach (var (_, body) in i.ElseIfs) GatherData(body, blob, labels);
          if (i.Else is { } e) GatherData(e, blob, labels);
          break;
        case ForStmt f: GatherData(f.Body, blob, labels); break;
        case DoLoopStmt dl: GatherData(dl.Body, blob, labels); break;
        case SelectStmt sel:
          foreach (var arm in sel.Arms) GatherData(arm.Body, blob, labels);
          break;
      }
  }

  /// <summary>The shared DATA blob and read cursor, created on the module on first use.</summary>
  private (IrGlobalVariable Blob, IrGlobalVariable Cursor) DataGlobals() {
    if (this._module is null)
      throw new IrLoweringException("DATA/READ requires whole-module lowering");
    var blob = this._module.FindGlobal(".data")
      ?? this._module.AddGlobal(new IrGlobalVariable(".data", IrType.I8) { Bytes = this.GetDataLayout().Blob, IsZeroInitialized = false });
    var cursor = this._module.FindGlobal(".data_cursor")
      ?? this._module.AddGlobal(new IrGlobalVariable(".data_cursor", IrType.I32) { IsZeroInitialized = true });
    return (blob, cursor);
  }

  private void LowerRead(ReadStmt r) {
    foreach (var target in r.Targets)
      this.LowerReadInto(target);
  }

  private void LowerReadInto(Expression target) {
    var (blob, cursor) = this.DataGlobals();
    var off = this._b.Load(IrType.I32, cursor);
    var len = this._b.ZExt(this._b.Load(IrType.I16, this._b.Gep(blob, off)), IrType.I32);       // 2-byte length prefix
    var dataPtr = this._b.Gep(blob, this._b.Add(off, new IrConstantInt(IrType.I32, 2)));
    var handle = this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_const", IrType.Ptr, IrType.Ptr, IrType.I32), dataPtr, len);
    this._b.Store(this._b.Add(this._b.Add(off, new IrConstantInt(IrType.I32, 2)), len), cursor); // advance past length + bytes

    this._model.VariableBindings.TryGetValue(target, out var sym);
    var elementType = sym?.Type is ArrayType at ? at.Element : sym?.Type ?? this._model.TypeOf(target);
    if (elementType is StringType) {
      var address = target is CallOrIndexExpr ce ? this.ElementAddress(ce).Address : this.SlotFor(sym!);
      this._b.Store(handle, address);                  // a string item is stored as its handle
      return;
    }
    var value = this._b.Call(IrType.F64, this.RuntimeFn("rt_str_val", IrType.F64, IrType.Ptr), handle);  // parse a numeric item
    var (addr, type) = this.LValue(target);
    this._b.Store(this.Coerce(value, PbType.Double, type), addr);
  }

  private void LowerRestore(RestoreStmt r) {
    var (_, cursor) = this.DataGlobals();
    var offset = 0;
    if (r.Target is { } label && !this.GetDataLayout().LabelOffsets.TryGetValue(label, out offset))
      throw new IrLoweringException($"RESTORE to unknown DATA label {label}");
    this._b.Store(new IrConstantInt(IrType.I32, offset), cursor);
  }

  private void LowerEnd() {
    // END terminates the whole program. In main that is simply a return; inside a
    // procedure it would need a program-exit primitive the IR does not model yet.
    if (!this._isMain)
      throw new IrLoweringException("END inside a procedure");
    this.ReturnFromFunction();
  }

  private void LowerRedim(RedimStmt r) {
    foreach (var v in r.Variables) {
      if (!this._model.RedimBindings.TryGetValue(v, out var symbol) || symbol.Type is not ArrayType { IsDynamic: true } arr)
        throw new IrLoweringException($"REDIM of non-dynamic array {v.Name}");
      if (v.ArrayBounds is not { } dims || dims.Count != arr.Rank)
        throw new IrLoweringException("REDIM rank mismatch");

      var descriptor = this.DynDescriptor(symbol, arr.Rank);
      IrValue? count = null;
      for (var k = 0; k < dims.Count; ++k) {
        var (lower, upper) = dims[k];
        var lo = lower is null
          ? new IrConstantInt(IrType.I32, 0)
          : this.Coerce(this.LowerExpr(lower), this._model.TypeOf(lower), PbType.Long);
        var hi = this.Coerce(this.LowerExpr(upper), this._model.TypeOf(upper), PbType.Long);
        var size = this._b.Add(this._b.Sub(hi, lo), new IrConstantInt(IrType.I32, 1));
        this._b.Store(lo, descriptor.Lo[k]);
        this._b.Store(size, descriptor.Size[k]);
        count = count is null ? size : this._b.Mul(count, size);
      }

      var isString = arr.Element is StringType;
      IrValue data;
      if (r.Preserve) {                                // realloc keeps the existing prefix (mem2reg seeds the unallocated slot to null = fresh malloc)
        var old = this._b.Load(IrType.Ptr, descriptor.Data);
        data = isString
          ? this._b.Call(IrType.Ptr, this.RuntimeFn("rt_arr_realloc_ptr", IrType.Ptr, IrType.Ptr, IrType.I32), old, count!)
          : this._b.Call(IrType.Ptr, this.RuntimeFn("rt_arr_realloc", IrType.Ptr, IrType.Ptr, IrType.I32, IrType.I32), old, count!, new IrConstantInt(IrType.I32, arr.Element.Size));
      } else {
        data = isString
          ? this._b.Call(IrType.Ptr, this.RuntimeFn("rt_arr_alloc_ptr", IrType.Ptr, IrType.I32), count!)            // count target-pointers
          : this._b.Call(IrType.Ptr, this.RuntimeFn("rt_arr_alloc", IrType.Ptr, IrType.I32, IrType.I32), count!, new IrConstantInt(IrType.I32, arr.Element.Size));
      }
      this._b.Store(data, descriptor.Data);
    }
  }

  private void LowerErase(EraseStmt e) {
    foreach (var name in e.Arrays) {
      if (!this._model.VariableBindings.TryGetValue(name, out var symbol) || symbol.Type is not ArrayType arr)
        throw new IrLoweringException("ERASE of a non-array");
      if (!arr.IsDynamic)
        throw new IrLoweringException("ERASE of a static array");   // PB zeroes it in place; not modeled here
      var descriptor = this.DynDescriptor(symbol, arr.Rank);
      this._b.Call(IrType.Void, this.RuntimeFn("rt_arr_free", IrType.Void, IrType.Ptr), this._b.Load(IrType.Ptr, descriptor.Data));
      this._b.Store(new IrNullPtr(), descriptor.Data);
    }
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
    var constStep = this.TryConstStep(f.Step);
    var slot = this.SlotFor(symbol);
    var limitSlot = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(ty) { Name = symbol.Name + ".limit" });

    this._b.Store(this.Coerce(this.LowerExpr(f.From), this._model.TypeOf(f.From), symbol.Type), slot);
    this._b.Store(this.Coerce(this.LowerExpr(f.To), this._model.TypeOf(f.To), symbol.Type), limitSlot);

    // a non-constant step has an unknown direction, so keep it as a loop-invariant SSA value
    IrValue? stepValue = null;
    if (constStep is null) {
      if (!signed)
        throw new IrLoweringException("FOR with a runtime STEP over an unsigned counter");
      stepValue = this.Coerce(this.LowerExpr(f.Step!), this._model.TypeOf(f.Step!), symbol.Type);
    }

    var header = this.NewBlock("for.head");
    var body = this.NewBlock("for.body");
    var inc = this.NewBlock("for.inc");
    var exit = this.NewBlock("for.exit");
    this._b.Br(header);

    this._b.Position(header);
    var i = this._b.Load(ty, slot);
    var limit = this._b.Load(ty, limitSlot);
    IrValue cond;
    if (constStep is { } cs) {
      var pred = cs > 0 ? (signed ? IrCmpPred.Sle : IrCmpPred.Ule) : (signed ? IrCmpPred.Sge : IrCmpPred.Uge);
      cond = this._b.Cmp(pred, i, limit);
    } else {
      // continue = step >= 0 ? i <= limit : i >= limit  (the sign test is loop-invariant; LICM hoists it)
      var ascending = this._b.Cmp(IrCmpPred.Sge, stepValue!, new IrConstantInt(ty, 0));
      var inAsc = this._b.And(ascending, this._b.Cmp(IrCmpPred.Sle, i, limit));
      var notAsc = this._b.Xor(ascending, new IrConstantInt(IrType.I1, 1));
      var inDesc = this._b.And(notAsc, this._b.Cmp(IrCmpPred.Sge, i, limit));
      cond = this._b.Or(inAsc, inDesc);
    }
    this._b.CondBr(cond, body, exit);

    this._b.Position(body);
    this._loops.Push(new LoopContext(ExitKind.For, exit, inc));
    this.LowerStatements(f.Body);
    this._loops.Pop();
    if (!this.Terminated)
      this._b.Br(inc);

    this._b.Position(inc);
    var iv = this._b.Load(ty, slot);
    var increment = constStep is { } c2 ? (IrValue)new IrConstantInt(ty, c2) : stepValue!;
    this._b.Store(this._b.Binary(IrBinaryOp.Add, iv, increment), slot);
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

  private long? TryConstStep(Expression? step) {
    if (step is null)
      return 1;
    if (this._folder.TryFold(step) is { Integer: { } n } && n != 0)
      return n;
    return null;   // runtime step (or a constant zero, which the direction test also handles)
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
      case CallOrIndexExpr intr when this._model.IntrinsicBindings.TryGetValue(intr, out var info):
        return this.LowerIntrinsic(intr, info.Name);
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

  /// <summary>Lowers a pure numeric intrinsic that needs no runtime (ABS, SGN); declines the rest.</summary>
  private IrValue LowerIntrinsic(CallOrIndexExpr call, string name) {
    if (call.Arguments.Count != 1)
      throw new IrLoweringException($"intrinsic {name} with {call.Arguments.Count} arguments");
    return name.ToUpperInvariant() switch {
      "ABS" => this.LowerAbs(call),
      "SGN" => this.LowerSgn(call),
      "FIX" => this.LowerFix(call),
      "INT" => this.LowerInt(call),
      "CDBL" or "CSNG" => this.LowerConvert(call),
      "LEN" => this.LowerLen(call),
      "ASC" => this.LowerAsc(call),
      "VAL" => this.LowerVal(call),
      "SQR" => this.LowerMath(call, "sqrt"),
      "SIN" => this.LowerMath(call, "sin"),
      "COS" => this.LowerMath(call, "cos"),
      "EXP" => this.LowerMath(call, "exp"),
      "LOG" => this.LowerMath(call, "log"),
      "TAN" => this.LowerMath(call, "tan"),
      "ATN" => this.LowerMath(call, "atan"),
      _ => throw new IrLoweringException($"intrinsic {name}"),
    };
  }

  /// <summary>Lowers a string-returning intrinsic (LEFT$/RIGHT$/MID$/CHR$) to a runtime call.</summary>
  private IrValue LowerStringIntrinsic(CallOrIndexExpr ci, string name) {
    IrValue Str(int i) => this.LowerStringExpr(ci.Arguments[i]);
    IrValue Num(int i) => this.Coerce(this.LowerExpr(ci.Arguments[i]), this._model.TypeOf(ci.Arguments[i]), PbType.Long);
    return name.ToUpperInvariant() switch {
      "LEFT$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_left", IrType.Ptr, IrType.Ptr, IrType.I32), Str(0), Num(1)),
      "RIGHT$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_right", IrType.Ptr, IrType.Ptr, IrType.I32), Str(0), Num(1)),
      "MID$" when ci.Arguments.Count >= 3 => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_mid", IrType.Ptr, IrType.Ptr, IrType.I32, IrType.I32), Str(0), Num(1), Num(2)),
      "MID$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_mid2", IrType.Ptr, IrType.Ptr, IrType.I32), Str(0), Num(1)),
      "CHR$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_chr", IrType.Ptr, IrType.I32), Num(0)),
      "SPACE$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_space", IrType.Ptr, IrType.I32), Num(0)),
      "STRING$" when this._model.TypeOf(ci.Arguments[1]) is StringType =>
        this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_string_s", IrType.Ptr, IrType.I32, IrType.Ptr), Num(0), Str(1)),
      "STRING$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_string", IrType.Ptr, IrType.I32, IrType.I32), Num(0), Num(1)),
      "STR$" => this.LowerStrOf(ci.Arguments[0]),
      "HEX$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_hex", IrType.Ptr, IrType.I32), Num(0)),
      "OCT$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_oct", IrType.Ptr, IrType.I32), Num(0)),
      _ => throw new IrLoweringException($"string intrinsic {name}"),
    };
  }

  private IrValue LowerStrOf(Expression arg) {
    if (this._model.TypeOf(arg) is not ScalarType s)
      throw new IrLoweringException("STR$ of a non-numeric value");
    var (name, ty) = s.IsFloat
      ? (s.ByteSize == 8 ? ("rt_str_from_double", IrType.F64) : ("rt_str_from_single", IrType.F32))
      : ($"rt_str_from_{(s.Signed ? "i" : "u")}{s.ByteSize * 8}", IrType.Integer(s.ByteSize * 8));
    return this._b.Call(IrType.Ptr, this.RuntimeFn(name, IrType.Ptr, ty), this.Coerce(this.LowerExpr(arg), s, s));
  }

  private IrValue LowerVal(CallOrIndexExpr call) {
    var value = this._b.Call(IrType.F64, this.RuntimeFn("rt_str_val", IrType.F64, IrType.Ptr), this.LowerStringExpr(call.Arguments[0]));
    return this.Coerce(value, PbType.Double, this._model.TypeOf(call));
  }

  /// <summary>Lowers a floating-point math intrinsic to the matching LLVM intrinsic (llvm.sqrt.fN, etc.).</summary>
  private IrValue LowerMath(CallOrIndexExpr call, string fn) {
    var resultPb = this._model.TypeOf(call);
    var ty = IrTypeMapper.Map(resultPb);
    if (!ty.IsFloat)
      throw new IrLoweringException($"{fn} on a non-float result");
    var arg = this.Coerce(this.LowerExpr(call.Arguments[0]), this._model.TypeOf(call.Arguments[0]), resultPb);
    return this._b.Call(ty, this.RuntimeFn($"llvm.{fn}.f{ty.Bits}", ty, ty), arg);
  }

  private IrValue LowerAsc(CallOrIndexExpr call) {
    var code = this._b.Call(IrType.I32, this.RuntimeFn("rt_str_asc", IrType.I32, IrType.Ptr), this.LowerStringExpr(call.Arguments[0]));
    return this.Coerce(code, PbType.Long, this._model.TypeOf(call));
  }

  private IrValue LowerLen(CallOrIndexExpr call) {
    if (this._model.TypeOf(call.Arguments[0]) is not StringType)
      throw new IrLoweringException("LEN of a non-string");
    var length = this._b.Call(IrType.I32, this.RuntimeFn("rt_str_len", IrType.I32, IrType.Ptr), this.LowerStringExpr(call.Arguments[0]));
    return this.Coerce(length, PbType.Long, this._model.TypeOf(call));   // LEN result narrows to its bound type
  }

  private IrValue LowerStringComparison(BinaryExpr expr, PbType resultPb) {
    var cmp = this._b.Call(IrType.I32, this.RuntimeFn("rt_str_compare", IrType.I32, IrType.Ptr, IrType.Ptr),
      this.LowerStringExpr(expr.Left), this.LowerStringExpr(expr.Right));   // <0 / 0 / >0
    var pred = expr.Op switch {
      BinaryOp.Equal => IrCmpPred.Eq,
      BinaryOp.NotEqual => IrCmpPred.Ne,
      BinaryOp.Less => IrCmpPred.Slt,
      BinaryOp.LessEqual => IrCmpPred.Sle,
      BinaryOp.Greater => IrCmpPred.Sgt,
      BinaryOp.GreaterEqual => IrCmpPred.Sge,
      _ => throw new IrLoweringException($"string comparison {expr.Op}"),
    };
    return this._b.SExt(this._b.Cmp(pred, cmp, new IrConstantInt(IrType.I32, 0)), IrTypeMapper.Map(resultPb));
  }

  private IrValue LowerConvert(CallOrIndexExpr call) {
    // CDBL/CSNG are exactly a type conversion to the result type
    var resultPb = this._model.TypeOf(call);
    return this.Coerce(this.LowerExpr(call.Arguments[0]), this._model.TypeOf(call.Arguments[0]), resultPb);
  }

  private IrValue LowerFix(CallOrIndexExpr call) {
    var resultPb = this._model.TypeOf(call);
    var ty = IrTypeMapper.Map(resultPb);
    var v = this.Coerce(this.LowerExpr(call.Arguments[0]), this._model.TypeOf(call.Arguments[0]), resultPb);
    if (ty.IsInteger)
      return v;                                       // integers have no fractional part
    // FIX = truncate toward zero: round-trip through a 64-bit integer
    return this._b.Cast(IrCastOp.SIToFP, this._b.Cast(IrCastOp.FPToSI, v, IrType.I64), ty);
  }

  private IrValue LowerInt(CallOrIndexExpr call) {
    var resultPb = this._model.TypeOf(call);
    var ty = IrTypeMapper.Map(resultPb);
    var v = this.Coerce(this.LowerExpr(call.Arguments[0]), this._model.TypeOf(call.Arguments[0]), resultPb);
    if (ty.IsInteger)
      return v;
    // INT = floor: trunc toward zero, then subtract one when truncation rounded a negative up
    var trunc = this._b.Cast(IrCastOp.SIToFP, this._b.Cast(IrCastOp.FPToSI, v, IrType.I64), ty);
    var roundedUp = this._b.Cmp(IrCmpPred.Folt, v, trunc);              // v < trunc(v) => was negative non-integer
    var one = this._b.Cast(IrCastOp.SIToFP, this._b.ZExt(roundedUp, IrType.I32), ty);
    return this._b.Binary(IrBinaryOp.FSub, trunc, one);
  }

  private IrValue LowerAbs(CallOrIndexExpr call) {
    var resultPb = this._model.TypeOf(call);
    var ty = IrTypeMapper.Map(resultPb);
    var v = this.Coerce(this.LowerExpr(call.Arguments[0]), this._model.TypeOf(call.Arguments[0]), resultPb);
    if (ty.IsInteger) {
      // branchless two's-complement abs: m = v >>s (bits-1); (v ^ m) - m
      var mask = this._b.Binary(IrBinaryOp.AShr, v, new IrConstantInt(ty, ty.Bits - 1));
      return this._b.Sub(this._b.Xor(v, mask), mask);
    }
    // float abs: clear the sign bit through an integer of the same width
    var intTy = IrType.Integer(ty.Bits);
    var bits = ty.Bits switch {
      32 => 0x7FFFFFFFL,
      64 => long.MaxValue,
      _ => throw new IrLoweringException("ABS of EXT (80-bit) not supported"),
    };
    var asInt = this._b.Cast(IrCastOp.BitCast, v, intTy);
    var cleared = this._b.And(asInt, new IrConstantInt(intTy, bits));
    return this._b.Cast(IrCastOp.BitCast, cleared, ty);
  }

  private IrValue LowerSgn(CallOrIndexExpr call) {
    var argPb = this._model.TypeOf(call.Arguments[0]);
    var resultTy = IrTypeMapper.Map(this._model.TypeOf(call));   // INTEGER (-1/0/1)
    var v = this.LowerExpr(call.Arguments[0]);
    IrValue pos, neg;
    if (argPb is ScalarType { IsFloat: true }) {
      pos = this._b.Cmp(IrCmpPred.Fogt, v, new IrConstantFloat(v.Type, 0.0));
      neg = this._b.Cmp(IrCmpPred.Folt, v, new IrConstantFloat(v.Type, 0.0));
    } else {
      var signed = argPb is ScalarType { Signed: true };
      pos = this._b.Cmp(signed ? IrCmpPred.Sgt : IrCmpPred.Ugt, v, new IrConstantInt(v.Type, 0));
      neg = signed ? this._b.Cmp(IrCmpPred.Slt, v, new IrConstantInt(v.Type, 0)) : new IrConstantInt(IrType.I1, 0);
    }
    return this._b.Sub(this._b.ZExt(pos, resultTy), this._b.ZExt(neg, resultTy));
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
        or BinaryOp.LessEqual or BinaryOp.GreaterEqual => leftPb is StringType
          ? this.LowerStringComparison(expr, resultPb)
          : this.LowerComparison(expr, leftPb, rightPb, resultPb),
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
