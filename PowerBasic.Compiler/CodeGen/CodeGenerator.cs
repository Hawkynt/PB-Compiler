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
  private readonly DosRuntime _rt = new() { Dialect = model.Dialect };
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
  private Dictionary<VariableSymbol, (Mem Cell, PbType Type)>? _inlineParamSlots;
  private Label _epilogue = null!;
  private Label _frameBytesLabel = null!;
  private Label _frameWordsLabel = null!;
  private int _frameLocalBytes;
  private int _cseBytes;
  private Dictionary<Expression, Pb36CommonSubexpr.CseMark>? _cseMarks;
  private Dictionary<Syntax.Ast.NameExpr, long>? _provenReads;
  private IReadOnlyDictionary<Syntax.Ast.NameExpr, VariableSymbol>? _copyReads;
  private HashSet<Statement>? _deadStatements;
  private Dictionary<VariableSymbol, ConstantValue>? _ipcp;
  private (VariableSymbol Symbol, Reg Reg)? _registerCounter;
  private (VariableSymbol Symbol, Reg Reg)? _registerAccumulator;

  /// <summary>O16: the proven [lo,hi] range of each FOR counter active over the current body
  /// (constant From/To, counter never written or aliased in the body). Used to drop a bounds
  /// check whose index is exactly such a counter and whose range lies inside the array bounds.</summary>
  private readonly Dictionary<VariableSymbol, (long Lo, long Hi)> _forRanges = new(ReferenceEqualityComparer.Instance);

  /// <summary>Registers the counter's proven range for the loop body, removing it on Dispose; null (no scope) when the range is not statically known or the counter could change in the body.</summary>
  private IDisposable? PushForRange(ForStmt f, VariableSymbol counter) {
    if (!this.Optimize)
      return null;
    if (counter.Type is not ScalarType { IsFloat: false })
      return null;
    if (this.Pb36Folder.TryFold(f.From) is not { Integer: { } fromV }
        || this.Pb36Folder.TryFold(f.To) is not { Integer: { } toV })
      return null;
    if (!CounterStableInBody(f.Body, counter, model))
      return null;
    this._forRanges[counter] = (Math.Min(fromV, toV), Math.Max(fromV, toV));
    var registered = new List<VariableSymbol> { counter };

    // O16 derived range: a leading run of statements that each assign a scalar-INTEGER
    // variable a range-known counter expression (j = i+1, k = i*2, ...) - and never modify
    // it later - carries those ranges for the body. Processing in order is sound: a forward
    // reference to a not-yet-registered var makes IndexRangeOf fail and ends the run, and the
    // assignment of each var precedes every read of it (the prefix only assigns other vars).
    for (var idx = 0; idx < f.Body.Count; ++idx) {
      if (f.Body[idx] is AssignStmt { Target: NameExpr dvt, Value: { } drhs }
          && model.VariableBindings.TryGetValue(dvt, out var dv)
          && dv.Type is ScalarType { IsFloat: false, ByteSize: <= 2 }
          && !registered.Contains(dv)                       // distinct, and not the counter
          && !ReferencesVar(drhs, dv, model)
          && this.IndexRangeOf(drhs) is { } dvr
          && !IsModifiedIn(f.Body.Skip(idx + 1), dv, model)) {
        this._forRanges[dv] = dvr;
        registered.Add(dv);
        continue;
      }
      break;                                                // first non-derived statement ends the run
    }
    return new ForRangeScope(this, registered);
  }

  private sealed class ForRangeScope(CodeGenerator gen, List<VariableSymbol> symbols) : IDisposable {
    public void Dispose() { foreach (var s in symbols) gen._forRanges.Remove(s); }
  }

  /// <summary>True when any name read of <paramref name="v"/> appears in the tree.</summary>
  private static bool ReferencesVar(Expression e, VariableSymbol v, SemanticModel model) {
    if (e is NameExpr && model.VariableBindings.TryGetValue(e, out var s) && ReferenceEquals(s, v))
      return true;
    return e switch {
      UnaryExpr u => ReferencesVar(u.Operand, v, model),
      BinaryExpr b => ReferencesVar(b.Left, v, model) || ReferencesVar(b.Right, v, model),
      CallOrIndexExpr c => c.Arguments.Any(a => ReferencesVar(a, v, model)),
      MemberExpr m => ReferencesVar(m.Target, v, model),
      ByValArgExpr bv => ReferencesVar(bv.Value, v, model),
      _ => false,
    };
  }

  /// <summary>True when any statement assigns or incr/decrs <paramref name="v"/> (recursively).</summary>
  private static bool IsModifiedIn(IEnumerable<Statement> stmts, VariableSymbol v, SemanticModel model) {
    bool Writes(Expression t) => t is NameExpr && model.VariableBindings.TryGetValue(t, out var s) && ReferenceEquals(s, v);
    foreach (var st in stmts)
      switch (st) {
        case AssignStmt a when Writes(a.Target): return true;
        case IncrDecrStmt id when Writes(id.Target): return true;
        case IfStmt iff when IsModifiedIn(iff.Then, v, model)
            || iff.ElseIfs.Any(e => IsModifiedIn(e.Body, v, model))
            || (iff.Else != null && IsModifiedIn(iff.Else, v, model)): return true;
        case SelectStmt sel when sel.Arms.Any(arm => IsModifiedIn(arm.Body, v, model)): return true;
        default: break;
      }
    return false;
  }

  /// <summary>
  /// O16: the proven [lo,hi] range of an array-index expression, or null when unknown.
  /// Covers a compile-time constant, an active FOR counter, and an affine counter
  /// expression (counter +/- constant), so neighbour accesses like a(i-1)/a(i+1) prove in
  /// range. Range arithmetic is exact (the index value is exactly this expression).
  /// </summary>
  private (long Lo, long Hi)? IndexRangeOf(Expression idx) {
    if (this.Pb36Folder.TryFold(idx) is { Integer: { } c })
      return (c, c);
    switch (idx) {
      case NameExpr n when model.VariableBindings.TryGetValue(n, out var v) && this._forRanges.TryGetValue(v, out var r):
        return r;
      case BinaryExpr { Op: BinaryOp.Add } b:
        if (this.IndexRangeOf(b.Left) is { } la && this.Pb36Folder.TryFold(b.Right) is { Integer: { } ra })
          return (la.Lo + ra, la.Hi + ra);
        if (this.IndexRangeOf(b.Right) is { } ra2 && this.Pb36Folder.TryFold(b.Left) is { Integer: { } la2 })
          return (ra2.Lo + la2, ra2.Hi + la2);
        return null;
      case BinaryExpr { Op: BinaryOp.Subtract } b
          when this.IndexRangeOf(b.Left) is { } ls && this.Pb36Folder.TryFold(b.Right) is { Integer: { } rs }:
        return (ls.Lo - rs, ls.Hi - rs);
      case BinaryExpr { Op: BinaryOp.Multiply } b:
        // scaling by a constant (strided access a(i*2)) - the endpoints flip when k < 0
        if (this.IndexRangeOf(b.Left) is { } lm && this.Pb36Folder.TryFold(b.Right) is { Integer: { } rm })
          return ScaleRange(lm, rm);
        if (this.IndexRangeOf(b.Right) is { } rm2 && this.Pb36Folder.TryFold(b.Left) is { Integer: { } lm2 })
          return ScaleRange(rm2, lm2);
        return null;
      default:
        return null;
    }
  }

  private static (long Lo, long Hi) ScaleRange((long Lo, long Hi) r, long k)
    => k >= 0 ? (r.Lo * k, r.Hi * k) : (r.Hi * k, r.Lo * k);

  /// <summary>The proven range of <paramref name="e"/> only when it is NOT itself a constant - i.e. a genuine FOR-counter / affine-counter expression (so SCCP keeps the constant-vs-constant cases).</summary>
  private (long Lo, long Hi)? CounterRangeOf(Expression e)
    => this.Pb36Folder.TryFold(e) is { Integer: not null } ? null : this.IndexRangeOf(e);

  /// <summary>
  /// pb36 O16: true when an INTEGER add/subtract <paramref name="b"/> over a FOR-counter
  /// affine range provably stays inside 16 bits, so it can never raise Error 6 - the
  /// $ERROR OVERFLOW check is dead and can be dropped. Only affine counter expressions
  /// (counter +/- const) are range-known, and their single operands are themselves 16-bit,
  /// so a result inside [-32768,32767] means the operation did not overflow.
  /// </summary>
  private bool ProvablyNoOverflow(BinaryExpr b)
    => this.Optimize
       && b.Op is BinaryOp.Add or BinaryOp.Subtract
       && this.IndexRangeOf(b) is { } r
       && r.Lo >= short.MinValue && r.Hi <= short.MaxValue;

  /// <summary>
  /// pb36 O16: true when the divisor of <paramref name="b"/> has a FOR-counter range that
  /// excludes zero, so the integer divide can never raise Error 11 - the divide-by-zero
  /// guard is dead. (The guard tests only for zero, so the unchanged MININT \ -1 overflow
  /// behaviour is unaffected.)
  /// </summary>
  private bool DivisorNonZero(BinaryExpr b)
    => this.Optimize
       && this.IndexRangeOf(b.Right) is { } r
       && (r.Lo > 0 || r.Hi < 0);

  /// <summary>
  /// pb36 O16 (general branch folding): a signed 16-bit comparison of a range-known FOR
  /// counter expression against a constant whose result is invariant over the range folds
  /// to the constant boolean (-1/0). Fires in ordinary code (no $ERROR needed) - the value
  /// equals what the runtime compare would produce, so output is byte-identical.
  /// </summary>
  private bool TryEmitRangeComparison(BinaryExpr b) {
    if (!this.Optimize)
      return false;
    if (b.Op is not (BinaryOp.Less or BinaryOp.Greater or BinaryOp.LessEqual
        or BinaryOp.GreaterEqual or BinaryOp.Equal or BinaryOp.NotEqual))
      return false;
    // both operands must be signed integers no wider than 16 bits (counter ranges are
    // signed INTEGER); a DWORD/unsigned side would compare unsigned and break the fold
    if (model.TypeOf(b.Left) is not ScalarType { IsFloat: false, ByteSize: <= 2, Signed: true }
        || model.TypeOf(b.Right) is not ScalarType { IsFloat: false, ByteSize: <= 2, Signed: true })
      return false;

    (long Lo, long Hi) range;
    long c;
    BinaryOp op;
    if (this.Pb36Folder.TryFold(b.Right) is { Integer: { } rc } && this.CounterRangeOf(b.Left) is { } lr) {
      range = lr; c = rc; op = b.Op;
    } else if (this.Pb36Folder.TryFold(b.Left) is { Integer: { } lc } && this.CounterRangeOf(b.Right) is { } rr) {
      range = rr; c = lc; op = SwapComparison(b.Op);   // normalise to "range OP const"
    } else
      return false;

    // a 16-bit-overflowing affine range would wrap at runtime, so the proven range is unsafe
    if (range.Lo < short.MinValue || range.Hi > short.MaxValue)
      return false;

    if (FoldRangeCompare(range.Lo, range.Hi, op, c) is not { } verdict)
      return false;
    this._asm.Mov(Reg.AX, verdict ? -1 : 0);   // PB boolean: TRUE = -1, FALSE = 0
    return true;
  }

  private static BinaryOp SwapComparison(BinaryOp op) => op switch {
    BinaryOp.Less => BinaryOp.Greater,
    BinaryOp.Greater => BinaryOp.Less,
    BinaryOp.LessEqual => BinaryOp.GreaterEqual,
    BinaryOp.GreaterEqual => BinaryOp.LessEqual,
    _ => op, // Equal / NotEqual are symmetric
  };

  /// <summary>True/false when "v OP const" is invariant for every v in [lo,hi]; null when it varies.</summary>
  private static bool? FoldRangeCompare(long lo, long hi, BinaryOp op, long c) => op switch {
    BinaryOp.Less => hi < c ? true : lo >= c ? false : null,
    BinaryOp.LessEqual => hi <= c ? true : lo > c ? false : null,
    BinaryOp.Greater => lo > c ? true : hi <= c ? false : null,
    BinaryOp.GreaterEqual => lo >= c ? true : hi < c ? false : null,
    BinaryOp.Equal => lo == c && hi == c ? true : c < lo || c > hi ? false : (bool?)null,
    BinaryOp.NotEqual => c < lo || c > hi ? true : lo == c && hi == c ? false : (bool?)null,
    _ => null,
  };

  /// <summary>
  /// Conservative allow-list: true only when no statement in <paramref name="body"/> can
  /// change <paramref name="counter"/> - so a constant From/To range holds throughout. Only
  /// counter-safe statement shapes pass; a call (BYREF aliasing), GOSUB/GOTO, INPUT/READ, a
  /// write to the counter, or any unrecognised statement makes it decline. Sound by design:
  /// anything not provably safe is rejected.
  /// </summary>
  private static bool CounterStableInBody(IReadOnlyList<Statement> body, VariableSymbol counter, SemanticModel model) {
    foreach (var s in body)
      switch (s) {
        case AssignStmt a:
          if (WritesCounter(a.Target, counter, model) || !CallFree(a.Value, model)
              || (a.Target is not NameExpr && !CallFree(a.Target, model)))
            return false;
          break;
        case IncrDecrStmt id:
          if (WritesCounter(id.Target, counter, model) || (id.Amount != null && !CallFree(id.Amount, model)))
            return false;
          break;
        case PrintStmt p:
          if ((p.FileNumber != null && !CallFree(p.FileNumber, model))
              || p.Items.Any(i => i.Value != null && !CallFree(i.Value, model)))
            return false;
          break;
        case IfStmt iff:
          if (!CallFree(iff.Condition, model) || !CounterStableInBody(iff.Then, counter, model)
              || iff.ElseIfs.Any(e => !CallFree(e.Condition, model) || !CounterStableInBody(e.Body, counter, model))
              || (iff.Else != null && !CounterStableInBody(iff.Else, counter, model)))
            return false;
          break;
        case SelectStmt sel:
          if (!CallFree(sel.Subject, model) || sel.Arms.Any(arm => !CounterStableInBody(arm.Body, counter, model)))
            return false;
          break;
        case MetaStmt or EquateStmt or DefTypeStmt or DataStmt:
          break;
        default:
          return false; // calls, GOSUB/GOTO, INPUT/READ, nested loops, anything unrecognised
      }
    return true;
  }

  private static bool WritesCounter(Expression target, VariableSymbol counter, SemanticModel model)
    => target is NameExpr && model.VariableBindings.TryGetValue(target, out var s) && ReferenceEquals(s, counter);

  /// <summary>
  /// True when no user-procedure call appears in the tree - a call could pass the counter
  /// BYREF and rewrite it. Array reads and intrinsics (which never take a user var BYREF)
  /// are fine. Sound by design: any unrecognised expression shape returns false.
  /// </summary>
  private static bool CallFree(Expression e, SemanticModel model) => e switch {
    _ when model.CallBindings.ContainsKey(e) || model.ProcPtrCalls.ContainsKey(e) => false,
    IntegerLiteralExpr or FloatLiteralExpr or StringLiteralExpr or NamedConstantExpr => true,
    NameExpr => true,
    UnaryExpr u => CallFree(u.Operand, model),
    BinaryExpr b => CallFree(b.Left, model) && CallFree(b.Right, model),
    CallOrIndexExpr c => c.Arguments.All(a => CallFree(a, model)),
    MemberExpr m => CallFree(m.Target, model),
    ByValArgExpr v => CallFree(v.Value, model),
    _ => false,
  };

  /// <summary>The register a variable is currently resident in (O5 FOR counter in SI / accumulator in DI), or null when it lives in memory.</summary>
  private Reg? ResidentRegOf(VariableSymbol symbol) {
    if (this._registerCounter is { } counter && ReferenceEquals(counter.Symbol, symbol))
      return counter.Reg;
    if (this._registerAccumulator is { } accumulator && ReferenceEquals(accumulator.Symbol, symbol))
      return accumulator.Reg;
    return null;
  }
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
  /// unconditionally, <paramref name="libraries"/> contribute units on demand
  /// (<c>$LINK</c>). Link failures surface as compile diagnostics.
  /// </summary>
  public byte[] EmitExecutable(IReadOnlyList<PbuFile> units, IReadOnlyList<PblFile> libraries) {
    ArgumentNullException.ThrowIfNull(units);
    ArgumentNullException.ThrowIfNull(libraries);
    this._allowExternalCalls = units.Count > 0 || libraries.Count > 0;

    // pb36 O2/O10: drop unreachable statements and redundant DEF SEGs first -
    // dead code also vanishes from the trivial-lowering analysis below
    if (this.Optimize && !this._isUnit) {
      Pb36Pruner.Prune(model);
      Pb36FloatDemotion.Apply(model);
      this._ipcp = Pb36Ipcp.Analyze(model); // O18: constants into callee bodies
    }

    // P7: programs whose only effect is printing compile-time text lower to a
    // raw COM-style image of a few dozen bytes (docs/PB36.md) - a lean-output
    // optimization, available to any dialect under the optimizer flag
    if (this.Optimize && !this._allowExternalCalls && !this._isUnit
        && this.TryLowerTrivialProgram() is { } trivial)
      return trivial;

    var asm = this._asm;
    var userMain = asm.DefineLabel("user_main");
    this._scratch = asm.DefineLabel("cg_scratch");

    this._rt.EnableBss = this.Optimize && !this._allowExternalCalls && !this._isUnit;
    this._rt.Cpu386 = this.Optimize && this.Cpu386;
    this._rt.EmitEntry(asm, userMain);

    // pb36 (docs/PB36.md P1): the runtime is emitted AFTER user code, trimmed
    // to the sections the program actually reaches; pb35 keeps today's layout
    var trimRuntime = this.Optimize && !this._allowExternalCalls;
    if (!trimRuntime)
      this._rt.EmitProcedures(asm);
    else
      this._rt.BindDeferred(asm); // labels exist now, the trimmed bodies follow the user code

    // $OPTIMIZE SIZE|SPEED - one per module (PBC -OZF preselects SPEED)
    var optimizeMetas = model.MetaStatements.Where(m => m.Command.Equals("OPTIMIZE", StringComparison.OrdinalIgnoreCase)).ToList();
    if (optimizeMetas.Count > 1)
      this.Errors.Add(new(optimizeMetas[1].Position, "only one $OPTIMIZE per module"));
    if (optimizeMetas.Count > 0 && optimizeMetas[0].Arguments is [{ } optMode, ..])
      this.OptimizeSpeed = optMode.Text.Equals("SPEED", StringComparison.OrdinalIgnoreCase);

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

    this.PrepareCse(model.MainBody);
    this.PrepareSccp(model.MainBody);
    this.BeginFrame(skipZeroing: this.Optimize && !ContainsErrorHandling(model.MainBody));
    this.EmitChainCommonLoad();             // absorb a CHAIN handoff, when present
    this._trackResume = ContainsErrorHandling(model.MainBody);
    foreach (var statement in model.MainBody)
      this.EmitStatement(statement);

    // implicit END
    asm.Mov(Reg.AL, (Imm)0);
    asm.Jmp(this._rt.Exit);
    this.EndFrame();
    this._trackResume = false;

    foreach (var proc in model.ProcedureList)
      if (!proc.IsExternal)
        this.EmitProcedure(proc);

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

    this.EmitDataArea(trimmedSections);
    this._rt.PlaceBss(asm); // pb36 P3: zero blobs live behind the image

    var image = this._allowExternalCalls ? this.LinkImage(units, libraries) : asm.ToArray();
    if (image.Length == 0)
      return []; // link errors already reported

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

  #region frames & temporaries

  /// <summary>
  /// Opens a BP frame. The frame size is not known until the body has been
  /// emitted, so the SUB SP immediate is a label whose "position" is patched
  /// to the final byte count by <see cref="EndFrame"/>.
  /// </summary>
  private void BeginFrame(bool skipZeroing = false, Label? tailEntry = null) {
    var asm = this._asm;
    this._frameBytesLabel = asm.DefineLabel();
    this._frameWordsLabel = asm.DefineLabel();
    // their "positions" are byte counts, not image offsets - never relocate
    this._frameBytesLabel.IsConstant = true;
    this._frameWordsLabel.IsConstant = true;
    this._tempBytes = 0;
    this._tempMax = 0;

    asm.Push(Reg.BP);
    asm.Mov(Reg.BP, Reg.SP);
    asm.Mov(Reg.CX, Imm.OffsetOf(this._frameBytesLabel));
    asm.Sub(Reg.SP, Reg.CX);
    // pb36 O14: a tail self-call rewrites its parameter slots and re-enters
    // here - the frame is reused, locals re-zero exactly like a fresh call
    if (tailEntry != null)
      asm.MarkLabel(tailEntry);
    if (skipZeroing)
      return; // pb36 O19: every local is provably assigned before use (temps always are)
    // zero the whole frame: numeric locals start at 0, strings at handle 0
    asm.Push(Reg.DS);
    asm.Pop(Reg.ES);
    asm.Mov(Reg.DI, Reg.SP);
    asm.Mov(Reg.CX, Imm.OffsetOf(this._frameWordsLabel));
    asm.Xor(Reg.AX, Reg.AX);
    asm.Rep();
    asm.Stosw();
  }

  private void EndFrame() {
    var bytes = (this._frameLocalBytes + this._cseBytes + this._tempMax + 1) & ~1;
    this._frameBytesLabel.Position = bytes;
    this._frameWordsLabel.Position = bytes / 2;
    this._frameLocalBytes = 0;
    this._cseBytes = 0;
    this._cseMarks = null;
  }

  /// <summary>pb36 O3: runs the common-subexpression analysis for a body and reserves its frame slots; call right before <see cref="BeginFrame"/>.</summary>
  private void PrepareCse(IReadOnlyList<Statement> body) {
    this._cseMarks = null;
    this._cseBytes = 0;
    if (!this.Optimize)
      return;
    var result = Pb36CommonSubexpr.Analyze(body, model);
    if (result.SlotCount == 0)
      return;
    this._cseMarks = result.Marks;
    this._cseBytes = result.SlotCount * 4;
  }

  /// <summary>
  /// pb36 O17: runs the SSA + SCCP mid-end over a body and records the variable
  /// reads it proves constant (<see cref="_provenReads"/>), which the emitter
  /// folds. Null when the body is not analyzable (loops/SELECT/unstructured flow)
  /// or nothing is proven - then emission is exactly as before.
  /// </summary>
  private void PrepareSccp(IReadOnlyList<Statement> body, VariableSymbol? implicitResult = null) {
    this._provenReads = null;
    this._copyReads = null;
    this._deadStatements = null;
    if (!this.Optimize)
      return;
    if (Ssa.ControlFlowGraph.TryBuild(body) is not { } cfg)
      return;
    var implicitlyRead = implicitResult != null ? new[] { implicitResult } : null;
    if (Ssa.SsaForm.TryBuild(model, cfg, implicitlyRead) is not { } ssa)
      return;
    var proven = Ssa.Sccp.Solve(model, ssa);
    if (proven.Count > 0)
      this._provenReads = proven;
    // O2: assignments whose result SCCP propagated away (or never read) are dead
    var dead = Ssa.DeadStore.Compute(model, ssa, proven);
    // copy propagation: redirect reads of a copy y = x to x and drop the copy
    var (copyReads, deadCopies) = Pb36CopyProp.Analyze(ssa);
    if (copyReads.Count > 0)
      this._copyReads = copyReads;
    foreach (var s in deadCopies)
      dead.Add(s);
    if (dead.Count > 0)
      this._deadStatements = dead;
  }

  /// <summary>Reserves a BP-relative scratch block; release in reverse order.</summary>
  private Mem AllocTemp(int bytes, OperandSize size = OperandSize.Word) {
    bytes = (bytes + 1) & ~1;
    this._tempBytes += bytes;
    this._tempMax = Math.Max(this._tempMax, this._tempBytes);
    return Mem.At(Reg.BP, -(this._frameLocalBytes + this._cseBytes + this._tempBytes)).WithSize(size);
  }

  private void ReleaseTemp(int bytes) => this._tempBytes -= (bytes + 1) & ~1;

  #endregion

  #region slots, literals & labels

  private Label SlotOf(VariableSymbol symbol) {
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

  private Label ProcLabelOf(ProcedureSymbol proc) {
    if (!this._procLabels.TryGetValue(proc, out var label))
      // DECLAREd-but-undefined procedures resolve at link time by name; overloaded
      // definitions (PB 3.6) get an index suffix so each has its own label (the
      // first/only one keeps the plain p_<name> for byte-identical output).
      this._procLabels[proc] = label = proc.IsExternal && this._allowExternalCalls
        ? this._asm.External(proc.Name)
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
      this.EmitDataPool();
    }

    asm.Align(2);
    asm.MarkLabel(this._scratch);
    asm.Db(new byte[16]);   // 12 for the 32-bit shuffles + room for two staged QWORDs (C1 quad bitwise)

    foreach (var (slot, value) in this._floatConstants) {
      asm.Align(2);
      asm.MarkLabel(slot);
      asm.Dq(value);
    }

    foreach (var (slot, value) in this._quadConstants) {
      asm.Align(2);
      asm.MarkLabel(slot);
      asm.Db([.. BitConverter.GetBytes(value)]);
    }

    this.EmitLiteralPool(asm);

    foreach (var (symbol, label) in this._variableSlots) {
      asm.Align(2);
      asm.MarkLabel(label);
      var bytes = symbol.ArrayClass is ArrayClass.Huge or ArrayClass.Virtual
        ? HvDescriptorBytes                       // dword bounds + EMS handle + page cache
        : Math.Max(symbol.Type.Size, 1);
      asm.Db(new byte[bytes]);
    }

    foreach (var (symbol, label) in this._shadowDescriptors) {
      asm.Align(2);
      asm.MarkLabel(label);
      asm.Db(new byte[8 + ((ArrayType)symbol.Type).Rank * 4]);
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

  #region value categories

  /// <summary>
  /// Evaluation-register category. <see cref="ValueKind.Int64"/> (QUAD) values
  /// travel on the x87 stack like floats - the 64-bit mantissa holds the full
  /// integer range exactly - but print/store as integers.
  /// </summary>
  private enum ValueKind { Int16, Int32, Int64, Float, Str }

  private static ValueKind KindOf(PbType type) => type switch {
    ScalarType { IsFloat: true } => ValueKind.Float,
    ScalarType { ByteSize: <= 2 } => ValueKind.Int16,
    ScalarType { ByteSize: 8 } => ValueKind.Int64,
    ScalarType => ValueKind.Int32,
    PointerType or ProcPtrType => ValueKind.Int32, // far pointers are 32-bit values
    BcdType => ValueKind.Float,   // FIX/BCD compute as EXT on the x87 stack
    StringType or FixedStringType or FlexType or AsciizType => ValueKind.Str,
    _ => ValueKind.Int16,
  };

  #endregion

  #region statements

  private bool _trackResume;

  /// <summary>
  /// Emits one statement; inside scopes containing ON ERROR/RESUME every
  /// statement additionally records its own start and successor offsets so
  /// RESUME / RESUME NEXT can re-enter after an error unwound the stack.
  /// </summary>
  private void EmitStatement(Statement statement) {
    // pb36 O2: a dead store (pure RHS, value never really read) is not emitted
    if (this._deadStatements != null && this._deadStatements.Contains(statement))
      return;
    if (!this._trackResume || statement is LabelStmt or DataStmt or MetaStmt or EquateStmt or DefTypeStmt) {
      this.EmitStatementCore(statement);
      return;
    }
    var asm = this._asm;
    var start = asm.DefineLabel();
    var after = asm.DefineLabel();
    asm.MarkLabel(start);
    asm.Mov(Mem.Word(asm.Lbl("rt_resume")), Imm.OffsetOf(start));
    asm.Mov(Mem.Word(asm.Lbl("rt_resumenext")), Imm.OffsetOf(after));
    this.EmitStatementCore(statement);
    asm.MarkLabel(after);
  }

  private void EmitStatementCore(Statement statement) {
    var asm = this._asm;
    switch (statement) {
      // compile-time declarations carry no code here; a PB 3.6 nested SUB/FUNCTION is
      // lifted to its own top-level proc and emitted separately, not inline.
      case SubDecl or FunctionDecl or DeclareStmt or TypeDecl or UnionDecl or EnumDecl or DefTypeStmt or DefFnDecl:
        break;

      case AssignStmt a:
        this.EmitAssign(a);
        break;

      case PrintStmt p:
        this.EmitPrint(p);
        break;

      case IfStmt i:
        this.EmitIf(i);
        break;

      case ForStmt f:
        this.EmitFor(f);
        break;

      case DoLoopStmt d:
        this.EmitDoLoop(d);
        break;

      case SelectStmt s:
        this.EmitSelect(s);
        break;

      case LabelStmt l:
        asm.MarkLabel(this.UserLabel(l.Name));
        // ERL bookkeeping: numeric line labels only (PB: labels do not count)
        if (this._trackResume && l.Name.All(char.IsAsciiDigit) && int.TryParse(l.Name, out var lineNumber))
          asm.Mov(Mem.Word(asm.Lbl("rt_erl")), lineNumber & 0xFFFF);
        break;

      case GotoStmt g:
        asm.Jmp(this.UserLabel(g.Target));
        break;

      case GosubStmt g:
        asm.Call(this.UserLabel(g.Target));
        break;

      case GotoPtrStmt gp:
        this.EmitGotoGosubPtr(gp.Pointer, isGosub: false);
        break;

      case GosubPtrStmt gsp:
        this.EmitGotoGosubPtr(gsp.Pointer, isGosub: true);
        break;

      case OnGotoStmt og:
        this.EmitOnGoto(og);
        break;

      case ReturnStmt { Target: null }:
        asm.Ret();
        break;

      case IncrDecrStmt id:
        this.EmitIncrDecr(id);
        break;

      case CallStmt c:
        this.EmitCallStatement(c);
        break;

      case ExitStmt e:
        this.EmitExit(e);
        break;

      case IterateStmt it:
        this.EmitIterate(it);
        break;

      case WriteStmt write:
        this.EmitWrite(write);
        break;

      case EndStmt e:
        if (e.ExitCode != null) {
          this.EmitExpression(e.ExitCode);
          this.Coerce(model.TypeOf(e.ExitCode), PbType.Integer, e.ExitCode);
        } else
          asm.Xor(Reg.AX, Reg.AX);
        asm.Jmp(this._rt.Exit);
        break;

      case DimStmt dim:
        this.EmitDim(dim);
        break;

      case RedimStmt redim:
        this.EmitRedim(redim);
        break;

      case EraseStmt erase:
        this.EmitErase(erase);
        break;

      case MidAssignStmt mid:
        this.EmitMidAssign(mid);
        break;

      case AscAssignStmt ascAssign:
        this.EmitAscAssign(ascAssign);
        break;

      case StdOutStmt stdOut:
        this.EmitStdOut(stdOut);
        break;

      case StdInStmt stdIn:
        this.EmitStdIn(stdIn);
        break;

      case LsetRsetStmt ls:
        this.EmitLsetRset(ls);
        break;

      case OpenStmt open:
        this.EmitOpen(open);
        break;

      case CloseStmt close:
        this.EmitClose(close);
        break;

      case InputStmt input:
        this.EmitInput(input);
        break;

      case GetPutFileStmt gp:
        this.EmitGetPutFile(gp);
        break;

      case SeekStmt seek:
        this.EmitSeekStatement(seek);
        break;

      case FieldStmt field:
        this.EmitField(field);
        break;

      case ChainStmt chain:
        this.EmitChain(chain);
        break;

      case SwapStmt sw:
        this.EmitSwap(sw);
        break;

      case BitStmt bit:
        this.EmitBit(bit);
        break;

      case ReplaceStmt replace:
        this.EmitReplaceStmt(replace);
        break;

      case ExitFarStmt ef:
        this.EmitExitFar(ef);
        break;

      case ArraySortStmt sort:
        this.EmitArraySort(sort);
        break;

      case ArrayScanStmt scan:
        this.EmitArrayScan(scan);
        break;

      case DefSegStmt seg:
        this.EmitDefSeg(seg);
        break;

      case CallPtrStmt cp:
        this.EmitCallPtr(cp);
        break;

      case OnErrorStmt oe:
        this.EmitOnError(oe);
        break;

      case ResumeStmt rs:
        this.EmitResume(rs);
        break;

      case ErrorStmt err:
        this.EmitError(err);
        break;

      case ReadStmt read:
        this.EmitRead(read);
        break;

      case RestoreStmt restore:
        this.EmitRestore(restore);
        break;

      case OnEventStmt or EventControlStmt:
        break; // event statements are recorded-but-inert (no event dispatch; SVGA hooks ints itself)

      case CommandStmt cmd:
        this.EmitCommand(cmd);
        break;

      case InlineAsmStmt ia:
        this.EmitInlineAsm(ia);
        break;

      case MetaStmt meta:
        this.ApplyMeta(meta);
        break;

      case EquateStmt or DefTypeStmt or DataStmt:
        break; // declarations & module bookkeeping - nothing to execute

      default:
        this.Unsupported(statement);
        break;
    }
  }

  /// <summary>COMMON scalars in declaration order - the stable cross-image CHAIN layout.</summary>
  private List<VariableSymbol> CommonVariables() {
    var result = new List<VariableSymbol>();
    foreach (var statement in model.MainBody)
      if (statement is DimStmt { Storage: StorageClass.Common } dim)
        foreach (var v in dim.Variables) {
          var symbol = this.LookupVariable(v.Name, v.Suffix) ?? this.LookupVariable(v.Name, v.Suffix, isArray: true);
          if (symbol == null)
            continue;
          if (symbol.IsArray) {
            this.Unsupported(dim.Position, $"COMMON array {v.Name} across CHAIN (scalars and strings only)");
            continue;
          }
          if (!result.Contains(symbol))
            result.Add(symbol);
        }
    return result;
  }

  /// <summary>
  /// CHAIN file$: COMMON values stream into PBCHAIN.$$$ (declaration order),
  /// then the target runs via DOS EXEC and this image exits with its code.
  /// RUN file$: same transfer without the COMMON handoff.
  /// </summary>
  private void EmitChain(ChainStmt chain) {
    var asm = this._asm;
    var commons = chain.IsRun ? [] : this.CommonVariables();
    if (commons.Count > 0) {
      asm.Call(this._rt.ChainOpenWrite);
      foreach (var symbol in commons) {
        var cell = this.TryDirectCell(symbol)!.Value;
        if (symbol.Type is StringType or FlexType) {
          asm.Mov(Reg.AX, cell.WithSize(OperandSize.Word));
          asm.Call(this._rt.ChainWriteStr);
        } else {
          asm.Lea(Reg.DX, cell);
          asm.Mov(Reg.CX, Math.Max(symbol.Type.Size, 1));
          asm.Call(this._rt.ChainWrite);
        }
      }
      asm.Xor(Reg.AL, Reg.AL);              // close, keep the file
      asm.Call(this._rt.ChainClose);
    }

    this.EmitExpression(chain.Target);
    asm.Call(this._rt.ChainExec);           // never returns
  }

  /// <summary>The chained-to side: absorb PBCHAIN.$$$ into the COMMON cells, then delete it.</summary>
  private void EmitChainCommonLoad() {
    var asm = this._asm;
    var commons = this.CommonVariables();
    if (commons.Count == 0)
      return;
    var skip = asm.DefineLabel();
    asm.Call(this._rt.ChainOpenRead);
    asm.Test(Reg.AX, Reg.AX);
    asm.Jz(skip);
    foreach (var symbol in commons) {
      var cell = this.TryDirectCell(symbol)!.Value;
      if (symbol.Type is StringType or FlexType) {
        asm.Call(this._rt.ChainReadStr);
        asm.Lea(Reg.BX, cell);
        asm.Call(this._rt.StrAssign);
      } else {
        asm.Lea(Reg.DX, cell);
        asm.Mov(Reg.CX, Math.Max(symbol.Type.Size, 1));
        asm.Call(this._rt.ChainRead);
      }
    }
    asm.Mov(Reg.AL, (Imm)1);                // close + delete
    asm.Call(this._rt.ChainClose);
    asm.MarkLabel(skip);
  }

  /// <summary>FIELD #n, w AS a$, ...: registers record windows with the runtime.</summary>
  private void EmitField(FieldStmt field) {
    var asm = this._asm;
    foreach (var (width, target) in field.Fields) {
      if (model.TypeOf(target) is not (StringType or FlexType)) {
        this.Unsupported(field.Position, "FIELD target must be a dynamic string");
        continue;
      }
      this.EmitInt16Argument(UnwrapFileNumber(field.FileNumber));
      asm.Push(Reg.AX);
      this.EmitInt16Argument(width);
      asm.Push(Reg.AX);
      if (this.EmitPlace(target) is not { } place) {
        asm.Pop(Reg.AX);
        asm.Pop(Reg.AX);
        continue;
      }
      asm.Lea(Reg.BX, place.Cell);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.AX);
      asm.Call(this._rt.FieldAdd);
    }
  }

  /// <summary>$ERROR ... ON|OFF toggles the check state lexically; other metas were consumed earlier.</summary>
  private void ApplyMeta(MetaStmt meta) {
    if (!meta.Command.Equals("ERROR", StringComparison.OrdinalIgnoreCase) || meta.Arguments.Count < 2)
      return;
    var kind = meta.Arguments[0].Text.ToUpperInvariant();
    var mode = meta.Arguments[^1].Text.Equals("ON", StringComparison.OrdinalIgnoreCase);
    switch (kind) {
      case "BOUNDS": this.CheckBounds = mode; break;
      case "NUMERIC": this.CheckNumeric = mode; break;
      case "OVERFLOW": this.CheckOverflow = mode; break;
      case "STACK": this.CheckStack = mode; break;
      case "ALL":
        this.CheckBounds = this.CheckNumeric = this.CheckOverflow = this.CheckStack = mode;
        break;
    }
  }

  /// <summary>Generic keyword statements (BEEP, POKE, OUT, GET$, REG, SHIFT, ...).</summary>
  private void EmitCommand(CommandStmt cmd) {
    var asm = this._asm;
    switch (cmd.Keyword) {
      case "KILL" when cmd.Arguments is [{ } name]:
        this.EmitExpression(name);
        asm.Call(this._rt.Kill);
        break;

      case "NAME" when cmd.Arguments is [{ } oldName, { } newName]:
        this.EmitExpression(oldName);
        asm.Push(Reg.AX);
        this.EmitExpression(newName);
        asm.Mov(Reg.DX, Reg.AX);
        asm.Pop(Reg.AX);
        asm.Call(this._rt.Rename);
        break;

      case "POKE":
        this.EmitPoke(cmd);
        break;

      case "OUT":
        this.EmitOut(cmd);
        break;

      case "WAIT":
        this.EmitWait(cmd);
        break;

      case "REG":
        this.EmitRegStatement(cmd);
        break;

      case "INTERRUPT":
        this.EmitInterrupt(cmd);
        break;

      case "SHIFT LEFT" or "SHIFT RIGHT" or "ROTATE LEFT" or "ROTATE RIGHT":
        this.EmitShiftRotate(cmd);
        break;

      case "GET$" or "PUT$":
        this.EmitGetPutString(cmd);
        break;

      case "POKE$" when cmd.Arguments is [{ } pokeAddr, { } pokeValue]:
        this.EmitInt16Argument(pokeAddr);
        asm.Push(Reg.AX);
        this.EmitExpression(pokeValue);
        asm.Pop(Reg.DI);
        asm.Call(this._rt.PokeStr);
        break;

      case "CLS":
        asm.Call(this._rt.Cls);
        break;

      case "ERRCLEAR":
        asm.Mov(Mem.Word(asm.Lbl("rt_err")), (Imm)0);
        break;

      case "SETEOF" when cmd.Arguments is [{ } setEofFile]:
        // truncate at the current position: DOS write of 0 bytes
        this.EmitInt16Argument(UnwrapFileNumber(setEofFile));
        asm.Call(this._rt.FHandle);
        asm.Xor(Reg.CX, Reg.CX);
        asm.Mov(Reg.AH, 0x40);
        asm.Int(0x21);
        break;

      case "LOCATE": {
        if (cmd.Arguments.Count >= 1 && cmd.Arguments[0] is { } row)
          this.EmitInt16Argument(row);
        else
          asm.Xor(Reg.AX, Reg.AX);
        asm.Push(Reg.AX);
        if (cmd.Arguments.Count >= 2 && cmd.Arguments[1] is { } column)
          this.EmitInt16Argument(column);
        else
          asm.Xor(Reg.AX, Reg.AX);
        asm.Mov(Reg.CX, Reg.AX);
        asm.Pop(Reg.AX);
        asm.Call(this._rt.Locate);
        break;
      }

      case "SCREEN" when cmd.Arguments.Count >= 1 && cmd.Arguments[0] is { } mode:
        // PB SCREEN numbers map onto BIOS modes for the ones the suites use
        this.EmitInt16Argument(mode);
        asm.Call(this._rt.ScreenMode);
        break;

      case "RANDOMIZE": {
        if (cmd.Arguments.Count >= 1 && cmd.Arguments[0] is { } seed) {
          this.EmitExpression(seed);
          this.Coerce(model.TypeOf(seed), PbType.Long, seed);
        } else {
          asm.Xor(Reg.AH, Reg.AH);
          asm.Int(0x1A);
          asm.Mov(Reg.AX, Reg.DX);
          asm.Mov(Reg.DX, Reg.CX);
        }
        asm.Mov(Mem.Word(asm.Lbl("rt_rndseed")), Reg.AX);
        asm.Mov(Mem.Word(asm.Lbl("rt_rndseed"), 2), Reg.DX);
        break;
      }

      case "BEEP":
        asm.Mov(Reg.AX, 880);
        asm.Mov(Reg.DX, 4);
        asm.Call(this._rt.Sound);
        break;

      case "SOUND" when cmd.Arguments is [{ } frequency, { } duration]: {
        this.EmitInt16Argument(frequency);
        asm.Push(Reg.AX);
        this.EmitInt16Argument(duration);
        asm.Mov(Reg.DX, Reg.AX);
        asm.Pop(Reg.AX);
        asm.Call(this._rt.Sound);
        break;
      }

      case "DELAY" when cmd.Arguments is [{ } seconds]:
        this.EmitExpression(seconds);
        this.Coerce(model.TypeOf(seconds), PbType.Double, seconds);
        asm.Call(this._rt.Delay);
        break;

      case "SLEEP": { // SLEEP [n]: wait n seconds; 0 / no argument = wait for a key
        var waitKey = asm.DefineLabel();
        var sleepDone = asm.DefineLabel();
        if (cmd.Arguments.Count >= 1 && cmd.Arguments[0] is { } sleepArg) {
          this.EmitExpression(sleepArg);
          this.Coerce(model.TypeOf(sleepArg), PbType.Double, sleepArg);
          asm.Ftst();
          asm.FstswAx();
          asm.Sahf();
          asm.Jz(waitKey);
          asm.Call(this._rt.Delay);
          asm.Jmp(sleepDone);
          asm.MarkLabel(waitKey);
          asm.Fstp(St.St0);
        } else
          asm.MarkLabel(waitKey);
        asm.Xor(Reg.AH, Reg.AH);   // BIOS blocking key read
        asm.Int(0x16);
        asm.MarkLabel(sleepDone);
        break;
      }

      case "SHELL" when cmd.Arguments is [{ } shellCmd]:
        this.EmitExpression(shellCmd);
        asm.Call(this._rt.Shell);
        break;

      case "EXECUTE" when cmd.Arguments is [{ } executeCmd]:
        // EXECUTE: run the program, then terminate
        this.EmitExpression(executeCmd);
        asm.Call(this._rt.Shell);
        asm.Xor(Reg.AL, Reg.AL);
        asm.Jmp(this._rt.Exit);
        break;

      case "PLAY": // parse-and-ignore stub: evaluate and drop the tune string
        foreach (var argument in cmd.Arguments)
          if (argument != null) {
            this.EmitExpression(argument);
            if (KindOf(model.TypeOf(argument)) == ValueKind.Str)
              asm.Call(this._rt.StrFree);
          }
        break;

      case "COLOR" or "WIDTH" or "KEY" or "VIEW" or "VIEW TEXT" or "VIEW PRINT" or "VIEW SCREEN"
        or "WINDOW" or "PALETTE" or "PALETTE USING" or "OPTION BASE":
        break; // accepted, harmless no-ops on this runtime

      default:
        this.Unsupported(cmd);
        break;
    }
  }

  private void EmitExit(ExitStmt e) {
    var asm = this._asm;
    switch (e.Kind) {
      case ExitKind.For when this._exitFor.Count > 0:
        asm.Jmp(this._exitFor.Peek());
        break;
      case ExitKind.Do or ExitKind.Loop when this._exitDo.Count > 0:
        asm.Jmp(this._exitDo.Peek());
        break;
      case ExitKind.Select when this._exitSelect.Count > 0:
        asm.Jmp(this._exitSelect.Peek());
        break;
      case ExitKind.Sub or ExitKind.Function or ExitKind.Def when this._currentProc != null:
        asm.Jmp(this._epilogue);
        break;
      default:
        this.Unsupported(e);
        break;
    }
  }

  /// <summary>ITERATE [FOR|DO]: jump to the loop's continue point (FOR increment / DO retest).</summary>
  private void EmitIterate(IterateStmt it) {
    var asm = this._asm;
    var target = it.Kind switch {
      ExitKind.For when this._iterateFor.Count > 0 => this._iterateFor.Peek(),
      ExitKind.Do when this._iterateDo.Count > 0 => this._iterateDo.Peek(),
      ExitKind.Loop when this._iterateAny.Count > 0 => this._iterateAny.Peek(),
      _ => null,
    };
    if (target == null) {
      this.Unsupported(it);
      return;
    }
    asm.Jmp(target);
  }

  /// <summary>WRITE [#n,] items: comma-delimited, strings quoted, numbers without padding.</summary>
  private void EmitWrite(WriteStmt write) {
    var asm = this._asm;
    if (write.FileNumber != null) {
      this.EmitInt16Argument(UnwrapFileNumber(write.FileNumber));
      asm.Call(this._rt.FSelect);
    }

    for (var i = 0; i < write.Items.Count; ++i) {
      if (i > 0) {
        asm.Mov(Reg.SI, Imm.OffsetOf(this.LiteralOf(",")));
        asm.Mov(Reg.CX, 1);
        asm.Call(this._rt.PrintStr);
      }
      var item = write.Items[i];
      this.EmitExpression(item);
      var kind = KindOf(model.TypeOf(item));
      if (kind == ValueKind.Str) {
        asm.Push(Reg.AX);
        asm.Mov(Reg.SI, Imm.OffsetOf(this.LiteralOf("\"")));
        asm.Mov(Reg.CX, 1);
        asm.Call(this._rt.PrintStr);
        asm.Pop(Reg.AX);
        asm.Call(this._rt.StrPrint);
        asm.Mov(Reg.SI, Imm.OffsetOf(this.LiteralOf("\"")));
        asm.Mov(Reg.CX, 1);
        asm.Call(this._rt.PrintStr);
        continue;
      }
      switch (kind) { // STR$-style text, leading space trimmed
        case ValueKind.Int16: asm.Call(this._rt.StrI16); break;
        case ValueKind.Int32: asm.Call(this._rt.StrI32); break;
        default: asm.Call(this._rt.StrF64); break;
      }
      asm.Call(this._rt.LTrim);
      asm.Call(this._rt.StrPrint);
    }

    asm.Call(this._rt.PrintNewLine);
    if (write.FileNumber != null)
      asm.Mov(Mem.Word(asm.Lbl("rt_curout")), 1);
  }

  private void EmitCondition(Expression condition) {
    // leaves truth in AX (0 / nonzero) and sets ZF accordingly
    var asm = this._asm;
    this.EmitExpression(condition);
    switch (KindOf(model.TypeOf(condition))) {
      case ValueKind.Int16:
        asm.Test(Reg.AX, Reg.AX);
        break;
      case ValueKind.Int32:
        asm.Or(Reg.AX, Reg.DX);
        break;
      case ValueKind.Int64 or ValueKind.Float:
        asm.Ftst();
        asm.FstswAx();
        asm.Fstp(St.St0);
        asm.And(Reg.AX, 0x4000);     // C3 set = zero
        asm.Xor(Reg.AX, 0x4000);     // AX nonzero exactly when value nonzero
        break;
      default:
        this.Unsupported(condition, "condition of this type");
        break;
    }
  }

  /// <summary>
  /// Folds an IF/ELSEIF condition to its constant truth value, substituting
  /// SCCP-proven reads first so cross-block constants count. Gated off under
  /// $ERROR OVERFLOW/NUMERIC, where folding (and dropping) the condition would
  /// skip a trap the real evaluation must raise.
  /// </summary>
  private long? FoldConditionWithProven(Expression condition) {
    if (this.IsUnsignedDwordCompare(condition))
      return null; // a DWORD ordered comparison must run unsigned; the type-less folder does it signed
    var folded = condition;
    if (this._provenReads is { Count: > 0 } proven && !this.CheckOverflow && !this.CheckNumeric)
      folded = SubstituteProven(condition, proven, out _);
    return this.Pb36Folder.TryFold(folded)?.Integer;
  }

  private void EmitIf(IfStmt i) {
    var asm = this._asm;

    // pb36 O17 (SCCP): a condition proven constant - locally or by cross-block
    // SSA propagation - selects one arm at compile time and the dead arm is not
    // emitted at all (whole-branch dead-code elimination). Cascades through the
    // ELSEIF chain until a non-constant condition appears.
    if (this.Optimize && this.FoldConditionWithProven(i.Condition) is { } c) {
      if (c != 0) {
        foreach (var s in i.Then)
          this.EmitStatement(s);
        return;
      }
      if (i.ElseIfs.Count > 0) {
        var (firstCond, firstBody) = i.ElseIfs[0];
        this.EmitIf(i with { Condition = firstCond, Then = firstBody, ElseIfs = i.ElseIfs.Skip(1).ToList() });
        return;
      }
      if (i.Else != null)
        foreach (var s in i.Else)
          this.EmitStatement(s);
      return;
    }

    var elseLabel = asm.DefineLabel();
    var endLabel = asm.DefineLabel();

    this.EmitCondition(i.Condition);
    asm.Jz(elseLabel);
    foreach (var s in i.Then)
      this.EmitStatement(s);
    asm.Jmp(endLabel);

    asm.MarkLabel(elseLabel);
    foreach (var (condition, body) in i.ElseIfs) {
      var next = asm.DefineLabel();
      this.EmitCondition(condition);
      asm.Jz(next);
      foreach (var s in body)
        this.EmitStatement(s);
      asm.Jmp(endLabel);
      asm.MarkLabel(next);
    }
    if (i.Else != null)
      foreach (var s in i.Else)
        this.EmitStatement(s);

    asm.MarkLabel(endLabel);
  }

  private void EmitFor(ForStmt f) {
    var asm = this._asm;
    if (f.Variable is not NameExpr name || !model.VariableBindings.TryGetValue(name, out var counter)
        || this.TryDirectCell(counter) is not { } slot) {
      this.Unsupported(f);
      return;
    }
    var kind = KindOf(counter.Type);
    if (kind == ValueKind.Str) {
      this.Unsupported(f);
      return;
    }

    // pb36 O16: register the counter's proven [From,To] range for the loop body so a
    // bounds check whose index is exactly this counter, within the array bounds, drops.
    // Disposed on every exit path (including the early returns below).
    using var _forRange = this.PushForRange(f, counter);

    // pb36 O20 ($OPTIMIZE SPEED): whole-loop algorithm replacement - empty
    // bodies, constant fills and arithmetic-series sums collapse to their
    // closed forms before unrolling is even considered
    if (kind == ValueKind.Int16 && this.TryEmitForIdiom(f, counter, slot.WithSize(OperandSize.Word)))
      return;

    // pb36 O7 ($OPTIMIZE SPEED): tiny constant-trip INTEGER loops unroll fully
    if (kind == ValueKind.Int16 && this.TryEmitUnrolledFor(f, counter, slot.WithSize(OperandSize.Word)))
      return;

    // pb36 O13 ($OPTIMIZE SPEED): a float counter on a power-of-two-fraction
    // grid runs as a scaled 16-bit integer (cheap compare/increment)
    if (kind == ValueKind.Float && this.TryEmitFixedPointFor(f, counter, slot))
      return;

    // constant steps fix the loop direction at compile time
    long? constantStep = f.Step switch {
      null => 1L,
      IntegerLiteralExpr lit => lit.Value,
      UnaryExpr { Op: UnaryOp.Negate, Operand: IntegerLiteralExpr neg } => -neg.Value,
      _ => null,
    };

    if (kind == ValueKind.Int16 && constantStep is { } fastStep) {
      // pb36 O6b: a single-statement array store a%(i%)=expr steps a pointer
      // instead of recomputing (i-lbound)*2 with IMUL on every iteration
      if (this.TryEmitForArrayStore(f, counter, slot.WithSize(OperandSize.Word), fastStep))
        return;
      // pb36 O5: an SI-clean body keeps the counter in SI - no per-iteration
      // cell traffic for the compare, increment or counter reads
      if (this.TryEmitForCounterInRegister(f, counter, slot.WithSize(OperandSize.Word), fastStep))
        return;
      // pb36 O6b: a single-statement a%(i%) read replaces the per-iteration IMUL
      // with a stepped frame-slot pointer
      if (this.TryEmitForArrayIvsr(f, counter, slot, fastStep))
        return;
      this.EmitForInt16Fast(f, slot.WithSize(OperandSize.Word), fastStep);
      return;
    }

    var counterPlace = new Place(slot, false);
    var slotBytes = kind switch { ValueKind.Int16 => 2, ValueKind.Int32 => 4, _ => 8 };
    var limit = this.AllocTemp(slotBytes);
    var step = this.AllocTemp(slotBytes);
    var limitType = kind switch { ValueKind.Int16 => PbType.Integer, ValueKind.Int32 => PbType.Long, _ => PbType.Double };

    // counter = from; limit and step into per-invocation stack temps
    this.EmitExpression(f.From);
    this.Coerce(model.TypeOf(f.From), counter.Type, f.From);
    this.EmitStorePlace(counterPlace, counter.Type, f.From);

    this.EmitExpression(f.To);
    this.Coerce(model.TypeOf(f.To), limitType, f.To);
    this.EmitStorePlace(new(limit, false), limitType, f.To);

    if (f.Step is { } stepExpr) {
      this.EmitExpression(stepExpr);
      this.Coerce(model.TypeOf(stepExpr), limitType, stepExpr);
    } else {
      asm.Mov(Reg.AX, 1);
      this.Coerce(PbType.Integer, limitType, f.From);
    }
    this.EmitStorePlace(new(step, false), limitType, f.From);

    // pb36 LICM: hoist loop-invariant pure subexpressions into the preheader
    this.EmitLicmPreheader(f, counter);

    var top = asm.DefineLabel();
    var negative = asm.DefineLabel();
    var body = asm.DefineLabel();
    var done = asm.DefineLabel();
    var continueLabel = asm.DefineLabel();
    this._exitFor.Push(done);
    this._iterateFor.Push(continueLabel);
    this._iterateAny.Push(continueLabel);
    asm.MarkLabel(top);

    switch (kind) {
      case ValueKind.Int16:
        if (constantStep is { } cs16) {
          asm.Mov(Reg.AX, slot.WithSize(OperandSize.Word));
          asm.Cmp(Reg.AX, limit.WithSize(OperandSize.Word));
          if (cs16 >= 0)
            asm.Jg(done);
          else
            asm.Jl(done);
        } else {
          asm.Mov(Reg.AX, slot.WithSize(OperandSize.Word));
          asm.Cmp(step.WithSize(OperandSize.Word), (Imm)0);
          asm.Jl(negative);
          asm.Cmp(Reg.AX, limit.WithSize(OperandSize.Word));
          asm.Jg(done);
          asm.Jmp(body);
          asm.MarkLabel(negative);
          asm.Cmp(Reg.AX, limit.WithSize(OperandSize.Word));
          asm.Jl(done);
        }
        break;

      case ValueKind.Int32: {
        var stepSign = constantStep is { } cs32 ? Math.Sign(cs32) : 0;
        if (stepSign == 0) {
          asm.Cmp(Adjust(step, 2, OperandSize.Word), (Imm)0);
          asm.Jl(negative);
        }
        if (stepSign >= 0) {
          // ascending: done when limit - counter < 0
          asm.Mov(Reg.AX, limit.WithSize(OperandSize.Word));
          asm.Mov(Reg.DX, Adjust(limit, 2, OperandSize.Word));
          asm.Sub(Reg.AX, Adjust(slot, 0, OperandSize.Word));
          asm.Sbb(Reg.DX, Adjust(slot, 2, OperandSize.Word));
          asm.Test(Reg.DX, Reg.DX);
          asm.Js(done);
          asm.Jmp(body);
        }
        if (stepSign == 0)
          asm.MarkLabel(negative);
        if (stepSign <= 0) {
          // descending: done when counter - limit < 0
          asm.Mov(Reg.AX, Adjust(slot, 0, OperandSize.Word));
          asm.Mov(Reg.DX, Adjust(slot, 2, OperandSize.Word));
          asm.Sub(Reg.AX, limit.WithSize(OperandSize.Word));
          asm.Sbb(Reg.DX, Adjust(limit, 2, OperandSize.Word));
          asm.Test(Reg.DX, Reg.DX);
          asm.Js(done);
        }
        break;
      }

      default: {
        var stepSign = constantStep is { } csf ? Math.Sign(csf) : 0;
        if (stepSign == 0) {
          asm.Fld(step.WithSize(OperandSize.Qword));
          asm.Ftst();
          asm.FstswAx();
          asm.Fstp(St.St0);
          asm.Sahf();
          asm.Jb(negative);
        }
        if (stepSign >= 0) {
          this.EmitLoadPlace(counterPlace, counter.Type, f.From);
          asm.Fcomp(limit.WithSize(OperandSize.Qword));
          asm.FstswAx();
          asm.Sahf();
          asm.Ja(done);
          asm.Jmp(body);
        }
        if (stepSign == 0)
          asm.MarkLabel(negative);
        if (stepSign <= 0) {
          this.EmitLoadPlace(counterPlace, counter.Type, f.From);
          asm.Fcomp(limit.WithSize(OperandSize.Qword));
          asm.FstswAx();
          asm.Sahf();
          asm.Jb(done);
        }
        break;
      }
    }

    asm.MarkLabel(body);
    foreach (var s in f.Body)
      this.EmitStatement(s);

    asm.MarkLabel(continueLabel);
    switch (kind) {
      case ValueKind.Int16:
        asm.Mov(Reg.AX, slot.WithSize(OperandSize.Word));
        asm.Add(Reg.AX, step.WithSize(OperandSize.Word));
        asm.Mov(slot.WithSize(OperandSize.Word), Reg.AX);
        break;
      case ValueKind.Int32:
        asm.Mov(Reg.AX, Adjust(slot, 0, OperandSize.Word));
        asm.Mov(Reg.DX, Adjust(slot, 2, OperandSize.Word));
        asm.Add(Reg.AX, step.WithSize(OperandSize.Word));
        asm.Adc(Reg.DX, Adjust(step, 2, OperandSize.Word));
        asm.Mov(Adjust(slot, 0, OperandSize.Word), Reg.AX);
        asm.Mov(Adjust(slot, 2, OperandSize.Word), Reg.DX);
        break;
      default:
        this.EmitLoadPlace(counterPlace, counter.Type, f.From);
        asm.Fadd(step.WithSize(OperandSize.Qword));
        this.EmitStorePlace(counterPlace, counter.Type, f.From);
        break;
    }
    asm.Jmp(top);
    asm.MarkLabel(done);
    this._exitFor.Pop();
    this._iterateFor.Pop();
    this._iterateAny.Pop();
    this.ReleaseTemp(slotBytes);
    this.ReleaseTemp(slotBytes);
  }

  /// <summary>
  /// The common case: 8/16-bit counter with a constant step. The increment runs
  /// at the counter's own width, so BYTE/WORD counters wrap at their type
  /// boundary (QUIRK 2.28/2.29: FOR b? = 1 TO 255 never exits) - unless
  /// $ERROR NUMERIC ON turns the wrap into runtime error 6.
  /// </summary>
  private void EmitForInt16Fast(ForStmt f, Mem slot, long step) {
    var asm = this._asm;
    var counterType = model.VariableBindings[(NameExpr)f.Variable].Type;
    var isByte = counterType is ScalarType { ByteSize: 1 };
    var unsigned = counterType is ScalarType { Signed: false };
    var cell = slot.WithSize(isByte ? OperandSize.Byte : OperandSize.Word);

    // unsigned counters read a negative STEP as its unsigned bit pattern
    // (oracle-verified: FOR w?? = 2 TO 0 STEP -1 never enters the body)
    if (unsigned && step < 0)
      step &= isByte ? 0xFF : 0xFFFF;

    this.EmitExpression(f.From);
    this.Coerce(model.TypeOf(f.From), counterType, f.From);
    if (isByte)
      asm.Mov(cell, Reg.AL);
    else
      asm.Mov(cell, Reg.AX);

    var limit = this.AllocTemp(2);
    this.EmitExpression(f.To);
    this.Coerce(model.TypeOf(f.To), counterType, f.To);
    asm.Mov(limit, Reg.AX);

    // pb36 LICM: hoist loop-invariant pure subexpressions into the preheader
    if (f.Variable is NameExpr nameVar && model.VariableBindings.TryGetValue(nameVar, out var counterSym))
      this.EmitLicmPreheader(f, counterSym);

    var top = asm.DefineLabel();
    var done = asm.DefineLabel();
    var continueLabel = asm.DefineLabel();
    this._exitFor.Push(done);
    this._iterateFor.Push(continueLabel);
    this._iterateAny.Push(continueLabel);
    asm.MarkLabel(top);
    if (isByte) {
      asm.Mov(Reg.AL, cell);
      asm.Cmp(Reg.AL, limit.WithSize(OperandSize.Byte));
    } else {
      asm.Mov(Reg.AX, cell);
      asm.Cmp(Reg.AX, limit);
    }
    if (step >= 0) {
      if (unsigned)
        asm.Ja(done);
      else
        asm.Jg(done);
    } else {
      if (unsigned)
        asm.Jb(done);
      else
        asm.Jl(done);
    }

    foreach (var s in f.Body)
      this.EmitStatement(s);

    asm.MarkLabel(continueLabel);
    var magnitude = (int)Math.Abs(step);
    if (isByte) {
      asm.Mov(Reg.AL, cell);
      if (step >= 0)
        asm.Add(Reg.AL, (Imm)magnitude);
      else
        asm.Sub(Reg.AL, (Imm)magnitude);
      if (this.CheckNumeric)
        this.EmitRaiseWhen(asm.Jnc, 6);     // byte counters are unsigned: carry = wrap
      asm.Mov(cell, Reg.AL);
    } else {
      asm.Mov(Reg.AX, cell);
      if (step >= 0)
        asm.Add(Reg.AX, magnitude);
      else
        asm.Sub(Reg.AX, magnitude);
      if (this.CheckNumeric)
        this.EmitRaiseWhen(unsigned ? asm.Jnc : asm.Jno, 6);
      asm.Mov(cell, Reg.AX);
    }
    asm.Jmp(top);
    asm.MarkLabel(done);
    this._exitFor.Pop();
    this._iterateFor.Pop();
    this._iterateAny.Pop();
    this.ReleaseTemp(2);
  }

  private void EmitDoLoop(DoLoopStmt d) {
    var asm = this._asm;
    var top = asm.DefineLabel();
    var done = asm.DefineLabel();
    var continueLabel = asm.DefineLabel();
    this._exitDo.Push(done);
    this._iterateDo.Push(continueLabel);
    this._iterateAny.Push(continueLabel);

    asm.MarkLabel(top);
    if (d.PreCondition != null) {
      this.EmitCondition(d.PreCondition);
      if (d.PreTest == LoopTestKind.While)
        asm.Jz(done);
      else
        asm.Jnz(done);
    }

    foreach (var s in d.Body)
      this.EmitStatement(s);

    asm.MarkLabel(continueLabel);
    if (d.PostCondition != null) {
      this.EmitCondition(d.PostCondition);
      if (d.PostTest == LoopTestKind.While)
        asm.Jnz(top);
      else
        asm.Jz(top);
    } else
      asm.Jmp(top);

    asm.MarkLabel(done);
    this._exitDo.Pop();
    this._iterateDo.Pop();
    this._iterateAny.Pop();
  }

  private void EmitSelect(SelectStmt s) {
    // pb36: a dense integer SELECT (all single-value constant cases) jumps through a
    // table instead of a compare chain - O(1) dispatch, same arm runs (output-identical)
    if (this.Optimize && this.TryEmitSelectJumpTable(s))
      return;

    var asm = this._asm;
    var subjectType = model.TypeOf(s.Subject);
    var kind = KindOf(subjectType);
    if (kind is ValueKind.Int64) {
      this.Unsupported(s); // QUAD subjects are not used by the corpus
      return;
    }

    var subjectBytes = kind switch { ValueKind.Int32 => 4, ValueKind.Float => 8, _ => 2 };
    var subject = this.AllocTemp(subjectBytes);
    this.EmitExpression(s.Subject);
    switch (kind) {
      case ValueKind.Int16:
        this.Coerce(subjectType, PbType.Integer, s.Subject);
        asm.Mov(subject, Reg.AX);
        break;
      case ValueKind.Int32:
        asm.Mov(subject, Reg.AX);
        asm.Mov(Adjust(subject, 2, OperandSize.Word), Reg.DX);
        break;
      case ValueKind.Float:
        // a DOUBLE slot holds every SINGLE exactly; comparisons stay x87-exact
        asm.Fstp(Adjust(subject, 0, OperandSize.Qword));
        break;
      default: // owned string handle for the SELECT's duration
        asm.Mov(subject, Reg.AX);
        break;
    }

    var endLabel = asm.DefineLabel();
    this._exitSelect.Push(endLabel);
    foreach (var arm in s.Arms) {
      var armBody = asm.DefineLabel();
      var nextArm = asm.DefineLabel();

      if (arm.Selectors.Count == 0)
        asm.Jmp(armBody); // CASE ELSE
      else {
        foreach (var selector in arm.Selectors) {
          if (selector.Value == null) {
            this.Unsupported(s);
            continue;
          }
          switch (kind) {
            case ValueKind.Int16:
              this.EmitSelectorInt16(s, subject, selector, armBody);
              break;
            case ValueKind.Int32:
              this.EmitSelectorInt32(s, subject, selector, armBody);
              break;
            case ValueKind.Float:
              this.EmitSelectorFloat(subject, selector, armBody);
              break;
            default:
              this.EmitSelectorString(s, subject, selector, armBody);
              break;
          }
        }
        asm.Jmp(nextArm);
      }

      asm.MarkLabel(armBody);
      foreach (var statement in arm.Body)
        this.EmitStatement(statement);
      asm.Jmp(endLabel);
      asm.MarkLabel(nextArm);
    }
    asm.MarkLabel(endLabel);
    if (kind == ValueKind.Str) {
      asm.Mov(Reg.AX, subject);
      asm.Call(this._rt.StrFree);
    }
    this._exitSelect.Pop();
    this.ReleaseTemp(subjectBytes);
  }

  /// <summary>
  /// pb36: a dense integer SELECT (all single-value constant cases, no ranges / IS) with
  /// a small value span dispatches through a word jump table: subtract the minimum, one
  /// unsigned bounds check, then an indexed indirect JMP - the same arm runs as the
  /// compare chain, so output is unchanged. Handles both 16-bit (Int16) and 32-bit
  /// (Int32 / LONG) subjects. Declines (false) to the chain otherwise.
  /// </summary>
  private bool TryEmitSelectJumpTable(SelectStmt s) {
    var kind = KindOf(model.TypeOf(s.Subject));
    if (kind is not (ValueKind.Int16 or ValueKind.Int32))
      return false;
    var byValue = new Dictionary<long, int>();   // case value -> first arm index (first match wins)
    int? elseArm = null;
    for (var i = 0; i < s.Arms.Count; ++i) {
      var arm = s.Arms[i];
      if (arm.Selectors.Count == 0) {
        if (elseArm != null)
          return false;
        elseArm = i;
        continue;
      }
      foreach (var sel in arm.Selectors) {
        if (sel.Value == null || sel.RangeUpper != null || sel.IsComparison != null)
          return false;
        if (kind == ValueKind.Int16) {
          if (this.Pb36Folder.TryFold(sel.Value) is not { Integer: { } v } || v is < short.MinValue or > short.MaxValue)
            return false;
          byValue.TryAdd(v, i);
        } else {
          // Int32: values must be compile-time constants in LONG range
          if (this.Pb36Folder.TryFold(sel.Value) is not { Integer: { } v } || v is < int.MinValue or > int.MaxValue)
            return false;
          byValue.TryAdd(v, i);
        }
      }
    }
    if (byValue.Count < 4)
      return false;                               // below this a compare chain is smaller
    long min = byValue.Keys.Min(), max = byValue.Keys.Max();
    var span = max - min + 1;
    if (span > 256 || span > 4L * byValue.Count)
      return false;                               // keep the table dense and small

    var asm = this._asm;
    var end = asm.DefineLabel();
    var table = asm.DefineLabel();
    var armLabels = s.Arms.Select(_ => asm.DefineLabel()).ToList();
    var defaultLabel = elseArm is { } e ? armLabels[e] : end;

    this._exitSelect.Push(end);
    this.EmitExpression(s.Subject);

    if (kind == ValueKind.Int16) {
      this.Coerce(model.TypeOf(s.Subject), PbType.Integer, s.Subject);  // subject -> AX
      if (min != 0)
        asm.Sub(Reg.AX, (Imm)(int)min);           // AX = index (0..span-1)
      asm.Cmp(Reg.AX, (Imm)(int)span);
      asm.Jae(defaultLabel);                       // unsigned: catches below-min (wrapped) and above-max
    } else {
      // Int32: subject is DX:AX after coerce to LONG
      this.Coerce(model.TypeOf(s.Subject), PbType.Long, s.Subject);     // subject -> DX:AX
      // 32-bit subtract: (DX:AX) -= min, giving the 0-based index
      // Split min into two 16-bit halves for the two-instruction 32-bit subtract
      var minLo = (int)min & 0xFFFF;
      var minHi = (int)((int)min >> 16) & 0xFFFF;
      if (min != 0) {
        asm.Sub(Reg.AX, (Imm)minLo);              // AX -= lo16(min), sets borrow
        asm.Sbb(Reg.DX, (Imm)minHi);              // DX -= hi16(min) - borrow
      }
      // In-range iff DX == 0 (index fits in 16 bits) AND AX < span (unsigned)
      asm.Test(Reg.DX, Reg.DX);
      asm.Jnz(defaultLabel);                       // high word nonzero: far out of range
      asm.Cmp(Reg.AX, (Imm)(int)span);
      asm.Jae(defaultLabel);                       // AX >= span (unsigned): below min or above max
    }

    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 1);                            // word-sized entries
    asm.Jmp(Mem.Word(Reg.BX, table));              // JMP [table + index*2]

    asm.MarkLabel(table);                          // data: only reached via the indexed jump above
    for (var v = min; v <= max; ++v)
      asm.Dw(byValue.TryGetValue(v, out var arm) ? armLabels[arm] : defaultLabel);

    for (var i = 0; i < s.Arms.Count; ++i) {
      asm.MarkLabel(armLabels[i]);
      foreach (var statement in s.Arms[i].Body)
        this.EmitStatement(statement);
      asm.Jmp(end);
    }
    asm.MarkLabel(end);
    this._exitSelect.Pop();
    return true;
  }

  private void EmitSelectorInt16(SelectStmt s, Mem subject, CaseSelector selector, Label armBody) {
    var asm = this._asm;
    this.EmitExpression(selector.Value!);
    this.Coerce(model.TypeOf(selector.Value!), PbType.Integer, selector.Value!);

    if (selector.RangeUpper != null) {
      // lower <= subject <= upper
      var noMatch = asm.DefineLabel();
      asm.Cmp(subject, Reg.AX);
      asm.Jl(noMatch);
      this.EmitExpression(selector.RangeUpper);
      this.Coerce(model.TypeOf(selector.RangeUpper), PbType.Integer, selector.RangeUpper);
      asm.Cmp(subject, Reg.AX);
      asm.Jle(armBody);
      asm.MarkLabel(noMatch);
    } else if (selector.IsComparison is { } relation) {
      asm.Mov(Reg.BX, Reg.AX);
      asm.Mov(Reg.AX, subject);
      asm.Cmp(Reg.AX, Reg.BX);
      this.EmitRelationJump(relation, armBody);
    } else {
      asm.Cmp(subject, Reg.AX);
      asm.Je(armBody);
    }
  }

  /// <summary>
  /// Float CASE selector: ST-based compares against the DOUBLE subject slot.
  /// The CASE value loads first, then the subject on top, so after FCOMPP +
  /// SAHF the flags read as subject-versus-value (JB = below, ...); x87
  /// ordered compares match the runtime's relational semantics exactly.
  /// </summary>
  private void EmitSelectorFloat(Mem subject, CaseSelector selector, Label armBody) {
    var asm = this._asm;

    void CompareSubjectWith(Expression value) {
      this.EmitExpression(value);
      this.Coerce(model.TypeOf(value), PbType.Double, value);
      asm.Fld(Adjust(subject, 0, OperandSize.Qword)); // ST0 = subject, ST1 = value
      asm.Fcompp();
      asm.FstswAx();
      asm.Sahf();
    }

    if (selector.RangeUpper != null) {
      // lower <= subject <= upper
      var noMatch = asm.DefineLabel();
      CompareSubjectWith(selector.Value!);
      asm.Jb(noMatch);
      CompareSubjectWith(selector.RangeUpper);
      asm.Jbe(armBody);
      asm.MarkLabel(noMatch);
    } else if (selector.IsComparison is { } relation) {
      CompareSubjectWith(selector.Value!);
      switch (relation) {
        case CaseComparison.Equal: asm.Je(armBody); break;
        case CaseComparison.NotEqual: asm.Jne(armBody); break;
        case CaseComparison.Less: asm.Jb(armBody); break;
        case CaseComparison.LessEqual: asm.Jbe(armBody); break;
        case CaseComparison.Greater: asm.Ja(armBody); break;
        case CaseComparison.GreaterEqual: asm.Jae(armBody); break;
      }
    } else {
      CompareSubjectWith(selector.Value!);
      asm.Je(armBody);
    }
  }

  private void EmitRelationJump(CaseComparison relation, Label armBody) {
    var asm = this._asm;
    switch (relation) {
      case CaseComparison.Equal: asm.Je(armBody); break;
      case CaseComparison.NotEqual: asm.Jne(armBody); break;
      case CaseComparison.Less: asm.Jl(armBody); break;
      case CaseComparison.LessEqual: asm.Jle(armBody); break;
      case CaseComparison.Greater: asm.Jg(armBody); break;
      case CaseComparison.GreaterEqual: asm.Jge(armBody); break;
    }
  }

  /// <summary>Loads subject - (DX:AX) into DX:AX (sign in DX, zero iff AX|DX == 0).</summary>
  private void EmitSubjectMinusValue32(Mem subject) {
    var asm = this._asm;
    asm.Mov(Reg.BX, Reg.AX);
    asm.Mov(Reg.CX, Reg.DX);
    asm.Mov(Reg.AX, subject.WithSize(OperandSize.Word));
    asm.Mov(Reg.DX, Adjust(subject, 2, OperandSize.Word));
    asm.Sub(Reg.AX, Reg.BX);
    asm.Sbb(Reg.DX, Reg.CX);
  }

  private void EmitSelectorInt32(SelectStmt s, Mem subject, CaseSelector selector, Label armBody) {
    var asm = this._asm;
    this.EmitExpression(selector.Value!);
    this.Coerce(model.TypeOf(selector.Value!), PbType.Long, selector.Value!);

    if (selector.RangeUpper != null) {
      var noMatch = asm.DefineLabel();
      this.EmitSubjectMinusValue32(subject);     // subject - lower
      asm.Test(Reg.DX, Reg.DX);
      asm.Js(noMatch);
      this.EmitExpression(selector.RangeUpper);
      this.Coerce(model.TypeOf(selector.RangeUpper), PbType.Long, selector.RangeUpper);
      this.EmitSubjectMinusValue32(subject);     // subject - upper: match when <= 0
      asm.Test(Reg.DX, Reg.DX);
      asm.Js(armBody);
      asm.Or(Reg.AX, Reg.DX);
      asm.Jz(armBody);
      asm.MarkLabel(noMatch);
      return;
    }

    this.EmitSubjectMinusValue32(subject);
    var relation = selector.IsComparison ?? CaseComparison.Equal;
    var skip = asm.DefineLabel();
    switch (relation) {
      case CaseComparison.Equal:
        asm.Or(Reg.AX, Reg.DX);
        asm.Jz(armBody);
        break;
      case CaseComparison.NotEqual:
        asm.Or(Reg.AX, Reg.DX);
        asm.Jnz(armBody);
        break;
      case CaseComparison.Less:
        asm.Test(Reg.DX, Reg.DX);
        asm.Js(armBody);
        break;
      case CaseComparison.GreaterEqual:
        asm.Test(Reg.DX, Reg.DX);
        asm.Jns(armBody);
        break;
      case CaseComparison.LessEqual:
        asm.Test(Reg.DX, Reg.DX);
        asm.Js(armBody);
        asm.Or(Reg.AX, Reg.DX);
        asm.Jz(armBody);
        break;
      case CaseComparison.Greater:
        asm.Test(Reg.DX, Reg.DX);
        asm.Js(skip);
        asm.Or(Reg.AX, Reg.DX);
        asm.Jnz(armBody);
        break;
    }
    asm.MarkLabel(skip);
  }

  private void EmitSelectorString(SelectStmt s, Mem subject, CaseSelector selector, Label armBody) {
    var asm = this._asm;
    if (selector.RangeUpper != null) {
      this.Unsupported(s); // string ranges are not used by the corpus
      return;
    }
    asm.Mov(Reg.AX, subject.WithSize(OperandSize.Word));
    asm.Call(this._rt.StrDup);                  // compare consumes - keep the subject alive
    asm.Push(Reg.AX);
    this.EmitExpression(selector.Value!);
    asm.Mov(Reg.DX, Reg.AX);
    asm.Pop(Reg.AX);
    asm.Call(this._rt.StrCmp);                  // AX = -1/0/1
    asm.Test(Reg.AX, Reg.AX);
    switch (selector.IsComparison ?? CaseComparison.Equal) {
      case CaseComparison.Equal: asm.Jz(armBody); break;
      case CaseComparison.NotEqual: asm.Jnz(armBody); break;
      case CaseComparison.Less: asm.Js(armBody); break;
      case CaseComparison.GreaterEqual: asm.Jns(armBody); break;
      case CaseComparison.Greater: {
        var skip = asm.DefineLabel();
        asm.Js(skip);
        asm.Jnz(armBody);
        asm.MarkLabel(skip);
        break;
      }
      case CaseComparison.LessEqual: {
        asm.Js(armBody);
        asm.Jz(armBody);
        break;
      }
    }
  }

  private void EmitIncrDecr(IncrDecrStmt id) {
    var asm = this._asm;
    var targetType = model.TypeOf(id.Target);

    // pb36 O5: INCR/DECR of a register-resident accumulator updates the register
    if (id.Target is NameExpr regTarget
        && model.VariableBindings.TryGetValue(regTarget, out var regSym)
        && this.ResidentRegOf(regSym) is { } accReg) {
      if (id.Amount == null) {
        if (id.Increment)
          asm.Inc(accReg);
        else
          asm.Dec(accReg);
      } else {
        this.EmitExpression(id.Amount);
        this.Coerce(model.TypeOf(id.Amount), PbType.Integer, id.Amount);
        if (id.Increment)
          asm.Add(accReg, Reg.AX);
        else
          asm.Sub(accReg, Reg.AX);
      }
      return;
    }

    var kind = KindOf(targetType);
    if (kind is not (ValueKind.Int16 or ValueKind.Int32)) {
      this.Unsupported(id);
      return;
    }
    var isByte = targetType.Size == 1;

    if (id.Amount != null) {
      this.EmitExpression(id.Amount);
      this.Coerce(model.TypeOf(id.Amount), targetType, id.Amount);
      if (kind == ValueKind.Int32)
        asm.Push(Reg.DX);
      asm.Push(Reg.AX);
    }

    if (this.EmitPlace(id.Target) is not { } place) {
      this.Unsupported(id);
      return;
    }
    var cell = place.Cell.WithSize(isByte ? OperandSize.Byte : OperandSize.Word);

    if (id.Amount == null) {
      if (kind == ValueKind.Int16) {
        if (id.Increment)
          asm.Inc(cell);
        else
          asm.Dec(cell);
      } else if (id.Increment) {
        asm.Add(cell, (Imm)1);
        asm.Adc(Adjust(cell, 2, OperandSize.Word), (Imm)0);
      } else {
        asm.Sub(cell, (Imm)1);
        asm.Sbb(Adjust(cell, 2, OperandSize.Word), (Imm)0);
      }
      return;
    }

    asm.Pop(Reg.AX);
    if (kind == ValueKind.Int32)
      asm.Pop(Reg.DX);
    if (id.Increment) {
      if (isByte)
        asm.Add(cell, Reg.AL);
      else
        asm.Add(cell, Reg.AX);
      if (kind == ValueKind.Int32)
        asm.Adc(Adjust(cell, 2, OperandSize.Word), Reg.DX);
    } else {
      if (isByte)
        asm.Sub(cell, Reg.AL);
      else
        asm.Sub(cell, Reg.AX);
      if (kind == ValueKind.Int32)
        asm.Sbb(Adjust(cell, 2, OperandSize.Word), Reg.DX);
    }
  }

  #endregion
}
