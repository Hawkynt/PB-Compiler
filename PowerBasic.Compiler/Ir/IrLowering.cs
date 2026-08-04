using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
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

  /// <summary>
  /// Symbols whose storage must be ONE location for the whole program rather than a frame slot:
  /// a STATIC local (it has to survive the call) and any module-level variable a procedure can
  /// reach (main and that procedure must see the same cell). Shared across the per-procedure
  /// lowering instances, so every function resolves such a symbol to the same global.
  /// </summary>
  private readonly Dictionary<VariableSymbol, IrGlobalVariable>? _sharedStorage;

  /// <summary>Module-level symbols some PROCEDURE reads or writes, so main cannot keep them in its frame.</summary>
  private readonly HashSet<VariableSymbol>? _escapesToProcedures;
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

  private IrLowering(SemanticModel model, IReadOnlyDictionary<ProcedureSymbol, IrFunction>? procMap, IrModule? module,
      Dictionary<VariableSymbol, IrGlobalVariable>? sharedStorage = null, HashSet<VariableSymbol>? escapesToProcedures = null) {
    this._sharedStorage = sharedStorage;
    this._escapesToProcedures = escapesToProcedures;
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
  public static IrModule? TryLowerModule(SemanticModel model) => TryLowerModule(model, out _);

  /// <summary>
  /// As <see cref="TryLowerModule(SemanticModel)"/>, but also reports WHY the lowering declined -
  /// the construct that fell outside the modelled subset. A caller can put that in a diagnostic
  /// instead of a generic "unsupported", which is the difference between a usable message and a
  /// shrug.
  /// </summary>
  public static IrModule? TryLowerModule(SemanticModel model, out string? declinedBecause) {
    declinedBecause = null;
    var module = new IrModule(model.FileName);
    var procMap = new Dictionary<ProcedureSymbol, IrFunction>(ReferenceEqualityComparer.Instance);

    foreach (var proc in model.Procedures.Values)
      if (!proc.IsExternal && TrySignature(proc, out var irfn)) {
        procMap[proc] = irfn!;
        module.AddFunction(irfn!);
      }

    var shared = new Dictionary<VariableSymbol, IrGlobalVariable>(ReferenceEqualityComparer.Instance);
    var escapes = ModuleVariablesUsedByProcedures(model);
    var main = new IrFunction("main", IrType.Void);
    module.AddFunction(main);
    try {
      new IrLowering(model, procMap, module, shared, escapes).LowerBodyInto(main, model.MainBody, null);
    } catch (IrLoweringException e) {
      declinedBecause = e.Message;
      return null;
    }

    foreach (var (proc, irfn) in procMap) {
      try {
        new IrLowering(model, procMap, module, shared, escapes).LowerProcedure(proc, irfn);
      } catch (IrLoweringException) {
        irfn.ClearBody();                              // leave it a declaration; callers can still call it
      }
    }
    return module;
  }

  /// <summary>
  /// The module-level variables at least one procedure touches. Only those need one shared cell;
  /// a module variable used solely by the main body stays an alloca there, which mem2reg promotes
  /// to an SSA register - so keeping the analysis precise is what stops "correct globals" from
  /// costing the optimizer its best case.
  /// </summary>
  private static HashSet<VariableSymbol> ModuleVariablesUsedByProcedures(SemanticModel model) {
    var used = new HashSet<VariableSymbol>(ReferenceEqualityComparer.Instance);
    foreach (var proc in model.Procedures.Values) {
      if (proc.Body is not { } body)
        continue;
      foreach (var node in CodeGen.OptReachability.DescendantNodes(body))
        if (node is Expression e && model.VariableBindings.TryGetValue(e, out var symbol)
            && symbol.Storage == VariableStorage.Global)
          used.Add(symbol);
    }
    return used;
  }

  /// <summary>Builds an IR signature for a procedure, or false if it is outside the supported subset.</summary>
  private static bool TrySignature(ProcedureSymbol proc, out IrFunction? fn) {
    fn = null;
    var ret = IrType.Void;
    if (proc.IsFunction) {
      if (proc.ReturnType is StringType)
        ret = IrType.Ptr;                              // a string result IS its runtime handle
      else if (proc.ReturnType is null || !IrTypeMapper.TryMap(proc.ReturnType, out ret) || ret.IsMbf)
        return false;
    }
    var args = new List<IrArgument>();
    foreach (var p in proc.Parameters) {
      if (p.Seg || p.Optional)
        return false;                                  // SEG / CDECL-optional excluded
      if (p.Type is UdtType) {
        args.Add(new IrArgument(IrType.Ptr, args.Count, p.Name));   // a record is passed as a pointer (BYVAL = callee copies on entry)
        continue;
      }
      if (p.Type is StringType) {
        // BYVAL passes the handle itself, BYREF a pointer to the caller's handle slot - both
        // are pointers to the IR, and the existing parameter binding already does the right
        // thing with each (store the value / adopt the address)
        args.Add(new IrArgument(IrType.Ptr, args.Count, p.Name));
        continue;
      }
      if (!IrTypeMapper.TryMap(p.Type, out var pty) || pty.IsMbf)
        return false;                                  // scalar parameters only
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
        if (p.Type is UdtType pudt) {
          if (p.ByVal) {                               // BYVAL record: copy the caller's record into a private local
            var local = this.SlotFor(p);
            this._b.Call(IrType.Void, this.RuntimeFn("llvm.memcpy.p0.p0.i32", IrType.Void, IrType.Ptr, IrType.Ptr, IrType.I32, IrType.I1),
              local, fn.Parameters[i], new IrConstantInt(IrType.I32, pudt.Size), IrBuilder.ConstBool(false));
          } else
            this._addr[p] = fn.Parameters[i];          // BYREF record: use the caller's storage
        } else if (p.ByVal)
          this._b.Store(fn.Parameters[i], this.SlotFor(p));
        else
          this._addr[p] = fn.Parameters[i];
      }

    // pre-create a block for every label so forward GOTOs have a target
    foreach (var label in CollectLabels(body))
      this._labels[label] = this._fn.CreateBlock("lbl." + label);

    if (ContainsGosub(body))
      this.SetupGosub();

    // whether statements must publish their boundaries has to be known BEFORE the first one is
    // lowered: RESUME NEXT can name a statement that ran long before the handler was armed
    this._resumeTracking = ContainsResume(body);

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

  /// <summary>
  /// Whether the body resumes at a statement boundary the fault picks - <c>ON ERROR RESUME NEXT</c>,
  /// or a bare <c>RESUME</c> / <c>RESUME NEXT</c> in a handler. <c>RESUME &lt;label&gt;</c> does not
  /// count: it names its destination, so it is an ordinary branch and costs no bookkeeping.
  /// </summary>
  private static bool ContainsResume(IReadOnlyList<Statement> statements) {
    foreach (var s in statements)
      switch (s) {
        case OnErrorStmt { ResumeNext: true }: return true;
        case ResumeStmt { Kind: not ResumeKind.Label }: return true;
        case IfStmt i when ContainsResume(i.Then) || i.ElseIfs.Any(e => ContainsResume(e.Body)) || (i.Else is { } el && ContainsResume(el)):
          return true;
        case ForStmt f when ContainsResume(f.Body): return true;
        case DoLoopStmt d when ContainsResume(d.Body): return true;
        case SelectStmt sel when sel.Arms.Any(a => ContainsResume(a.Body)): return true;
      }
    return false;
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
      this._b.Ret(this._b.Load(this._resultVar.Type is StringType ? IrType.Ptr : MapType(this._resultVar.Type),
        this.SlotFor(this._resultVar)));
    else
      this._b.Ret();
  }

  private IrValue SlotFor(VariableSymbol symbol) {
    if (this._addr.TryGetValue(symbol, out var existing))
      return existing;
    if (this.NeedsSharedStorage(symbol))
      return this.GlobalFor(symbol);
    IrAlloca alloca;
    if (symbol.Type is StringType) {
      alloca = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(IrType.Ptr) { Name = symbol.Name });  // holds a string handle
    } else if (symbol.Type is FixedStringType fixedStr) {
      alloca = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(IrType.I8) { Count = fixedStr.Length, Name = symbol.Name });  // inline fixed buffer
    } else if (symbol.Type is ArrayType arr) {
      if (arr.IsDynamic)
        throw new IrLoweringException("dynamic array");
      IrType elem;
      int count;
      if (arr.Element is StringType) {
        elem = IrType.Ptr; count = arr.ElementCount;   // an array of string handles
      } else if (arr.Element is UdtType ue) {
        elem = IrType.I8; count = arr.ElementCount * ue.Size;   // a packed buffer of records
      } else if (IrTypeMapper.TryMap(arr.Element, out elem) && !elem.IsMbf) {
        count = arr.ElementCount;
      } else
        throw new IrLoweringException("non-scalar array element");
      alloca = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(elem) { Count = count, Name = symbol.Name });
    } else if (symbol.Type is UdtType udt) {
      alloca = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(IrType.I8) { Count = udt.Size, Name = symbol.Name });   // a packed record buffer
    } else {
      alloca = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(MapType(symbol.Type)) { Name = symbol.Name });
    }
    this._addr[symbol] = alloca;
    return alloca;
  }

  /// <summary>
  /// True when a frame slot would be the wrong storage: a STATIC local must outlive the call, and
  /// a module-level variable is one cell the whole program shares. Everything else stays an
  /// alloca, which mem2reg can promote to SSA - so ordinary locals lose no optimization.
  /// </summary>
  private bool NeedsSharedStorage(VariableSymbol symbol) =>
    this._sharedStorage is not null && this._module is not null
    && (symbol.Storage == VariableStorage.Static
        || (symbol.Storage == VariableStorage.Global && this._escapesToProcedures?.Contains(symbol) == true));

  /// <summary>The one module global backing <paramref name="symbol"/>, created on first use.</summary>
  private IrValue GlobalFor(VariableSymbol symbol) {
    if (this._sharedStorage!.TryGetValue(symbol, out var existing))
      return existing;
    var (valueType, count) = symbol.Type switch {
      StringType => (IrType.Ptr, 1),
      FixedStringType fs => (IrType.I8, fs.Length),
      UdtType udt => (IrType.I8, udt.Size),
      ArrayType { IsDynamic: false } arr => arr.Element switch {
        StringType => (IrType.Ptr, arr.ElementCount),
        UdtType ue => (IrType.I8, arr.ElementCount * ue.Size),
        _ => (MapType(arr.Element), arr.ElementCount),
      },
      ArrayType => throw new IrLoweringException("dynamic array with shared storage"),
      _ => (MapType(symbol.Type), 1),
    };
    // the name is qualified so a STATIC local cannot collide with a module variable of the
    // same spelling, and the IR stays readable
    var name = symbol.Storage == VariableStorage.Static ? $"static.{symbol.Name}" : $"g.{symbol.Name}";
    var suffix = 0;
    while (this._module!.FindGlobal(name) is not null)
      name = $"{name}.{++suffix}";
    var global = this._module.AddGlobal(new IrGlobalVariable(name, valueType) { Count = count });
    this._sharedStorage[symbol] = global;
    return global;
  }

  /// <summary>
  /// Raises a PB runtime error when <paramref name="condition"/> holds - the shape every
  /// <c>$ERROR … ON</c> trap takes in the IR. The direct emitter spells this as a conditional jump
  /// over a call; with no flags register in a target-independent IR it is an ordinary branch instead.
  ///
  /// <c>rt_raise</c> does not come back - it dispatches through the armed ON ERROR handler or ends the
  /// program - but the IR still needs a terminator on the block that called it, so that block branches
  /// to the continuation it never actually reaches.
  /// </summary>
  private void RaiseWhen(IrValue condition, int code, string what) {
    var bad = this.NewBlock(what + ".trap");
    var ok = this.NewBlock(what + ".ok");
    this._b.CondBr(condition, bad, ok);

    this._b.Position(bad);
    this._b.Call(IrType.Void, this.RuntimeFn("rt_error", IrType.Void, IrType.I32), new IrConstantInt(IrType.I32, code));
    this._b.Br(ok);
    this._b.Position(ok);
  }

  /// <summary>
  /// One subscript checked against its dimension, raising Error 9 when it falls outside. The bounds
  /// are values rather than constants because a dynamic array carries its own: a REDIM writes the
  /// lower bound and the size into the descriptor slots, so the check reads them back the same way
  /// the address arithmetic does.
  /// </summary>
  private void EmitBoundsCheck(IrValue index, IrValue lower, IrValue upper)
    => this.RaiseWhen(this._b.Or(this._b.Cmp(IrCmpPred.Slt, index, lower),
                                 this._b.Cmp(IrCmpPred.Sgt, index, upper)), 9, "bounds");

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
      if (this._checkBounds)
        this.EmitBoundsCheck(idx, new IrConstantInt(IrType.I32, bounds[k].Lower), new IrConstantInt(IrType.I32, bounds[k].Upper));
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

  /// <summary>$ERROR BOUNDS ON: subscripts are checked and Error 9 raised when one is out of range.</summary>
  private bool _checkBounds;

  /// <summary>$ERROR OVERFLOW ON: integer +, - and * are checked and Error 6 raised when they wrap.</summary>
  private bool _checkOverflow;

  /// <summary>$ERROR NUMERIC ON: a FOR counter that wraps past its own range raises Error 6.</summary>
  private bool _checkNumeric;

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
      var lo = this._b.Load(IrType.I32, descriptor.Lo[k]);
      // $ERROR BOUNDS ON over a dynamic array: the dimension is not a compile-time constant, so the
      // upper bound is reconstructed from the descriptor the REDIM filled in - lo + size - 1
      if (this._checkBounds) {
        var size = this._b.Load(IrType.I32, descriptor.Size[k]);
        this.EmitBoundsCheck(idx, lo, this._b.Sub(this._b.Add(lo, size), new IrConstantInt(IrType.I32, 1)));
      }
      var rel = this._b.Sub(idx, lo);
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
      if (this._resumeTracking && statement is not (LabelStmt or DataStmt or MetaStmt))
        this.LowerStatementWithResumeBoundary(statement);
      else
        this.LowerStatement(statement);
    }
  }

  /// <summary>
  /// One statement, bracketed by the two addresses RESUME and RESUME NEXT resume at. The direct
  /// emitter writes the same pair into rt_resume / rt_resumenext in front of every statement; rt_raise
  /// latches whichever one is current when the fault happens, and the resume then jumps through it.
  ///
  /// Giving each statement its own block is what makes those two points addressable at all - "the
  /// start of this statement" is not a thing an SSA instruction list has otherwise. It costs nothing:
  /// a function that gets here is already out of the optimizer's hands.
  /// </summary>
  private void LowerStatementWithResumeBoundary(Statement statement) {
    var start = this.NewBlock("stmt");
    var after = this.NewBlock("stmt.next");
    this._b.Br(start);
    this._b.Position(start);
    this._b.Call(IrType.Void, this.RuntimeFn("rt_resume_mark", IrType.Void, IrType.Ptr, IrType.Ptr),
      new IrBlockAddress(start), new IrBlockAddress(after));

    this.LowerStatement(statement);

    if (!this.Terminated)
      this._b.Br(after);
    this._b.Position(after);
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
      case MidAssignStmt mid: this.LowerMidAssign(mid); break;
      case LabelStmt l: this.LowerLabel(l); break;
      case GotoStmt g: this.LowerGoto(g); break;
      case GosubStmt gs: this.LowerGosub(gs); break;
      case ReturnStmt rs: this.LowerReturn(rs); break;
      case OnGotoStmt og: this.LowerOnGoto(og); break;
      case PrintStmt pr: this.LowerPrint(pr); break;
      case InputStmt inp: this.LowerInput(inp); break;
      case OpenStmt op: this.LowerOpen(op); break;
      case CloseStmt cl: this.LowerClose(cl); break;
      case GetPutFileStmt gp: this.LowerGetPut(gp); break;
      case DataStmt: break;                          // DATA is gathered once into a module blob; the statement itself emits nothing
      case ReadStmt rd: this.LowerRead(rd); break;
      case RestoreStmt rs: this.LowerRestore(rs); break;
      case EndStmt: this.LowerEnd(); break;
      case OnErrorStmt oe: this.LowerOnError(oe); break;
      case ResumeStmt rs2: this.LowerResume(rs2); break;
      case ErrorStmt err: this.LowerErrorStatement(err); break;
      case MetaStmt meta: this.LowerMeta(meta); break;
      case CommandStmt { Keyword: "SHIFT LEFT" or "SHIFT RIGHT" } shift: this.LowerShift(shift); break;
      case CommandStmt { Keyword: "ROTATE LEFT" or "ROTATE RIGHT" } rotate: this.LowerRotate(rotate); break;
      case CommandStmt { Keyword: "LOCATE" } locate: this.LowerLocate(locate); break;
      // ERRCLEAR: forget the last fault, so a later ERR read sees zero rather than a stale code
      case CommandStmt { Keyword: "ERRCLEAR" }:
        this._b.Store(new IrConstantInt(IrType.I16, 0), this.ErrorCell("rt_err", IrType.I16));
        break;
      case CommandStmt { Keyword: "KILL", Arguments: [{ } file] }:
        this._b.Call(IrType.Void, this.RuntimeFn("rt_kill", IrType.Void, IrType.Ptr), this.LowerStringExpr(file));
        break;
      // CommandStmt is a catch-all for a dozen unrelated statements (KILL, POKE, OUT, RANDOMIZE...),
      // so it names the keyword: "unsupported statement: CommandStmt" ranks nothing
      default: throw new IrLoweringException(statement is CommandStmt command
        ? $"unsupported statement: {command.Keyword}"
        : $"unsupported statement: {statement.GetType().Name}");
    }
  }

  private void LowerAssign(AssignStmt a) {
    if (a.Target is NameExpr && this._model.VariableBindings.TryGetValue(a.Target, out var strSym) && strSym.Type is StringType) {
      this._b.Store(this.LowerStringExpr(a.Value), this.SlotFor(strSym));   // strings are immutable handles
      return;
    }
    if (a.Target is NameExpr && this._model.VariableBindings.TryGetValue(a.Target, out var fstrSym) && fstrSym.Type is FixedStringType fixedStr) {
      this._b.Call(IrType.Void, this.RuntimeFn("rt_str_to_fixed", IrType.Void, IrType.Ptr, IrType.I32, IrType.Ptr),
        this.SlotFor(fstrSym), new IrConstantInt(IrType.I32, fixedStr.Length), this.LowerStringExpr(a.Value));  // copy, space-pad / truncate to N
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
    if (a.Target is NameExpr && this._model.VariableBindings.TryGetValue(a.Target, out var udtSym) && udtSym.Type is UdtType udt) {
      // whole-record copy via the LLVM intrinsic - llc inlines small fixed-size copies to plain load/store
      this._b.Call(IrType.Void, this.RuntimeFn("llvm.memcpy.p0.p0.i32", IrType.Void, IrType.Ptr, IrType.Ptr, IrType.I32, IrType.I1),
        this.SlotFor(udtSym), this.UdtAddress(a.Value), new IrConstantInt(IrType.I32, udt.Size), IrBuilder.ConstBool(false));
      return;
    }
    if (a.Target is MemberExpr member) {
      if (!this._model.VariableBindings.ContainsKey(member)) {
        var (fieldAddr, field) = this.MemberFieldAddress(member);
        if (field.Type is FixedStringType ffs) {       // a fixed-string record field: pad/truncate into its bytes
          this._b.Call(IrType.Void, this.RuntimeFn("rt_str_to_fixed", IrType.Void, IrType.Ptr, IrType.I32, IrType.Ptr),
            fieldAddr, new IrConstantInt(IrType.I32, ffs.Length), this.LowerStringExpr(a.Value));
          return;
        }
      }
      var (address, fieldType) = this.MemberLValue(member);
      this._b.Store(this.Coerce(this.LowerExpr(a.Value), this._model.TypeOf(a.Value), fieldType), address);
      return;
    }
    var symbol = this.SymbolOf(a.Target);
    var slot = this.SlotFor(symbol);
    var value = this.Coerce(this.LowerExpr(a.Value), this._model.TypeOf(a.Value), symbol.Type);
    this._b.Store(value, slot);
  }

  /// <summary>MID$(target$, start[, length]) = value$ - replace a substring in place (strings are handles, so store a new one back).</summary>
  private void LowerMidAssign(MidAssignStmt m) {
    if (this._module is null)
      throw new IrLoweringException("MID$ statement requires whole-module lowering");
    var current = this.LowerStringExpr(m.Target);
    var start = this.Coerce(this.LowerExpr(m.Start), this._model.TypeOf(m.Start), PbType.Long);
    var length = m.Length is { } len
      ? this.Coerce(this.LowerExpr(len), this._model.TypeOf(len), PbType.Long)
      : new IrConstantInt(IrType.I32, -1);             // -1 = replace to the end of the source slice
    var result = this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_mid_assign", IrType.Ptr, IrType.Ptr, IrType.I32, IrType.I32, IrType.Ptr),
      current, start, length, this.LowerStringExpr(m.Value));
    this._b.Store(result, this.StringTargetAddress(m.Target));
  }

  /// <summary>The storage slot of a string lvalue (a string variable or a string-array element).</summary>
  private IrValue StringTargetAddress(Expression target) {
    if (target is NameExpr && this._model.VariableBindings.TryGetValue(target, out var sym) && sym.Type is StringType)
      return this.SlotFor(sym);
    if (target is CallOrIndexExpr ce && this._model.VariableBindings.TryGetValue(ce, out var arr) && arr.Type is ArrayType { Element: StringType })
      return this.ElementAddress(ce).Address;
    throw new IrLoweringException("unsupported MID$ target");
  }

  private void LowerSwap(SwapStmt sw) {
    var (leftAddr, leftType) = this.LValue(sw.Left);
    var (rightAddr, rightType) = this.LValue(sw.Right);
    if (!leftType.Equals(rightType))
      throw new IrLoweringException("SWAP of differently-typed operands");
    var ty = MapType(leftType);
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
    if (e is MemberExpr m)
      return this.MemberLValue(m);
    throw new IrLoweringException("unsupported lvalue");
  }

  /// <summary>The storage address and field type of a UDT member (or a flat QB-style dotted variable).</summary>
  private (IrValue Address, PbType Type) MemberLValue(MemberExpr m) {
    if (this._model.VariableBindings.TryGetValue(m, out var flat)) {       // Max.X where Max is not a UDT: one flat scalar variable
      if (flat.Type is not ScalarType)
        throw new IrLoweringException("non-scalar dotted variable");
      return (this.SlotFor(flat), flat.Type);
    }
    var (address, field) = this.MemberFieldAddress(m);
    if (field.Type is not ScalarType || field.ElementCount != 1)
      throw new IrLoweringException("non-scalar UDT field");
    return (address, field.Type);
  }

  /// <summary>The byte address and field descriptor of a real UDT member (variable or array element); not a flat dotted variable.</summary>
  private (IrValue Address, UdtField Field) MemberFieldAddress(MemberExpr m) {
    IrValue basePtr;
    UdtType udt;
    if (m.Target is NameExpr && this._model.VariableBindings.TryGetValue(m.Target, out var baseSym) && baseSym.Type is UdtType nameUdt) {
      basePtr = this.SlotFor(baseSym);
      udt = nameUdt;
    } else if (m.Target is CallOrIndexExpr ce && this._model.VariableBindings.TryGetValue(ce, out var arrSym) && arrSym.Type is ArrayType { Element: UdtType elemUdt }) {
      basePtr = this.ElementAddress(ce).Address;
      udt = elemUdt;
    } else
      throw new IrLoweringException("unsupported member access");

    var field = udt.FindField(m.Member) ?? throw new IrLoweringException($"unknown field {m.Member}");
    if (field.ElementCount != 1)
      throw new IrLoweringException("UDT array field");
    var address = field.Offset == 0 ? basePtr : this._b.Gep(basePtr, new IrConstantInt(IrType.I32, field.Offset));
    return (address, field);
  }

  private void LowerPrint(PrintStmt p) {
    if (this._module is null)
      throw new IrLoweringException("PRINT requires whole-module lowering");
    if (p.IsLPrint || p.UsingFormat is not null)
      throw new IrLoweringException("LPRINT / PRINT USING");
    var file = p.FileNumber is { } fn ? this.FileNum(fn) : null;

    foreach (var item in p.Items) {
      if (item.Value is { } expr)
        this.LowerPrintItem(file, expr);
      if (item.Separator == PrintSeparator.Comma)
        this.EmitIo(file, "print", "comma", IrType.Void, []);   // advance to the next 14-column print zone
    }

    if (p.Items.Count == 0 || p.Items[^1].Separator == PrintSeparator.Newline)
      this.EmitIo(file, "print", "nl", IrType.Void, []);
  }

  private void LowerPrintItem(IrValue? file, Expression expr) {
    if (expr is CallOrIndexExpr ts && this._model.IntrinsicBindings.TryGetValue(ts, out var tsi) && tsi.Name is "TAB" or "SPC") {
      var n = this.Coerce(this.LowerExpr(ts.Arguments[0]), this._model.TypeOf(ts.Arguments[0]), PbType.Long);
      this.EmitIo(file, "print", tsi.Name == "TAB" ? "tab" : "spc", IrType.Void, [IrType.I32], n);
      return;
    }
    if (expr is StringLiteralExpr lit) {
      var bytes = System.Text.Encoding.ASCII.GetBytes(lit.Value);
      var global = this._module!.AddStringConstant(bytes);
      this.EmitIo(file, "print", "str", IrType.Void, [IrType.Ptr, IrType.I32], global, new IrConstantInt(IrType.I32, bytes.Length));
      return;
    }
    if (this._model.TypeOf(expr) is StringType or FixedStringType) {
      this.EmitIo(file, "print", "strvar", IrType.Void, [IrType.Ptr], this.LowerStringExpr(expr));
      return;
    }
    // an MBF value prints as the IEEE number it converts to - the runtime's print entries take a
    // value on the x87, which is the one thing MBF bits cannot be
    var printed = this._model.TypeOf(expr);
    if (printed is MbfType mbf)
      printed = IeeeFormOf(mbf);
    if (printed is not ScalarType s)
      throw new IrLoweringException("PRINT of a non-numeric, non-literal item");
    var (suffix, ty) = NumericSuffix(s);
    this.EmitIo(file, "print", suffix, IrType.Void, [ty],
      this.Coerce(this.LowerExpr(expr), this._model.TypeOf(expr), s));
  }

  private void LowerInput(InputStmt input) {
    if (this._module is null)
      throw new IrLoweringException("INPUT requires whole-module lowering");
    var file = input.FileNumber is { } fn ? this.FileNum(fn) : null;

    // A console INPUT prompts once per STATEMENT, not once per variable it reads: with the
    // program's own prompt string when it has one, else PB's bare "? " - which LINE INPUT does
    // not print (it prompts only when told to).
    if (file is null && (input.Prompt is not null || !input.IsLineInput)) {
      var bytes = System.Text.Encoding.ASCII.GetBytes(input.Prompt ?? "? ");
      var global = this._module.AddStringConstant(bytes);
      this.EmitIo(null, "print", "str", IrType.Void, [IrType.Ptr, IrType.I32], global, new IrConstantInt(IrType.I32, bytes.Length));
    }

    foreach (var target in input.Targets) {
      if (target is NameExpr && this._model.VariableBindings.TryGetValue(target, out var strSym) && strSym.Type is StringType) {
        this._b.Store(this.EmitIo(file, "input", input.IsLineInput ? "line" : "str", IrType.Ptr, []), this.SlotFor(strSym));
        continue;
      }
      if (target is NameExpr && this._model.VariableBindings.TryGetValue(target, out var fstrSym) && fstrSym.Type is FixedStringType fixedStr) {
        var handle = this.EmitIo(file, "input", input.IsLineInput ? "line" : "str", IrType.Ptr, []);
        this._b.Call(IrType.Void, this.RuntimeFn("rt_str_to_fixed", IrType.Void, IrType.Ptr, IrType.I32, IrType.Ptr),
          this.SlotFor(fstrSym), new IrConstantInt(IrType.I32, fixedStr.Length), handle);   // pad/truncate the input into the fixed buffer
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
    var recLen = o.RecordLength is { } rl
      ? this.Coerce(this.LowerExpr(rl), this._model.TypeOf(rl), PbType.Long)
      : new IrConstantInt(IrType.I32, 0);              // 0 = no fixed record length (sequential)
    this._b.Call(IrType.Void, this.RuntimeFn("rt_file_open", IrType.Void, IrType.I32, IrType.Ptr, IrType.I32, IrType.I32),
      this.FileNum(o.FileNumber), this.LowerStringExpr(o.FileName), new IrConstantInt(IrType.I32, (int)o.Mode), recLen);
  }

  /// <summary>Random/binary record I/O of one fixed-size scalar variable (GET/PUT #n, rec, var).</summary>
  private void LowerGetPut(GetPutFileStmt s) {
    if (this._module is null)
      throw new IrLoweringException("GET/PUT requires whole-module lowering");
    if (s.Variable is null)
      throw new IrLoweringException("FIELD-based GET/PUT");   // the buffer/FIELD form is not modeled
    IrValue address;
    int recordSize;
    if (s.Variable is NameExpr && this._model.VariableBindings.TryGetValue(s.Variable, out var sym) && sym.Type is UdtType udt) {
      address = this.SlotFor(sym);                    // a whole-record GET/PUT of a UDT buffer
      recordSize = udt.Size;
    } else {
      var (addr, type) = this.LValue(s.Variable);
      if (type is not ScalarType scalar)
        throw new IrLoweringException("GET/PUT of a non-scalar record");
      address = addr;
      recordSize = scalar.Size;
    }
    var fileNo = this.FileNum(s.FileNumber);
    var recNo = s.RecordNumber is { } rn
      ? this.Coerce(this.LowerExpr(rn), this._model.TypeOf(rn), PbType.Long)
      : new IrConstantInt(IrType.I32, 0);             // 0 = the current/next record
    this._b.Call(IrType.Void, this.RuntimeFn(s.IsGet ? "rt_file_get" : "rt_file_put", IrType.Void, IrType.I32, IrType.I32, IrType.Ptr, IrType.I32),
      fileNo, recNo, address, new IrConstantInt(IrType.I32, recordSize));
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
      // Reading a string VARIABLE yields a copy, not the variable's own handle. PB's runtime works on
      // the rule that every string value in generated code is an owned temporary, and the routines
      // that say "consumes" free what they are given - rt_strcat consumes both operands, rt_str_print
      // consumes what it prints. Handing them the variable's handle destroys the variable: PRINT a$
      // twice printed "hello" and then nothing, and a$ + b$ emptied both. The direct emitter
      // duplicates here for the same reason.
      case NameExpr when this._model.VariableBindings.TryGetValue(expr, out var sym) && sym.Type is StringType:
        return this.BorrowString(this._b.Load(IrType.Ptr, this.SlotFor(sym)));
      case NameExpr when this._model.VariableBindings.TryGetValue(expr, out var fsym) && fsym.Type is FixedStringType fixedStr:
        return this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_from_fixed", IrType.Ptr, IrType.Ptr, IrType.I32),
          this.SlotFor(fsym), new IrConstantInt(IrType.I32, fixedStr.Length));   // the inline N bytes as a handle
      // '+' between strings is concatenation too - the original BASIC spelling, and still the
      // common one; '&' (PB 3.5) is the unambiguous form of the same operation
      case BinaryExpr { Op: BinaryOp.Concat or BinaryOp.Add } cat:
        return this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_concat", IrType.Ptr, IrType.Ptr, IrType.Ptr),
          this.LowerStringExpr(cat.Left), this.LowerStringExpr(cat.Right));
      case CallOrIndexExpr arrayRead when this._model.VariableBindings.TryGetValue(arrayRead, out var arr) && arr.Type is ArrayType { Element: StringType }:
        return this.BorrowString(this._b.Load(IrType.Ptr, this.ElementAddress(arrayRead).Address));   // an element is storage too
      case MemberExpr fm when !this._model.VariableBindings.ContainsKey(fm) && this.MemberFieldAddress(fm) is { Field.Type: FixedStringType ffs } fa:
        return this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_from_fixed", IrType.Ptr, IrType.Ptr, IrType.I32),
          fa.Address, new IrConstantInt(IrType.I32, ffs.Length));   // read a fixed-string record field as a handle
      case CallOrIndexExpr ci when this._model.IntrinsicBindings.TryGetValue(ci, out var info):
        return this.LowerStringIntrinsic(ci, info.Name);
      // a user FUNCTION whose result is a string - its IR result already IS the handle
      case CallOrIndexExpr uc when this._model.CallBindings.TryGetValue(uc, out var proc) && proc.IsFunction:
        return this._procMap is not null && this._procMap.TryGetValue(proc, out var callee)
          ? this.EmitCall(callee, proc, uc.Arguments)
          : throw new IrLoweringException($"call to {proc.Name} outside the modelled subset");
      case NameExpr bare when this._model.CallBindings.TryGetValue(bare, out var bareProc) && bareProc.IsFunction:
        return this._procMap is not null && this._procMap.TryGetValue(bareProc, out var bareCallee)
          ? this.EmitCall(bareCallee, bareProc, [])
          : throw new IrLoweringException($"call to {bareProc.Name} outside the modelled subset");
      default:
        throw new IrLoweringException($"unsupported string expression: {expr.GetType().Name}");
    }
  }

  /// <summary>
  /// A copy of a string that lives in STORAGE, so the value handed on is an owned temporary the
  /// consuming runtime routines may free. Everything else in the lowering already produces one: a
  /// literal comes from rt_str_const, a concatenation from rt_str_concat, an intrinsic from its own
  /// allocation. Only a read of a variable or an array element does not, because the handle it finds
  /// belongs to that cell.
  /// </summary>
  private IrValue BorrowString(IrValue stored)
    => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_dup", IrType.Ptr, IrType.Ptr), stored);

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

  // ---- ON ERROR / RESUME ---------------------------------------------------
  //
  // PB's error handling is a non-local jump the CFG cannot express: ON ERROR GOTO writes a code
  // address into a runtime cell, and a fault ANYWHERE afterwards - including deep inside a runtime
  // routine, where the compiler emitted nothing at all - restores the armed frame and lands on it.
  //
  // Two consequences run through everything below. First, arming has to be inline code rather than a
  // call, because it captures the CURRENT frame (BP and SP); a call would capture its own. It is
  // written here as a call to an rt_ intrinsic the back end expands in place - the IR's way of saying
  // "target-specific sequence", not "transfer control". Second, the function is marked
  // HasErrorHandler, which takes it out of the optimizer entirely: no CFG-based pass can be trusted
  // on a graph that is missing its most important edge.

  private void LowerOnError(OnErrorStmt oe) {
    this._fn.HasErrorHandler = true;
    if (oe.ResumeNext) {
      // inline mode: the runtime's own RESUME NEXT stub becomes the handler, so a faulting statement
      // is skipped and the next one runs. Every statement has already published where it starts and
      // where it ends - ContainsResume saw this coming before the first one was lowered
      this._b.Call(IrType.Void, this.RuntimeFn("rt_onerr_resume_next", IrType.Void));
      return;
    }
    if (oe.Target is null or "0") {
      this._b.Call(IrType.Void, this.RuntimeFn("rt_onerr_disarm", IrType.Void));
      return;
    }
    if (!this._labels.TryGetValue(oe.Target, out var handler))
      throw new IrLoweringException($"ON ERROR GOTO unknown label {oe.Target}");
    this._b.Call(IrType.Void, this.RuntimeFn("rt_onerr_arm", IrType.Void, IrType.Ptr), new IrBlockAddress(handler));
  }

  private void LowerResume(ResumeStmt rs) {
    this._fn.HasErrorHandler = true;
    switch (rs.Kind) {
      case ResumeKind.Label when rs.Target is { } target:
        if (!this._labels.TryGetValue(target, out var block))
          throw new IrLoweringException($"RESUME to unknown label {target}");
        this._b.Call(IrType.Void, this.RuntimeFn("rt_err_clear", IrType.Void));
        this._b.Br(block);
        return;
      // RESUME and RESUME NEXT go back to a statement the FAULT chose, not one this code names, so
      // the destination is only known at run time. It is a jump through a runtime cell, which the
      // runtime routine performs itself - hence a call that never returns rather than a terminator
      // with an unknown target
      default:
        this._b.Call(IrType.Void, this.RuntimeFn(
          rs.Kind == ResumeKind.SameStatement ? "rt_resume_same" : "rt_resume_next", IrType.Void));
        this._b.Unreachable();
        return;
    }
  }

  private void LowerErrorStatement(ErrorStmt err)
    => this._b.Call(IrType.Void, this.RuntimeFn("rt_error", IrType.Void, IrType.I32),
         this.Coerce(this.LowerExpr(err.Code), this._model.TypeOf(err.Code), PbType.Long));

  /// <summary>
  /// True once the body has used RESUME or RESUME NEXT, which resume at a statement boundary chosen
  /// by the fault. Each statement then has to publish its own start and successor addresses, exactly
  /// as the direct emitter's <c>_trackResume</c> does.
  /// </summary>
  private bool _resumeTracking;

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
    var ty = MapType(symbol.Type);
    if (ty.IsFloat)
      throw new IrLoweringException("INCR/DECR on float");
    var current = this._b.Load(ty, slot);
    var amount = id.Amount is null
      ? new IrConstantInt(ty, 1)
      : this.Coerce(this.LowerExpr(id.Amount), this._model.TypeOf(id.Amount), symbol.Type);
    this._b.Store(this._b.Binary(id.Increment ? IrBinaryOp.Add : IrBinaryOp.Sub, current, amount), slot);
  }

  /// <summary>
  /// <c>SHIFT LEFT v, n</c> / <c>SHIFT RIGHT v, n</c>: a shift written as a statement, updating the
  /// variable in place. The right shift is <b>logical</b> - the direct emitter uses <c>SHR</c>
  /// whatever the variable's signedness, so a negative INTEGER shifts its sign bit along like any
  /// other bit rather than smearing it. ROTATE is not this: it would need the bits that fall off the
  /// end put back at the other, which no IR operation carries, so it still declines.
  /// </summary>
  private void LowerShift(CommandStmt cmd) {
    if (cmd.Arguments is not [{ } target, { } count])
      throw new IrLoweringException($"{cmd.Keyword} with {cmd.Arguments.Count} arguments");
    if (target is not NameExpr || this._model.TypeOf(target) is not ScalarType { IsFloat: false } scalar)
      throw new IrLoweringException($"{cmd.Keyword} of a non-scalar target");

    var symbol = this.SymbolOf(target);
    var ty = MapType(symbol.Type);
    var slot = this.SlotFor(symbol);
    var amount = this.Coerce(this.LowerExpr(count), this._model.TypeOf(count), scalar);
    var value = this._b.Load(ty, slot);
    var op = cmd.Keyword.EndsWith("LEFT", StringComparison.Ordinal) ? IrBinaryOp.Shl : IrBinaryOp.LShr;
    this._b.Store(this._b.Binary(op, value, amount), slot);
  }

  /// <summary>
  /// <c>LOCATE row, col</c>. Either argument may be omitted (<c>LOCATE , 40</c> moves the column and
  /// leaves the row alone), and the runtime reads a zero as "keep the current one" - so an absent
  /// argument lowers to a literal zero rather than to a read of the cursor.
  /// </summary>
  private void LowerLocate(CommandStmt cmd) {
    IrValue Argument(int index) =>
      cmd.Arguments.Count > index && cmd.Arguments[index] is { } e
        ? this.Coerce(this.LowerExpr(e), this._model.TypeOf(e), PbType.Long)
        : new IrConstantInt(IrType.I32, 0);
    if (cmd.Arguments.Count > 2)
      throw new IrLoweringException("LOCATE with a cursor-shape argument");
    this._b.Call(IrType.Void, this.RuntimeFn("rt_locate", IrType.Void, IrType.I32, IrType.I32),
      Argument(0), Argument(1));
  }

  /// <summary>
  /// <c>ROTATE LEFT v, n</c> / <c>ROTATE RIGHT v, n</c>: the bits that fall off one end come back at
  /// the other. No IR operation carries that, so it is written out as the two shifts that make it -
  /// <c>(v &lt;&lt; n) OR (v &gt;&gt;u (width - n))</c> - which is exact because both halves are
  /// modular in the variable's own width.
  ///
  /// Only a compile-time count in <c>1 .. width-1</c> qualifies. Zero and width are the cases where
  /// the complementary shift is a shift by the whole width, which is undefined in the IR (and on the
  /// hardware differs between the 8086, which does not mask the count, and later parts, which mask it
  /// to five bits) - so a runtime count declines rather than pick one of those behaviours.
  /// </summary>
  private void LowerRotate(CommandStmt cmd) {
    if (cmd.Arguments is not [{ } target, { } count])
      throw new IrLoweringException($"{cmd.Keyword} with {cmd.Arguments.Count} arguments");
    if (target is not NameExpr || this._model.TypeOf(target) is not ScalarType { IsFloat: false } scalar)
      throw new IrLoweringException($"{cmd.Keyword} of a non-scalar target");
    if (this._folder.TryFold(count) is not { Integer: { } n })
      throw new IrLoweringException($"{cmd.Keyword} by a runtime count");

    var ty = MapType(scalar);
    var width = ty.Bits;
    if (n <= 0 || n >= width)
      throw new IrLoweringException($"{cmd.Keyword} by {n} over a {width}-bit value");

    var slot = this.SlotFor(this.SymbolOf(target));
    var value = this._b.Load(ty, slot);
    var left = cmd.Keyword.EndsWith("LEFT", StringComparison.Ordinal);
    var up = this._b.Binary(IrBinaryOp.Shl, value, new IrConstantInt(ty, left ? n : width - n));
    var down = this._b.Binary(IrBinaryOp.LShr, value, new IrConstantInt(ty, left ? width - n : n));
    this._b.Store(this._b.Or(up, down), slot);
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
    var ty = MapType(symbol.Type);
    if (ty.IsIeeeFloat) {
      this.LowerFloatFor(f, symbol, ty);
      return;
    }
    if (!ty.IsInteger)
      throw new IrLoweringException($"FOR over a {ty} counter");
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
    // $ERROR NUMERIC ON traps the counter's own wrap (QUIRK 2.28/2.29: a BYTE counter FOR b? = 1 TO
    // 255 wraps past 255 and loops forever without it). It is the ONLY thing NUMERIC raises, so the
    // check goes here and nowhere else - the direct emitter puts its JNO/JNC on this same increment
    this._b.Store(this._checkNumeric
      ? this.CheckedArithmetic(IrBinaryOp.Add, iv, increment, ty, ty.Signed)
      : this._b.Binary(IrBinaryOp.Add, iv, increment), slot);
    this._b.Br(header);

    this._b.Position(exit);
  }

  /// <summary>
  /// <c>FOR x! = a TO b STEP c</c> over a SINGLE/DOUBLE counter. The block structure is the integer
  /// loop's, with float operations in place of integer ones: an ordered compare for the test and
  /// <c>FAdd</c> for the step.
  ///
  /// Two things are deliberately NOT done here. The counter is not turned into an integer loop even
  /// when the bounds look whole - a float counter accumulates its step, and <c>FOR x! = 0 TO 1 STEP
  /// .1</c> famously runs nine times, not ten, because a tenth is not representable. Reproducing that
  /// is the point. And the ordered predicates mean a NaN bound exits the loop rather than looping
  /// forever, which is what comparing on the x87 does.
  /// </summary>
  private void LowerFloatFor(ForStmt f, VariableSymbol symbol, IrType ty) {
    var slot = this.SlotFor(symbol);
    var limitSlot = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(ty) { Name = symbol.Name + ".limit" });

    this._b.Store(this.Coerce(this.LowerExpr(f.From), this._model.TypeOf(f.From), symbol.Type), slot);
    this._b.Store(this.Coerce(this.LowerExpr(f.To), this._model.TypeOf(f.To), symbol.Type), limitSlot);

    // a step whose sign is known at compile time picks the test outright; otherwise the direction is
    // a loop-invariant value the test asks about each time round (LICM hoists it)
    double? constStep = f.Step is null ? 1 : this._folder.TryFold(f.Step) is { IsNumeric: true } folded ? folded.AsFloat : null;
    IrValue? stepValue = null;
    if (constStep is null or 0)
      stepValue = this.Coerce(this.LowerExpr(f.Step!), this._model.TypeOf(f.Step!), symbol.Type);

    var header = this.NewBlock("for.head");
    var body = this.NewBlock("for.body");
    var inc = this.NewBlock("for.inc");
    var exit = this.NewBlock("for.exit");
    this._b.Br(header);

    this._b.Position(header);
    var i = this._b.Load(ty, slot);
    var limit = this._b.Load(ty, limitSlot);
    IrValue cond;
    if (constStep is { } cs and not 0) {
      cond = this._b.Cmp(cs > 0 ? IrCmpPred.Fole : IrCmpPred.Foge, i, limit);
    } else {
      var ascending = this._b.Cmp(IrCmpPred.Foge, stepValue!, new IrConstantFloat(ty, 0));
      var inAsc = this._b.And(ascending, this._b.Cmp(IrCmpPred.Fole, i, limit));
      var notAsc = this._b.Xor(ascending, new IrConstantInt(IrType.I1, 1));
      var inDesc = this._b.And(notAsc, this._b.Cmp(IrCmpPred.Foge, i, limit));
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
    var increment = constStep is { } c2 and not 0 ? (IrValue)new IrConstantFloat(ty, c2) : stepValue!;
    this._b.Store(this._b.Binary(IrBinaryOp.FAdd, iv, increment), slot);
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
          // a bare EXIT LOOP means the nearest real loop - a SELECT sits on the same stack but is
          // not one, so it is stepped over rather than jumped out of
          if (loop.Kind == e.Kind || (e.Kind is ExitKind.Loop && loop.Kind is not ExitKind.Select)) {
            this._b.Br(loop.Exit);
            return;
          }
        throw new IrLoweringException($"EXIT {e.Kind} outside a matching loop");
    }
  }

  private void LowerIterate(IterateStmt it) {
    foreach (var loop in this._loops)
      if (loop.Kind == it.Kind || (it.Kind is ExitKind.Loop && loop.Kind is not ExitKind.Select)) {
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
    var subjectPb = this._model.TypeOf(s.Subject);
    if (subjectPb is not (ScalarType or StringType))
      throw new IrLoweringException("SELECT CASE on a non-scalar subject");
    var subject = subjectPb is StringType ? this.LowerStringExpr(s.Subject) : this.LowerExpr(s.Subject);

    var endsel = this.NewBlock("sel.end");
    CaseArm? elseArm = null;
    var arms = new List<CaseArm>();
    foreach (var arm in s.Arms) {
      if (arm.Selectors.Count == 0)
        elseArm = arm;                               // CASE ELSE
      else
        arms.Add(arm);
    }

    // EXIT SELECT jumps to the end of the block, so the arms are lowered with the SELECT on the exit
    // stack - the same mechanism the loops use. It is NOT a loop, so it carries no continue target
    // and EXIT LOOP steps over it.
    this._loops.Push(new LoopContext(ExitKind.Select, endsel, endsel));
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
    this._loops.Pop();
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
    if (subjectPb is StringType) {
      var cmp = this._b.Call(IrType.I32, this.RuntimeFn("rt_str_compare", IrType.I32, IrType.Ptr, IrType.Ptr), subject, this.LowerStringExpr(rightExpr));
      var spred = op switch {
        CaseComparison.Equal => IrCmpPred.Eq,
        CaseComparison.NotEqual => IrCmpPred.Ne,
        CaseComparison.Less => IrCmpPred.Slt,
        CaseComparison.LessEqual => IrCmpPred.Sle,
        CaseComparison.Greater => IrCmpPred.Sgt,
        CaseComparison.GreaterEqual => IrCmpPred.Sge,
        _ => throw new IrLoweringException($"string case comparison {op}"),
      };
      return this._b.Cmp(spred, cmp, new IrConstantInt(IrType.I32, 0));
    }
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
        return new IrConstantInt(MapType(this._model.TypeOf(lit)), lit.Value);
      case FloatLiteralExpr lit:
        return new IrConstantFloat(MapType(this._model.TypeOf(lit)), lit.Value);
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
        return this._b.Load(MapType(element), address);
      case CallOrIndexExpr intr when this._model.IntrinsicBindings.TryGetValue(intr, out var info):
        return this.LowerIntrinsic(intr, info.Name);
      case CallOrIndexExpr call:
        return this.LowerCallExpr(call);
      case MemberExpr member:
        var (memberAddr, memberType) = this.MemberLValue(member);
        return this._b.Load(MapType(memberType), memberAddr);
      default:
        throw new IrLoweringException($"unsupported expression: {expr.GetType().Name}");
    }
  }

  private IrValue LowerNamedConstant(NamedConstantExpr nc) {
    if (!this._model.Equates.TryGetValue(nc.Name, out var value))
      throw new IrLoweringException($"unknown equate {nc.Name}");
    var ty = MapType(this._model.TypeOf(nc));
    if (value.Integer is { } n)
      return new IrConstantInt(ty, n);
    if (value.Float is { } f)
      return new IrConstantFloat(ty, f);
    throw new IrLoweringException("non-numeric equate");
  }

  private IrValue LowerNameRead(NameExpr name) {
    // a parameterless FUNCTION is called by naming it - "PRINT Counter%" is a call, not a read
    if (this._model.CallBindings.TryGetValue(name, out var proc)) {
      if (this._procMap is null || !this._procMap.TryGetValue(proc, out var callee))
        throw new IrLoweringException($"call to {proc.Name} outside the modelled subset");
      if (!proc.IsFunction)
        throw new IrLoweringException("SUB used in expression position");
      return this.EmitCall(callee, proc, []);
    }
    if (!this._model.VariableBindings.TryGetValue(name, out var symbol))
      return this.LowerErrorPseudoVariable(name.Name)
        ?? throw new IrLoweringException($"unbound name {name.Name}");
    return this._b.Load(MapType(symbol.Type), this.SlotFor(symbol));
  }

  /// <summary>
  /// The parameterless error intrinsics, which are reads of runtime cells rather than calls: ERR is
  /// the code of the last fault, ERL the last numeric line label to run, ERADR its address. They bind
  /// to no variable, so a handler naming one arrives here as an unbound name.
  ///
  /// ERDEV / ERDEV$ are deliberately absent: the direct emitter answers both with zero (there is no
  /// device-error reporting), and a stub that silently agrees is not worth having on this path.
  /// </summary>
  private IrValue? LowerErrorPseudoVariable(string name) {
    if (this._module is null)
      return null;
    var (cell, type) = name.ToUpperInvariant() switch {
      "ERR" => ("rt_err", IrType.I16),
      "ERL" => ("rt_erl", IrType.I16),
      "ERADR" => ("rt_eresume", IrType.I16),
      _ => (null, IrType.I16),
    };
    if (cell is null)
      return null;
    var read = this._b.Load(type, this.ErrorCell(cell, type));
    // ERL is a LONG in PB even though the runtime keeps a word - the direct emitter widens with CWD
    return name.Equals("ERL", StringComparison.OrdinalIgnoreCase) ? this._b.SExt(read, IrType.I32) : read;
  }

  /// <summary>
  /// One of the runtime's error cells as an IR global, named exactly as the runtime labels it so the
  /// back end's data-cell bridge resolves it to the very storage the direct emitter uses.
  /// </summary>
  private IrGlobalVariable ErrorCell(string name, IrType type) {
    if (this._module is null)
      throw new IrLoweringException($"{name} requires whole-module lowering");
    return this._module.FindGlobal(name)
      ?? this._module.AddGlobal(new IrGlobalVariable(name, type) { IsZeroInitialized = true });
  }

  /// <summary>Lowers a pure numeric intrinsic that needs no runtime (ABS, SGN); declines the rest.</summary>
  private IrValue LowerIntrinsic(CallOrIndexExpr call, string name) {
    if (name.Equals("INSTR", StringComparison.OrdinalIgnoreCase))
      return this.LowerInstr(call);
    if (name.Equals("LBOUND", StringComparison.OrdinalIgnoreCase) || name.Equals("UBOUND", StringComparison.OrdinalIgnoreCase))
      return this.LowerArrayBound(call, name.Equals("UBOUND", StringComparison.OrdinalIgnoreCase));
    if (call.Arguments.Count != 1)
      throw new IrLoweringException($"intrinsic {name} with {call.Arguments.Count} arguments");
    return name.ToUpperInvariant() switch {
      "ABS" => this.LowerAbs(call),
      "SGN" => this.LowerSgn(call),
      "FIX" => this.LowerFix(call),
      "INT" => this.LowerInt(call),
      "CDBL" or "CSNG" or "CEXT" => this.LowerConvert(call),
      // CINT/CLNG and the unsigned spellings are the ordinary assignment conversion written out: the
      // result type carries the width, and Coerce rounds into it
      "CINT" or "CBYT" or "CWRD" or "CLNG" or "CDWD" => this.LowerConvert(call),
      "LEN" => this.LowerLen(call),
      "ASC" => this.LowerAsc(call),
      "VAL" => this.LowerVal(call),
      "CVI" => this.LowerCv(call, "rt_str_cvi", IrType.I16),
      "CVL" => this.LowerCv(call, "rt_str_cvl", IrType.I32),
      "CVDWD" => this.LowerCv(call, "rt_str_cvdwd", IrType.I32),
      "CVS" => this.LowerCv(call, "rt_str_cvs", IrType.F32),
      "CVD" => this.LowerCv(call, "rt_str_cvd", IrType.F64),
      "POS" => this.LowerPos(call),
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

  /// <summary>
  /// <c>POS(n)</c>: the current print column, which the runtime keeps in <c>rt_col</c> and counts
  /// from zero while BASIC counts from one - so it is that cell plus one, exactly as the direct
  /// emitter reads it. The argument is lowered and discarded, because PowerBASIC ignores its value
  /// but a call inside it still has to happen.
  /// </summary>
  private IrValue LowerPos(CallOrIndexExpr call) {
    if (this._module is null)
      throw new IrLoweringException("POS requires whole-module lowering");
    foreach (var argument in call.Arguments)
      this.LowerExpr(argument);
    var column = this._module.FindGlobal("rt_col")
      ?? this._module.AddGlobal(new IrGlobalVariable("rt_col", IrType.I16) { IsZeroInitialized = true });
    return this._b.Add(this._b.Load(IrType.I16, column), new IrConstantInt(IrType.I16, 1));
  }

  /// <summary>LBOUND/UBOUND of an array dimension: a compile-time constant for static arrays, a descriptor read for dynamic ones.</summary>
  private IrValue LowerArrayBound(CallOrIndexExpr call, bool upper) {
    if (!this._model.VariableBindings.TryGetValue(call.Arguments[0], out var sym) || sym.Type is not ArrayType arr)
      throw new IrLoweringException("LBOUND/UBOUND of a non-array");
    var dim = call.Arguments.Count >= 2
      ? call.Arguments[1] is IntegerLiteralExpr lit ? (int)lit.Value - 1 : throw new IrLoweringException("non-constant LBOUND/UBOUND dimension")
      : 0;
    if (dim < 0 || dim >= arr.Rank)
      throw new IrLoweringException("LBOUND/UBOUND dimension out of range");

    IrValue result;
    if (!arr.IsDynamic) {
      if (arr.StaticBounds is not { } bounds)
        throw new IrLoweringException("static array without bounds");
      result = new IrConstantInt(IrType.I32, upper ? bounds[dim].Upper : bounds[dim].Lower);
    } else {
      var descriptor = this.DynDescriptor(sym, arr.Rank);
      var lo = this._b.Load(IrType.I32, descriptor.Lo[dim]);
      result = upper
        ? this._b.Sub(this._b.Add(lo, this._b.Load(IrType.I32, descriptor.Size[dim])), new IrConstantInt(IrType.I32, 1))
        : lo;
    }
    return this.Coerce(result, PbType.Long, this._model.TypeOf(call));
  }

  /// <summary>CVI/CVL/CVS/CVD: decode a number from a binary-record string's raw bytes.</summary>
  private IrValue LowerCv(CallOrIndexExpr call, string fn, IrType resultType) =>
    this._b.Call(resultType, this.RuntimeFn(fn, resultType, IrType.Ptr), this.LowerStringExpr(call.Arguments[0]));

  /// <summary>INSTR(haystack$, needle$) or INSTR(start%, haystack$, needle$) -> 1-based position (0 = not found).</summary>
  private IrValue LowerInstr(CallOrIndexExpr call) {
    IrValue position;
    if (call.Arguments.Count == 2) {
      position = this._b.Call(IrType.I32, this.RuntimeFn("rt_str_instr", IrType.I32, IrType.Ptr, IrType.Ptr),
        this.LowerStringExpr(call.Arguments[0]), this.LowerStringExpr(call.Arguments[1]));
    } else if (call.Arguments.Count == 3) {
      var start = this.Coerce(this.LowerExpr(call.Arguments[0]), this._model.TypeOf(call.Arguments[0]), PbType.Long);
      position = this._b.Call(IrType.I32, this.RuntimeFn("rt_str_instr_start", IrType.I32, IrType.I32, IrType.Ptr, IrType.Ptr),
        start, this.LowerStringExpr(call.Arguments[1]), this.LowerStringExpr(call.Arguments[2]));
    } else
      throw new IrLoweringException($"INSTR with {call.Arguments.Count} arguments");
    return this.Coerce(position, PbType.Long, this._model.TypeOf(call));
  }

  /// <summary>Lowers a string-returning intrinsic (LEFT$/RIGHT$/MID$/CHR$) to a runtime call.</summary>
  private IrValue LowerStringIntrinsic(CallOrIndexExpr ci, string name) {
    IrValue Str(int i) => this.LowerStringExpr(ci.Arguments[i]);
    IrValue Num(int i) => this.Coerce(this.LowerExpr(ci.Arguments[i]), this._model.TypeOf(ci.Arguments[i]), PbType.Long);
    IrValue Val(int i, ScalarType t) => this.Coerce(this.LowerExpr(ci.Arguments[i]), this._model.TypeOf(ci.Arguments[i]), t);
    return name.ToUpperInvariant() switch {
      // binary-record encoders: a number to its raw little-endian bytes as a string
      "MKI$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_mki", IrType.Ptr, IrType.I16), Val(0, PbType.Integer)),
      "MKL$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_mkl", IrType.Ptr, IrType.I32), Val(0, PbType.Long)),
      "MKDWD$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_mkdwd", IrType.Ptr, IrType.I32), Val(0, PbType.Dword)),
      "MKS$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_mks", IrType.Ptr, IrType.F32), Val(0, PbType.Single)),
      "MKD$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_mkd", IrType.Ptr, IrType.F64), Val(0, PbType.Double)),
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
      "UCASE$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_ucase", IrType.Ptr, IrType.Ptr), Str(0)),
      "LCASE$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_lcase", IrType.Ptr, IrType.Ptr), Str(0)),
      "LTRIM$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_ltrim", IrType.Ptr, IrType.Ptr), Str(0)),
      "RTRIM$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_rtrim", IrType.Ptr, IrType.Ptr), Str(0)),
      // the radix conversions. Their two-argument form fixes the digit count (HEX$(n, 4) pads or
      // truncates to four) - it is a different result, not a formatting nicety, so it declines rather
      // than quietly dropping the count the way taking argument 0 alone would
      "HEX$" or "OCT$" or "BIN$" when ci.Arguments.Count > 1 =>
        throw new IrLoweringException($"{name} with a digit count"),
      "HEX$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_hex", IrType.Ptr, IrType.I32), Num(0)),
      "OCT$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_oct", IrType.Ptr, IrType.I32), Num(0)),
      "BIN$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_bin", IrType.Ptr, IrType.I32), Num(0)),
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
    var ty = MapType(resultPb);
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
    return this._b.SExt(this._b.Cmp(pred, cmp, new IrConstantInt(IrType.I32, 0)), MapType(resultPb));
  }

  private IrValue LowerUdtComparison(BinaryExpr expr, PbType resultPb) {
    if (this._model.TypeOf(expr.Left) is not UdtType udt)
      throw new IrLoweringException("UDT comparison of non-UDT");
    var pred = expr.Op switch {
      BinaryOp.Equal => IrCmpPred.Eq,
      BinaryOp.NotEqual => IrCmpPred.Ne,
      _ => throw new IrLoweringException($"UDT comparison {expr.Op}"),   // the binder allows only = / <>
    };
    var cmp = this._b.Call(IrType.I32, this.RuntimeFn("rt_mem_compare", IrType.I32, IrType.Ptr, IrType.Ptr, IrType.I32),
      this.UdtAddress(expr.Left), this.UdtAddress(expr.Right), new IrConstantInt(IrType.I32, udt.Size));
    return this._b.SExt(this._b.Cmp(pred, cmp, new IrConstantInt(IrType.I32, 0)), MapType(resultPb));
  }

  /// <summary>The base address of a whole UDT value (a UDT variable).</summary>
  private IrValue UdtAddress(Expression e) {
    if (e is NameExpr && this._model.VariableBindings.TryGetValue(e, out var sym) && sym.Type is UdtType)
      return this.SlotFor(sym);
    throw new IrLoweringException("unsupported UDT value");
  }

  private IrValue LowerConvert(CallOrIndexExpr call) {
    // CDBL/CSNG are exactly a type conversion to the result type
    var resultPb = this._model.TypeOf(call);
    return this.Coerce(this.LowerExpr(call.Arguments[0]), this._model.TypeOf(call.Arguments[0]), resultPb);
  }

  private IrValue LowerFix(CallOrIndexExpr call) {
    var resultPb = this._model.TypeOf(call);
    var ty = MapType(resultPb);
    var v = this.Coerce(this.LowerExpr(call.Arguments[0]), this._model.TypeOf(call.Arguments[0]), resultPb);
    if (ty.IsInteger)
      return v;                                       // integers have no fractional part
    // FIX = truncate toward zero: round-trip through a 64-bit integer
    return this._b.Cast(IrCastOp.SIToFP, this._b.Cast(IrCastOp.FPToSI, v, IrType.I64), ty);
  }

  private IrValue LowerInt(CallOrIndexExpr call) {
    var resultPb = this._model.TypeOf(call);
    var ty = MapType(resultPb);
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
    var ty = MapType(resultPb);
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
    var resultTy = MapType(this._model.TypeOf(call));   // INTEGER (-1/0/1)
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

  /// <summary>
  /// A string argument: BYVAL hands over the handle, BYREF a pointer to the slot holding it. A
  /// BYREF argument that is not a plain variable (a literal or an expression) gets a temporary
  /// slot, exactly as PB materializes one - the callee may write through it, but nothing outside
  /// can observe that write.
  /// </summary>
  private IrValue StringArgument(Expression argument, bool byVal) {
    var handle = this.LowerStringExpr(argument);
    if (byVal)
      return handle;
    if (argument is NameExpr && this._model.VariableBindings.TryGetValue(argument, out var sym) && sym.Type is StringType)
      return this.SlotFor(sym);
    var temp = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(IrType.Ptr) { Name = "str.arg" });
    this._b.Store(handle, temp);
    return temp;
  }

  private IrValue EmitCall(IrFunction callee, ProcedureSymbol proc, IReadOnlyList<Expression> arguments) {
    if (arguments.Count != proc.Parameters.Count)
      throw new IrLoweringException("argument count mismatch (optional/CDECL not modelled)");
    var args = new List<IrValue>(arguments.Count);
    for (var i = 0; i < arguments.Count; ++i) {
      var p = proc.Parameters[i];
      args.Add(p.Type is UdtType
        ? this.UdtAddress(arguments[i])                 // a record argument passes its address (BYVAL callee copies, BYREF uses it)
        : p.Type is StringType
          ? this.StringArgument(arguments[i], p.ByVal)
        : p.ByVal
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
    var temp = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(MapType(paramType)) { Name = "byref.tmp" });
    this._b.Store(this.Coerce(this.LowerExpr(arg), this._model.TypeOf(arg), paramType), temp);
    return temp;
  }

  private IrValue LowerUnary(UnaryExpr u) {
    var pb = this._model.TypeOf(u);
    var ty = MapType(pb);
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
        or BinaryOp.LessEqual or BinaryOp.GreaterEqual => leftPb is StringType or FixedStringType
          ? this.LowerStringComparison(expr, resultPb)
          : leftPb is UdtType
            ? this.LowerUdtComparison(expr, resultPb)
            : this.LowerComparison(expr, leftPb, rightPb, resultPb),
      _ => this.LowerArithmetic(expr, leftPb, rightPb, resultPb),
    };
  }

  private IrValue LowerArithmetic(BinaryExpr expr, PbType leftPb, PbType rightPb, PbType resultPb) {
    var resultTy = MapType(resultPb);
    var signed = resultPb is ScalarType { Signed: true };
    var l = this.Coerce(this.LowerExpr(expr.Left), leftPb, resultPb);
    var r = this.Coerce(this.LowerExpr(expr.Right), rightPb, resultPb);

    switch (expr.Op) {
      case BinaryOp.Eqv:
        return this._b.Xor(this._b.Xor(l, r), new IrConstantInt(resultTy, -1));
      case BinaryOp.Imp:
        return this._b.Or(this._b.Xor(l, new IrConstantInt(resultTy, -1)), r);
      case BinaryOp.Power:
        if (!resultTy.IsFloat)
          throw new IrLoweringException("integer exponentiation");   // PB ^ yields a floating result
        return this._b.Call(resultTy, this.RuntimeFn($"llvm.pow.f{resultTy.Bits}", resultTy, resultTy, resultTy), l, r);
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
    return this._checkOverflow && op is IrBinaryOp.Add or IrBinaryOp.Sub or IrBinaryOp.Mul
      ? this.CheckedArithmetic(op, l, r, resultTy, signed)
      : this._b.Binary(op, l, r);
  }

  /// <summary>
  /// The Error 6 trap <c>$ERROR OVERFLOW ON</c> arms, asked without a flags register. The direct
  /// emitter reads OF straight off the ADD/SUB/IMUL it has just emitted (a JNO over the raise); a
  /// target-independent IR has no such thing, so the identical question is put in arithmetic every
  /// back end already has.
  ///
  /// For + and - that is the textbook sign rule, exact in the operand's own width: an addition
  /// overflows exactly when both operands share a sign the sum does not, and a subtraction exactly
  /// when the operands differ in sign and the difference takes the subtrahend's. Unsigned types have
  /// no OF to read either - their wrap is a carry, which is one unsigned compare.
  ///
  /// A multiply has no such rule, so it is done one width up, where the product is exact, and range
  /// checked before being truncated back.
  /// </summary>
  private IrValue CheckedArithmetic(IrBinaryOp op, IrValue l, IrValue r, IrType ty, bool signed) {
    if (op == IrBinaryOp.Mul)
      return this.CheckedMultiply(l, r, ty, signed);

    var sum = this._b.Binary(op, l, r);
    IrValue overflowed;
    if (!signed)
      // an unsigned + carries exactly when the sum comes out below an operand, and an unsigned -
      // borrows exactly when the minuend was below the subtrahend
      overflowed = op == IrBinaryOp.Add
        ? this._b.Cmp(IrCmpPred.Ult, sum, l)
        : this._b.Cmp(IrCmpPred.Ult, l, r);
    else {
      var operandSigns = this._b.Xor(l, r);
      // + wants the operands to have AGREED in sign (~(l^r)); - wants them to have differed
      var interesting = op == IrBinaryOp.Add
        ? this._b.Xor(operandSigns, new IrConstantInt(ty, -1))
        : (IrValue)operandSigns;
      var movedAway = this._b.Xor(sum, l);           // ... and the result to disagree with the left one
      overflowed = this._b.Cmp(IrCmpPred.Slt, this._b.And(interesting, movedAway), new IrConstantInt(ty, 0));
    }
    this.RaiseWhen(overflowed, 6, "overflow");
    return sum;
  }

  /// <summary>The checked multiply: exact one width up, then range-checked back down.</summary>
  private IrValue CheckedMultiply(IrValue l, IrValue r, IrType ty, bool signed) {
    if (ty.Bits >= 64)
      throw new IrLoweringException(
        "$ERROR OVERFLOW ON over a 64-bit multiply (there is no wider integer to hold the exact product)");
    var wide = IrType.Integer(ty.Bits * 2, signed);
    var product = this._b.Binary(IrBinaryOp.Mul,
      signed ? this._b.SExt(l, wide) : this._b.ZExt(l, wide),
      signed ? this._b.SExt(r, wide) : this._b.ZExt(r, wide));

    var highest = signed ? (1L << (ty.Bits - 1)) - 1 : (1L << ty.Bits) - 1;
    var outside = signed
      ? this._b.Or(this._b.Cmp(IrCmpPred.Slt, product, new IrConstantInt(wide, -(1L << (ty.Bits - 1)))),
                   this._b.Cmp(IrCmpPred.Sgt, product, new IrConstantInt(wide, highest)))
      : (IrValue)this._b.Cmp(IrCmpPred.Ugt, product, new IrConstantInt(wide, highest));
    this.RaiseWhen(outside, 6, "overflow");
    return this._b.Trunc(product, ty);
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
    return this._b.SExt(this._b.Cmp(pred, l, r), MapType(resultPb));
  }

  /// <summary>
  /// A metastatement is a compile-time directive, and most carry no runtime semantics at all for a
  /// target-independent IR - they steer the direct emitter's policy or its target, which each IR back
  /// end decides for itself. Those are ignored, because declining on them kept most of the corpus off
  /// the IR path for no reason: <c>$OPTIMIZE</c> and <c>$CPU</c> alone account for the majority of
  /// every metastatement in the battery.
  ///
  /// <c>$ERROR … ON</c> is the one that is not policy: it arms the Error 6/9/11 traps, which are
  /// observable behaviour this lowering does not emit. A program that turns a check <b>on</b>
  /// therefore declines - silently dropping its traps would be a miscompile, not a missing
  /// optimization. Anything unrecognized declines too, so a metastatement with semantics added later
  /// is refused by default rather than ignored by accident.
  /// </summary>
  private void LowerMeta(MetaStmt meta) {
    var arm = meta.Arguments.Count > 0 ? meta.Arguments[0].Text : "";
    var on = meta.Arguments.Count >= 2 && meta.Arguments[^1].Text.Equals("ON", StringComparison.OrdinalIgnoreCase);
    switch (meta.Command.ToUpperInvariant()) {
      case "OPTIMIZE":   // optimizer policy - the IR path runs its own pipeline
      case "CPU":        // the instruction-set floor of the DOS emitter; an IR back end picks its own
        return;
      // $ERROR BOUNDS ON: every subscript is checked against its dimension and Error 9 raised when it
      // falls outside - the same guard CodeGenerator.Arrays emits when CheckBounds is set
      case "ERROR" when arm.Equals("BOUNDS", StringComparison.OrdinalIgnoreCase):
        this._checkBounds = on;
        return;
      // $ERROR OVERFLOW ON: integer +, - and * raise Error 6 when they wrap (see CheckedArithmetic)
      case "ERROR" when arm.Equals("OVERFLOW", StringComparison.OrdinalIgnoreCase):
        this._checkOverflow = on;
        return;
      // $ERROR NUMERIC ON: the FOR counter increment is the one place this raises - see LowerFor
      case "ERROR" when arm.Equals("NUMERIC", StringComparison.OrdinalIgnoreCase):
        this._checkNumeric = on;
        return;
      case "ERROR" when meta.Arguments.Count >= 2 && !on:
        return;          // turning a check OFF is exactly what this lowering already assumes
      case "ERROR":
        throw new IrLoweringException($"$ERROR {arm} ON arms a runtime trap the IR lowering does not emit");
      default:
        throw new IrLoweringException($"metastatement ${meta.Command}");
    }
  }

  /// <summary>
  /// Maps a PB type for a lowered value. The IR type system can express Microsoft Binary Format
  /// (<c>mbf32</c>/<c>mbf64</c> - the SINGLE/DOUBLE storage of BASICA, GW-BASIC and the
  /// BASCOM-heritage QuickBASIC releases), but the lowering does not yet emit the
  /// <see cref="IrCastOp.MbfToFP"/>/<see cref="IrCastOp.FPToMbf"/> conversions a load and a store of
  /// such a cell perform, so it declines rather than treat the bits as IEEE - which would be a
  /// miscompile, the two encodings disagreeing on exponent bias and layout.
  /// </summary>
  /// <summary>
  /// Maps a PB type for a lowered value, Microsoft Binary Format included.
  ///
  /// MBF used to be refused here, which kept every BASICA and GW-BASIC program with a SINGLE variable
  /// off the IR path entirely. It is carried instead: the IR type system already distinguishes it
  /// (<see cref="IrFloatFormat.Mbf"/>), and a back end that cannot compute on those bits declines on
  /// the TYPE - which the x86-16 selector and the C and LLVM emitters all do. Carrying a fact and
  /// refusing to act on it is what a type system is for; refusing to record it loses the program.
  ///
  /// The BASIC writer does act on it, by dropping it: pb35 has no MBF, so a rendered SINGLE is IEEE
  /// and the writer says so rather than pretending the storage survived.
  /// </summary>
  private static IrType MapType(PbType type) => IrTypeMapper.Map(type);

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

  /// <summary>
  /// The IEEE scalar an MBF type computes as. Microsoft Binary Format is STORAGE - the x87 cannot
  /// add two of them - so a value is converted to IEEE the moment it is used and back when it is
  /// stored, which is exactly what the direct emitter does around an MBF cell.
  /// </summary>
  private static ScalarType IeeeFormOf(MbfType mbf) =>
    new(mbf.IsDouble ? ScalarKind.Double : ScalarKind.Single, mbf.IsDouble ? 8 : 4, true, true);

  private IrValue Coerce(IrValue value, PbType from, PbType to) {
    // MBF on either side: convert to IEEE to compute, and back to MBF to store. Recording the format
    // in the IR is only worth anything if the conversions it implies are emitted too - a type nobody
    // converts is a type nobody honoured.
    if (from is MbfType fromMbf) {
      var ieee = IeeeFormOf(fromMbf);
      return this.Coerce(this._b.Cast(IrCastOp.MbfToFP, value, MapType(ieee)), ieee, to);
    }
    if (to is MbfType toMbf) {
      var ieee = IeeeFormOf(toMbf);
      return this._b.Cast(IrCastOp.FPToMbf, this.Coerce(value, from, ieee), MapType(to));
    }
    if (from is not ScalarType sf || to is not ScalarType st)
      throw new IrLoweringException("coercion between non-scalar types");
    var toTy = MapType(to);
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
    // BASIC ROUNDS a real on its way into an integer variable - n% = 2.7 is 3, not 2 - so this is
    // the rounding conversion, not the truncating one FIX/INT ask for.
    //
    // WHICH rounding is a dialect fact, and the IR carries it rather than flattening both to one
    // opcode. The BASCOM lineage (QuickBASIC 1.0 to 3.0) rounds half AWAY from zero - CINT(2.5) is 3
    // and CINT(-2.5) is -3, oracle-verified - where QB 4.x and PowerBASIC take the FPU's
    // round-half-to-even. Flattening them made every QB 1-3 program round the pb35 way on this path,
    // which the rendered-BASIC harness caught as a disagreement on qb10/qb20/qb30 DIFF02.
    //
    // It is spelled as a named runtime call, not a second cast opcode, because that is what lets each
    // back end decide: the pb35 writer expands it into the arithmetic that reproduces it, and a back
    // end with no such entry declines instead of rounding the other way.
    if (st.Signed && this._model.EffectiveDialect.IsBascomRuntime())
      return this._b.Call(toTy, this.RuntimeFn("rt_round_half_away", toTy, value.Type), value);
    return this._b.Cast(st.Signed ? IrCastOp.FPToSIRound : IrCastOp.FPToUI, value, toTy);
  }
}
