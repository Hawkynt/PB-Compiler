using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Runtime;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// Translates a bound program into a 16-bit real-mode DOS executable.
/// Evaluation model: stack machine - INTEGER/WORD/BYTE in AX, LONG/DWORD in
/// DX:AX, floats on the x87 stack, dynamic strings as owned temp handles in AX,
/// machine stack for spills. Memory model: one segment (CS=DS=SS) with the data
/// area behind the code; far string heap at CS+0x1000, far array heap at
/// CS+0x2000. Procedures use BP frames (params at [BP+4..], locals/temps below
/// BP, RET n callee-clean); main gets a BP frame for statement temporaries too.
/// </summary>
public sealed partial class CodeGenerator(SemanticModel model) {

  private readonly Assembler _asm = new();
  private readonly DosRuntime _rt = new() { Dialect = model.Dialect, CompatDialect = model.CompatDialect };
  private readonly Dictionary<VariableSymbol, Label> _variableSlots = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<string, Label> _stringLiterals = new(StringComparer.Ordinal);
  private readonly Dictionary<ProcedureSymbol, Label> _procLabels = new(ReferenceEqualityComparer.Instance);
  private readonly List<(Label Slot, double Value)> _floatConstants = [];
  private readonly Stack<Label> _exitFor = new();
  private readonly Stack<Label> _exitDo = new();
  private readonly Stack<Label> _exitSelect = new();
  private readonly Stack<Label> _iterateFor = new();
  private readonly Stack<Label> _iterateDo = new();
  private readonly Stack<Label> _iterateAny = new();
  private Dictionary<string, Label> _userLabels = new(StringComparer.OrdinalIgnoreCase);
  private Label _scratch = null!;

  // current frame (main or procedure)
  private ProcedureSymbol? _currentProc;
  private HashSet<Statement>? _tailSelfCalls;
  private Label? _tailEntry;
  // pb36 O14 general tail calls: a tail-position CALL to ANOTHER in-module proc B
  // becomes "tear down A's frame, lay out B's call frame at A's caller's boundary,
  // jmp B" - B returns straight to A's caller. Keyed by the CallStmt -> target B.
  private Dictionary<Statement, ProcedureSymbol>? _tailGeneralCalls;
  // byte count of the current procedure's stack parameters ([BP+4..]); the tail-call
  // teardown discards exactly these before laying out the callee's arguments.
  private int _currentParamBytes;
  private Dictionary<VariableSymbol, (Mem Cell, PbType Type)>? _inlineParamSlots;
  // pb36: inlined parameters that are BYREF (the receiver THIS of a member method) - their slot
  // holds a near pointer to the argument, so a field access THIS.f loads the pointer then [BX+off].
  private HashSet<VariableSymbol>? _inlineByRefParams;
  private Label _epilogue = null!;
  private Label _frameBytesLabel = null!;
  private Label _frameWordsLabel = null!;
  private int _frameLocalBytes;
  private int _cseBytes;
  private Dictionary<Expression, OptCommonSubexpr.CseMark>? _cseMarks;
  private Dictionary<Syntax.Ast.NameExpr, long>? _provenReads;
  private IReadOnlyDictionary<Syntax.Ast.NameExpr, VariableSymbol>? _copyReads;
  private HashSet<Statement>? _deadStatements;
  // O23 whole-program data tree-shaking: globals nothing reachable reads, and the pure
  // stores to them - both removed under Optimize for a self-contained main (see OptDeadGlobals).
  private HashSet<VariableSymbol>? _deadGlobals;
  private HashSet<Statement>? _deadGlobalStores;
  private Dictionary<VariableSymbol, ConstantValue>? _ipcp;
  private Dictionary<CallOrIndexExpr, ConstantValue>? _pureFold;
  /// <summary>
  /// O8 branch fusion: the comparison node whose CMP flags may drive a branch directly instead of
  /// materializing PB's -1/0 truth value, together with where to jump and on which outcome. Armed
  /// by <see cref="EmitConditionalBranch"/> for one node only and matched by identity.
  /// </summary>
  private (BinaryExpr Node, Label Target, bool WhenFalse)? _compareBranch;
  private bool _compareBranchTaken;

  private (VariableSymbol Symbol, Reg Reg)? _registerCounter;
  private (VariableSymbol Symbol, Reg Reg)? _registerAccumulator;

  /// <summary>
  /// O6b: the array whose current element address is parked in BX for the loop being emitted,
  /// together with the counter that indexes it. Only an accumulate-over-an-array body establishes
  /// it (see <c>MatchSteppedAccumulateBody</c>), and that body's single read is the only thing
  /// that touches BX - so the address steps by the element size per iteration instead of being
  /// recomputed from the counter.
  /// </summary>
  private (VariableSymbol Array, VariableSymbol Counter)? _residentElementPtr;

  /// <summary>True when a register-resident loop counter or accumulator currently lives in SI (or ESI, whose low half is SI), so a code path that overwrites SI (e.g. loading a string-literal pointer) must save and restore it.</summary>
  private bool SiHoldsResident =>
    this._registerCounter?.Reg is Reg.SI or Reg.ESI || this._registerAccumulator?.Reg is Reg.SI or Reg.ESI;

  /// <summary>O16 interval lattice: the per-statement-entry interval environment of the main body
  /// (<see cref="IntervalRangeAnalysis"/>), consulted by <see cref="IndexRangeOf"/> through
  /// <see cref="_currentStatement"/> to prove a non-FOR-counter variable's range at a use site.</summary>
  private IReadOnlyDictionary<Statement, IReadOnlyDictionary<VariableSymbol, ValueFacts>>? _intervalPoints;
  private Statement? _currentStatement;

  /// <summary>O16: the proven [lo,hi] range of each FOR counter active over the current body
  /// (constant From/To, counter never written or aliased in the body). Used to drop a bounds
  /// check whose index is exactly such a counter and whose range lies inside the array bounds.</summary>
  private readonly Dictionary<VariableSymbol, (long Lo, long Hi)> _forRanges = new(ReferenceEqualityComparer.Instance);

  private int _tempBytes;
  private int _tempMax;

  /// <summary>Generated diagnostics for constructs the generator does not support yet.</summary>
  public List<Diagnostic> Errors { get; } = [];

  // $ERROR BOUNDS/NUMERIC/OVERFLOW/STACK state (PBC -EB/-EN/-EO/-ES set the
  // initial state; $ERROR ... ON|OFF metastatements toggle it lexically)
  public bool CheckBounds { get; set; }
  public bool CheckNumeric { get; set; }
  public bool CheckOverflow { get; set; }
  public bool CheckStack { get; set; }

  /// <summary>$OPTIMIZE SPEED / -OZF: favor inline code over runtime calls.</summary>
  public bool OptimizeSpeed { get; set; }

  /// <summary>S1 $OPTIMIZE SIZE: bias for image size - short-jump relaxation on, inlining off (unrolling/alignment/scheduler are SPEED-only anyway).</summary>
  public bool OptimizeSize { get; set; }

  /// <summary>
  /// Compile every eligible function through the in-house x86-16 back end (docs/X86-BACKEND.md) - it
  /// owns the whole function via its SSA IR (no shared cells), so it never reads an optimizer-stale
  /// cell. <b>Default ON.</b> Set <c>PBC_X_BACKEND=0</c> / <c>--no-x-backend</c> to compile through
  /// the direct emitter instead, which is retained only until the fixtures that assert its byte
  /// output have been read (docs/DIRECT-EMITTER-RETIREMENT.md).
  ///
  /// <para>
  /// Default-on was tried and reverted once, and the reason has since gone. That measurement read
  /// 109 failures, of which the two that settled it needed TAIL RECURSION to run in constant stack -
  /// a behavioural promise rather than code quality. Both now run and pass. Re-measured with the
  /// emulator actually present, default-on costs <b>62 of 6493</b>: 57 assertions about emitted
  /// code, 3 an opcode the test interpreter did not decode, 2 the pre-existing TIMER corpus case,
  /// and <b>zero behavioural failures</b>. The differential oracle agrees with the genuine vintage
  /// compilers either way, and the golden gate holds.
  /// </para>
  /// <para>
  /// The 57 are the remaining work, and they are not a regression: each names an INSTRUCTION the
  /// routed path reaches by another shape. Two of the first three read turned out to be measuring
  /// nothing on either path - a probe whose body constant-folds away, and a detector scanning a
  /// whole image for a marker every epilogue carries.
  /// </para>
  /// </summary>
  // The analysis-aware backend is the sole production and test compilation path.  Keep the old
  // internal spelling temporarily so downstream test fixtures compile, but make attempts to select
  // the retired emitter a no-op rather than allowing a second architecture to re-enter the graph.
  internal bool UseExperimentalBackend {
    get => true;
    set { }
  }

  /// <summary>
  /// Routing is MANDATORY: a body the back end does not take is a compile error rather than a quiet
  /// fall back to the direct emitter. Off by default; <c>PBC_X_BACKEND_STRICT</c> / <c>--x-backend-strict</c>.
  ///
  /// <para>
  /// This is the measurement the direct-emitter retirement needs and the one the ordinary routed
  /// build cannot give. With a fallback present, every gate is satisfied by construction: a decline
  /// is invisible, the program still compiles, and the differential still agrees - because both sides
  /// ran the SAME emitter for that body. Turning declines into errors is what asks the real question,
  /// which is whether the program would still compile if <c>CodeGen/</c> were not there.
  /// </para>
  /// <para>
  /// A bodiless EXTERNAL declaration is exempt, and that is not a loophole: it is a link import with
  /// no code to emit on either path, so it is nobody's coverage. Everything else counts.
  /// </para>
  /// </summary>
  internal bool RequireBackend { get; set; } = true;


  /// <summary>
  /// Turns every routing decline into a compile error when <see cref="RequireBackend"/> is set.
  ///
  /// <para>
  /// The exemption is a bodiless EXTERNAL declaration: it is a link import, so neither emitter
  /// produces code for it and it is nobody's coverage. Every other decline is reported with the
  /// reason the routing itself gave, because the reason is the work item - a shape the ABI cannot
  /// express reads differently from a body the allocator ran out of registers on.
  /// </para>
  /// </summary>
  private bool RaiseWhenRoutingIsMandatoryAndSomethingDeclined() {
    if (!this.RequireBackend || !this.UseExperimentalBackend)
      return false;

    var declined = false;
    foreach (var (name, reason) in this.BackendDeclines) {
      if (reason.StartsWith("filter: external declaration", StringComparison.Ordinal))
        continue;
      declined = true;
      this.Errors.Add(new(new("", 0, 0),
        $"routing is mandatory and '{name}' was not taken by the x86-16 back end: {reason}"));
    }
    return declined;
  }

  /// <summary>Raises trappable runtime error <paramref name="code"/> when the preceding Jcc falls through.</summary>
  private void EmitRaiseWhen(Action<Label> skipJump, int code) {
    var asm = this._asm;
    var ok = asm.DefineLabel();
    skipJump(ok);
    asm.Mov(Reg.AX, code);
    asm.Call(this._rt.Raise);
    asm.MarkLabel(ok);
  }

  public byte[] EmitExecutable() => this.EmitExecutable([], []);

  /// <summary>
  /// Emits the program as a DOS MZ executable; <paramref name="units"/> link
  /// unconditionally, <paramref name="libraries"/> and <paramref name="omfLibraries"/>
  /// contribute units on demand (<c>$LINK</c>) - the foreign OMF .LIBs by lazy,
  /// dictionary-driven selective extraction. Link failures surface as compile diagnostics.
  /// </summary>
  public byte[] EmitExecutable(IReadOnlyList<PbuFile> units, IReadOnlyList<PblFile> libraries, IReadOnlyList<Emit.Omf.OmfLibrary>? omfLibraries = null)
    => this.EmitDosProgram(units, libraries, omfLibraries, emitCom: false);

  /// <summary>
  /// Emits a flat DOS COM image at the conventional PSP:0100h load address. COM has no relocation
  /// table, so this form is intentionally standalone: external units/libraries require EXE.
  /// </summary>
  public byte[] EmitCom() => this.EmitDosProgram([], [], null, emitCom: true);

  private byte[] EmitDosProgram(
      IReadOnlyList<PbuFile> units,
      IReadOnlyList<PblFile> libraries,
      IReadOnlyList<Emit.Omf.OmfLibrary>? omfLibraries,
      bool emitCom) {
    ArgumentNullException.ThrowIfNull(units);
    ArgumentNullException.ThrowIfNull(libraries);
    omfLibraries ??= [];
    this._allowExternalCalls = units.Count > 0 || libraries.Count > 0 || omfLibraries.Count > 0;
    if (emitCom && this._allowExternalCalls) {
      this.Errors.Add(new(default, "COM output cannot contain linked units/libraries or unresolved externals; use EXE"));
      return [];
    }
    var optimizeMeta = this.ResolveOptimizeMetastatement();

    // BASICA/GW dead interpreter text, decided before anything rewrites the body: which
    // DeferredSourceStmt nodes control cannot reach. Not gated on the optimizer - whether a program
    // COMPILES must not depend on it.
    if (model.Dialect.IsGwBasica()) {
      this._unreachableDeferred = UnreachableDeferredSource(model.MainBody, this.OptFolder);
      if (this.UseExperimentalBackend && !this.ValidateDeferredInterpreterSource())
        return [];
    }

    // $CPU is target legality, not an optimization. Backend routing consults SelectionTarget before
    // normal image emission reaches the later runtime setup, so initialize it before any routing query.
    this._rt.Target = this.RuntimeTargetForRuntime();

    // Production compilation never mutates executable semantics through the legacy bound-AST
    // optimizer. The bound program crosses IrLowering once; optimization then belongs exclusively to
    // IrMiddleEndPipeline and the target machine pipeline.
    // SPEED/SIZE, resolved BEFORE anything can ask the back end a question. The objective is not only
    // an emission setting: SelectionCost hands the selector a cost model under SPEED and null
    // otherwise, so it decides every byte-for-cycles trade the selector may make - the membership
    // masks, the perfect hash, the byte-index table.
    //
    // It used to be resolved further down, next to the peephole and scheduler switches it also sets,
    // and that was correct for as long as nothing consulted the routing before then. Mandatory
    // routing does: BackendDeclines forces BackendProcs/BackendMain, which runs selection. So under
    // PBC_X_BACKEND_STRICT the selector ran with OptimizeSpeed still false, every cost-model trade
    // declined, and the cached machine code was the compact form - strict mode did not merely measure
    // a different program from the one it shipped, it emitted one. ResolveOptimizeObjective's own
    // summary already promised "before backend selection and emission"; this is that promise.
    //
    this.ResolveOptimizeObjective(optimizeMeta);

    // Asked HERE, after the optimizer has had its say about calling conventions and before a single
    // byte is emitted, because the routing's answer depends on both. Production routing is mandatory:
    // a decline is a compile failure and MUST NOT fall through to the legacy direct emitter. Tests may
    // still opt into that emitter explicitly as a behavioural oracle.
    if (this.RaiseWhenRoutingIsMandatoryAndSomethingDeclined())
      return [];

    var asm = this._asm;
    // Model COM's PSP:0100h load origin inside the assembler itself. The prefix is not written to
    // disk; it exists so every label/fixup/pseudo-address sees the same offsets DOS will expose.
    if (emitCom)
      asm.Db(new byte[ComWriter.LoadOffset]);

    // peephole / scheduler: record the instruction stream of a standalone program image so a
    // post-emit pass can rewrite it (units/libraries keep the faithful stream). The two passes both
    // rewrite by recorded byte position, so they are mutually exclusive: an optimized standalone under
    // $OPTIMIZE SPEED gets the instruction scheduler (reorders the FINAL stream - after
    // unrolling/inlining/const-fold - to group memory/ALU ops), every other optimized standalone keeps
    // the peephole (staging coalesce, CMP->TEST). Gated on the optimizer flags, not the dialect (the
    // optimizer is dialect-agnostic; SPEED merely defaults on for pb36) - resolved above, before the
    // back end could be asked anything.
    var standalone = this.Optimize && !this._allowExternalCalls && !this._isUnit;
    asm.EnableSchedule = standalone && this.OptimizeSpeed;
    asm.EnablePeephole = standalone && !asm.EnableSchedule;
    // jump threading composes with either pass: a pure fixup rewrite over the final stream,
    // collapsing ITERATE -> loop-end -> loop-head and GOTO -> GOTO cascades to one hop
    asm.EnableJumpThreading = standalone;
    // a reload of a frame cell the register still holds is dead; composes with either pass
    // (it runs first, on records the scheduler's permutation would otherwise invalidate)
    asm.EnableLoadForwarding = standalone;
    // S1: short-jump relaxation shrinks every in-range near jump to the 2-byte form. A forward
    // branch is emitted near only because its target was still unbound when it was encoded, and
    // the short form is both smaller and easier on the 8086's 4-byte prefetch queue.
    //
    // This runs for the FAITHFUL path too, because that is what the oracle does: a genuine
    // PB 3.5 image (PowerBASIC Compiler 3.50, Robert S. Zale) contains ~1600 conditional jumps
    // of which 7 use the near 0F 8x form, and 373 short JMPs against 186 near ones - it picks
    // the short encoding whenever the displacement fits, forward branches included. Always
    // emitting near for a forward branch is therefore a deviation FROM the oracle, not fidelity
    // to it. Units and external-call modules keep the near forms (their targets are relocated).
    asm.EnableJumpRelaxation = !this._allowExternalCalls && !this._isUnit;
    // The near conditional jump (0F 8x) is 80386. Without this the assembler had no idea what it
    // was building for and emitted it regardless, which is fine for anything relaxation could pull
    // back into a byte and an invalid instruction for anything it could not.
    asm.Allow386Jcc = this.Has32BitCpu;
    // S3 SIZE: identical procedures fold to one copy (entry labels re-bound to the survivor)
    asm.EnableTailMerge = standalone && this.OptimizeSize;
    var userMain = asm.DefineLabel("user_main");
    this._scratch = asm.DefineLabel("cg_scratch");

    this._rt.EnableBss = this.Optimize && !this._allowExternalCalls && !this._isUnit;
    this._rt.EnableUmb = this.Optimize && !this._allowExternalCalls && !this._isUnit;   // C6: HUGE-array heap prefers upper memory
    this._rt.EnableFastVideo = model.FastVideo;   // R1: $OPTION VIDEO direct-video console PRINT
    this._rt.Target = this.RuntimeTargetForRuntime();
    // $CPU says what the runtime MAY encode, $OPTIMIZE says whether it may trade bytes for cycles.
    // Keeping the second out of the target is what lets $FLOAT NPX still reach native x87 with the
    // optimizer off, while a copy stays the copy the user wrote.
    this._rt.EnableTargetOptimizations = this.Optimize;
    // The immediate-count shift and rotate (C0/C1 ib) is 80186, and the runtime is where it leaked:
    // an array index scaled by SHL BX,2 is a C1 in an image whose declared target is an 8086. This
    // sits after the target is known because SelectionTarget reads it, and before the first byte is
    // emitted; it deliberately reuses the selector's notion of the CPU floor rather than a second one.
    asm.Allow186ImmediateShifts = this.SelectionTarget.Cpu186OrLater;
    this._rt.EmitEntry(asm, userMain);

    // pb36 (docs/PB36.md P1): the runtime is emitted AFTER user code, trimmed
    // to the sections the program actually reaches; pb35 keeps today's layout
    var trimRuntime = this.Optimize && !this._allowExternalCalls;
    if (!trimRuntime)
      this._rt.EmitProcedures(asm);
    else
      this._rt.BindDeferred(asm); // labels exist now, the trimmed bodies follow the user code

    asm.MarkLabel(userMain);

    // stack probe threshold: $STACK n reserves n bytes below the 0xFFFE top,
    // otherwise everything above the data area (margin 256) counts as stack
    var stackMeta = model.MetaStatements.FirstOrDefault(m => m.Command.Equals("STACK", StringComparison.OrdinalIgnoreCase));
    if (stackMeta is { Arguments: [{ Kind: TokenKind.IntegerLiteral } stackSize, ..] })
      asm.Mov(Mem.Word(asm.Lbl("rt_stackmin")), (int)(0xFFFE - Math.Clamp(stackSize.IntegerValue, 256, 0xF000)) & 0xFFFF);
    else {
      // with virtual BSS (P3) the data area really ends behind the image
      asm.Mov(Reg.AX, Imm.OffsetOf(asm.Lbl(this._rt.EnableBss ? "rt_bss_end" : "rt_memend")));
      asm.Add(Reg.AX, 256);
      asm.Mov(Mem.Word(asm.Lbl("rt_stackmin")), Reg.AX);
    }

    // $OPTION CNTLBREAK ON|OFF: int 23h handler (OFF ignores Ctrl-Break,
    // ON terminates cleanly through the runtime exit)
    var cntlBreak = model.MetaStatements.FirstOrDefault(m =>
      m.Command.Equals("OPTION", StringComparison.OrdinalIgnoreCase)
      && m.Arguments is [{ } o, ..] && o.Text.Equals("CNTLBREAK", StringComparison.OrdinalIgnoreCase));
    if (cntlBreak != null) {
      var breakOff = cntlBreak.Arguments[^1].Text.Equals("OFF", StringComparison.OrdinalIgnoreCase);
      var install = asm.DefineLabel();
      var handler = asm.DefineLabel();
      asm.Jmp(install);
      asm.MarkLabel(handler);
      if (breakOff)
        asm.Iret();
      else {
        asm.Mov(Reg.AL, (Imm)255);
        asm.Jmp(this._rt.Exit);
      }
      asm.MarkLabel(install);
      asm.Mov(Reg.DX, Imm.OffsetOf(handler));
      asm.Mov(Reg.AX, 0x2523);
      asm.Int(0x21);
    }

    // $STRING n selects the string-segment granularity; observable limit =
    // usable bytes per string (the multi-segment design stays single-heap)
    var stringMeta = model.MetaStatements.FirstOrDefault(m => m.Command.Equals("STRING", StringComparison.OrdinalIgnoreCase));
    if (stringMeta is { Arguments: [{ Kind: TokenKind.IntegerLiteral } granularity, ..] }) {
      var usable = granularity.IntegerValue switch {
        1 => 1006, 2 => 2030, 4 => 4078, 8 => 8174, 16 => 16366, _ => 32750,
      };
      asm.Mov(Mem.Word(asm.Lbl("rt_strmaxlen")), usable);
    }

    // Artifact assembly consumes the machine product directly. No AST CSE/SCCP/reachability/
    // inlining pass is allowed to make a second semantic decision after IrMiddleEndPipeline.
    var backendMain = this.BackendMain();
    if (backendMain is null) {
      this.Errors.Add(new(default,
        "IR/x86-16 compilation reached image emission without a machine body for main"));
      return [];
    }
    this.EmitBackendMain();

    // Emit exactly the source definitions that survived the IR module pipeline, preserving source
    // order for deterministic layout. A live definition that failed machine lowering was already
    // diagnosed by mandatory routing before image emission; a missing one here is an invariant breach.
    var backendProcedures = this.BackendProcs();
    foreach (var proc in model.ProcedureList)
      if (!proc.IsExternal && backendProcedures.ContainsKey(proc))
        this.EmitBackendFunction(proc);

    this.EmitFarThunks();

    HashSet<string>? trimmedSections = null;
    if (trimRuntime) {
      // seed = every named label user code (and the entry stub) references
      // that no user code bound - exactly the runtime's surface in use
      var seed = asm.LabelReferences()
        .Select(r => r.Target)
        .Where(t => t is { Name: not null, IsBound: false })
        .Select(t => t.Name!)
        .Distinct(StringComparer.OrdinalIgnoreCase);
      trimmedSections = RuntimeTrimmer.Instance.CloseOver(seed);
      this._rt.EmitProcedures(asm, trimmedSections.Contains);
    }

    this._listingCodeLength = asm.Position; // listing: code ends, data area begins here
    this.EmitDataArea(trimmedSections);
    this._listingDataLength = asm.Position - this._listingCodeLength;
    this._rt.PlaceBss(asm); // pb36 P3: zero blobs live behind the image

    RelocatableImage? comImage = null;
    var image = this._allowExternalCalls
      ? this.LinkImage(units, libraries, omfLibraries)
      : emitCom
        ? (comImage = asm.ToRelocatable()).Image
        : asm.ToArray();
    if (image.Length == 0)
      return []; // link/format errors already reported

    if (emitCom) {
      try {
        var virtualEnd = this._rt.EnableBss ? asm.Lbl("rt_bss_end").Position : image.Length;
        return ComWriter.Write(comImage!, virtualEnd);
      } catch (InvalidDataException e) {
        this.Errors.Add(new(default, "COM: " + e.Message));
        return [];
      }
    }

    // grow the single segment to its full 64 KiB so data + stack always fit,
    // then reserve the far string and array heap segments behind it - under
    // pb36 trimming unused heap segments are not reserved at all (P4)
    var heapParagraphs = DosRuntime.ExtraHeapParagraphs;
    if (trimmedSections != null && !trimmedSections.Contains("chain")) {
      var needArrayHeap = trimmedSections.Contains("arrays") || trimmedSections.Contains("ems");
      var needStringHeap = trimmedSections.Contains("strings");
      heapParagraphs = needArrayHeap ? DosRuntime.ExtraHeapParagraphs
        : needStringHeap ? DosRuntime.ExtraHeapParagraphs / 2
        : 0;
    }
    var extraParagraphs = (ushort)((0x10000 - image.Length % 0x10000 + 15) / 16 + heapParagraphs);
    var writer = new MzExeWriter(image) {
      EntrySegment = 0,
      EntryOffset = 0,
      StackSegment = 0,
      StackPointer = 0xFFFE,
      MinExtraParagraphs = extraParagraphs,
      // cap the allocation at what we actually use, freeing the rest of
      // conventional memory for SHELL/EXEC and DOS 48h allocations (HUGE arrays)
      MaxExtraParagraphs = extraParagraphs,
    };
    writer.AddRelocations(this._allowExternalCalls ? this._linkedSegmentSites : asm.SegmentRelocations);
    return writer.ToArray();
  }

  #region slots, literals & labels

  private Label SlotOf(VariableSymbol symbol) {
    // a pb36 STACK array has no data-segment slot - any use that lands here (whole-array pass,
    // ERASE, VARPTR of the array, ...) is outside the supported element/LBOUND/UBOUND surface
    if (symbol is { IsArray: true, ArrayClass: ArrayClass.Stack })
      this.Errors.Add(new(new("", 0, 0), $"STACK array {symbol.Name}: only element access and LBOUND/UBOUND are supported"));
    // PB internal variables (pbvScrnCols, ...) live in runtime data cells
    if (symbol.Storage == VariableStorage.Global && DosRuntime.InternalVariableLabel(symbol.Name) is { } internalCell)
      return this._asm.Lbl(internalCell);
    if (!this._variableSlots.TryGetValue(symbol, out var label))
      this._variableSlots[symbol] = label = this._asm.DefineLabel($"v_{symbol.Name}_{this._variableSlots.Count}");
    return label;
  }

  private Label LiteralOf(string text) {
    if (!this._stringLiterals.TryGetValue(text, out var label))
      this._stringLiterals[text] = label = this._asm.DefineLabel($"s_{this._stringLiterals.Count}");
    return label;
  }

  private Label FloatConstOf(double value) {
    var slot = this._asm.DefineLabel($"f_{this._floatConstants.Count}");
    this._floatConstants.Add((slot, value));
    return slot;
  }

  private Label UserLabel(string name) {
    if (!this._userLabels.TryGetValue(name, out var label))
      this._userLabels[name] = label = this._asm.DefineLabel($"l_{name}");
    return label;
  }

  /// <summary>
  /// True when the compiler sees every caller of <paramref name="proc"/> and nothing
  /// external can reach it, so it may be freely rewritten or dropped. A nested procedure
  /// is always private to its container; in a self-contained main every procedure is ours.
  /// A $COMPILE UNIT's top-level procedures are exported, and a main linked with foreign
  /// objects could be called by name from them - those are not fully owned.
  /// </summary>
  private bool IsFullyOwned(ProcedureSymbol proc) => proc.IsNested || (!this._isUnit && !this._allowExternalCalls);

  /// <summary>
  /// O23 soundness: true when $ERROR NUMERIC/OVERFLOW/BOUNDS checking is active initially or any
  /// <c>$ERROR ... ON</c> (or <c>ALL</c>) metastatement could turn it on - the data tree-shaker
  /// must then treat a trap-capable store RHS (arithmetic, an array read) as side-effecting and
  /// keep the global, lest it drop a store whose evaluation was meant to raise Error 6/9.
  /// </summary>
  private bool NumericCheckingPossible()
    => this.CheckNumeric || this.CheckOverflow || this.CheckBounds
       || model.MetaStatements.Any(m =>
            m.Command.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
            && m.Arguments.Count >= 2
            && m.Arguments[0].Text.ToUpperInvariant() is "NUMERIC" or "OVERFLOW" or "BOUNDS" or "ALL"
            && m.Arguments[^1].Text.Equals("ON", StringComparison.OrdinalIgnoreCase));

  private Label ProcLabelOf(ProcedureSymbol proc) {
    if (!this._procLabels.TryGetValue(proc, out var label))
      // DECLAREd-but-undefined procedures resolve at link time by name; overloaded
      // definitions (PB 3.6) get an index suffix so each has its own label (the
      // first/only one keeps the plain p_<name> for byte-identical output).
      this._procLabels[proc] = label = proc.IsExternal && this._allowExternalCalls
        ? this._asm.External(proc.Alias ?? proc.Name)   // ALIAS names the external (link) symbol, e.g. a C public "_foo"
        : this._asm.DefineLabel(proc.OverloadIndex == 0 ? $"p_{proc.Name}" : $"p_{proc.Name}__{proc.OverloadIndex}");
    return label;
  }

  private void EmitDataArea(HashSet<string>? trimmedSections = null) {
    var asm = this._asm;
    asm.Align(2);
    if (!this._isUnit) { // units import the runtime (and the main module's DATA pool) instead
      if (trimmedSections == null || trimmedSections.Contains("consts"))
        this._rt.EmitConstants(asm);
      this._rt.EmitData(asm, trimmedSections == null ? null : trimmedSections.Contains);
    }

    asm.Align(2);
    asm.MarkLabel(this._scratch);
    asm.Db(new byte[16]);   // 12 for the 32-bit shuffles + room for two staged QWORDs (C1 quad bitwise)

    foreach (var (slot, value) in this._floatConstants) {
      asm.Align(2);
      asm.MarkLabel(slot);
      asm.Dq(value);
    }

    this.EmitBackendDataPool(asm);

    if (!this._isUnit) {
      // The DOS runtime still contains the public READ/RESTORE compatibility entry points. Production
      // IR DATA lowering uses its own ir.datapool/ir.datacursor cells, but an emitted runtime section
      // may still reference these legacy ABI symbols even when the source has no DATA. Bind an EMPTY
      // pool so the runtime ABI remains linkable without restoring the deleted syntax DATA emitter.
      asm.Align(2);
      asm.MarkLabel("rt_dataptr");
      asm.Dw(asm.Lbl("rt_datapool"));
      asm.MarkLabel("rt_datapool");
      asm.MarkLabel("rt_dataend");
    }

    foreach (var (symbol, label) in this._variableSlots) {
      asm.Align(2);
      asm.MarkLabel(label);
      var bytes = symbol.ArrayClass is ArrayClass.Huge or ArrayClass.Virtual or ArrayClass.Ems or ArrayClass.Xms
        ? _PAGED_ARRAY_DESCRIPTOR_BYTES
        : Math.Max(symbol.Type.Size, 1);
      // pb36 $RESOURCE: the array's slot IS the embedded file (padded to the slot size)
      if (model.ResourceData.TryGetValue(symbol, out var resource)) {
        asm.Db(resource);
        if (bytes > resource.Length)
          asm.Db(new byte[bytes - resource.Length]);
        continue;
      }
      asm.Db(new byte[bytes]);
    }

    asm.Align(2);
    asm.MarkLabel("rt_stackmin");
    asm.Dw(0);
    asm.MarkLabel("rt_memend");    // stack probe baseline ($ERROR STACK ON)
  }

  private void Unsupported(Statement s) => this.Errors.Add(new(s.Position, $"not yet generated: {(s is CommandStmt c ? $"command {c.Keyword}" : s.GetType().Name)}"));
  private void Unsupported(Expression e, string what) => this.Errors.Add(new(e.Position, $"not yet generated: {what}"));
  private void Unsupported(SourcePosition position, string what) => this.Errors.Add(new(position, $"not yet generated: {what}"));

  /// <summary>Replicates the binder's variable table key (name + canonical suffix text; arrays carry a "()" tail).</summary>
  private static string KeyOf(string name, TypeSuffix suffix, bool isArray = false) => name + suffix.KeyText() + (isArray ? "()" : "");

  private VariableSymbol? LookupVariable(string name, TypeSuffix suffix, bool isArray = false) {
    var key = KeyOf(name, suffix, isArray);
    if (this._currentProc != null && this._currentProc.Variables.TryGetValue(key, out var local))
      return local;
    return model.ModuleVariables.GetValueOrDefault(key);
  }

  #endregion

}
