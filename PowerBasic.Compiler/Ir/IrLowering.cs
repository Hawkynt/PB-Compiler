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

  /// <summary>The procedure being lowered, or null in the main body - needed to resolve inline-asm names.</summary>
  private ProcedureSymbol? _proc;
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
    this._checkStack = StackCheckArmed(model);
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
    var module = new IrModule(model.FileName, model.Dialect, model.CompatDialect);
    var procMap = new Dictionary<ProcedureSymbol, IrFunction>(ReferenceEqualityComparer.Instance);

    // An EXTERNAL procedure is included too, as a signature with no body - which is exactly what
    // IrFunction calls a declaration. It has no code to lower and never will: DECLARE FUNCTION
    // AddInts%(BYVAL a%, BYVAL b%) names a symbol another object file supplies. Leaving it out of
    // the map meant every CALL to one declined, and the program with it, for want of a callee that
    // was never going to have a body.
    foreach (var proc in model.Procedures.Values)
      if (TrySignature(proc, out var irfn)) {
        procMap[proc] = irfn!;
        module.AddFunction(irfn!);
      }

    var shared = new Dictionary<VariableSymbol, IrGlobalVariable>(ReferenceEqualityComparer.Instance);
    var escapes = ModuleVariablesUsedByProcedures(model);
    // A COMMON variable needs a DATA cell rather than a frame slot, whether or not any procedure
    // reads it. The CHAIN handoff is copied by the runtime, and rt_chwrite/rt_chread take their
    // buffer as a bare offset with DS assumed - so a COMMON value in the frame would be streamed
    // from the wrong segment. Naming it here is also what makes both back ends address the SAME
    // storage: the codegen resolves g.<name> to the direct emitter's own cell.
    foreach (var symbol in CommonVariables(model))
      escapes.Add(symbol);
    // A FIELD variable needs one for the same reason and a stronger one: rt_fldadd keeps the ADDRESS
    // of the handle cell in a table and the record walk dereferences it later, long after the
    // statement that registered it - through DS, and with no segment of its own to check against.
    foreach (var symbol in FieldTargets(model))
      escapes.Add(symbol);
    var main = new IrFunction("main", IrType.Void);
    module.AddFunction(main);
    try {
      new IrLowering(model, procMap, module, shared, escapes).LowerBodyInto(main, model.MainBody, null);
    } catch (IrLoweringException e) {
      declinedBecause = e.Message;
      return null;
    }

    foreach (var (proc, irfn) in procMap) {
      if (proc.IsExternal)
        continue;                                      // no body to lower, and that is the point
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

  /// <summary>
  /// The module's COMMON variables in DECLARATION order, which is the order CHAIN streams them into
  /// the handoff file and the order the next image reads them back out of it. The two sides agree on
  /// nothing else - there is no name or type in the file, only bytes - so the order IS the protocol.
  ///
  /// <para>
  /// A COMMON ARRAY is deliberately absent rather than reported here: the direct emitter refuses one
  /// too, and a caller that needs to say so raises it where the statement is (see
  /// <see cref="LowerChain"/>). Returning the list unconditionally keeps this callable from
  /// <see cref="TryLowerModule"/>, which runs outside the lowering's own try block.
  /// </para>
  /// </summary>
  private static List<VariableSymbol> CommonVariables(SemanticModel model) {
    var result = new List<VariableSymbol>();
    foreach (var statement in model.MainBody)
      if (statement is DimStmt { Storage: StorageClass.Common } dim)
        foreach (var v in dim.Variables) {
          var key = v.Name + v.Suffix.KeyText();
          var symbol = model.ModuleVariables.GetValueOrDefault(key)
            ?? model.ModuleVariables.GetValueOrDefault(key + "()");
          if (symbol is not null && !result.Contains(symbol))
            result.Add(symbol);
        }
    return result;
  }

  /// <summary>
  /// Every module-level string a <c>FIELD</c> statement names as a record window. The whole module
  /// body is walked rather than its top level, because a FIELD may sit inside an IF or a loop and the
  /// registration outlives the statement either way.
  /// </summary>
  private static IEnumerable<VariableSymbol> FieldTargets(SemanticModel model) {
    foreach (var node in CodeGen.OptReachability.DescendantNodes(model.MainBody))
      if (node is FieldStmt field)
        foreach (var (_, target) in field.Fields)
          if (model.VariableBindings.TryGetValue(target, out var symbol))
            yield return symbol;
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
    this._proc = proc;
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

    // $ERROR STACK ON: the headroom probe, at the head of the procedure and before anything that
    // could consume more of the stack. The module body is not probed, matching the direct emitter -
    // it is the RECURSION a procedure can start that exhausts a stack, and main is entered once.
    if (proc is not null && this._checkStack)
      this._b.Call(IrType.Void, this.RuntimeFn("rt_stack_probe", IrType.Void));

    // pre-create a block for every label so forward GOTOs have a target
    foreach (var label in CollectLabels(body))
      this._labels[label] = this._fn.CreateBlock("lbl." + label);

    if (ContainsGosub(body))
      this.SetupGosub();

    // whether statements must publish their boundaries has to be known BEFORE the first one is
    // lowered: RESUME NEXT can name a statement that ran long before the handler was armed
    this._resumeTracking = ContainsResume(body);

    // ...and the other half of CHAIN: whatever the PREVIOUS image left in PBCHAIN.$$$ is absorbed
    // into the COMMON cells before the first statement runs. The direct emitter writes this at the
    // head of its module body too, but only on the path where it owns that body - so a routed main
    // that did not carry it would start every chained-to pass with the values it was handed missing.
    this.LowerChainCommonLoad();

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

  /// <summary>
  /// Lowers one <c>!</c> statement, binding every BASIC identifier it mentions to the storage that
  /// identifier names.
  ///
  /// The names are collected by ASSEMBLING the text against a throwaway target with a resolver that
  /// records what it is asked for. That is precise where a lexical scan would not be: the real parser
  /// knows which tokens are registers, which are mnemonics and which are operands, and asks about
  /// exactly the last group. Guessing that set is the failure mode this whole node exists to avoid.
  ///
  /// A name that does not bind to a variable leaves the block un-routable. It might be a BASIC label,
  /// an equate, or something the model does not know at all; the direct emitter resolves all of those,
  /// and until a back end can too, emitting a partly-resolved block would put a name on the wrong cell
  /// without saying so.
  /// </summary>
  private void LowerInlineAsm(InlineAsmStmt stmt) {
    var node = new IrInlineAsm(stmt.Text);
    var seen = new AsmNames();
    var parsed = new Asm.TextAssembler(new Asm.Assembler()).TryParse(stmt.Text, seen, out _);

    var routable = parsed;
    foreach (var name in seen.Collected)
      if (this.AsmVariable(name) is { } symbol)
        node.Bind(name, this.SlotFor(symbol));
      else
        routable = false;

    node.Routable = routable;
    this._b.InlineAsm(node);
    this._fn.HasInlineAsm = true;
  }

  /// <summary>Records every identifier the assembler asks about, answering so that parsing continues.</summary>
  private sealed class AsmNames : Asm.IAsmSymbolResolver {
    public List<string> Collected { get; } = [];

    public bool TryResolve(string name, out Asm.AsmSymbol symbol) {
      if (!this.Collected.Contains(name, StringComparer.OrdinalIgnoreCase))
        this.Collected.Add(name);
      // The stand-in has to be MEMORY, not a constant. The parse must reach the same conclusions the
      // real one will, and "MOV n, AX" with n a constant is not an instruction - answering with a
      // constant made every write-to-a-variable block report itself unbindable.
      symbol = Asm.AsmSymbol.OfMemory(Asm.Mem.Word(Asm.Reg.BP, 0));
      return true;
    }
  }

  /// <summary>The variable an inline-asm identifier names, trying the suffix spellings PB allows.</summary>
  private VariableSymbol? AsmVariable(string name) {
    foreach (var suffix in _ASM_SUFFIXES) {
      var key = name + suffix.KeyText();
      if (this._proc?.Variables.TryGetValue(key, out var local) == true)
        return local;
      if (this._model.ModuleVariables.TryGetValue(key, out var global))
        return global;
    }
    return null;
  }

  /// <summary>Suffix spellings tried when an inline-asm name carries none, as the direct emitter tries them.</summary>
  private static readonly TypeSuffix[] _ASM_SUFFIXES = [
    TypeSuffix.None, TypeSuffix.Integer, TypeSuffix.Long, TypeSuffix.Single, TypeSuffix.Double, TypeSuffix.Ext, TypeSuffix.String,
  ];

  /// <summary>String slots this lowering has null-initialised, so a read of the previous value is sound.</summary>
  private readonly HashSet<IrValue> _nullInitialisedStrings = [];

  private IrValue SlotFor(VariableSymbol symbol) {
    if (this._addr.TryGetValue(symbol, out var existing))
      return existing;
    // A PB data pointer is a 4-byte seg:off cell and the IR's pointer is the 2-byte near offset the
    // whole program shares a segment for, so the two layouts differ. That costs nothing while the
    // cell is this lowering's own frame slot, and everything the moment a DIRECTLY EMITTED procedure
    // reads the same storage: it would load the offset from the low word and a segment from whatever
    // the high word still held. So a pointer that needs shared storage declines instead.
    if (symbol.Type is PointerType && this.NeedsSharedStorage(symbol))
      throw new IrLoweringException("pointer variable with shared storage");
    if (this.NeedsSharedStorage(symbol))
      return this.GlobalFor(symbol);
    IrAlloca alloca;
    if (symbol.Type is PointerType) {
      alloca = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(IrType.Ptr) { Name = symbol.Name });   // holds a near address
    } else if (symbol.Type is StringType) {
      alloca = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(IrType.Ptr) { Name = symbol.Name });  // holds a string handle
      // ...starting EMPTY, which is both what PB says a string variable holds before its first
      // assignment and what makes the handle readable at all. An alloca is uninitialised, so anything
      // that looks at the previous value - freeing the handle an assignment replaces, most obviously -
      // would be reading whatever the frame happened to contain.
      this._entry.InsertAt(this._entryAllocaCount++, new IrStore(new IrNullPtr(), alloca));
      this._nullInitialisedStrings.Add(alloca);
    } else if (symbol.Type is FixedStringType fixedStr) {
      alloca = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(IrType.I8) { Count = fixedStr.Length, Name = symbol.Name });  // inline fixed buffer
    } else if (symbol.Type is AsciizType asciiz) {
      alloca = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(IrType.I8) { Count = asciiz.Length, Name = symbol.Name });   // inline NUL-terminated buffer
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

  /// <summary>
  /// The stable IR name of one STATIC local's process-lifetime cell. The owning procedure is part of
  /// the name because two procedures may legally declare the same local spelling without sharing it;
  /// an overload index distinguishes same-named PB 3.6 procedures while preserving readable names for
  /// the common, non-overloaded case.
  /// </summary>
  internal static string StaticGlobalName(ProcedureSymbol? procedure, VariableSymbol symbol) {
    var owner = procedure?.Name ?? "main";
    var overload = procedure is { OverloadIndex: > 0 } ? $".{procedure.OverloadIndex}" : "";
    return $"static.{owner}{overload}.{symbol.Name}";
  }

  /// <summary>The one module global backing <paramref name="symbol"/>, created on first use.</summary>
  private IrValue GlobalFor(VariableSymbol symbol) {
    if (this._sharedStorage!.TryGetValue(symbol, out var existing))
      return existing;
    var (valueType, count) = symbol.Type switch {
      StringType => (IrType.Ptr, 1),
      FixedStringType fs => (IrType.I8, fs.Length),
      AsciizType az => (IrType.I8, az.Length),
      UdtType udt => (IrType.I8, udt.Size),
      ArrayType { IsDynamic: false } arr => arr.Element switch {
        StringType => (IrType.Ptr, arr.ElementCount),
        UdtType ue => (IrType.I8, arr.ElementCount * ue.Size),
        _ => (MapType(arr.Element), arr.ElementCount),
      },
      ArrayType => throw new IrLoweringException("dynamic array with shared storage"),
      _ => (MapType(symbol.Type), 1),
    };
    // The procedure qualification prevents same-named STATIC locals from aliasing; module globals
    // need only the storage-class prefix because their source names are already module-unique.
    var name = symbol.Storage == VariableStorage.Static
      ? StaticGlobalName(this._proc, symbol)
      : $"g.{symbol.Name}";
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

  /// <summary>
  /// $ERROR STACK ON: every procedure entry probes for headroom and raises Error 201 without it.
  ///
  /// <para>
  /// Read from the DIRECTIVES rather than accumulated as the statements go by, which the three flags
  /// above cannot be: each procedure is lowered by its own <see cref="IrLowering"/>, so a flag a
  /// metastatement sets while the module body is being lowered has no way to reach one. The probe
  /// belongs to the procedure prologue, so it needs an answer that survives that boundary.
  /// </para>
  /// </summary>
  private bool _checkStack;

  /// <summary>
  /// Whether <c>$ERROR STACK ON</c> (or <c>$ERROR ALL ON</c>) is the last word on the subject.
  /// The direct emitter's flag is positional - whatever the metastatement handler last set while
  /// emitting - which for a directive at the top of the file, where they all live, is this.
  /// </summary>
  private static bool StackCheckArmed(SemanticModel model) {
    var armed = false;
    foreach (var meta in model.MetaStatements)
      if (meta.Command.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
          && meta.Arguments is [{ } arm, { } state, ..]
          && (arm.Text.Equals("STACK", StringComparison.OrdinalIgnoreCase)
              || arm.Text.Equals("ALL", StringComparison.OrdinalIgnoreCase)))
        armed = state.Text.Equals("ON", StringComparison.OrdinalIgnoreCase);
    return armed;
  }

  private DynArr DynDescriptor(VariableSymbol symbol, int rank) {
    if (this._dynArrays.TryGetValue(symbol, out var existing))
      return existing;
    // FarPtr, not Ptr: dynamic array storage comes out of the runtime's far array heap, and the cell
    // that holds the block address is the only place that fact is written down. Every read of it - a
    // load here, a phi mem2reg mints for it, a GEP off either - inherits the space from this type.
    var data = this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(IrType.FarPtr) { Name = symbol.Name + ".data" });
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
    var data = this._b.Load(IrType.FarPtr, descriptor.Data);

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
      case ArraySortStmt sort: this.LowerArraySort(sort); break;
      case ArrayScanStmt scan: this.LowerArrayScan(scan); break;
      case SwapStmt sw: this.LowerSwap(sw); break;
      case MidAssignStmt mid: this.LowerMidAssign(mid); break;
      case AscAssignStmt asc: this.LowerAscAssign(asc); break;
      case ReplaceStmt replace: this.LowerReplace(replace); break;
      case BitStmt bit: this.LowerBitStmt(bit); break;
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
      // Inline asm is carried, not understood: an opaque barrier plus a flag that takes the whole
      // function out of the optimizer. That is enough to stop it being a wall - a program with one
      // "!" line used to keep EVERY one of its procedures off the IR path, and now only the procedure
      // that contains it is unoptimized.
      case InlineAsmStmt asm: this.LowerInlineAsm(asm); break;
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
      // DEF SEG = n stores the word; bare DEF SEG puts DS back, which only the runtime can say
      case DefSegStmt { Segment: { } segment }:
        this._b.Store(this.Coerce(this.LowerExpr(segment), this._model.TypeOf(segment), PbType.Integer),
          this.ErrorCell("rt_defseg", IrType.I16));
        break;
      case DefSegStmt:
        this._b.Call(IrType.Void, this.RuntimeFn("rt_defseg_reset", IrType.Void));
        break;
      // SEEK #n, p - PB's own numbering: 0-based BYTES for BINARY, 1-based RECORDS for RANDOM, which
      // rt_fseekstmt sorts out from the file's own record length. The position is a LONG.
      case SeekStmt seek:
        this._b.Call(IrType.Void, this.RuntimeFn("rt_file_seek", IrType.Void, IrType.I32, IrType.I32),
          this.FileNum(seek.FileNumber),
          this.Coerce(this.LowerExpr(seek.Target), this._model.TypeOf(seek.Target), PbType.Long));
        break;
      // PUT$ fh, s$ - the string's bytes written raw, no terminator and no record structure
      case CommandStmt { Keyword: "PUT$", Arguments: [{ } putFile, { } putValue] }:
        this._b.Call(IrType.Void, this.RuntimeFn("rt_fput_str", IrType.Void, IrType.I32, IrType.Ptr),
          this.FileNum(putFile), this.LowerStringExpr(putValue));
        break;
      // GET$ fh, count, target$ - count bytes read raw; a short read yields what there was
      case CommandStmt { Keyword: "GET$", Arguments: [{ } getFile, { } getCount, { } getTarget] }:
        this._b.Store(
          this._b.Call(IrType.Ptr, this.RuntimeFn("rt_fget_str", IrType.Ptr, IrType.I32, IrType.I32),
            this.FileNum(getFile),
            this.Coerce(this.LowerExpr(getCount), this._model.TypeOf(getCount), PbType.Long)),
          this.StringTargetAddress(getTarget));
        break;
      case WriteStmt write:
        this.LowerWrite(write);
        break;
      case ChainStmt chain:
        this.LowerChain(chain);
        break;
      case FieldStmt field:
        this.LowerField(field);
        break;
      case LsetRsetStmt lset:
        this.LowerLsetRset(lset);
        break;
      // SETEOF #n truncates the file where it stands. The direct emitter writes it INLINE as a DOS
      // write of zero bytes, so it needs a routine to call, like PEEK and POKE before it.
      case CommandStmt { Keyword: "SETEOF", Arguments: [{ } setEofFile] }:
        this._b.Call(IrType.Void, this.RuntimeFn("rt_file_seteof", IrType.Void, IrType.I32),
          this.FileNum(setEofFile));
        break;
      // INTERRUPT n - the vector goes in AL and the routine patches its own INT instruction with it,
      // loading every register from rt_regs beforehand and storing them all back after. Which is why
      // REG had to lower first: the two are one facility, and either alone is unusable.
      case CommandStmt { Keyword: "INTERRUPT", Arguments: [{ } vector] }:
        this._b.Call(IrType.Void, this.RuntimeFn("rt_interrupt", IrType.Void, IrType.I16),
          this.Coerce(this.LowerExpr(vector), this._model.TypeOf(vector), PbType.Integer));
        break;
      // REG n, v - the register buffer INTERRUPT loads from. The scaling lives in the routine because
      // the IR has no way to name a scaled index into a runtime table.
      case CommandStmt { Keyword: "REG", Arguments: [{ } regIndex, { } regValue] }:
        this._b.Call(IrType.Void, this.RuntimeFn("rt_reg_set", IrType.Void, IrType.I16, IrType.I16),
          this.Coerce(this.LowerExpr(regIndex), this._model.TypeOf(regIndex), PbType.Integer),
          this.Coerce(this.LowerExpr(regValue), this._model.TypeOf(regValue), PbType.Integer));
        break;
      // POKE offset, value | POKE seg, offset, value - the segmented form sets DEF SEG first and
      // LEAVES it set, which is what the classic pair does: it is two statements written as one,
      // not a scoped override, so a later bare POKE writes into the segment this one named.
      case CommandStmt { Keyword: "POKE", Arguments: [{ } pokeAddress, { } pokeValue] }:
        this.LowerPoke(pokeAddress, pokeValue);
        break;
      case CommandStmt { Keyword: "POKE", Arguments: [{ } pokeSegment, { } pokeOffset, { } pokeSegValue] }:
        this.SetDefaultSegment(pokeSegment);
        this.LowerPoke(pokeOffset, pokeSegValue);
        break;
      // CommandStmt is a catch-all for a dozen unrelated statements (KILL, POKE, OUT, RANDOMIZE...),
      // so it names the keyword: "unsupported statement: CommandStmt" ranks nothing
      default: throw new IrLoweringException(statement is CommandStmt command
        ? $"unsupported statement: {command.Keyword}"
        : $"unsupported statement: {statement.GetType().Name}");
    }
  }

  private void LowerAssign(AssignStmt a) {
    // p = VARPTR32(x) / p = q: a pointer variable takes a pointer, never a number. The value is
    // lowered as an ADDRESS rather than coerced, because there is no conversion between the two.
    if (a.Target is NameExpr && this._model.VariableBindings.TryGetValue(a.Target, out var ptrSym) && ptrSym.Type is PointerType) {
      this._b.Store(this.PointerValue(a.Value), this.SlotFor(ptrSym));
      return;
    }
    // @p = v / @p[i] = v: a store through the pointer, in the target's own type
    if (a.Target is PtrDerefExpr targetDeref) {
      if (this._model.TypeOf(targetDeref) is not ScalarType derefTarget)
        throw new IrLoweringException("assignment through a pointer to a non-scalar");
      var derefAddress = this.DerefAddress(targetDeref);
      this._b.Store(this.Coerce(this.LowerExpr(a.Value), this._model.TypeOf(a.Value), derefTarget), derefAddress);
      return;
    }
    if (a.Target is NameExpr && this._model.VariableBindings.TryGetValue(a.Target, out var strSym) && strSym.Type is StringType) {
      // the value FIRST, so `t = t + "x"` has taken its own copy before the old handle goes
      var strSlot = this.SlotFor(strSym);
      var strValue = this.LowerStringExpr(a.Value);
      this.FreeReplacedString(strSlot);
      this._b.Store(strValue, strSlot);                                     // strings are immutable handles
      return;
    }
    if (a.Target is NameExpr && this._model.VariableBindings.TryGetValue(a.Target, out var fstrSym) && fstrSym.Type is FixedStringType fixedStr) {
      this._b.Call(IrType.Void, this.RuntimeFn("rt_str_to_fixed", IrType.Void, IrType.Ptr, IrType.I32, IrType.Ptr),
        this.SlotFor(fstrSym), new IrConstantInt(IrType.I32, fixedStr.Length), this.LowerStringExpr(a.Value));  // copy, space-pad / truncate to N
      return;
    }
    // ASCIIZ * n: copy and TERMINATE rather than copy and blank-pad, which is the whole difference
    // between it and a fixed string - assigning a ten-character value to an ASCIIZ * 6 keeps five
    // characters and a NUL, where a STRING * 6 would keep six and no terminator at all
    if (a.Target is NameExpr && this._model.VariableBindings.TryGetValue(a.Target, out var azSym) && azSym.Type is AsciizType azTarget) {
      this._b.Call(IrType.Void, this.RuntimeFn("rt_asciiz_store", IrType.Void, IrType.Ptr, IrType.I32, IrType.Ptr),
        this.SlotFor(azSym), new IrConstantInt(IrType.I32, azTarget.Length), this.LowerStringExpr(a.Value));
      return;
    }
    if (a.Target is CallOrIndexExpr indexed && this._model.VariableBindings.TryGetValue(indexed, out var arrSym) && arrSym.Type is ArrayType) {
      var (address, element) = this.ElementAddress(indexed);
      if (element is StringType) {
        this._b.Store(this.LowerStringExpr(a.Value), address);   // a string array element holds an immutable handle
        return;
      }
      // a RECORD element is copied whole, as a record variable is - it has no single value to load,
      // and asking for one is what used to take the program off the IR path. This declined for a
      // while over a defect further down: two pointer arguments into the SAME frame object had the
      // selector's staging overwrite one with the other, and grid(3) = grid(1) copied zeros. The
      // staging now reserves every destination it has filled, so both pointers survive.
      if (element is UdtType elementRecord) {
        this._b.Call(IrType.Void, this.RuntimeFn("llvm.memcpy.p0.p0.i32", IrType.Void, IrType.Ptr, IrType.Ptr, IrType.I32, IrType.I1),
          address, this.UdtAddress(a.Value), new IrConstantInt(IrType.I32, elementRecord.Size), IrBuilder.ConstBool(false));
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
        if (field.Type is AsciizType faz) {            // an ASCIIZ record field: truncate and terminate
          this._b.Call(IrType.Void, this.RuntimeFn("rt_asciiz_store", IrType.Void, IrType.Ptr, IrType.I32, IrType.Ptr),
            fieldAddr, new IrConstantInt(IrType.I32, faz.Length), this.LowerStringExpr(a.Value));
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

  /// <summary>
  /// Frees the handle a string assignment is about to replace - what <c>rt_strassign</c> does for the
  /// direct emitter, which hands the routine the CELL and lets it free what it finds there.
  ///
  /// Without this the IR leaks every value a string variable ever held: the runtime entries consume
  /// the temporaries inside an expression (that is what the borrow on a read is for), and the one
  /// handle nothing consumes is the variable's previous value. STRHEAP.BAS is the program that
  /// notices - two thousand assignments of a 200-byte concatenation through a 64 KiB compacting heap
  /// - and it says OUT OF STRING SPACE rather than anything that points here.
  ///
  /// Only slots this lowering NULL-INITIALISED are freed. That is not a formality: an alloca holds
  /// whatever the frame did, so freeing the previous value of a variable that has never been
  /// assigned hands the allocator a garbage handle. The first attempt at this did exactly that and
  /// took 15 tests with it.
  /// </summary>
  private void FreeReplacedString(IrValue slot) {
    if (!this._nullInitialisedStrings.Contains(slot))
      return;
    this._b.Call(IrType.Void, this.RuntimeFn("rt_str_free", IrType.Void, IrType.Ptr),
      this._b.Load(IrType.Ptr, slot));
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

  /// <summary>
  /// <c>ASC(target$[, n]) = code</c> - one byte poked in place. Modelled the way MID$ assignment is,
  /// and for the same reason: a read of a string variable hands out an owned COPY, so poking what
  /// LowerStringExpr returned would change a temporary and leave the variable alone. The routine
  /// answers with the handle it was given, so storing that back is what makes the edit visible -
  /// and the copy the borrow made is the thing that ends up in the variable.
  ///
  /// Only the dynamic-string target is lowered. FIXED and ASCIIZ ones are a different sequence in
  /// the direct emitter (an address, not a handle) and decline here rather than being approximated.
  /// </summary>
  private void LowerAscAssign(AscAssignStmt asc) {
    if (this._module is null)
      throw new IrLoweringException("ASC assignment requires whole-module lowering");
    if (this._model.TypeOf(asc.Target) is not (StringType or FlexType))
      throw new IrLoweringException("ASC assignment to a fixed-length or ASCIIZ target");
    var index = asc.Index is { } ix
      ? this.Coerce(this.LowerExpr(ix), this._model.TypeOf(ix), PbType.Integer)
      : new IrConstantInt(IrType.I16, 1);
    var code = this.Coerce(this.LowerExpr(asc.Value), this._model.TypeOf(asc.Value), PbType.Integer);
    var poked = this._b.Call(IrType.Ptr,
      this.RuntimeFn("rt_str_asc_set", IrType.Ptr, IrType.Ptr, IrType.I16, IrType.I16),
      this.LowerStringExpr(asc.Target), index, code);
    this._b.Store(poked, this.StringTargetAddress(asc.Target));
  }

  /// <summary>
  /// <c>BIT SET</c> / <c>RESET</c> / <c>TOGGLE var, n</c> - one bit of an integer variable, in the
  /// variable's own width. No runtime is involved on either path: the direct emitter builds
  /// <c>1 &lt;&lt; n</c> in DX:AX with a shift loop and ORs, ANDs or XORs it into the cell.
  ///
  /// <para>
  /// A count of 32 or more yields a mask of ZERO rather than an undefined shift - the emitter's loop
  /// shifts the one out and lands on nothing, and a negative n reaches the same place by counting
  /// through 65535 iterations of it. The guard folds away whenever n is a literal, which it nearly
  /// always is. It is the same reasoning the BIT() function's own guard is built on.
  /// </para>
  /// </summary>
  private void LowerBitStmt(BitStmt bit) {
    // the bit number is evaluated BEFORE the target place, which is the order the direct emitter
    // pushes them in and the only thing that distinguishes the two when either has a side effect
    var index = this.Coerce(this.LowerExpr(bit.Bit), this._model.TypeOf(bit.Bit), PbType.Long);
    var (address, targetType) = this.LValue(bit.Target);
    if (targetType is not ScalarType { IsFloat: false, ByteSize: 1 or 2 or 4 })
      throw new IrLoweringException($"BIT statement on {targetType}");
    var ty = MapType(targetType);

    var wide = this._b.Select(this._b.Cmp(IrCmpPred.Ult, index, new IrConstantInt(IrType.I32, 32)),
      this._b.Binary(IrBinaryOp.Shl, new IrConstantInt(IrType.I32, 1), index), new IrConstantInt(IrType.I32, 0));
    IrValue mask = ty.Bits == 32 ? wide : this._b.Trunc(wide, ty);
    var value = this._b.Load(ty, address);
    this._b.Store(bit.Op switch {
      BitOp.Set => this._b.Or(value, mask),
      BitOp.Reset => this._b.And(value, this._b.Xor(mask, new IrConstantInt(ty, -1))),
      _ => this._b.Xor(value, mask),
    }, address);
  }

  /// <summary>
  /// <c>REPLACE find$ WITH new$ IN target$</c> - every occurrence, in one pass, answered as a fresh
  /// handle the target then takes.
  ///
  /// The subject is read as an ordinary string expression (a borrowed copy, which the routine
  /// consumes like every other runtime entry) and the handle the variable HELD is freed before the
  /// new one lands, which is what the direct emitter's store into the place does for it.
  /// </summary>
  private void LowerReplace(ReplaceStmt replace) {
    if (this._module is null)
      throw new IrLoweringException("REPLACE requires whole-module lowering");
    if (this._model.TypeOf(replace.Target) is not StringType)
      throw new IrLoweringException("REPLACE into a fixed-length or ASCIIZ target");
    var replaced = this._b.Call(IrType.Ptr,
      this.RuntimeFn("rt_str_replace", IrType.Ptr, IrType.Ptr, IrType.Ptr, IrType.Ptr),
      this.LowerStringExpr(replace.Target), this.LowerStringExpr(replace.Find), this.LowerStringExpr(replace.With));
    var slot = this.StringTargetAddress(replace.Target);
    this.FreeReplacedString(slot);
    this._b.Store(replaced, slot);
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
    if (e is PtrDerefExpr deref && this._model.TypeOf(deref) is ScalarType target)
      return (this.DerefAddress(deref), target);
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
    } else if (m.Target is PtrDerefExpr deref && this._model.TypeOf(deref) is UdtType derefUdt) {   // @q.Field - the record the pointer names
      basePtr = this.DerefAddress(deref);
      udt = derefUdt;
    } else
      throw new IrLoweringException("unsupported member access");

    var field = udt.FindField(m.Member) ?? throw new IrLoweringException($"unknown field {m.Member}");
    if (field.ElementCount != 1)
      throw new IrLoweringException("UDT array field");
    var address = field.Offset == 0 ? basePtr : this._b.Gep(basePtr, new IrConstantInt(IrType.I32, field.Offset));
    return (address, field);
  }

  #region data pointers (PB 3.2)

  /// <summary>
  /// The value of a PB data pointer, as an IR pointer.
  ///
  /// <para>
  /// PB spells it as a 32-bit seg:off pair and the IR's pointer is a near offset, which is not a
  /// narrowing here: <c>VARPTR32</c> answers <c>DS</c> for every near place (the direct emitter's own
  /// <c>LEA AX, cell</c> / <c>MOV DX, DS</c>), the image is one segment, and a BYVAL pointer override
  /// against a BYREF parameter passes the OFFSET word alone - so the segment half is the one the
  /// program is already running in wherever a pointer can be formed at all.
  /// </para>
  /// <para>
  /// Only the forms whose segment is known that way lower: <c>VARPTR32</c> of storage, and a pointer
  /// read out of another pointer. Making one from an arbitrary DWORD would need an integer-to-pointer
  /// cast the IR does not have, and would be wrong to fake with a value whose segment nobody has
  /// said - so it declines.
  /// </para>
  /// </summary>
  private IrValue PointerValue(Expression e) {
    if (e is ByValArgExpr byVal)
      return this.PointerValue(byVal.Value);
    if (e is CallOrIndexExpr call && this._model.IntrinsicBindings.TryGetValue(call, out var intrinsic)
        && intrinsic.Name.Equals("VARPTR32", StringComparison.OrdinalIgnoreCase) && call.Arguments.Count == 1)
      return this.AddressOfStorage(call.Arguments[0]);
    if (this._model.TypeOf(e) is not PointerType)
      throw new IrLoweringException("unsupported pointer value");
    if (e is NameExpr && this._model.VariableBindings.TryGetValue(e, out var symbol) && symbol.Type is PointerType)
      return this._b.Load(IrType.Ptr, this.SlotFor(symbol));
    if (e is PtrDerefExpr indirect)
      return this._b.Load(IrType.Ptr, this.DerefAddress(indirect));
    throw new IrLoweringException("unsupported pointer value");
  }

  /// <summary>
  /// The address of what a <c>VARPTR32</c> names: a variable, a static-array element, a record field
  /// or the place another pointer already points at. Anything else has no address to take.
  /// </summary>
  private IrValue AddressOfStorage(Expression e) {
    if (e is NameExpr && this._model.VariableBindings.TryGetValue(e, out var symbol))
      return this.SlotFor(symbol);
    if (e is CallOrIndexExpr indexed && this._model.VariableBindings.TryGetValue(indexed, out var array) && array.Type is ArrayType)
      return this.ElementAddress(indexed).Address;
    if (e is MemberExpr member)
      return this._model.VariableBindings.TryGetValue(member, out var flat)
        ? this.SlotFor(flat)                                  // Max.X where Max is not a record: one flat variable
        : this.MemberFieldAddress(member).Address;
    if (e is PtrDerefExpr deref)
      return this.DerefAddress(deref);
    throw new IrLoweringException("VARPTR32 of an expression that is not storage");
  }

  /// <summary>
  /// The address <c>@p</c> / <c>@p[i]</c> denotes: the pointer itself, or the pointer stepped by
  /// <c>i</c> whole targets. The index is ZERO-based whatever OPTION BASE says and is scaled by the
  /// target's size, which is what the direct emitter's <c>IMUL BX</c> against <c>SIZEOF(target)</c>
  /// does.
  /// </summary>
  private IrValue DerefAddress(PtrDerefExpr deref) {
    var address = this.PointerValue(deref.Pointer);
    if (deref.Index is not { } index)
      return address;
    var size = Math.Max(this._model.TypeOf(deref).Size, 1);
    var scaled = this._b.Mul(this.Coerce(this.LowerExpr(index), this._model.TypeOf(index), PbType.Long),
      new IrConstantInt(IrType.I32, size));
    return this._b.Gep(address, scaled);
  }

  #endregion

  /// <summary>
  /// PRINT, LPRINT and PRINT USING, console and file form.
  ///
  /// <para>
  /// LPRINT is the same statement with the output pointed at the printer, so it is this one
  /// bracketed by <c>rt_lprint_on</c> / <c>rt_lprint_off</c> - the direct emitter's own four MOVs,
  /// given a name because the IR cannot write them inline (DosRuntime.Printer.cs). Everything
  /// between them - items, separators, comma zones, TAB, a USING clause - is unchanged, because all
  /// of it already writes through the runtime's current-output cell.
  /// </para>
  ///
  /// <para>
  /// LPRINT to a FILE NUMBER declines. The parser accepts <c>LPRINT #1,</c> and the direct emitter
  /// resolves it by letting the file win - it points the output at the printer and then at the file,
  /// one statement wide. This path selects the file per CALL and puts the console back after each
  /// one, so the printer half would survive exactly as far as the first item and then be silently
  /// dropped. Two answers for one statement is worse than none.
  /// </para>
  /// </summary>
  private void LowerPrint(PrintStmt p) {
    if (this._module is null)
      throw new IrLoweringException("PRINT requires whole-module lowering");
    if (p.IsLPrint && p.FileNumber is not null)
      throw new IrLoweringException("LPRINT to a file number");
    var file = p.FileNumber is { } fn ? this.FileNum(fn) : null;

    if (p.IsLPrint)
      this._b.Call(IrType.Void, this.RuntimeFn("rt_lprint_on", IrType.Void));

    if (p.UsingFormat is not null)
      this.LowerPrintUsing(p, file);
    else {
      foreach (var item in p.Items) {
        if (item.Value is { } expr)
          this.LowerPrintItem(file, expr);
        if (item.Separator == PrintSeparator.Comma)
          this.EmitIo(file, "print", "comma", IrType.Void, []);   // advance to the next 14-column print zone
      }

      if (p.Items.Count == 0 || p.Items[^1].Separator == PrintSeparator.Newline)
        this.EmitIo(file, "print", "nl", IrType.Void, []);
    }

    if (p.IsLPrint)
      this._b.Call(IrType.Void, this.RuntimeFn("rt_lprint_off", IrType.Void));
  }

  /// <summary>
  /// <c>PRINT USING "fmt"; a; b; ...</c> - the format read at COMPILE time into literal runs and
  /// numeric fields (<see cref="Runtime.UsingFormat"/>), then one runtime call per piece.
  ///
  /// <para>
  /// Nothing here is inline, so the whole statement is composition, and the composition is the
  /// direct emitter's own piece for piece: literal text goes through the same <c>rt_print_str</c> a
  /// string literal in an ordinary PRINT does, and a numeric field is scaled by ten to its decimal
  /// count, rounded to a 32-bit integer and handed to <c>rt_usefmt</c> with the packed field spec.
  /// The scale-then-round is not a rendering choice: <c>rt_usefmt</c> prints DIGITS and places a
  /// point among them, so 3.14159 in <c>##.##</c> has to reach it as 314.
  /// </para>
  ///
  /// <para>
  /// The rounding is the x87's - nearest, ties to even - because the direct emitter's FISTP is, and
  /// it is spelled <see cref="IrCastOp.FPToSIRound"/> DIRECTLY rather than through
  /// <see cref="Coerce"/>. Coerce would additionally apply <c>$ERROR OVERFLOW</c>'s range trap and
  /// the BASCOM dialects' round-half-away-from-zero, neither of which a USING field goes through on
  /// the other path: a field too narrow for its value overflows the field, not the program.
  /// </para>
  ///
  /// <para>
  /// The arithmetic is done at the x87's own width whatever the value's declared type, for the
  /// reason PRINT hands floats over at eighty bits: that is where the direct emitter's value already
  /// is when it multiplies, and rounding it to sixty-four first would be a step that emitter does
  /// not take.
  /// </para>
  ///
  /// <para>
  /// What DOES not lower: a format that is not a literal (there is nothing to read at compile time,
  /// and the direct emitter refuses it too), and more values than the format has fields. A value
  /// left without a field would have to print somewhere, and dropping it silently is the one answer
  /// worse than declining. Trailing FIELDS with no value are not an error on either path - the
  /// literal text after the last filled field prints, and the format is not recycled.
  /// </para>
  ///
  /// <para>
  /// <b>The 32-bit ceiling, and where the two paths part company inside it.</b> <c>rt_usefmt</c>
  /// takes the scaled value in <c>DX:AX</c>, so a field whose value times ten to its decimal count
  /// reaches 2^31 is beyond what EITHER back end can render - <c>PRINT USING "###,###,###.##"</c>
  /// tops out at 21474836.47. Genuine PowerBASIC prints an overflowing field with a leading
  /// <c>%</c>; neither of these does, and above the ceiling they do not even agree on the wrong
  /// answer: the direct emitter scales in ASSEMBLY, so its FISTP faults and stores the x87's
  /// integer-indefinite 8000_0000h, while the scale here is an IR multiply that <c>IrConstFold</c>
  /// can reach and WRAPS. Matching it would mean folding out-of-range conversions the x87 way -
  /// which the direct emitter itself does not do for an ordinary <c>n&amp; = d#</c> store, where it
  /// folds and wraps like this. The two are inconsistent with each OTHER at the source, so there is
  /// no single rule this path could adopt that agrees with both. Lifting the ceiling is the real
  /// fix, and it is a 64-bit formatter in the runtime rather than anything here.
  /// </para>
  /// </summary>
  private void LowerPrintUsing(PrintStmt p, IrValue? file) {
    if (p.UsingFormat is not StringLiteralExpr literal)
      throw new IrLoweringException("non-literal PRINT USING format");
    var segments = Runtime.UsingFormat.Parse(literal.Value);
    var index = 0;

    foreach (var item in p.Items) {
      if (item.Value is not { } value)
        continue;                                   // a bare separator carries no value to place
      while (index < segments.Count && segments[index].Field is null)
        this.UsingLiteral(file, segments[index++].Literal!);
      if (index >= segments.Count)
        throw new IrLoweringException("more PRINT USING values than fields");
      var field = segments[index++].Field!.Value;

      // a string in a numeric field prints as itself: PB's '&' approximation, and what the direct
      // emitter does with it
      if (this._model.TypeOf(value) is StringType or FixedStringType or AsciizType) {
        this.EmitIo(file, "print", "strvar", IrType.Void, [IrType.Ptr], this.LowerStringExpr(value));
        continue;
      }

      var valueType = this._model.TypeOf(value);
      var effective = valueType is MbfType mbf ? IeeeFormOf(mbf) : valueType;
      if (effective is not ScalarType)
        throw new IrLoweringException("PRINT USING of a non-numeric, non-string value");
      var scaled = this.Coerce(this.LowerExpr(value), valueType, PbType.Ext);
      if (field.Decimals > 0)
        scaled = this._b.Binary(IrBinaryOp.FMul, scaled,
          new IrConstantFloat(IrType.F80, Math.Pow(10, field.Decimals)));
      this.EmitIo(file, "using", "field", IrType.Void, [IrType.I32, IrType.I32],
        this._b.Cast(IrCastOp.FPToSIRound, scaled, IrType.I32),
        new IrConstantInt(IrType.I32, field.Spec));
    }

    while (index < segments.Count && segments[index].Field is null)
      this.UsingLiteral(file, segments[index++].Literal!);

    if (p.Items.Count == 0 || p.Items[^1].Separator == PrintSeparator.Newline)
      this.EmitIo(file, "print", "nl", IrType.Void, []);
  }

  /// <summary>One literal run of a USING format, through the literal pool - the same call a string literal in an ordinary PRINT makes.</summary>
  private void UsingLiteral(IrValue? file, string text) {
    if (text.Length == 0)
      return;
    var bytes = System.Text.Encoding.ASCII.GetBytes(text);
    this.EmitIo(file, "print", "str", IrType.Void, [IrType.Ptr, IrType.I32],
      this._module!.AddStringConstant(bytes), new IrConstantInt(IrType.I32, bytes.Length));
  }

  /// <summary>
  /// WRITE: the items comma-separated, strings in quotes, numbers with no sign column, then a newline.
  ///
  /// <para>
  /// Every piece of this is already a runtime call the IR can make - there is no inline anything -
  /// so the whole statement is composition, and the composition is the direct emitter's own, item for
  /// item. A number goes through STR$ and then LTRIM$ because that is what strips the leading space
  /// PB's numeric formatter reserves for a sign; WRITE is the one output statement that does not want
  /// it.
  /// </para>
  /// </summary>
  private void LowerWrite(WriteStmt write) {
    if (this._module is null)
      throw new IrLoweringException("WRITE requires whole-module lowering");
    var file = write.FileNumber is { } fn ? this.FileNum(fn) : null;

    for (var i = 0; i < write.Items.Count; ++i) {
      if (i > 0)
        this.WritePunctuation(file, ",");
      var item = write.Items[i];
      if (this._model.TypeOf(item) is StringType or FixedStringType or AsciizType) {
        this.WritePunctuation(file, "\"");
        this.EmitIo(file, "print", "strvar", IrType.Void, [IrType.Ptr], this.LowerStringExpr(item));
        this.WritePunctuation(file, "\"");
        continue;
      }
      this.EmitIo(file, "print", "strvar", IrType.Void, [IrType.Ptr],
        this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_ltrim", IrType.Ptr, IrType.Ptr), this.LowerStrOf(item)));
    }

    this.EmitIo(file, "print", "nl", IrType.Void, []);
  }

  /// <summary>One of WRITE's fixed characters - the separator or a quote - through the literal pool.</summary>
  private void WritePunctuation(IrValue? file, string text) {
    var bytes = System.Text.Encoding.ASCII.GetBytes(text);
    this.EmitIo(file, "print", "str", IrType.Void, [IrType.Ptr, IrType.I32],
      this._module!.AddStringConstant(bytes), new IrConstantInt(IrType.I32, bytes.Length));
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
    if (this._model.TypeOf(expr) is StringType or FixedStringType or AsciizType) {
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
    // A float is handed to the formatter at the x87's own width whatever its declared type, and the
    // NAME picks the digit count - which is the runtime's own model: rt_print_f32 and rt_print_f64
    // share a body and differ only in the significant digits they set. Narrowing here would undo
    // exactly the precision LowerArithmetic keeps.
    var (suffix, ty) = NumericSuffix(s);
    var printedAt = s.IsFloat ? PbType.Ext : s;
    this.EmitIo(file, "print", suffix, IrType.Void, [s.IsFloat ? IrType.F80 : ty],
      this.Coerce(this.LowerExpr(expr), this._model.TypeOf(expr), printedAt));
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
    // The bare form names no variable: the record moves through the FIELD windows registered for the
    // file. Positioning is a separate call rather than an argument, because the runtime routine that
    // walks the fields takes only the file - the direct emitter seeks first for the same reason.
    if (s.Variable is null) {
      var target = this.FileNum(s.FileNumber);
      if (s.RecordNumber is { } at)
        this._b.Call(IrType.Void, this.RuntimeFn("rt_file_setpos", IrType.Void, IrType.I32, IrType.I32),
          target, this.Coerce(this.LowerExpr(at), this._model.TypeOf(at), PbType.Long));
      this._b.Call(IrType.Void,
        this.RuntimeFn(s.IsGet ? "rt_field_get" : "rt_field_put", IrType.Void, IrType.I32), target);
      return;
    }
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
      case NameExpr when this._model.VariableBindings.TryGetValue(expr, out var azs) && azs.Type is AsciizType azRead:
        return this._b.Call(IrType.Ptr, this.RuntimeFn("rt_asciiz_load", IrType.Ptr, IrType.Ptr, IrType.I32),
          this.SlotFor(azs), new IrConstantInt(IrType.I32, azRead.Length));      // the bytes BEFORE the NUL
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
      case MemberExpr am when !this._model.VariableBindings.ContainsKey(am) && this.MemberFieldAddress(am) is { Field.Type: AsciizType afz } aa:
        return this._b.Call(IrType.Ptr, this.RuntimeFn("rt_asciiz_load", IrType.Ptr, IrType.Ptr, IrType.I32),
          aa.Address, new IrConstantInt(IrType.I32, afz.Length));
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
    // The direct emitter always coerces ON GOTO/GOSUB to INTEGER before dispatch. That truncation is
    // observable: 65537& selects arm 1, not the default. Put the historical word rule in the
    // target-independent IR instead of making every back end rediscover it.
    var selector = this.Coerce(this.LowerExpr(o.Selector), this._model.TypeOf(o.Selector), PbType.Integer);
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
    // rt_str_from_fixed rather than rt_str_const, and the difference is where the bytes ARE: a
    // constant names a whole pooled literal, while a DATA item is n bytes at an OFFSET into the pool.
    // Both make a handle from n bytes and both are rt_make in the C runtime and rt_strmem on DOS, but
    // rt_str_const's argument is a global's own address, and this one's is an address computed from it.
    var handle = this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_from_fixed", IrType.Ptr, IrType.Ptr, IrType.I32), dataPtr, len);
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

  /// <summary>
  /// The element count a dynamic array's descriptor currently describes - the product of its per-
  /// dimension extents, which <c>REDIM</c> fills in and every read of the array uses. Zero for an
  /// array that has never been allocated, since the cells start zeroed.
  /// </summary>
  private IrValue DynElementCount(DynArr descriptor, int rank) {
    IrValue count = this._b.Load(IrType.I32, descriptor.Size[0]);
    for (var k = 1; k < rank; ++k)
      count = this._b.Mul(count, this._b.Load(IrType.I32, descriptor.Size[k]));
    return count;
  }

  /// <summary>
  /// An element count as a BYTE count, which is the unit every allocation entry in the family takes.
  ///
  /// <para>
  /// Bytes rather than (count, elementSize) because the element size is a COMPILE-TIME property of the
  /// source type and the DOS runtime's allocator has always taken a 32-bit byte count in <c>DX:AX</c> -
  /// so passing the pair meant asking a register-mapping table to multiply, which it cannot do. Doing
  /// the multiply here gives both back ends one shape, and it folds to a literal whenever the bounds
  /// are constant.
  /// </para>
  /// <para>
  /// The product is formed at 32 bits ON PURPOSE. A count that fits a word and an element size that
  /// fits a word can still need seventeen: <c>DIM x(1 TO 5000) AS LONG</c> is 20000 bytes and
  /// <c>DIM x(1 TO 20000) AS LONG</c> is 80000, which does not fit one. A 16-bit multiply would wrap
  /// 80000 to 14464 and allocate a quarter of the array with nothing to say so; at 32 bits the high
  /// half reaches the runtime, which refuses it as Error 7 exactly as the direct emitter does.
  /// </para>
  /// <para>
  /// The pointer entries stay COUNT-based for the opposite reason: a target pointer's size is a
  /// property of the target, not of the program, so only the runtime can know it. That is the whole
  /// difference between <c>rt_arr_alloc</c> and <c>rt_arr_alloc_ptr</c>.
  /// </para>
  /// </summary>
  private IrValue ArrayBytes(IrValue count, ArrayType arr)
    => this._b.Mul(count, new IrConstantInt(IrType.I32, Math.Max(arr.Element.Size, 1)));

  private void LowerRedim(RedimStmt r) {
    foreach (var v in r.Variables) {
      if (!this._model.RedimBindings.TryGetValue(v, out var symbol) || symbol.Type is not ArrayType { IsDynamic: true } arr)
        throw new IrLoweringException($"REDIM of non-dynamic array {v.Name}");
      if (v.ArrayBounds is not { } dims || dims.Count != arr.Rank)
        throw new IrLoweringException("REDIM rank mismatch");

      var descriptor = this.DynDescriptor(symbol, arr.Rank);
      var isString = arr.Element is StringType;
      // REDIM PRESERVE carries the old contents over, so the OLD extent has to be read out of the
      // descriptor BEFORE the new bounds overwrite it. An array that was never allocated reads zero
      // in every size cell, which is the "nothing to preserve" the direct emitter spells as a
      // segment-word test.
      var oldCount = r.Preserve ? this.DynElementCount(descriptor, arr.Rank) : null;

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

      IrValue data;
      if (r.Preserve) {                                // realloc keeps the existing prefix (mem2reg seeds the unallocated slot to null = fresh malloc)
        var old = this._b.Load(IrType.FarPtr, descriptor.Data);
        data = isString
          ? this._b.Call(IrType.FarPtr, this.RuntimeFn("rt_arr_realloc_ptr", IrType.FarPtr, IrType.FarPtr, IrType.I32, IrType.I32),
              old, oldCount!, count!)
          : this._b.Call(IrType.FarPtr, this.RuntimeFn("rt_arr_realloc", IrType.FarPtr, IrType.FarPtr, IrType.I32, IrType.I32),
              old, this.ArrayBytes(oldCount!, arr), this.ArrayBytes(count!, arr));
      } else {
        data = isString
          ? this._b.Call(IrType.FarPtr, this.RuntimeFn("rt_arr_alloc_ptr", IrType.FarPtr, IrType.I32), count!)      // count target-pointers
          : this._b.Call(IrType.FarPtr, this.RuntimeFn("rt_arr_alloc", IrType.FarPtr, IrType.I32), this.ArrayBytes(count!, arr));
      }
      this._b.Store(data, descriptor.Data);
    }
  }

  private void LowerErase(EraseStmt e) {
    foreach (var name in e.Arrays) {
      if (!this._model.VariableBindings.TryGetValue(name, out var symbol) || symbol.Type is not ArrayType arr)
        throw new IrLoweringException("ERASE of a non-array");
      if (!arr.IsDynamic) {
        // A static array is not freed - PB zeroes it where it stands, and the storage stays. The
        // direct emitter writes a REP STOSW over the word-rounded size; the portable spelling of
        // that is a memset, which the C back end renders as one and an LLVM target lowers itself.
        this._b.Call(IrType.Void,
          this.RuntimeFn("llvm.memset.p0.i32", IrType.Void, IrType.Ptr, IrType.I8, IrType.I32, IrType.I1),
          this.SlotFor(symbol), new IrConstantInt(IrType.I8, 0),
          new IrConstantInt(IrType.I32, arr.Size), new IrConstantInt(IrType.I1, 0));
        continue;
      }
      // The byte count travels with the pointer: the DOS heap is a bump allocator that can only give
      // a block back when it is the topmost one, and "is this block on top" is `offset + bytes ==
      // top`. A malloc/free runtime ignores the second argument, but the IR cannot know which kind of
      // runtime it is talking to and the size is free to compute here.
      var descriptor = this.DynDescriptor(symbol, arr.Rank);
      var count = this.DynElementCount(descriptor, arr.Rank);
      var block = this._b.Load(IrType.FarPtr, descriptor.Data);
      if (arr.Element is StringType)
        this._b.Call(IrType.Void, this.RuntimeFn("rt_arr_free_ptr", IrType.Void, IrType.FarPtr, IrType.I32), block, count);
      else
        this._b.Call(IrType.Void, this.RuntimeFn("rt_arr_free", IrType.Void, IrType.FarPtr, IrType.I32),
          block, this.ArrayBytes(count, arr));
      this._b.Store(new IrNullPtr(IrType.FarPtr), descriptor.Data);
    }
  }

  // ---- ARRAY SORT / ARRAY SCAN ---------------------------------------------

  /// <summary>
  /// The three things the sort/scan parameter block needs to know about an array: where its elements
  /// start, and the bounds the runtime turns a start index into a byte offset with.
  /// </summary>
  private readonly record struct SortArray(ArrayType Type, IrValue Data, int Lower, int Extent);

  /// <summary>
  /// The array an ARRAY SORT / ARRAY SCAN names, or a decline. Only a STATIC one-dimensional array is
  /// taken: a dynamic array's elements live in the far array heap, whose segment is a runtime cell
  /// rather than the DS or SS an IR pointer carries, so there is no descriptor this path could build
  /// for it - and a rank above one has bounds past the single pair <see cref="SortArray"/> holds.
  /// </summary>
  private SortArray SortOperand(CallOrIndexExpr array) {
    if (!this._model.VariableBindings.TryGetValue(array, out var symbol) || symbol.Type is not ArrayType arr)
      throw new IrLoweringException("ARRAY SORT/SCAN of a non-array");
    if (symbol.Storage == VariableStorage.Parameter)
      throw new IrLoweringException("ARRAY SORT/SCAN of an array parameter");
    if (arr.StaticBounds is not { } bounds)
      throw new IrLoweringException("ARRAY SORT/SCAN of a dynamic array");
    if (bounds.Count != 1)
      throw new IrLoweringException("ARRAY SORT/SCAN of a multi-dimensional array");
    return new(arr, this.SlotFor(symbol), bounds[0].Lower, bounds[0].Upper - bounds[0].Lower + 1);
  }

  /// <summary>
  /// One field of the runtime's shared ARRAY SORT/SCAN parameter block. The runtime spells the block
  /// as displacements off <c>rt_arpb</c>; each field also carries a name, and the name is what this
  /// path uses - a GEP into the block would put its address in a register, and a register holding a
  /// memory BASE is the one thing the spiller cannot move. Filling six fields that way needs six such
  /// registers at once, which is more than the machine has, and the whole function then declines at
  /// allocation with nothing in the machine IR looking wrong.
  /// </summary>
  private void StoreArpb(string field, IrValue value)
    => this._b.Store(value, this.ErrorCell(field, IrType.I16));

  /// <summary>
  /// Fills a runtime array descriptor and answers its address - the one part of ARRAY SORT the IR
  /// cannot say for itself. A descriptor opens with the SEGMENT its elements live in, and a segment
  /// register is not a value the IR can name; it must also live where DS reaches it, which a frame
  /// object of a routed function does not promise. So the near address, the bounds and the element
  /// size go to a runtime routine which supplies the segment and the storage, exactly as CSRLIN and
  /// the bare DEF SEG became routines for the same reason.
  /// </summary>
  private IrValue DescriptorOf(SortArray array, bool forTagArray)
    => this._b.Call(IrType.I16,
      this.RuntimeFn(forTagArray ? "rt_arr_tagdesc" : "rt_arr_desc", IrType.I16, IrType.Ptr, IrType.I16, IrType.I16, IrType.I16),
      array.Data,
      new IrConstantInt(IrType.I16, array.Lower),
      new IrConstantInt(IrType.I16, Math.Max(array.Type.Element.Size, 1)),
      new IrConstantInt(IrType.I16, array.Extent));

  /// <summary>
  /// Start index (+2) and element count (+4). Both have defaults, and both defaults are read out of
  /// the DESCRIPTOR by the direct emitter - the array's own lower bound, and "everything from the
  /// start element on". A static array knows both at compile time, so they are constants here rather
  /// than two loads through the descriptor pointer.
  /// </summary>
  private void StoreStartAndCount(CallOrIndexExpr array, SortArray shape, Expression? count) {
    var start = array.Arguments.Count == 1
      ? this.Coerce(this.LowerExpr(array.Arguments[0]), this._model.TypeOf(array.Arguments[0]), PbType.Integer)
      : new IrConstantInt(IrType.I16, shape.Lower);
    this.StoreArpb("rt_arpb_start", start);
    this.StoreArpb("rt_arpb_count", count is null
      ? this._b.Sub(new IrConstantInt(IrType.I16, shape.Lower + shape.Extent), start)
      : this.Coerce(this.LowerExpr(count), this._model.TypeOf(count), PbType.Integer));
  }

  /// <summary>
  /// (kind code, copy size, x87 load width) for a non-string element - the direct emitter's own table
  /// in CodeGenerator.Vendor. An unsigned integer loads through the next WIDER signed FILD so it stays
  /// positive, which the zero-padded staging cell is what makes sound; a float loads at its own width.
  /// </summary>
  private static (int Kind, int Size, int Load)? NumericElement(PbType element) => element switch {
    ScalarType { IsFloat: false, Signed: true } s => (0, s.ByteSize, s.ByteSize),
    ScalarType { IsFloat: false, Signed: false, ByteSize: var b } => (0, b, b == 4 ? 8 : b == 2 ? 4 : 2),
    ScalarType { IsFloat: true } s => (2, s.ByteSize, s.ByteSize),
    _ => null,
  };

  /// <summary>The relop encoding rt_scannum and rt_scanstr share (rt_num_relop, and the rt_arpb flags high byte).</summary>
  private static int ScanRelop(CaseComparison op) => op switch {
    CaseComparison.Equal => 0,
    CaseComparison.NotEqual => 1,
    CaseComparison.Less => 2,
    CaseComparison.LessEqual => 3,
    CaseComparison.Greater => 4,
    _ => 5,
  };

  /// <summary>The numeric parameter block: descriptor, element shape, range, and the optional TAGARRAY.</summary>
  private void StoreNumericHeader(CallOrIndexExpr array, SortArray shape, Expression? count, Expression? fromPos, CallOrIndexExpr? tagArray) {
    if (NumericElement(shape.Type.Element) is not { } element)
      throw new IrLoweringException($"ARRAY SORT/SCAN over {shape.Type.Element} elements");
    if (fromPos is not null)
      throw new IrLoweringException("FROM/TO range on a non-string ARRAY SORT/SCAN");   // a character range, which a number has none of
    var (kind, size, load) = element;

    this.StoreArpb("rt_arpb", this.DescriptorOf(shape, forTagArray: false));
    this._b.Store(new IrConstantInt(IrType.I8, kind), this.ErrorCell("rt_num_kind", IrType.I8));
    this._b.Store(new IrConstantInt(IrType.I8, size), this.ErrorCell("rt_num_size", IrType.I8));
    this._b.Store(new IrConstantInt(IrType.I8, load), this.ErrorCell("rt_num_load", IrType.I8));
    this.StoreStartAndCount(array, shape, count);

    if (tagArray is null) {
      this._b.Store(new IrConstantInt(IrType.I16, 0), this.ErrorCell("rt_num_tagdesc", IrType.I16));
      return;
    }
    // the tag array shares the KEY's start index but has its own lower bound and element size, which
    // is why it needs a descriptor of its own rather than an offset off the key's
    var tagShape = this.SortOperand(tagArray);
    this._b.Store(this.DescriptorOf(tagShape, forTagArray: true), this.ErrorCell("rt_num_tagdesc", IrType.I16));
    this._b.Store(new IrConstantInt(IrType.I16, Math.Max(tagShape.Type.Element.Size, 1)),
      this.ErrorCell("rt_num_tagsize", IrType.I16));
  }

  /// <summary>The string parameter block: descriptor, range, the FROM/TO character window and the collate table.</summary>
  private void StoreStringHeader(CallOrIndexExpr array, SortArray shape, Expression? count, Expression? fromPos, Expression? toPos) {
    this.StoreArpb("rt_arpb", this.DescriptorOf(shape, forTagArray: false));
    this.StoreStartAndCount(array, shape, count);
    if (fromPos is null) {
      this.StoreArpb("rt_arpb_from", new IrConstantInt(IrType.I16, 1));
      this.StoreArpb("rt_arpb_to", new IrConstantInt(IrType.I16, 0x7FFF));   // the whole string, clamped by the runtime
    } else {
      this.StoreArpb("rt_arpb_from", this.Coerce(this.LowerExpr(fromPos), this._model.TypeOf(fromPos), PbType.Integer));
      this.StoreArpb("rt_arpb_to", this.Coerce(this.LowerExpr(toPos!), this._model.TypeOf(toPos!), PbType.Integer));
    }
    this.StoreArpb("rt_arpb_collate", new IrConstantInt(IrType.I16, 0));           // no COLLATE table - the form declines above
  }

  /// <summary>
  /// ARRAY SORT arr([start]) [FOR n] [, DESCEND] [, TAGARRAY t()] - an insertion sort in the runtime,
  /// driven entirely from the parameter block. COLLATE declines: its table is an owned handle the
  /// block would have to hold across the call and release after, and no corpus program asks for one.
  /// </summary>
  private void LowerArraySort(ArraySortStmt sort) {
    if (this._module is null)
      throw new IrLoweringException("ARRAY SORT requires whole-module lowering");
    if (sort.Collate is not null)
      throw new IrLoweringException("COLLATE on an ARRAY SORT");
    var shape = this.SortOperand(sort.Array);
    if (shape.Type.Element is StringType) {
      if (sort.TagArray is not null)
        throw new IrLoweringException("ARRAY SORT TAGARRAY on a string array");
      this.StoreStringHeader(sort.Array, shape, sort.Count, sort.FromPos, sort.ToPos);
      this.StoreArpb("rt_arpb_flags", new IrConstantInt(IrType.I16, sort.Descend ? 1 : 0));   // flags: bit0 = descending
      this._b.Call(IrType.Void, this.RuntimeFn("rt_array_sort_str", IrType.Void));
      return;
    }
    this.StoreNumericHeader(sort.Array, shape, sort.Count, sort.FromPos, sort.TagArray);
    this._b.Store(new IrConstantInt(IrType.I8, sort.Descend ? 1 : 0), this.ErrorCell("rt_num_desc", IrType.I8));
    this._b.Call(IrType.Void, this.RuntimeFn("rt_array_sort_num", IrType.Void));
  }

  /// <summary>
  /// ARRAY SCAN arr([start]) [FOR n] [, FROM x TO y], relop expr, TO var - the 1-based position of the
  /// first element the relation holds for, relative to the start element, or zero.
  /// </summary>
  private void LowerArrayScan(ArrayScanStmt scan) {
    if (this._module is null)
      throw new IrLoweringException("ARRAY SCAN requires whole-module lowering");
    if (scan.Collate is not null)
      throw new IrLoweringException("COLLATE on an ARRAY SCAN");
    var shape = this.SortOperand(scan.Array);

    IrValue found;
    if (shape.Type.Element is StringType) {
      this.StoreStringHeader(scan.Array, shape, scan.Count, scan.FromPos, scan.ToPos);
      // flags: bit1 says the FROM/TO window clamps the ELEMENT side only, and the relop rides in the
      // high byte, which is where rt_scanstr reads it from
      this.StoreArpb("rt_arpb_flags", new IrConstantInt(IrType.I16, 2 | (ScanRelop(scan.Op) << 8)));
      var match = this.LowerStringExpr(scan.Match);
      this.StoreArpb("rt_arpb_match", match);
      found = this._b.Call(IrType.I16, this.RuntimeFn("rt_array_scan_str", IrType.I16));
      // the comparison does not consume its operands, so the match handle is still this statement's
      // to release - and the answer is already in hand when it goes
      this._b.Call(IrType.Void, this.RuntimeFn("rt_str_free", IrType.Void, IrType.Ptr), match);
    } else {
      this.StoreNumericHeader(scan.Array, shape, scan.Count, scan.FromPos, null);
      this._b.Store(new IrConstantInt(IrType.I8, ScanRelop(scan.Op)), this.ErrorCell("rt_num_relop", IrType.I8));
      // the match is compared as an ELEMENT, so it is coerced to the element type and stored as its
      // raw bytes - the staging cell reads it back with the same FILD/FLD the elements go through
      this._b.Store(this.Coerce(this.LowerExpr(scan.Match), this._model.TypeOf(scan.Match), shape.Type.Element),
        this.ErrorCell("rt_num_match", MapType(shape.Type.Element)));
      found = this._b.Call(IrType.I16, this.RuntimeFn("rt_array_scan_num", IrType.I16));
    }

    var (address, targetType) = this.LValue(scan.Target);
    this._b.Store(this.Coerce(found, PbType.Integer, targetType), address);
  }

  /// <summary>
  /// <c>FIELD #n, w AS a$, ...</c> - each name becomes a WINDOW on the file's record buffer rather
  /// than a variable of its own. The runtime keeps the association in a table: the file, the width,
  /// and the address of the variable's handle cell, so that a later bare <c>GET #n</c> can scatter a
  /// record through the names and a bare <c>PUT #n</c> gather one back out of them.
  ///
  /// <para>
  /// Registering a field also ASSIGNS the variable a fresh blank string of the field's width, which
  /// is what makes it a window at all - LSET justifies within the length that is already there, so a
  /// zero-length field would take no characters. That happens inside <c>rt_fldadd</c>, which is why
  /// nothing here writes to the variable.
  /// </para>
  /// </summary>
  private void LowerField(FieldStmt field) {
    foreach (var (width, target) in field.Fields) {
      if (this._model.TypeOf(target) is not StringType)
        throw new IrLoweringException("FIELD target that is not a dynamic string");
      this._b.Call(IrType.Void,
        this.RuntimeFn("rt_field_add", IrType.Void, IrType.I32, IrType.I32, IrType.Ptr),
        this.FileNum(field.FileNumber),
        this.Coerce(this.LowerExpr(width), this._model.TypeOf(width), PbType.Long),
        this.StringTargetAddress(target));
    }
  }

  /// <summary>
  /// <c>LSET a$ = v$</c> / <c>RSET a$ = v$</c> into a dynamic string: the value justified IN PLACE
  /// within the length the target already has, blank-padded to fill it. That is not an assignment -
  /// the target keeps its handle and its length, which is the whole point for a FIELD variable, whose
  /// length is the width of its window on the record.
  ///
  /// <para>
  /// The value is lowered before the target's cell is read, matching the direct emitter's order: it
  /// evaluates the right-hand side and pushes it before touching the place.
  /// </para>
  /// </summary>
  private void LowerLsetRset(LsetRsetStmt ls) {
    if (this._model.TypeOf(ls.Target) is not StringType)
      throw new IrLoweringException($"{(ls.IsLeft ? "LSET" : "RSET")} into a {this._model.TypeOf(ls.Target)}");
    var value = this.LowerStringExpr(ls.Value);
    var target = this._b.Load(IrType.Ptr, this.StringTargetAddress(ls.Target));
    this._b.Call(IrType.Void,
      this.RuntimeFn("rt_str_justify", IrType.Void, IrType.Ptr, IrType.Ptr, IrType.I16),
      target, value, new IrConstantInt(IrType.I16, ls.IsLeft ? 0 : 1));
  }

  /// <summary>
  /// <c>CHAIN file$</c> - the COMMON values into the handoff file, then hand the machine to the named
  /// image. <c>RUN file$</c> is the same transfer with no handoff, which is what
  /// <see cref="ChainStmt.IsRun"/> selects.
  ///
  /// <para>
  /// <c>rt_chainexec</c> never comes back: it EXECs the child and leaves with the child's exit code.
  /// Nothing is emitted to say so, and nothing needs to be - the statements after a CHAIN lower into
  /// code that is simply never reached, exactly as the direct emitter leaves them.
  /// </para>
  /// </summary>
  private void LowerChain(ChainStmt chain) {
    if (this._module is null)
      throw new IrLoweringException("CHAIN requires whole-module lowering");
    var commons = chain.IsRun ? [] : CommonVariables(this._model);
    if (commons.Count > 0) {
      this._b.Call(IrType.Void, this.RuntimeFn("rt_chain_open_write", IrType.Void));
      foreach (var symbol in commons)
        this.LowerChainTransfer(symbol, writing: true);
      // AL = 0: close the file and KEEP it - it is what the next image reads
      this._b.Call(IrType.Void, this.RuntimeFn("rt_chain_close", IrType.Void, IrType.I16),
        new IrConstantInt(IrType.I16, 0));
    }
    this._b.Call(IrType.Void, this.RuntimeFn("rt_chain_exec", IrType.Void, IrType.Ptr),
      this.LowerStringExpr(chain.Target));
  }

  /// <summary>
  /// The chained-TO side, at the head of the module body: absorb PBCHAIN.$$$ into the COMMON cells
  /// and delete it. <c>rt_chopenr</c> answers 0 when there is no handoff, which is the ordinary case
  /// of a program started from the command line - so the whole load sits behind that test.
  /// </summary>
  private void LowerChainCommonLoad() {
    if (!this._isMain || this._module is null)
      return;
    var commons = CommonVariables(this._model);
    if (commons.Count == 0)
      return;

    var present = this._b.Call(IrType.I16, this.RuntimeFn("rt_chain_open_read", IrType.I16));
    var load = this.NewBlock("chain.load");
    var after = this.NewBlock("chain.ready");
    this._b.CondBr(this._b.Cmp(IrCmpPred.Ne, present, new IrConstantInt(IrType.I16, 0)), load, after);

    this._b.Position(load);
    foreach (var symbol in commons)
      this.LowerChainTransfer(symbol, writing: false);
    // AL = 1: close AND unlink, so a second run without a CHAIN in front of it starts clean
    this._b.Call(IrType.Void, this.RuntimeFn("rt_chain_close", IrType.Void, IrType.I16),
      new IrConstantInt(IrType.I16, 1));
    this._b.Br(after);
    this._b.Position(after);
  }

  /// <summary>
  /// One COMMON variable through the handoff, in whichever direction. A string travels as its length
  /// word followed by its bytes, which only the runtime can take apart; everything else travels as
  /// the raw bytes of its cell.
  ///
  /// <para>
  /// The string handle is passed WITHOUT a borrowing copy, unlike every other runtime string
  /// argument: <c>rt_chwstr</c> is documented to keep what it is given, so duplicating for it would
  /// leak a handle per CHAIN. On the read side the returned handle is stored straight into the cell
  /// rather than assigned through the string manager, because this runs before the first statement
  /// of the image - the cell is still the zero its data segment was created with, so there is no
  /// previous handle to release.
  /// </para>
  /// </summary>
  private void LowerChainTransfer(VariableSymbol symbol, bool writing) {
    if (symbol.IsArray)
      throw new IrLoweringException("COMMON array across CHAIN");
    var slot = this.SlotFor(symbol);
    if (symbol.Type is StringType) {
      if (writing)
        this._b.Call(IrType.Void, this.RuntimeFn("rt_chain_write_str", IrType.Void, IrType.Ptr),
          this._b.Load(IrType.Ptr, slot));
      else
        this._b.Store(this._b.Call(IrType.Ptr, this.RuntimeFn("rt_chain_read_str", IrType.Ptr)), slot);
      return;
    }
    if (symbol.Type is not ScalarType scalar)
      throw new IrLoweringException($"COMMON {symbol.Type} across CHAIN");
    var bytes = new IrConstantInt(IrType.I32, Math.Max(scalar.Size, 1));
    this._b.Call(IrType.Void,
      this.RuntimeFn(writing ? "rt_chain_write" : "rt_chain_read", IrType.Void, IrType.Ptr, IrType.I32),
      slot, bytes);
  }

  private void LowerDim(DimStmt d) {
    // DIM AT and the memory-model classes stay OUT, and the reason is worth stating because letting
    // them through costs nothing at the lowering and everything afterwards.
    //
    // What HUGE, VIRTUAL/EMS/XMS and ABSOLUTE have in common is that an element of one is not at a
    // near address. The direct emitter reaches each of them through ES with a segment worked out per
    // access - base + (byteOffset >> 4) for HUGE, the EMS page frame after mapping the right logical
    // page for VIRTUAL, and the AT segment itself for ABSOLUTE - and the machine IR has no segment
    // to put that in: MOperand.Memory is a base, an index, a scale and a displacement, all implicitly
    // DS or SS. So the DECLARATION is the only part of the statement the IR could carry, and the
    // declaration is the part that does not matter.
    //
    // Measured rather than assumed. With this refusal removed, DIM DYNAMIC ab(0 TO 7) AT &HB800
    // lowers to an ordinary dynamic-array descriptor, the AT segment never appears in the IR at all,
    // and the function SELECTS - so ab(0) = n would have been emitted as a store through an
    // uninitialised near pointer instead of a write to video memory. It assembles, it routes, and it
    // is wrong, which is the worst of the three outcomes available here.
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
    // BASICA/GW-BASIC can retain text whose syntax is checked only if execution reaches it. This is
    // a correctness fold, not an optimization: eliminate a constant arm before lowering so a dead
    // DeferredSourceStmt never forces the IR/x86-16 route to decline. If any tested condition is not
    // constant, ordinary lowering reaches the deferred node and safely declines.
    if (ContainsDeferredSource(stmt) && this._folder.TryFold(stmt.Condition) is { Integer: { } c }) {
      if (c != 0) {
        this.LowerStatements(stmt.Then);
        return;
      }
      if (stmt.ElseIfs.Count > 0) {
        var (firstCondition, firstBody) = stmt.ElseIfs[0];
        this.LowerIf(stmt with {
          Condition = firstCondition,
          Then = firstBody,
          ElseIfs = stmt.ElseIfs.Skip(1).ToList(),
        });
        return;
      }
      if (stmt.Else is { } selectedElse)
        this.LowerStatements(selectedElse);
      return;
    }

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

  private static bool ContainsDeferredSource(IfStmt statement) =>
    statement.Then.Any(ContainsDeferredSource)
    || statement.ElseIfs.Any(e => e.Body.Any(ContainsDeferredSource))
    || statement.Else?.Any(ContainsDeferredSource) == true;

  private static bool ContainsDeferredSource(Statement statement) => statement switch {
    DeferredSourceStmt => true,
    IfStmt nested => ContainsDeferredSource(nested),
    _ => false,
  };

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
    if (!signed && constStep is < 0)
      constStep = TruncateUnsignedConstant(constStep.Value, ty.Bits);
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
      var ascending = !signed || cs >= 0;
      var pred = ascending ? (signed ? IrCmpPred.Sle : IrCmpPred.Ule) : IrCmpPred.Sge;
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
    double? constStep = f.Step is null
      ? 1
      : this._folder.TryFold(f.Step) is { IsNumeric: true } folded ? folded.AsFloat : null;
    // The value still goes through ordinary expression lowering even when only its sign is needed at
    // run time. In particular, an unsuffixed 0.3 is a SINGLE literal before it widens to a DOUBLE
    // counter; constructing a raw f64 constant from ConstantFolder's host double loses that boundary.
    var stepValue = f.Step is null
      ? (IrValue)new IrConstantFloat(ty, 1)
      : this.Coerce(this.LowerExpr(f.Step), this._model.TypeOf(f.Step), symbol.Type);

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
    this._b.Store(this._b.Binary(IrBinaryOp.FAdd, iv, stepValue), slot);
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
    if (this._folder.TryFold(step) is { Integer: { } n })
      return n;
    return null;
  }

  private static long TruncateUnsignedConstant(long value, int bits)
    => bits >= 64 ? value : value & ((1L << bits) - 1);

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
      case FloatLiteralExpr lit: {
        var type = this._model.TypeOf(lit);
        // The parser carries every decimal in a host double, but an unsuffixed PB literal is a
        // SINGLE. Quantize at the source boundary before a later FPExt can make the wider container
        // preserve bits the original literal never had. This mirrors the direct emitter exactly.
        var value = type is ScalarType { Kind: ScalarKind.Single } ? (float)lit.Value : lit.Value;
        return new IrConstantFloat(MapType(type), value);
      }
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
      // @p / @p[i] as a value. A record target has no single value to load - only its fields do, and
      // those arrive here as a MemberExpr - so it declines rather than reading the first word of one.
      case PtrDerefExpr deref when this._model.TypeOf(deref) is ScalarType target:
        return this._b.Load(MapType(target), this.DerefAddress(deref));
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
        ?? this.LowerNullaryIntrinsicName(name.Name)
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
  /// <summary>
  /// An intrinsic that takes no arguments and is written without parentheses. The binder does not
  /// turn a bare name into a call, so it reaches the lowering as an unbound name - the same route
  /// the error pseudo-variables take, and for the same reason.
  /// </summary>
  private IrValue? LowerNullaryIntrinsicName(string name) => name.ToUpperInvariant() switch {
    // "FREEFILE: no arguments -> AX = the lowest unused file number" - it raises an I/O error itself
    // when all fifteen are taken, so there is nothing to check here
    "FREEFILE" => this._b.Call(IrType.I16, this.RuntimeFn("rt_freefile", IrType.I16)),
    "CSRLIN" => this._b.Call(IrType.I16, this.RuntimeFn("rt_csrlin", IrType.I16)),
    "CONSIN" => this._b.Call(IrType.I16, this.RuntimeFn("rt_consin", IrType.I16)),
    "CONSOUT" => this._b.Call(IrType.I16, this.RuntimeFn("rt_consout", IrType.I16)),
    _ => null,
  };

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
    // VERIFY(s$, set$) / VERIFY(start%, s$, set$): the first character NOT in the set. The set is the
    // last argument, written plainly - VERIFY is a set scan by definition and needs no ANY to say so
    if (name.Equals("VERIFY", StringComparison.OrdinalIgnoreCase) && call.Arguments.Count is 2 or 3)
      return this.Coerce(this.LowerScanSet(call, call.Arguments[^1], nonMember: true),
        PbType.Long, this._model.TypeOf(call));
    // TALLY(main$, match$) / TALLY(main$, ANY set$): how many times the match occurs
    if (name.Equals("TALLY", StringComparison.OrdinalIgnoreCase) && call.Arguments.Count == 2) {
      var tallyMain = this.LowerStringExpr(call.Arguments[0]);            // main first, as the direct emitter evaluates it
      var (tallyMatch, tallyIsSet) = this.MatchOperand(call.Arguments[1]);
      return this.Coerce(this._b.Call(IrType.I32,
        this.RuntimeFn(tallyIsSet ? "rt_str_tally_any" : "rt_str_tally", IrType.I32, IrType.Ptr, IrType.Ptr),
        tallyMain, tallyMatch), PbType.Long, this._model.TypeOf(call));
    }
    if (name.Equals("LBOUND", StringComparison.OrdinalIgnoreCase) || name.Equals("UBOUND", StringComparison.OrdinalIgnoreCase))
      return this.LowerArrayBound(call, name.Equals("UBOUND", StringComparison.OrdinalIgnoreCase));
    // MIN/MAX take two to sixteen arguments, so they are answered before the single-argument check
    // below. The string spellings MIN$/MAX$ are not these - they go through the string path.
    if (name.TrimEnd('%', '&', '!', '#', '?').ToUpperInvariant() is "MIN" or "MAX")
      return this.LowerMinMax(call, name.StartsWith("MAX", StringComparison.OrdinalIgnoreCase));
    // ASC takes one argument or two - ASC(s$, i) is the i-th character - so it is answered before
    // the single-argument check, the same way LBOUND and MIN/MAX are above.
    if (name.ToUpperInvariant() is "ASC" or "ASCII")
      return this.LowerAsc(call);
    // RND(a, z) is a LONG in [a, z], a different routine from the bare RND's fraction - answered
    // here because it takes two arguments and the check below allows one.
    if (name.Equals("RND", StringComparison.OrdinalIgnoreCase) && call.Arguments.Count == 2)
      return this.Coerce(
        this._b.Call(IrType.I32, this.RuntimeFn("rt_rnd_range", IrType.I32, IrType.I32, IrType.I32),
          this.Coerce(this.LowerExpr(call.Arguments[0]), this._model.TypeOf(call.Arguments[0]), PbType.Long),
          this.Coerce(this.LowerExpr(call.Arguments[1]), this._model.TypeOf(call.Arguments[1]), PbType.Long)),
        PbType.Long, this._model.TypeOf(call));
    // the CV family takes an optional starting offset, so it is answered here for the same reason
    if (name.ToUpperInvariant() is "CVI" or "CVBYT" or "CVWRD" or "CVL" or "CVDWD" or "CVS" or "CVD" or "CVE"
        && call.Arguments.Count == 2)
      return name.ToUpperInvariant() switch {
        "CVI" => this.LowerCv(call, "rt_str_cvi", IrType.I16, 2),
        "CVBYT" => this.LowerCv(call, "rt_str_cvbyt", IrType.U16, 1),
        "CVWRD" => this.LowerCv(call, "rt_str_cvwrd", IrType.U16, 2),
        "CVL" => this.LowerCv(call, "rt_str_cvl", IrType.I32, 4),
        "CVDWD" => this.LowerCv(call, "rt_str_cvdwd", IrType.U32, 4),
        "CVS" => this.LowerCv(call, "rt_str_cvs", IrType.F32, 4),
        "CVD" => this.LowerCv(call, "rt_str_cvd", IrType.F64, 8),
        _ => this.LowerCv(call, "rt_str_cve", IrType.F80, 8),
      };
    // PEEK takes one argument or two - PEEK(offset) and the pb36 PEEK(seg:offset) - so it is answered
    // ahead of the one-argument guard rather than inside it
    if (name.Equals("PEEK", StringComparison.OrdinalIgnoreCase))
      return this.LowerPeek(call, "rt_peek", IrType.I16);
    if (name.Equals("PEEKI", StringComparison.OrdinalIgnoreCase))
      return this.LowerPeek(call, "rt_peeki", IrType.I16);
    if (name.Equals("PEEKL", StringComparison.OrdinalIgnoreCase))
      return this.LowerPeek(call, "rt_peekl", IrType.I32);
    if (name.Equals("BIT", StringComparison.OrdinalIgnoreCase) && call.Arguments.Count == 2)
      return this.LowerBit(call);
    // VARSEG / STRSEG / CODESEG: a segment, not a value read out of the variable - the operand is
    // NEVER evaluated, which is the point of asking. STRSEG is the string heap's own cell; the other
    // two are registers the IR cannot name and so are answered by a routine.
    if (name.Equals("VARSEG", StringComparison.OrdinalIgnoreCase) && call.Arguments.Count == 1)
      return this.Coerce(this._b.Call(IrType.I16, this.RuntimeFn("rt_varseg", IrType.I16)),
        PbType.Integer, this._model.TypeOf(call));
    if (name.Equals("CODESEG", StringComparison.OrdinalIgnoreCase) && call.Arguments.Count == 1)
      return this.Coerce(this._b.Call(IrType.I16, this.RuntimeFn("rt_codeseg", IrType.I16)),
        PbType.Integer, this._model.TypeOf(call));
    if (name.Equals("STRSEG", StringComparison.OrdinalIgnoreCase) && call.Arguments.Count == 1)
      return this.Coerce(this._b.Load(IrType.I16, this.ErrorCell("rt_strseg", IrType.I16)),
        PbType.Integer, this._model.TypeOf(call));
    if (name.Equals("REG", StringComparison.OrdinalIgnoreCase) && call.Arguments.Count == 1)
      return this._b.Call(IrType.I16, this.RuntimeFn("rt_reg_get", IrType.I16, IrType.I16),
        this.Coerce(this.LowerExpr(call.Arguments[0]), this._model.TypeOf(call.Arguments[0]), PbType.Integer));
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
      "CVI" => this.LowerCv(call, "rt_str_cvi", IrType.I16, 2),
      "CVBYT" => this.LowerCv(call, "rt_str_cvbyt", IrType.U16, 1),
      "CVWRD" => this.LowerCv(call, "rt_str_cvwrd", IrType.U16, 2),
      "CVL" => this.LowerCv(call, "rt_str_cvl", IrType.I32, 4),
      "CVDWD" => this.LowerCv(call, "rt_str_cvdwd", IrType.U32, 4),
      "CVS" => this.LowerCv(call, "rt_str_cvs", IrType.F32, 4),
      "CVD" => this.LowerCv(call, "rt_str_cvd", IrType.F64, 8),
      "CVE" => this.LowerCv(call, "rt_str_cve", IrType.F80, 8),
      "POS" => this.LowerPos(call),
      // RND and RND(n): the next value in [0, 1). A reseed argument is EVALUATED and then dropped,
      // which is what the direct emitter does with it - the reseed semantics are not modelled on
      // either path, and evaluating it keeps any side effect it carries.
      "RND" => this.LowerRnd(call),
      // SIZEOF is answered at COMPILE time from the argument's type - the operand is never
      // evaluated, which is the whole point of asking. A dynamic string reports the 2 bytes of its
      // handle rather than its contents, and a zero-size type still reports 1, both of which are the
      // direct emitter's own answers.
      "SIZEOF" => this.Coerce(
        new IrConstantInt(IrType.I16, Math.Max(this._model.TypeOf(call.Arguments[0]).Size, 1)),
        PbType.Integer, this._model.TypeOf(call)),
      "FREEFILE" => this._b.Call(IrType.I16, this.RuntimeFn("rt_freefile", IrType.I16)),
      "CSRLIN" => this._b.Call(IrType.I16, this.RuntimeFn("rt_csrlin", IrType.I16)),
      "CONSIN" => this._b.Call(IrType.I16, this.RuntimeFn("rt_consin", IrType.I16)),
      "CONSOUT" => this._b.Call(IrType.I16, this.RuntimeFn("rt_consout", IrType.I16)),
      // LOF(n) is the file's length and SEEK(n)/LOC(n) the current position - all LONG, all reached
      // by the file number alone. SEEK and LOC share a routine: PB reports the same number for a
      // sequential file, and the direct emitter calls rt_fpos for both.
      "LOF" => this.Coerce(
        this._b.Call(IrType.I32, this.RuntimeFn("rt_file_length", IrType.I32, IrType.I32),
          this.FileNum(call.Arguments[0])), PbType.Long, this._model.TypeOf(call)),
      "SEEK" or "LOC" => this.Coerce(
        this._b.Call(IrType.I32, this.RuntimeFn("rt_file_pos", IrType.I32, IrType.I32),
          this.FileNum(call.Arguments[0])), PbType.Long, this._model.TypeOf(call)),
      // EOF(n): the file number in, PB's -1/0 truth out - the runtime answers in the same shape the
      // direct emitter's callers expect, so there is nothing to normalise here
      "EOF" => this._b.Call(IrType.I16, this.RuntimeFn("rt_eof", IrType.I16, IrType.I16),
        this.Coerce(this.LowerExpr(call.Arguments[0]), this._model.TypeOf(call.Arguments[0]), PbType.Integer)),
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
  private IrValue LowerRnd(CallOrIndexExpr call) {
    foreach (var argument in call.Arguments)
      this.LowerExpr(argument);                       // evaluated for its effects, then dropped
    return this.Coerce(this._b.Call(IrType.F64, this.RuntimeFn("rt_rnd", IrType.F64)),
      PbType.Double, this._model.TypeOf(call));
  }

  private IrValue LowerPos(CallOrIndexExpr call) {
    if (this._module is null)
      throw new IrLoweringException("POS requires whole-module lowering");
    foreach (var argument in call.Arguments)
      this.LowerExpr(argument);
    var column = this._module.FindGlobal("rt_col")
      ?? this._module.AddGlobal(new IrGlobalVariable("rt_col", IrType.I16) { IsZeroInitialized = true });
    return this._b.Add(this._b.Load(IrType.I16, column), new IrConstantInt(IrType.I16, 1));
  }

  /// <summary>
  /// PEEK(offset) or PEEK(seg, offset): the byte at that address in DEF SEG's segment, zero-extended.
  ///
  /// <para>
  /// Only the byte-wide form lowers. PEEK's 2- and 4-byte relatives read a word and a dword through
  /// the same segment override and would each need their own routine; naming them here without
  /// providing one would turn a decline into a wrong answer.
  /// </para>
  /// </summary>
  private IrValue LowerPeek(CallOrIndexExpr call, string routine, IrType answer) {
    if (call.Arguments.Count is not (1 or 2))
      throw new IrLoweringException("intrinsic PEEK takes one or two arguments");
    var offset = call.Arguments[^1];
    if (call.Arguments.Count == 2)
      this.SetDefaultSegment(call.Arguments[0]);
    var value = this._b.Call(answer, this.RuntimeFn(routine, answer, IrType.I16),
      this.Coerce(this.LowerExpr(offset), this._model.TypeOf(offset), PbType.Integer));
    return this.Coerce(value, answer.Bits == 32 ? PbType.Long : PbType.Integer, this._model.TypeOf(call));
  }

  /// <summary>
  /// BIT(value, n): bit <c>n</c> of <c>value</c>, as 0 or 1.
  ///
  /// <para>
  /// The value is widened to a LONG first and the shift is LOGICAL, which together are what make
  /// BIT(x, 31) of a negative number answer 1 rather than smearing the sign down. That is the direct
  /// emitter's own shape - SHR DX / RCR AX around a loop, then AND AX, 1 - said in one operation
  /// instead of a loop.
  /// </para>
  /// <para>
  /// A shift count of 32 or more is answered ZERO rather than left to the back end. The emitter's
  /// loop shifts zeros in and lands on nothing; <c>lshr</c> by the width or beyond has no defined
  /// answer, so the two would agree only by luck. The guard folds away whenever the count is a
  /// literal, which it nearly always is.
  /// </para>
  /// </summary>
  private IrValue LowerBit(CallOrIndexExpr call) {
    var value = this.Coerce(this.LowerExpr(call.Arguments[0]), this._model.TypeOf(call.Arguments[0]), PbType.Long);
    var index = this.Coerce(this.LowerExpr(call.Arguments[1]), this._model.TypeOf(call.Arguments[1]), PbType.Long);
    var bit = this._b.Binary(IrBinaryOp.And,
      this._b.Binary(IrBinaryOp.LShr, value, index), new IrConstantInt(IrType.I32, 1));
    var inRange = this._b.Cmp(IrCmpPred.Ult, index, new IrConstantInt(IrType.I32, 32));
    return this.Coerce(this._b.Select(inRange, bit, new IrConstantInt(IrType.I32, 0)),
      PbType.Long, this._model.TypeOf(call));
  }

  /// <summary>
  /// POKE: the low byte of <paramref name="value"/> written at <paramref name="address"/>.
  ///
  /// <para>
  /// Only the BYTE form. The parser accepts POKEI and POKEL, but the binder does not bind them - it
  /// reports "unknown SUB POKEI" - so neither reaches either back end, and lowering them would be
  /// writing code for a statement no program can contain. PEEK's wider relatives ARE bound and do
  /// lower, which is why the family is lopsided.
  /// </para>
  /// </summary>
  private void LowerPoke(Expression address, Expression value)
    => this._b.Call(IrType.Void, this.RuntimeFn("rt_poke", IrType.Void, IrType.I16, IrType.I16),
        this.Coerce(this.LowerExpr(address), this._model.TypeOf(address), PbType.Integer),
        this.Coerce(this.LowerExpr(value), this._model.TypeOf(value), PbType.Integer));

  /// <summary>Stores a segment into the <c>rt_defseg</c> cell - what <c>DEF SEG = n</c> does, shared with it.</summary>
  private void SetDefaultSegment(Expression segment)
    => this._b.Store(this.Coerce(this.LowerExpr(segment), this._model.TypeOf(segment), PbType.Integer),
        this.ErrorCell("rt_defseg", IrType.I16));

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

  /// <summary>
  /// The CV family reads a number from a binary-record string's raw bytes. The two-argument form
  /// starts at an offset, which the direct emitter spells as
  /// <c>MID$(s$, offset, size)</c> before the conversion, so that is what is written here: the size
  /// is the width the conversion reads, and the composition reuses an entry already mapped.
  /// </summary>
  private IrValue LowerCv(CallOrIndexExpr call, string fn, IrType resultType, int size) {
    var source = this.LowerStringExpr(call.Arguments[0]);
    if (call.Arguments.Count > 1)
      source = this._b.Call(IrType.Ptr,
        this.RuntimeFn("rt_str_mid", IrType.Ptr, IrType.Ptr, IrType.I32, IrType.I32),
        source,
        this.Coerce(this.LowerExpr(call.Arguments[1]), this._model.TypeOf(call.Arguments[1]), PbType.Long),
        new IrConstantInt(IrType.I32, size));
    return this._b.Call(resultType, this.RuntimeFn(fn, resultType, IrType.Ptr), source);
  }

  /// <summary>INSTR(haystack$, needle$) or INSTR(start%, haystack$, needle$) -> 1-based position (0 = not found).</summary>
  private IrValue LowerInstr(CallOrIndexExpr call) {
    IrValue position;
    if (call.Arguments.Count is not (2 or 3))
      throw new IrLoweringException($"INSTR with {call.Arguments.Count} arguments");
    var hasStart = call.Arguments.Count == 3;
    // INSTR(… ANY set$) is a different routine, not a different needle: it finds the first character
    // that BELONGS to a set rather than the first occurrence of a substring
    if (call.Arguments[hasStart ? 2 : 1] is AnyMatchExpr any)
      return this.Coerce(this.LowerScanSet(call, any.Value, nonMember: false), PbType.Long, this._model.TypeOf(call));
    if (!hasStart) {
      position = this._b.Call(IrType.I32, this.RuntimeFn("rt_str_instr", IrType.I32, IrType.Ptr, IrType.Ptr),
        this.LowerStringExpr(call.Arguments[0]), this.LowerStringExpr(call.Arguments[1]));
    } else {
      var start = this.Coerce(this.LowerExpr(call.Arguments[0]), this._model.TypeOf(call.Arguments[0]), PbType.Long);
      position = this._b.Call(IrType.I32, this.RuntimeFn("rt_str_instr_start", IrType.I32, IrType.I32, IrType.Ptr, IrType.Ptr),
        start, this.LowerStringExpr(call.Arguments[1]), this.LowerStringExpr(call.Arguments[2]));
    }
    return this.Coerce(position, PbType.Long, this._model.TypeOf(call));
  }

  /// <summary>
  /// The character-set scan both <c>INSTR … ANY</c> and <c>VERIFY</c> are: the position of the first
  /// character of the haystack that is IN the set, or - for VERIFY - the first that is not, counting
  /// from an optional 1-based start and answering zero when there is none.
  ///
  /// One runtime routine serves both, exactly as it does for the direct emitter, which differs only
  /// in the flag it loads. They are spelled as two IR entries rather than one with a flag argument
  /// because the flag is always a constant, and a constant belongs in the ABI table beside the
  /// routine's other presets rather than in an argument the optimizer has to fold back to it.
  /// </summary>
  private IrValue LowerScanSet(CallOrIndexExpr call, Expression set, bool nonMember) {
    var hasStart = call.Arguments.Count == 3;
    var start = hasStart
      ? this.Coerce(this.LowerExpr(call.Arguments[0]), this._model.TypeOf(call.Arguments[0]), PbType.Long)
      : new IrConstantInt(IrType.I32, 1);
    return this._b.Call(IrType.I32,
      this.RuntimeFn(nonMember ? "rt_str_verify" : "rt_str_scanset", IrType.I32, IrType.Ptr, IrType.Ptr, IrType.I32),
      this.LowerStringExpr(call.Arguments[hasStart ? 1 : 0]), this.LowerStringExpr(set), start);
  }

  /// <summary>
  /// The match operand of EXTRACT$ / TALLY, which is either a substring or - written <c>ANY set$</c>
  /// - a character set. The two are the same routine with a different flag, so what the caller needs
  /// back is the string underneath and which of the two it was.
  /// </summary>
  private (IrValue Handle, bool IsSet) MatchOperand(Expression match)
    => match is AnyMatchExpr any
      ? (this.LowerStringExpr(any.Value), true)
      : (this.LowerStringExpr(match), false);

  /// <summary>
  /// <c>CHR$(a[, b, …])</c>: the character of each code, concatenated left to right. One argument is
  /// the common case and the whole of it; more than one is the vendor spelling of a short literal
  /// string, and PB builds it exactly this way.
  /// </summary>
  private IrValue LowerChr(CallOrIndexExpr ci) {
    IrValue Character(int i) => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_chr", IrType.Ptr, IrType.I32),
      this.Coerce(this.LowerExpr(ci.Arguments[i]), this._model.TypeOf(ci.Arguments[i]), PbType.Long));

    var text = Character(0);
    for (var i = 1; i < ci.Arguments.Count; ++i)
      text = this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_concat", IrType.Ptr, IrType.Ptr, IrType.Ptr),
        text, Character(i));
    return text;
  }

  /// <summary>Lowers a string-returning intrinsic (LEFT$/RIGHT$/MID$/CHR$) to a runtime call.</summary>
  private IrValue LowerStringIntrinsic(CallOrIndexExpr ci, string name) {
    IrValue Str(int i) => this.LowerStringExpr(ci.Arguments[i]);
    IrValue Num(int i) => this.Coerce(this.LowerExpr(ci.Arguments[i]), this._model.TypeOf(ci.Arguments[i]), PbType.Long);
    IrValue Val(int i, ScalarType t) => this.Coerce(this.LowerExpr(ci.Arguments[i]), this._model.TypeOf(ci.Arguments[i]), t);

    // EXTRACT$(main$, match$) / EXTRACT$(main$, ANY set$): everything before the first match, or the
    // WHOLE string when there is none. The three-argument form takes a start position the runtime
    // entry has no slot for, so it declines rather than silently ignoring one.
    if (name.Equals("EXTRACT$", StringComparison.OrdinalIgnoreCase)) {
      if (ci.Arguments.Count != 2)
        throw new IrLoweringException($"EXTRACT$ with {ci.Arguments.Count} arguments");
      var main = Str(0);                                        // main first, as the direct emitter evaluates it
      var (match, isSet) = this.MatchOperand(ci.Arguments[1]);
      return this._b.Call(IrType.Ptr,
        this.RuntimeFn(isSet ? "rt_str_extract_any" : "rt_str_extract", IrType.Ptr, IrType.Ptr, IrType.Ptr),
        main, match);
    }

    return name.ToUpperInvariant() switch {
      // binary-record encoders: a number to its raw little-endian bytes as a string
      "MKBYT$" => this._b.Call(IrType.Ptr,
        this.RuntimeFn("rt_str_mkbyt", IrType.Ptr, IrType.I16), Val(0, PbType.Integer)),
      "MKI$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_mki", IrType.Ptr, IrType.I16), Val(0, PbType.Integer)),
      "MKWRD$" => this._b.Call(IrType.Ptr,
        this.RuntimeFn("rt_str_mki", IrType.Ptr, IrType.I16), Val(0, PbType.Integer)),
      "MKL$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_mkl", IrType.Ptr, IrType.I32), Val(0, PbType.Long)),
      "MKDWD$" => this._b.Call(IrType.Ptr,
        this.RuntimeFn("rt_str_mkdwd", IrType.Ptr, IrType.U32), Val(0, PbType.Dword)),
      "MKS$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_mks", IrType.Ptr, IrType.F32), Val(0, PbType.Single)),
      "MKD$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_mkd", IrType.Ptr, IrType.F64), Val(0, PbType.Double)),
      "MKE$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_mkd", IrType.Ptr, IrType.F64), Val(0, PbType.Double)),
      "LEFT$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_left", IrType.Ptr, IrType.Ptr, IrType.I32), Str(0), Num(1)),
      "RIGHT$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_right", IrType.Ptr, IrType.Ptr, IrType.I32), Str(0), Num(1)),
      "MID$" when ci.Arguments.Count >= 3 => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_mid", IrType.Ptr, IrType.Ptr, IrType.I32, IrType.I32), Str(0), Num(1), Num(2)),
      "MID$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_mid2", IrType.Ptr, IrType.Ptr, IrType.I32), Str(0), Num(1)),
      // CHR$ is VARIADIC: CHR$(65, 66, 67) is "ABC", not "A". It lowers as the left fold of
      // concatenation the direct emitter writes - one rt_chr per code, joined by rt_strcat - rather
      // than as a call that quietly reads the first argument and drops the rest.
      "CHR$" => this.LowerChr(ci),
      "SPACE$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_space", IrType.Ptr, IrType.I32), Num(0)),
      // STRING$(n, s$) repeats the FIRST CHARACTER of s$, so it is STRING$(n, ASC(s$)) - composed
      // from two calls the IR already has rather than a third runtime entry that would have to be
      // taught to every back end. It is also what the direct emitter does: ASC then StrFill.
      "STRING$" when this._model.TypeOf(ci.Arguments[1]) is StringType =>
        this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_string", IrType.Ptr, IrType.I32, IrType.I32), Num(0),
          this._b.Call(IrType.I32, this.RuntimeFn("rt_str_asc", IrType.I32, IrType.Ptr), Str(1))),
      "STRING$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_string", IrType.Ptr, IrType.I32, IrType.I32), Num(0), Num(1)),
      "STR$" => this.LowerStrOf(ci.Arguments[0]),
      "UCASE$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_ucase", IrType.Ptr, IrType.Ptr), Str(0)),
      "LCASE$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_lcase", IrType.Ptr, IrType.Ptr), Str(0)),
      "LTRIM$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_ltrim", IrType.Ptr, IrType.Ptr), Str(0)),
      "RTRIM$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_rtrim", IrType.Ptr, IrType.Ptr), Str(0)),
      // TRIM$ is both ends, which is how the direct emitter spells it too - there is no single
      // runtime entry, and composing the two is exactly what the one-sided trims mean
      "TRIM$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_rtrim", IrType.Ptr, IrType.Ptr),
        this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_ltrim", IrType.Ptr, IrType.Ptr), Str(0))),
      "REPEAT$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_repeat", IrType.Ptr, IrType.I32, IrType.Ptr),
        Num(0), Str(1)),
      // the radix conversions. Their two-argument form sets a MINIMUM digit count (HEX$(n, 4)
      // zero-pads to four; a value needing more still prints them all) - a different result, not a
      // formatting nicety, so it is carried rather
      // than quietly dropped the way taking argument 0 alone would. The runtime reads the count and
      // the bits-per-digit from ONE word, (digits << 8) | bits, so the packing is done here where a
      // constant folds away, and the direct emitter's clamp to 1..32 is reproduced exactly.
      // A NON-constant count declines, which is what the direct emitter does with it too.
      "HEX$" or "OCT$" or "BIN$" when ci.Arguments.Count > 1 =>
        ci.Arguments[1] is IntegerLiteralExpr digits
          ? this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_radix", IrType.Ptr, IrType.I32, IrType.I32),
              Num(0), new IrConstantInt(IrType.I32,
                (Math.Clamp((int)digits.Value, 1, 32) << 8) | (name == "HEX$" ? 4 : name == "OCT$" ? 3 : 1)))
          : throw new IrLoweringException($"{name} with a non-constant digit count"),
      "HEX$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_hex", IrType.Ptr, IrType.I32), Num(0)),
      "OCT$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_oct", IrType.Ptr, IrType.I32), Num(0)),
      "BIN$" => this._b.Call(IrType.Ptr, this.RuntimeFn("rt_str_bin", IrType.Ptr, IrType.I32), Num(0)),
      _ => throw new IrLoweringException($"string intrinsic {name}"),
    };
  }

  /// <summary>
  /// <c>STR$</c> of a number. A float is handed over at the x87's OWN width and the NAME picks the
  /// significant-digit count, exactly as the PRINT path does - rt_str_f32 and rt_str_f64 share a
  /// body and differ only in the digits they set.
  ///
  /// Coercing the argument to its declared width instead was a real loss, not a formality. PB
  /// evaluates a SINGLE-typed expression on the x87 at eighty bits and rounds when it is STORED;
  /// rounding it here threw those bits away before the formatter saw them. Under plain pb35 that is
  /// invisible, because a SINGLE prints 7 digits either way - but $COMPAT tb10 prints seventeen, and
  /// STR$(2 / 3) came back as .6666666865348816, the float 2/3 widened, against the direct emitter's
  /// .6666666666666667. Six of the differential battery's programs turned on this one line.
  /// </summary>
  private IrValue LowerStrOf(Expression arg) {
    if (this._model.TypeOf(arg) is not ScalarType s)
      throw new IrLoweringException("STR$ of a non-numeric value");
    var (name, ty) = s.IsFloat
      ? (s.ByteSize == 8 ? ("rt_str_from_double", IrType.F80) : ("rt_str_from_single", IrType.F80))
      : ($"rt_str_from_{(s.Signed ? "i" : "u")}{s.ByteSize * 8}", IrType.Integer(s.ByteSize * 8));
    var value = s.IsFloat
      ? this.Coerce(this.LowerExpr(arg), s, PbType.Ext)
      : this.Coerce(this.LowerExpr(arg), s, s);
    return this._b.Call(IrType.Ptr, this.RuntimeFn(name, IrType.Ptr, ty), value);
  }

  private IrValue LowerVal(CallOrIndexExpr call) {
    var value = this._b.Call(IrType.F64, this.RuntimeFn("rt_str_val", IrType.F64, IrType.Ptr), this.LowerStringExpr(call.Arguments[0]));
    return this.Coerce(value, PbType.Double, this._model.TypeOf(call));
  }

  /// <summary>Lowers a floating-point math intrinsic to the matching LLVM intrinsic (llvm.sqrt.fN, etc.).</summary>
  /// <summary>
  /// SQR, SIN, COS, TAN, ATN, LOG, EXP. The result is typed at the DECLARED width, and that is not
  /// an oversight: unlike ordinary arithmetic, which PB keeps on the x87 at eighty bits until it is
  /// stored, the direct emitter rounds a transcendental's answer to its declared type on the way out
  /// - <c>FSTP m64; FLD m64</c> right after the FYL2X - and genuine QuickBASIC agrees with it.
  /// LOG(2.718281828459045#) is 1 that way and .9999999999999999 with all eighty bits kept, and the
  /// oracle says 1. The back end has to make the same round trip; see SelectMathIntrinsic.
  /// </summary>
  private IrValue LowerMath(CallOrIndexExpr call, string fn) {
    var resultPb = this._model.TypeOf(call);
    var ty = MapType(resultPb);
    if (!ty.IsFloat)
      throw new IrLoweringException($"{fn} on a non-float result");
    var arg = this.Coerce(this.LowerExpr(call.Arguments[0]), this._model.TypeOf(call.Arguments[0]), resultPb);
    return this._b.Call(ty, this.RuntimeFn($"llvm.{fn}.f{ty.Bits}", ty, ty), arg);
  }

  /// <summary>
  /// <c>ASC(s$)</c>, and <c>ASC(s$, i)</c> as the one-character substring it is defined to be.
  ///
  /// The direct emitter has two spellings of the two-argument form: a direct byte read (rt_charat)
  /// under --optimize, and MID$(s$, i, 1) fed to ASC without it. The composition is the one written
  /// here - it is the unoptimized reference, it reuses entries that are already mapped, and leaving
  /// the pair adjacent is what lets the IR's own optimizer collapse it later. Both clamp a start
  /// below 1 to the first character and answer for a position past the end the same way.
  /// </summary>
  private IrValue LowerAsc(CallOrIndexExpr call) {
    var source = this.LowerStringExpr(call.Arguments[0]);
    if (call.Arguments.Count > 1)
      source = this._b.Call(IrType.Ptr,
        this.RuntimeFn("rt_str_mid", IrType.Ptr, IrType.Ptr, IrType.I32, IrType.I32),
        source,
        this.Coerce(this.LowerExpr(call.Arguments[1]), this._model.TypeOf(call.Arguments[1]), PbType.Long),
        new IrConstantInt(IrType.I32, 1));
    var code = this._b.Call(IrType.I32, this.RuntimeFn("rt_str_asc", IrType.I32, IrType.Ptr), source);
    return this.Coerce(code, PbType.Long, this._model.TypeOf(call));
  }

  /// <summary>
  /// LEN. For a dynamic string it is the handle's own length; for a FIXED string it is the declared
  /// width, known at compile time and never measured.
  ///
  /// <para>
  /// ASCIIZ is the one that has to be counted at run time: its length is the characters BEFORE the
  /// NUL, not its capacity, so <c>LEN</c> and <c>SIZEOF</c> disagree on it. Answering the capacity
  /// would look right on every value that happens to fill the buffer.
  /// </para>
  /// </summary>
  private IrValue LowerLen(CallOrIndexExpr call) {
    var argument = call.Arguments[0];
    var resultType = this._model.TypeOf(call);
    switch (this._model.TypeOf(argument)) {
      case StringType: {
        var length = this._b.Call(IrType.I32, this.RuntimeFn("rt_str_len", IrType.I32, IrType.Ptr), this.LowerStringExpr(argument));
        return this.Coerce(length, PbType.Long, resultType);   // LEN result narrows to its bound type
      }
      case AsciizType asciiz: {
        var address = this.StringStorageAddress(argument)
          ?? throw new IrLoweringException("LEN of an ASCIIZ expression that is not storage");
        var counted = this._b.Call(IrType.I32, this.RuntimeFn("rt_asciiz_len", IrType.I32, IrType.Ptr, IrType.I32),
          address, new IrConstantInt(IrType.I32, asciiz.Length));
        return this.Coerce(counted, PbType.Long, resultType);
      }
      // a fixed string and a record are their declared size, which the binder already knows
      case FixedStringType fixedStr:
        return this.Coerce(new IrConstantInt(IrType.I32, fixedStr.Length), PbType.Long, resultType);
      case UdtType udt:
        return this.Coerce(new IrConstantInt(IrType.I32, udt.Size), PbType.Long, resultType);
      default:
        throw new IrLoweringException("LEN of a non-string");
    }
  }

  /// <summary>
  /// The address of an inline string buffer - an ASCIIZ or fixed-string variable or record field -
  /// or null when the expression is not storage of that kind.
  /// </summary>
  private IrValue? StringStorageAddress(Expression expr) {
    if (expr is NameExpr && this._model.VariableBindings.TryGetValue(expr, out var symbol))
      return this.SlotFor(symbol);
    if (expr is MemberExpr member && !this._model.VariableBindings.ContainsKey(member))
      return this.MemberFieldAddress(member).Address;
    return null;
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
  /// <summary>
  /// Where a record VALUE lives. A record has no single value to load, so every operation on one -
  /// assignment, comparison, passing it by reference - is an operation on its address.
  /// </summary>
  private IrValue UdtAddress(Expression e) {
    if (e is NameExpr && this._model.VariableBindings.TryGetValue(e, out var sym) && sym.Type is UdtType)
      return this.SlotFor(sym);
    // an ARRAY ELEMENT of record type is storage in exactly the same way, one stride along
    if (e is CallOrIndexExpr indexed && this._model.VariableBindings.TryGetValue(indexed, out var arr)
        && arr.Type is ArrayType { Element: UdtType })
      return this.ElementAddress(indexed).Address;
    // and so is a record-typed FIELD of another record
    if (e is MemberExpr member && !this._model.VariableBindings.ContainsKey(member)
        && this.MemberFieldAddress(member) is { Field.Type: UdtType } field)
      return field.Address;
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

  /// <summary>
  /// <c>MIN</c>/<c>MAX</c> over two to sixteen arguments: a left fold of compare-and-select.
  ///
  /// The accumulator is kept when it already wins, which is the direct emitter's rule too (CMP then
  /// JGE for MAX, JLE for MIN) and the FPU fold's before that. It decides ties: MAX(a, b) with a = b
  /// yields the FIRST of them. Numerically that is nothing, but the two paths are checked against
  /// each other, and a fold that broke ties the other way would differ from the emitter on exactly
  /// the inputs a test is most likely to try.
  ///
  /// Every argument is coerced to the result type first, so the comparison happens in the domain the
  /// answer is returned in - MAX of an INTEGER and a LONG is a LONG comparison, not an INTEGER one.
  /// </summary>
  private IrValue LowerMinMax(CallOrIndexExpr call, bool wantMax) {
    var resultPb = this._model.TypeOf(call);
    var accumulator = this.Coerce(this.LowerExpr(call.Arguments[0]), this._model.TypeOf(call.Arguments[0]), resultPb);

    for (var i = 1; i < call.Arguments.Count; ++i) {
      var candidate = this.Coerce(this.LowerExpr(call.Arguments[i]), this._model.TypeOf(call.Arguments[i]), resultPb);
      var keepAccumulator = resultPb switch {
        ScalarType { IsFloat: true } => this._b.Cmp(wantMax ? IrCmpPred.Foge : IrCmpPred.Fole, accumulator, candidate),
        ScalarType { Signed: true } => this._b.Cmp(wantMax ? IrCmpPred.Sge : IrCmpPred.Sle, accumulator, candidate),
        ScalarType => this._b.Cmp(wantMax ? IrCmpPred.Uge : IrCmpPred.Ule, accumulator, candidate),
        _ => throw new IrLoweringException($"{(wantMax ? "MAX" : "MIN")} of {resultPb}"),
      };
      accumulator = this._b.Select(keepAccumulator, accumulator, candidate);
    }
    return accumulator;
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
    // BYVAL override (PB 3.2) against a BYREF parameter: the pointer's own value IS the address the
    // callee writes through, so the argument is the pointer rather than a pointer TO it. A BYVAL
    // override of anything else is not modelled and still declines.
    if (arg is ByValArgExpr byVal && this._model.TypeOf(byVal.Value) is PointerType)
      return this.PointerValue(byVal.Value);
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
        or BinaryOp.LessEqual or BinaryOp.GreaterEqual => leftPb is StringType or FixedStringType or AsciizType
          ? this.LowerStringComparison(expr, resultPb)
          : leftPb is UdtType
            ? this.LowerUdtComparison(expr, resultPb)
            : this.LowerComparison(expr, leftPb, rightPb, resultPb),
      _ => this.LowerArithmetic(expr, leftPb, rightPb, resultPb),
    };
  }

  private IrValue LowerArithmetic(BinaryExpr expr, PbType leftPb, PbType rightPb, PbType resultPb) {
    // A FLOATING expression is computed at the x87's own width, not at the width its declared type
    // names. That is PowerBASIC's rule and not a liberty: the type of `H?/3` is SINGLE, and genuine
    // PBC still divides in the register and prints 66.66667, where rounding to SINGLE first would
    // give 66.66666. The declared type chooses the FORMATTER; it does not round the value.
    //
    // Narrowing happens where PB actually narrows - storing into a declared variable - which the
    // Coerce at that use site emits, because Coerce measures the value's OWN width rather than
    // trusting the PB type it is told.
    var arithPb = resultPb is ScalarType { IsFloat: true } ? PbType.Ext : resultPb;
    var resultTy = MapType(arithPb);
    var signed = resultPb is ScalarType { Signed: true };
    var l = this.Coerce(this.LowerExpr(expr.Left), leftPb, arithPb);
    var r = this.Coerce(this.LowerExpr(expr.Right), rightPb, arithPb);

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
    // A zero divisor raises Error 11, and that guard belongs to the LANGUAGE rather than to an
    // $ERROR option - PB raises it whether or not any checking is armed. Emitted here rather than
    // left to each back end, so no back end has to carry its own.
    //
    // The constant cases are settled HERE rather than left to SCCP, which is the direct emitter's
    // O0220 said in the IR. Leaving them would work for most programs and fail for the ones that
    // matter: a function with an armed error handler is skipped by the whole optimizer, so the
    // comparison would survive with a literal on both sides and reach a selector that has no form
    // for it.
    if (op is IrBinaryOp.SDiv or IrBinaryOp.UDiv or IrBinaryOp.SRem or IrBinaryOp.URem)
      switch (r) {
        case IrConstantInt { Value: not 0 }:
          break;                                   // a constant that cannot be zero cannot trap
        case IrConstantInt:
          this.RaiseWhen(IrBuilder.ConstBool(true), 11, "division by zero");
          break;                                   // ...and one that IS zero always does
        default:
          this.RaiseWhen(this._b.Cmp(IrCmpPred.Eq, r, new IrConstantInt(resultTy, 0)), 11, "division by zero");
          break;
      }
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
    // An UNSIGNED product WRAPS. The Error 6 trap $ERROR OVERFLOW arms is the one the direct emitter
    // takes off IMUL's overflow flag, and that flag answers a SIGNED question; PB's own battery says
    // so out loud - DIFF105 multiplies two DWORDs of 100000 and expects 1410065408, "wraps and never
    // traps". Checking it here would raise where the language returns a number, and it also cost the
    // widening to a 64-bit intermediate that this back end has no register for.
    if (!signed)
      return this._b.Binary(IrBinaryOp.Mul, l, r);
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
      // $COMPILE UNIT / EXE says what KIND of artefact to produce. It is output policy, not runtime
      // semantics: the procedures in a unit mean exactly what they would in a program, and refusing
      // it kept every unit off the IR path over a directive that describes the file being written.
      case "COMPILE":
      // $IF / $ELSEIF / $ELSE / $ENDIF are resolved by the preprocessor; whatever survives to here is
      // the directive itself, with the branch it selected already spliced in
      case "IF":
      case "ELSEIF":
      case "ELSE":
      case "ENDIF":
      // $LINK names another source to compile alongside - also preprocessor-time
      case "LINK":
      // $STRING sizes the string heap, and the emitter reads it from model.MetaStatements in a
      // pre-pass rather than when it reaches the statement. That matters: a routed module body never
      // executes the statement list, so a directive applied DURING emission would be silently lost -
      // this one is not, which is why it is safe to ignore here
      case "STRING":
      // $COMPAT selects observable runtime semantics. TryLowerModule copied both the source and
      // effective dialect onto IrModule before lowering any statement, so the directive itself has
      // no instruction to emit and every detached back end can still make the dialect-aware choice.
      case "COMPAT":
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
      // $ERROR STACK ON: each procedure entry probes for headroom - see _checkStack, which is read
      // from the directives rather than set here, because this handler never runs for a procedure
      case "ERROR" when arm.Equals("STACK", StringComparison.OrdinalIgnoreCase):
        return;
      case "ERROR" when arm.Equals("ALL", StringComparison.OrdinalIgnoreCase):
        this._checkBounds = this._checkOverflow = this._checkNumeric = on;
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
    // Integer comparison is NOT simply "signed if either side is". PB's rule turns on whether the
    // unsigned operand has a signed type wide enough to hold it, and the direct emitter states it at
    // CodeGenerator.Expressions' isComparison branch, which this mirrors:
    //
    //   * a DWORD has no signed 32-bit counterpart, so a comparison involving one runs UNSIGNED and
    //     reads the signed side as unsigned - 4000000000 > 100 is TRUE and 4000000000 > -1 is FALSE.
    //     A wider QUAD or float operand keeps the widened compare instead.
    //   * two unsigned operands compare unsigned at the width that holds them.
    //   * a WORD or BYTE against a signed type compares SIGNED, widened to the next signed size that
    //     holds the unsigned one (WORD -> LONG, BYTE -> INTEGER), so its value stays positive:
    //     50000 > -1 is TRUE and not a 16-bit -15536 > -1.
    //
    // Getting this wrong does not fail loudly - it silently answers the other way for exactly the
    // values above the signed maximum, which is where DIFF61 lives.
    var width = Math.Max(sa.ByteSize, sb.ByteSize);
    var dwordOperand = (!sa.Signed && sa.ByteSize == 4) || (!sb.Signed && sb.ByteSize == 4);
    if (dwordOperand && width <= 4)
      return (new ScalarType(ScalarKind.Long, 4, false, false), false, false);
    if (!sa.Signed && !sb.Signed)
      return (new ScalarType(ScalarKind.Long, width > 2 ? 4 : 2, false, false), false, false);
    if (sa.Signed != sb.Signed && width <= 4) {
      var unsignedSide = sa.Signed ? sb : sa;
      width = Math.Max(width, unsignedSide.ByteSize == 1 ? 2 : 4);
    }
    return (new ScalarType(ScalarKind.Long, width, true, false), false, true);
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
    // $ERROR OVERFLOW ON: a real that will not FIT the integer traps with Error 6, and the check has
    // to happen BEFORE the conversion. FISTP stores a sentinel (8000_0000h for a LONG) when the value
    // is out of range and sets the Invalid-Operation flag beside it; the flag is the natural thing to
    // read and the wrong thing to rely on, so the limits are compared instead - which is what the
    // direct emitter does, and what DIFF105 pins.
    //
    // SIGNED only. An unsigned conversion WRAPS: PB multiplies two DWORDs of 100000 and answers
    // 1410065408 rather than raising, so range-checking one would trap where the language returns a
    // number.
    if (this._checkOverflow && st.Signed)
      this.RaiseWhen(this.OutsideIntegerRange(value, st), 6, "overflow");
    if (st.Signed && this._model.EffectiveDialect.IsBascomRuntime())
      return this._b.Call(toTy, this.RuntimeFn("rt_round_half_away", toTy, value.Type), value);
    return this._b.Cast(st.Signed ? IrCastOp.FPToSIRound : IrCastOp.FPToUI, value, toTy);
  }

  /// <summary>
  /// True when <paramref name="value"/> lies outside what <paramref name="target"/> can hold. The
  /// comparison is made in the FLOAT the value already is: converting first is what the check exists
  /// to guard against, so it cannot be part of the question.
  /// </summary>
  private IrValue OutsideIntegerRange(IrValue value, ScalarType target) {
    var bits = target.ByteSize * 8;
    var lowest = -(double)(1UL << (bits - 1));
    var highest = (double)((1UL << (bits - 1)) - 1);
    // ...and the bound itself is inclusive: rounding carries a value up to the next integer, so the
    // limit that must not be crossed is half a unit beyond the last representable one
    return this._b.Or(
      this._b.Cmp(IrCmpPred.Folt, value, new IrConstantFloat(value.Type, lowest - 0.5)),
      this._b.Cmp(IrCmpPred.Fogt, value, new IrConstantFloat(value.Type, highest + 0.5)));
  }
}
