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

  /// <summary>Registers the counter's proven range for the loop body, removing it on Dispose; null (no scope) when the range is not statically known or the counter could change in the body.</summary>
  private IDisposable? PushForRange(ForStmt f, VariableSymbol counter) {
    if (!this.Optimize)
      return null;
    if (counter.Type is not ScalarType { IsFloat: false })
      return null;
    if (this.OptFolder.TryFold(f.From) is not { Integer: { } fromV }
        || this.OptFolder.TryFold(f.To) is not { Integer: { } toV })
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
  /// <summary>
  /// O16 interval lattice: the proven [lo,hi] of variable <paramref name="v"/> at the statement
  /// currently being emitted, or null when unknown (Top) or outside the analyzed main body.
  /// Sound (over-approximation) and wrap-correct - a value that overflowed its type reads as Top,
  /// never a misleading mathematical range.
  /// </summary>
  private (long Lo, long Hi)? LatticeRangeOf(VariableSymbol v) {
    if (this.LatticeFactsOf(v) is { Range: { IsTop: false } r })
      return (r.Lo, r.Hi);
    return null;
  }

  /// <summary>
  /// O16: everything the lattice proved about <paramref name="v"/> at the statement being emitted
  /// - its range and its bits - or null when the variable is not tracked here.
  /// </summary>
  private ValueFacts? LatticeFactsOf(VariableSymbol v) {
    if (this._intervalPoints is { } points && this._currentStatement is { } s
        && points.TryGetValue(s, out var env) && env.TryGetValue(v, out var facts) && !facts.IsUnknown)
      return facts;
    return null;
  }

  private (long Lo, long Hi)? IndexRangeOf(Expression idx) {
    if (this.OptFolder.TryFold(idx) is { Integer: { } c })
      return (c, c);
    switch (idx) {
      case NameExpr n when model.VariableBindings.TryGetValue(n, out var v):
        // a FOR-counter range wins (it is the exact loop bound); otherwise the interval lattice
        // may prove a range for an arbitrary variable at this program point
        if (this._forRanges.TryGetValue(v, out var r))
          return r;
        return this.LatticeRangeOf(v);
      case BinaryExpr { Op: BinaryOp.Add } b:
        // both operands range-known (e.g. a(i+j) over two counters/derived vars): the
        // endpoints add. Interval arithmetic over independent operands over-approximates a
        // correlated sum (a(i+i) widens to [2*lo,2*hi]) - sound for every consumer, which only
        // fires when the whole (possibly loose) range qualifies. A constant operand folds to a
        // point range here, so this subsumes the affine counter +/- const cases.
        if (this.IndexRangeOf(b.Left) is { } la && this.IndexRangeOf(b.Right) is { } ra)
          return this.Compose(b, () => (checked(la.Lo + ra.Lo), checked(la.Hi + ra.Hi)));
        return null;
      case BinaryExpr { Op: BinaryOp.Subtract } b
          when this.IndexRangeOf(b.Left) is { } ls && this.IndexRangeOf(b.Right) is { } rs:
        // interval subtraction: min = lo(L) - hi(R), max = hi(L) - lo(R) (point range for a
        // constant subtrahend recovers the affine counter - const case)
        return this.Compose(b, () => (checked(ls.Lo - rs.Hi), checked(ls.Hi - rs.Lo)));
      case BinaryExpr { Op: BinaryOp.Multiply } b:
        // scaling by a constant (strided access a(i*2)) - the endpoints flip when k < 0
        if (this.IndexRangeOf(b.Left) is { } lm && this.OptFolder.TryFold(b.Right) is { Integer: { } rm })
          return this.Compose(b, () => ScaleRange(lm, rm));
        if (this.IndexRangeOf(b.Right) is { } rm2 && this.OptFolder.TryFold(b.Left) is { Integer: { } lm2 })
          return this.Compose(b, () => ScaleRange(rm2, lm2));
        return null;
      case BinaryExpr { Op: BinaryOp.And } b:
        // x AND m (m a non-negative constant): the result keeps only m's bits, so it is in
        // [0, m] for ANY x (sign included) - a clean bound for masked wrap-indexing a(h AND mask)
        if (this.OptFolder.TryFold(b.Right) is { Integer: { } am } && am >= 0)
          return (0, am);
        if (this.OptFolder.TryFold(b.Left) is { Integer: { } am2 } && am2 >= 0)
          return (0, am2);
        return null;
      case BinaryExpr { Op: BinaryOp.IntegerDivide } b
          when this.IndexRangeOf(b.Left) is { } ld && this.OptFolder.TryFold(b.Right) is { Integer: { } dk } && dk != 0:
        // truncated integer divide by a constant is monotonic in the dividend (trunc-toward-zero
        // preserves order), so the endpoints divide - flipping when the divisor is negative.
        // C# long division truncates toward zero, matching PB's `\`.
        // the quotient's magnitude never exceeds the dividend's, except for MIN \ -1 - which the
        // Exact check catches like any other value that left the type
        return this.Compose(b, () => dk > 0 ? (ld.Lo / dk, ld.Hi / dk) : (ld.Hi / dk, ld.Lo / dk));
      case BinaryExpr { Op: BinaryOp.Modulo } b
          when this.OptFolder.TryFold(b.Right) is { Integer: { } mk } && mk != 0: {
        // x MOD k (k constant != 0): |result| < |k| and PB's truncated MOD takes the sign of x,
        // so the result is in [-(|k|-1), |k|-1], tightening to [0, |k|-1] when x is provably >= 0
        var bound = Math.Abs(mk) - 1;
        return this.IndexRangeOf(b.Left) is { Lo: >= 0 } ? (0, bound) : (-bound, bound);
      }
      default:
        return null;
    }
  }

  private static (long Lo, long Hi) ScaleRange((long Lo, long Hi) r, long k)
    => k >= 0 ? (checked(r.Lo * k), checked(r.Hi * k)) : (checked(r.Hi * k), checked(r.Lo * k));

  /// <summary>
  /// Composes a node's range and keeps it only if it is the truth: the arithmetic must not have
  /// overflowed the 64-bit composition itself (a QUAD-sized constant can do that), and the result
  /// must fit the node's own type (see <see cref="Exact"/>).
  /// </summary>
  private (long Lo, long Hi)? Compose(Expression node, Func<(long Lo, long Hi)> compute) {
    try {
      return this.Exact(node, compute());
    } catch (OverflowException) {
      return null;                                     // beyond what this lattice can represent
    }
  }

  /// <summary>
  /// The composed range of <paramref name="node"/>, or null when the node's own type cannot hold
  /// it - in which case the operation WRAPPED at run time and the mathematical range is a fiction
  /// no consumer may act on.
  ///
  /// This matters because whether an integral <c>+ - *</c> wraps is a dialect property. PB 2.0+
  /// computes them in floating point, so they do not wrap and the composed range is the truth;
  /// the Microsoft family, Turbo Basic, and any dialect under <c>$COMPAT</c> or checked arithmetic
  /// wrap in place, and there a range that has left the type says nothing at all. A promoted
  /// (float-typed) node is exact while it stays inside the x87's 64-bit mantissa.
  /// </summary>
  private (long Lo, long Hi)? Exact(Expression node, (long Lo, long Hi) range) => model.TypeOf(node) switch {
    ScalarType { IsFloat: false } t => TypeRangeOf(t) is { } limit
      ? range.Lo >= limit.Lo && range.Hi <= limit.Hi ? range : null
      : range,                                                  // QUAD and wider: the 64-bit composition itself would have to overflow
    ScalarType { IsFloat: true } =>
      range.Lo >= -MantissaExactBound && range.Hi <= MantissaExactBound ? range : null,
    _ => null,
  };

  /// <summary>Integers of this magnitude and below travel through the x87's 64-bit mantissa exactly.</summary>
  private const long MantissaExactBound = 1L << 62;

  /// <summary>
  /// pb36 O16 type narrowing: the proven [lo,hi] of <paramref name="e"/> when EVERY arithmetic
  /// node inside it provably stays within one 16-bit word - signed [-32768,32767], or [0,65535]
  /// for an <paramref name="unsigned"/> operation. Null when any node's range is unknown or can
  /// leave the word.
  ///
  /// Deliberately stricter than <see cref="IndexRangeOf"/>: that one composes mathematical ranges
  /// without re-checking the intermediates, which is enough for a consumer that only needs a
  /// bound, but not for REPLACING a 32-bit operation with a 16-bit one - there an intermediate
  /// that wrapped at 32 bits would make the mathematical range a fiction and the narrowed result
  /// wrong. Requiring every node to fit one word makes the two coincide: nothing wrapped (16- and
  /// 32-bit types both hold the value), so the mathematical range IS the runtime value range.
  ///
  /// Nodes whose result is bounded regardless of the operand's value (<c>x AND mask</c>,
  /// <c>x MOD k</c>) need no proof about that operand - the bound holds for a wrapped value too.
  /// </summary>
  private (long Lo, long Hi)? NarrowRangeOf(Expression e, bool unsigned) {
    var floor = unsigned ? 0L : short.MinValue;
    var ceiling = unsigned ? ushort.MaxValue : (long)short.MaxValue;
    (long Lo, long Hi)? Fits((long Lo, long Hi) r) => r.Lo >= floor && r.Hi <= ceiling ? r : null;

    if (this.OptFolder.TryFold(e) is { Integer: { } c })
      return Fits((c, c));

    switch (e) {
      case NameExpr when this.IndexRangeOf(e) is { } named:
        return Fits(named);

      // even with nothing proven about its value, a variable never leaves its own type: an
      // INTEGER/WORD/BYTE operand of a 32-bit operation always fits one word, which is the whole
      // question here
      case NameExpr when model.VariableBindings.TryGetValue(e, out var typed) && TypeRangeOf(typed.Type) is { } bound:
        return Fits(bound);

      case UnaryExpr { Op: UnaryOp.Negate, Operand: { } operand } when this.NarrowRangeOf(operand, unsigned) is { } u:
        return Fits((-u.Hi, -u.Lo));

      case BinaryExpr { Op: BinaryOp.Add } b
          when this.NarrowRangeOf(b.Left, unsigned) is { } al && this.NarrowRangeOf(b.Right, unsigned) is { } ar:
        return Fits((al.Lo + ar.Lo, al.Hi + ar.Hi));

      case BinaryExpr { Op: BinaryOp.Subtract } b
          when this.NarrowRangeOf(b.Left, unsigned) is { } sl && this.NarrowRangeOf(b.Right, unsigned) is { } sr:
        return Fits((sl.Lo - sr.Hi, sl.Hi - sr.Lo));

      case BinaryExpr { Op: BinaryOp.Multiply } b
          when this.NarrowRangeOf(b.Left, unsigned) is { } ml && this.NarrowRangeOf(b.Right, unsigned) is { } mr: {
        // both operands fit a word, so every corner product fits a long - the hull is exact
        long[] corners = [ml.Lo * mr.Lo, ml.Lo * mr.Hi, ml.Hi * mr.Lo, ml.Hi * mr.Hi];
        return Fits((corners.Min(), corners.Max()));
      }

      // truncated divide by a constant is monotonic in the dividend (endpoints divide, flipping
      // for a negative divisor); the dividend itself must be proven, or its range is a fiction
      case BinaryExpr { Op: BinaryOp.IntegerDivide } b
          when this.OptFolder.TryFold(b.Right) is { Integer: { } dk } && dk != 0
            && this.NarrowRangeOf(b.Left, unsigned) is { } dl:
        return Fits(dk > 0 ? (dl.Lo / dk, dl.Hi / dk) : (dl.Hi / dk, dl.Lo / dk));

      // |x MOD k| < |k| for ANY dividend value, so no proof about the left is needed; a provably
      // non-negative dividend tightens the result to [0,|k|-1] (PB's MOD takes the dividend's sign)
      case BinaryExpr { Op: BinaryOp.Modulo } b when this.OptFolder.TryFold(b.Right) is { Integer: { } mk } && mk != 0: {
        var bound = Math.Abs(mk) - 1;
        return Fits(this.NarrowRangeOf(b.Left, unsigned) is { Lo: >= 0 } ? (0, bound) : (-bound, bound));
      }

      // x AND m (m a non-negative constant) keeps only m's bits whatever x is
      case BinaryExpr { Op: BinaryOp.And } b when this.OptFolder.TryFold(b.Right) is { Integer: >= 0 and { } am }:
        return Fits((0, am));
      case BinaryExpr { Op: BinaryOp.And } b when this.OptFolder.TryFold(b.Left) is { Integer: >= 0 and { } am2 }:
        return Fits((0, am2));

      default:
        return null;
    }
  }

  /// <summary>The values an integer type can hold; null for a width this cannot bound (QUAD and up).</summary>
  private static (long Lo, long Hi)? TypeRangeOf(PbType type) => type switch {
    ScalarType { IsFloat: false, ByteSize: 1, Signed: true } => (-128, 127),
    ScalarType { IsFloat: false, ByteSize: 1, Signed: false } => (0, 255),
    ScalarType { IsFloat: false, ByteSize: 2, Signed: true } => (short.MinValue, short.MaxValue),
    ScalarType { IsFloat: false, ByteSize: 2, Signed: false } => (0, ushort.MaxValue),
    ScalarType { IsFloat: false, ByteSize: 4, Signed: true } => (int.MinValue, int.MaxValue),
    ScalarType { IsFloat: false, ByteSize: 4, Signed: false } => (0, uint.MaxValue),
    _ => null,
  };

  /// <summary>
  /// pb36 O16 type narrowing: true when BOTH operands of a 32-bit <paramref name="b"/> provably
  /// fit one 16-bit word, so the operation can run on the 16-bit ALU (see
  /// <see cref="NarrowRangeOf"/> for why the proof has to hold at every node).
  /// </summary>
  private bool BothOperandsNarrow16(BinaryExpr b, bool unsigned)
    => this.Optimize && this.NarrowRangeOf(b.Left, unsigned) != null && this.NarrowRangeOf(b.Right, unsigned) != null;

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
  /// pb36 O16: true when a LONG add/subtract <paramref name="b"/> has an exact value range
  /// (from FOR-counter / affine-counter operands) that provably stays inside the signed 32-bit
  /// range, so the 32-bit ADD/SUB can never raise Error 6 - the $ERROR OVERFLOW check is dead.
  /// IndexRangeOf computes the result range with exact 64-bit arithmetic, so a result inside
  /// [-2^31, 2^31-1] means the operation produced no 32-bit overflow; when any operand's range
  /// is unknown IndexRangeOf yields null and the check is kept.
  /// </summary>
  private bool ProvablyNoOverflow32(BinaryExpr b)
    => this.Optimize
       && b.Op is BinaryOp.Add or BinaryOp.Subtract
       && this.IndexRangeOf(b) is { } r
       && r.Lo >= int.MinValue && r.Hi <= int.MaxValue;

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
    if (this.FoldComparisonViaRange(b) is not { } value)
      return false;
    this._asm.Mov(Reg.AX, (int)value);         // PB boolean: TRUE = -1, FALSE = 0
    return true;
  }

  /// <summary>
  /// The PB-boolean value (-1 true / 0 false) of a signed 16-bit comparison "v OP const" whose
  /// range-known side <paramref name="b"/> makes the result invariant over its proven [lo,hi];
  /// null when not foldable. Drives both branch folding (emit the constant) and dead-arm
  /// elimination (skip the unreachable arm).
  /// </summary>
  private long? FoldComparisonViaRange(Expression condition) {
    if (!this.Optimize || condition is not BinaryExpr b)
      return null;
    if (b.Op is not (BinaryOp.Less or BinaryOp.Greater or BinaryOp.LessEqual
        or BinaryOp.GreaterEqual or BinaryOp.Equal or BinaryOp.NotEqual))
      return null;
    // both operands must be signed integers no wider than 16 bits (counter ranges are
    // signed INTEGER); a DWORD/unsigned side would compare unsigned and break the fold
    if (model.TypeOf(b.Left) is not ScalarType { IsFloat: false, ByteSize: <= 2, Signed: true }
        || model.TypeOf(b.Right) is not ScalarType { IsFloat: false, ByteSize: <= 2, Signed: true })
      return null;

    // O16 completed: both sides go through the full range oracle (constants, FOR-counter
    // ranges, the per-program-point interval lattice, affine counter expressions), so the
    // fold fires for interval-vs-interval too - IF x% < 300 with x% proven in [0,255]
    // outside any loop folds just like a counter comparison
    // constant-vs-constant stays with SCCP/const-fold (their existing, byte-pinned path)
    if (this.OptFolder.TryFold(b.Left) is { Integer: not null } && this.OptFolder.TryFold(b.Right) is { Integer: not null })
      return null;

    // An equality against a constant is decidable from ANY domain, so it is asked first and does
    // not need a range at all: the bits rule out "(x \ 2) * 2 = 1" (an even value is never odd),
    // the congruence rules out "x * 10 = 25" (a multiple of ten is never 25). Neither fact is
    // expressible as an interval, which is exactly why all three domains are kept.
    if (b.Op is BinaryOp.Equal or BinaryOp.NotEqual && this.ImpossibleEquality(b))
      return b.Op == BinaryOp.Equal ? 0L : -1L;

    if (this.IndexRangeOf(b.Left) is not { } l || this.IndexRangeOf(b.Right) is not { } r)
      return null;

    // a 16-bit-overflowing range would wrap at runtime, so the proven range is unsafe
    if (l.Lo < short.MinValue || l.Hi > short.MaxValue || r.Lo < short.MinValue || r.Hi > short.MaxValue)
      return null;

    bool? verdict = b.Op switch {
      BinaryOp.Less => l.Hi < r.Lo ? true : l.Lo >= r.Hi ? false : null,
      BinaryOp.LessEqual => l.Hi <= r.Lo ? true : l.Lo > r.Hi ? false : null,
      BinaryOp.Greater => l.Lo > r.Hi ? true : l.Hi <= r.Lo ? false : null,
      BinaryOp.GreaterEqual => l.Lo >= r.Hi ? true : l.Hi < r.Lo ? false : null,
      BinaryOp.Equal => l.Lo == l.Hi && r.Lo == r.Hi && l.Lo == r.Lo ? true : l.Hi < r.Lo || l.Lo > r.Hi ? false : (bool?)null,
      BinaryOp.NotEqual => l.Hi < r.Lo || l.Lo > r.Hi ? true : l.Lo == l.Hi && r.Lo == r.Hi && l.Lo == r.Lo ? false : (bool?)null,
      _ => null,
    };

    return verdict is { } v ? (v ? -1L : 0L) : null;
  }

  /// <summary>
  /// pb36 O16: an operation whose result the value facts already know. Two shapes pay:
  /// <list type="bullet">
  ///   <item>the operation is the IDENTITY on this operand - <c>x AND 255</c> when the bits
  ///     already prove the high byte clear, <c>x OR 1</c> when bit 0 is already set,
  ///     <c>x MOD k</c> when x is already inside [0,k) - so only the operand is emitted;</item>
  ///   <item>the result is a CONSTANT regardless of the operand - <c>(x * 10) MOD 5</c> is always
  ///     zero because the congruence proves x*10 is a multiple of five, and <c>(x * 4) AND 3</c>
  ///     is always zero because the low two bits are. Only the operand's side effects remain.</item>
  /// </list>
  /// The operand is still evaluated in both cases: it may call a FUNCTION, and PB evaluates it.
  /// </summary>
  private bool TryEmitFactRedundantOp(BinaryExpr b, PbType opType) {
    if (!this.Optimize || opType is not ScalarType { IsFloat: false, ByteSize: <= 4 } scalar)
      return false;
    if (b.Op is not (BinaryOp.And or BinaryOp.Or or BinaryOp.Xor or BinaryOp.Modulo or BinaryOp.IntegerDivide))
      return false;
    var width = scalar.ByteSize * 8;
    var mask = MaskOf(width);

    // A bitwise operation is symmetric, so either side may be the redundant one - "mask% AND x%"
    // is the same question as "x% AND mask%". The side that disappears must be discardable: its
    // value is not emitted at all, so it may not be something whose evaluation is observable.
    if (b.Op is BinaryOp.And or BinaryOp.Or or BinaryOp.Xor) {
      // O0076 self-operand identities: x AND x = x, x OR x = x, x XOR x = 0. Sound only over a
      // discardable operand (a pure variable/constant read) so the shared value is evaluated once
      // with no side effect to duplicate or drop; bitwise ops stay integral, so this holds for any
      // value of x. x XOR x collapses to 0 without even reading x (it has no observable effect).
      if (this.IsDiscardable(b.Left) && this.IsSameLvalue(b.Left, b.Right)) {
        if (b.Op == BinaryOp.Xor) {
          this.EmitIntegralConstant(0, KindOf(opType));
          return true;
        }
        return this.EmitOperandOnly(b.Left, opType);   // AND / OR: the operand itself
      }
      var left = this.FactsOf(b.Left);
      var right = this.FactsOf(b.Right);
      if (this.IsDiscardable(b.Right) && IsBitwiseIdentity(b.Op, left, right, mask))
        return this.EmitOperandOnly(b.Left, opType);
      if (this.IsDiscardable(b.Left) && IsBitwiseIdentity(b.Op, right, left, mask))
        return this.EmitOperandOnly(b.Right, opType);
      // every bit is provably clear on one side or the other, so the AND is just zero
      if (b.Op == BinaryOp.And && (~left.Bits.Zeros & ~right.Bits.Zeros & mask) == 0
          && this.IsDiscardable(b.Right))
        return this.EmitConstantAfterOperand(b.Left, opType, 0, scalar);
      return false;
    }

    // MOD and \ keep the constant-divisor form: a variable divisor carries the Error-11 guard,
    // and dropping the operation would drop the trap with it
    if (this.OptFolder.TryFold(b.Right) is not { Integer: { } k } || k == 0)
      return false;
    var facts = this.FactsOf(b.Left);

    // O0080: x \ 1 is x. Integer division by 1 is the identity and never traps (unlike x \ -1,
    // whose MININT case would overflow IDIV), so this folds unconditionally for any value of x.
    if (b.Op == BinaryOp.IntegerDivide && k == 1)
      return this.EmitOperandOnly(b.Left, opType);

    // O0080: x \ -1 is -x, but ONLY once MININT is ruled out. IDIV traps (#DE) on MININT \ -1
    // because the quotient +32768 does not fit the destination, while NEG(8000h) is 8000h and
    // says nothing - so folding without the proof would delete a trap the genuine hardware path
    // takes. The interval domain supplies the proof: a range whose low end is above MININT cannot
    // contain it. Unproven, the IDIV stays and so does the trap.
    // Signed only - on an unsigned type the divisor is 0FFFFh, not minus one, and the quotient is
    // 0 or 1 rather than a negation.
    if (b.Op == BinaryOp.IntegerDivide && k == -1 && scalar.Signed
        && facts.Range is { } neg && neg.Lo > -(1L << (width - 1))) {
      if (!this.EmitOperandOnly(b.Left, opType))
        return false;
      var asm = this._asm;
      if (scalar.ByteSize <= 2)
        asm.Neg(Reg.AX);
      else {
        asm.Not(Reg.DX);
        asm.Neg(Reg.AX);
        asm.Sbb(Reg.DX, -1);
      }
      return true;
    }

    // a value already inside [0,|k|) is its own remainder
    if (b.Op == BinaryOp.Modulo && facts.Range is { Lo: >= 0 } r && r.Hi < Math.Abs(k))
      return this.EmitOperandOnly(b.Left, opType);
    // a multiple of k has no remainder; a value smaller than k has no quotient
    if (b.Op == BinaryOp.Modulo && facts.Mod.IsMultipleOf(k))
      return this.EmitConstantAfterOperand(b.Left, opType, 0, scalar);
    if (b.Op == BinaryOp.IntegerDivide && facts.Range is { } dr
        && dr.Lo > -Math.Abs(k) && dr.Hi < Math.Abs(k))
      return this.EmitConstantAfterOperand(b.Left, opType, 0, scalar);
    return false;
  }

  /// <summary>
  /// True when <paramref name="other"/> cannot change <paramref name="mine"/> under this operation:
  /// an AND only clears bits, so it is the identity when every bit it could clear is already 0; an
  /// OR only sets them, so it is the identity when every bit it could set is already 1; an XOR
  /// changes nothing only against a provable zero.
  /// </summary>
  private static bool IsBitwiseIdentity(BinaryOp op, ValueFacts mine, ValueFacts other, ulong mask) => op switch {
    BinaryOp.And => (~other.Bits.Ones & mask & ~mine.Bits.Zeros) == 0,
    BinaryOp.Or => (~other.Bits.Zeros & mask & ~mine.Bits.Ones) == 0,
    _ => (other.Bits.Zeros & mask) == mask,
  };

  /// <summary>
  /// True when an operand may simply not be emitted. Only a plain variable read or a compile-time
  /// constant qualifies: anything else could call a FUNCTION, or index an array whose bounds check
  /// is part of the program's observable behaviour under <c>$ERROR BOUNDS</c>.
  /// </summary>
  private bool IsDiscardable(Expression e) =>
    this.OptFolder.TryFold(e) is { Integer: not null }
    || (e is NameExpr && model.VariableBindings.ContainsKey(e));

  /// <summary>Emits just one operand of a redundant operation, coerced to what the operation would have produced.</summary>
  private bool EmitOperandOnly(Expression operand, PbType opType) {
    this.EmitExpression(operand);
    this.Coerce(model.TypeOf(operand), opType, operand);
    return true;
  }

  /// <summary>
  /// Emits an operand for its side effects and then the constant the facts prove the operation
  /// yields. The operand is still evaluated because PB evaluates it - it may call a FUNCTION.
  /// </summary>
  private bool EmitConstantAfterOperand(Expression operand, PbType opType, long value, ScalarType scalar) {
    this.EmitExpression(operand);
    this.Coerce(model.TypeOf(operand), opType, operand);
    this._asm.Mov(Reg.AX, (int)value);
    if (scalar.ByteSize == 4)
      this._asm.Cwd();
    return true;
  }

  private static ulong MaskOf(int width) => width >= 64 ? ulong.MaxValue : (1UL << width) - 1;

  /// <summary>
  /// True when one side is a constant the other side's proven facts exclude - so the two can
  /// never be equal. Sound because every domain over-approximates: a value it rejects is one the
  /// expression provably cannot produce.
  /// </summary>
  private bool ImpossibleEquality(BinaryExpr b) {
    if (this.OptFolder.TryFold(b.Right) is { Integer: { } rc })
      return !this.FactsOf(b.Left).Allows(rc, this.WidthOfExpr(b.Left));
    if (this.OptFolder.TryFold(b.Left) is { Integer: { } lc })
      return !this.FactsOf(b.Right).Allows(lc, this.WidthOfExpr(b.Right));
    return false;
  }

  private int WidthOfExpr(Expression e) => model.TypeOf(e) is ScalarType { IsFloat: false, ByteSize: var n } ? n * 8 : 0;

  /// <summary>
  /// Everything proven about an expression: a variable's facts come from the lattice, a composite
  /// is recomposed here from its operands so a fact holds even when the value was never stored -
  /// <c>IF n% * 10 = 25</c> is decidable without <c>n% * 10</c> ever being a variable.
  /// </summary>
  /// <summary>
  /// Everything proven about an expression. Never fails: an operand nothing is known about simply
  /// yields <see cref="ValueFacts.Unknown"/>, the lattice's top element, which allows every value.
  /// That matters because an operation can create a fact its operands do not have - <c>x AND 15</c>
  /// is bounded to the low nibble however unknown x is - so an unknown leaf must not sink the
  /// whole query.
  /// </summary>
  private ValueFacts FactsOf(Expression e) {
    if (this.OptFolder.TryFold(e) is { Integer: { } c })
      return ValueFacts.Of(c, this.WidthOfExpr(e));
    switch (e) {
      case NameExpr when model.VariableBindings.TryGetValue(e, out var v):
        return this.LatticeFactsOf(v) ?? ValueFacts.Unknown;
      case BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply
          or BinaryOp.And or BinaryOp.Or or BinaryOp.Xor } b: {
        var l = this.FactsOf(b.Left);
        var r = this.FactsOf(b.Right);
        var width = this.WidthOfExpr(e);
        var bits = b.Op switch {
          BinaryOp.And => l.Bits.And(r.Bits),
          BinaryOp.Or => l.Bits.Or(r.Bits),
          BinaryOp.Xor => l.Bits.Xor(r.Bits),
          BinaryOp.Add => l.Bits.AddSub(r.Bits, subtract: false),
          BinaryOp.Subtract => l.Bits.AddSub(r.Bits, subtract: true),
          _ => l.Bits.Multiply(r.Bits, width),
        };
        var mod = b.Op switch {
          BinaryOp.Add => l.Mod.Add(r.Mod),
          BinaryOp.Subtract => l.Mod.Subtract(r.Mod),
          BinaryOp.Multiply => l.Mod.Multiply(r.Mod),
          _ => Congruence.Unknown,
        };
        // the range half stays with IndexRangeOf, which already refuses a composition that wrapped
        var range = this.IndexRangeOf(e) is { } ir ? new Interval(ir.Lo, ir.Hi) : Interval.Top;
        return new ValueFacts(range, bits.Narrow(width), mod);
      }
      default:
        return this.IndexRangeOf(e) is { } other
          ? new ValueFacts(new Interval(other.Lo, other.Hi), KnownBits.Unknown, Congruence.Unknown)
          : ValueFacts.Unknown;
    }
  }

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

  #region frames & temporaries

  /// <summary>
  /// Opens a BP frame. The frame size is not known until the body has been
  /// emitted, so the SUB SP immediate is a label whose "position" is patched
  /// to the final byte count by <see cref="EndFrame"/>.
  /// </summary>
  private void BeginFrame(bool skipZeroing = false, Label? tailEntry = null, IReadOnlyList<Reg>? spillRegs = null) {
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

    // register-convention (WATCALL/FASTCALL) entry: the leading arguments arrived in
    // AX,DX,BX(,CX); push them so each occupies its negative parameter slot ([BP-2], ...)
    // BEFORE the zero-fill clobbers AX/CX, then allocate and zero only the rest of the frame.
    var spillCount = spillRegs?.Count ?? 0;
    if (spillCount > 0) {
      foreach (var reg in spillRegs!)
        asm.Push(reg);                                       // param 0 -> [BP-2], param 1 -> [BP-4], ...
      asm.Mov(Reg.CX, Imm.OffsetOf(this._frameBytesLabel));
      asm.Sub(Reg.CX, spillCount * 2);                       // the spill words are already on the stack
      asm.Sub(Reg.SP, Reg.CX);
      if (skipZeroing)
        return;
      asm.Push(Reg.DS);
      asm.Pop(Reg.ES);
      asm.Mov(Reg.DI, Reg.SP);
      asm.Mov(Reg.CX, Imm.OffsetOf(this._frameWordsLabel));
      asm.Sub(Reg.CX, spillCount);                           // do not re-zero the spilled register slots
      asm.Xor(Reg.AX, Reg.AX);
      asm.Rep();
      asm.Stosw();
      return;
    }

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
    this._remainderReuse = null;
    this._coveredArrayDims = null;
  }

  /// <summary>
  /// O0079: the MOD statements whose remainder a directly preceding <c>q = n\d</c> already computed,
  /// so their emission reuses DX instead of a second IDIV. Rebuilt per body (after CSE/SCCP, whose
  /// marks it consults); null clears it, exactly like <see cref="_cseMarks"/>.
  /// </summary>
  private HashSet<AssignStmt>? _remainderReuse;

  /// <summary>
  /// O0079, separated form: the divide whose remainder must be kept, and the MOD that later reads it,
  /// both mapped to the frame slot holding it. DX only survives to the next statement, but the VALUE
  /// survives anything - so when the two are apart the remainder is stashed instead of re-divided.
  /// The slot comes from the CSE area, which exists precisely to hold a value computed once and
  /// reloaded at a later statement; a temp from <see cref="AllocTemp"/> would not do, being released
  /// at the end of the expression that took it.
  /// </summary>
  private Dictionary<AssignStmt, int>? _remainderStash;

  private Dictionary<AssignStmt, int>? _remainderLoad;

  /// <summary>
  /// The mirror image: a MOD that runs first, whose IDIV also produced the QUOTIENT a later divide
  /// wants. It has to be stashed between the IDIV and the <c>MOV AX,DX</c> that overwrites it with
  /// the remainder, so the emitter is told through <see cref="_stashQuotientSlot"/> rather than after
  /// the statement as the remainder is.
  /// </summary>
  private Dictionary<AssignStmt, int>? _quotientStash;

  private Dictionary<AssignStmt, int>? _quotientLoad;

  private int? _stashQuotientSlot;

  /// <summary>
  /// O0079 shared divide: marks the MOD statement of a strictly-adjacent <c>q = n\d : m = n MOD d</c>
  /// pair so it reuses the remainder the divide left in DX. Only sound when the two are consecutive
  /// (LabelStmt is its own statement, so adjacency proves no branch lands on the MOD and DX is live),
  /// the operands are the same side-effect-free INTEGER values (the divide computed exactly this
  /// remainder and nothing re-evaluates them), the divisor is a genuine runtime value (a constant
  /// could strength-reduce or fact-fold the divide away, leaving no IDIV), the quotient target is
  /// neither operand (its store must not change n or d), both targets are plain scalars (their stores
  /// are <c>mov [cell],reg</c> and never clobber DX), and neither value is CSE- or SCCP-touched.
  /// </summary>
  private void PrepareDivMod(IReadOnlyList<Statement> body) {
    this._remainderReuse = null;
    this._remainderStash = null;
    this._remainderLoad = null;
    this._quotientStash = null;
    this._quotientLoad = null;
    if (!this.Optimize || this.CheckOverflow || this.CheckNumeric)
      return; // checked arithmetic keeps every operation and its own traps
    this.ScanDivMod(body);
    if (!ContainsErrorHandling(body))
      this.ScanSeparatedDivMod(body);   // a RESUME can re-enter between the two points
  }

  /// <summary>
  /// The separated form of O0079: <c>q = n \ d</c> and a LATER <c>m = n MOD d</c> in the same
  /// statement list, with anything at all between them - statements, loops, calls. The remainder is
  /// the same value however far apart they sit; it is stashed in a frame slot at the divide and
  /// loaded at the MOD, so the second IDIV (100-180 cycles on an 8086) disappears.
  ///
  /// What has to hold, beyond everything the adjacent form already checks:
  /// <list type="bullet">
  ///   <item>the divide DOMINATES the MOD - guaranteed by being earlier in the same list, which is
  ///     why a divide nested in an IF does not qualify for a MOD after it;</item>
  ///   <item>nothing between writes <c>n</c> or <c>d</c>, nested blocks included;</item>
  ///   <item>no label between: a GOTO landing there would reach the MOD without the divide having
  ///     run, and the slot would hold whatever was last in it;</item>
  ///   <item>a call between is only harmless when both operands are out of its reach - a local that
  ///     is not SHARED or STATIC and is never handed to a call anywhere in the body (a conservative
  ///     stand-in for "never passed BYREF"). Otherwise the call may have rewritten them.</item>
  /// </list>
  /// </summary>
  private void ScanSeparatedDivMod(IReadOnlyList<Statement> body) {
    for (var i = 0; i + 1 < body.Count; ++i) {
      if (body[i] is not AssignStmt { Value: BinaryExpr { Op: BinaryOp.IntegerDivide or BinaryOp.Modulo } } producer)
        continue;
      var wantedOp = ((BinaryExpr)producer.Value).Op == BinaryOp.IntegerDivide
        ? BinaryOp.Modulo
        : BinaryOp.IntegerDivide;
      for (var j = i + 1; j < body.Count; ++j) {
        if (body[j] is LabelStmt)
          break;                                    // control could arrive here without the divide
        if (body[j] is not AssignStmt { Value: BinaryExpr { Op: { } op } } candidate || op != wantedOp) {
          if (this.DivModRegionDisturbs(body[i], body[j]))
            break;
          continue;
        }
        if (this._remainderReuse?.Contains(candidate) == true)
          break;                                    // the adjacent form already has this one
        if (!this.IsSharedDivModPair(producer, candidate, out var divideIsFirst))
          break;
        var slot = this._cseBytes / 4;              // one more CSE slot, as LICM also takes them
        this._cseBytes += 4;
        if (divideIsFirst) {
          (this._remainderStash ??= new(ReferenceEqualityComparer.Instance))[producer] = slot;
          (this._remainderLoad ??= new(ReferenceEqualityComparer.Instance))[candidate] = slot;
        } else {
          // the MOD ran first: its IDIV left the QUOTIENT in AX, which the later divide wants
          (this._quotientStash ??= new(ReferenceEqualityComparer.Instance))[producer] = slot;
          (this._quotientLoad ??= new(ReferenceEqualityComparer.Instance))[candidate] = slot;
        }
        break;
      }
    }
    foreach (var s in body)
      foreach (var block in ChildStatementBlocks(s))
        this.ScanSeparatedDivMod(block);
  }

  /// <summary>True when <paramref name="between"/> could invalidate the remainder <paramref name="divide"/> produced.</summary>
  private bool DivModRegionDisturbs(Statement divide, Statement between) {
    if (divide is not AssignStmt { Value: BinaryExpr { Left: { } n, Right: { } d } })
      return true;
    foreach (var operand in new[] { n, d }) {
      if (operand is not NameExpr name || !model.VariableBindings.TryGetValue(name, out var symbol))
        continue;                                   // a constant operand cannot be disturbed
      if (StatementWrites(between, symbol, model))
        return true;
      if (this.ContainsUserCall(between) && !this.IsUnreachableByCall(symbol))
        return true;
    }
    return false;
  }

  /// <summary>A local a call cannot see: not SHARED, not STATIC, and never handed to a call in this body.</summary>
  private bool IsUnreachableByCall(VariableSymbol symbol)
    => symbol is { IsShared: false, Storage: VariableStorage.Local } && !this._callArguments.Contains(symbol);

  private readonly HashSet<VariableSymbol> _callArguments = new(ReferenceEqualityComparer.Instance);

  private static bool StatementWrites(Statement s, VariableSymbol symbol, SemanticModel model) {
    if (s is AssignStmt { Target: NameExpr target }
        && model.VariableBindings.TryGetValue(target, out var written) && ReferenceEquals(written, symbol))
      return true;
    if (s is ForStmt f && model.VariableBindings.TryGetValue(f.Variable, out var counter) && ReferenceEquals(counter, symbol))
      return true;
    if (s is AssignStmt or PrintStmt or CallStmt)
      return ChildStatementBlocks(s).Any(b => b.Any(x => StatementWrites(x, symbol, model)));
    return ChildStatementBlocks(s).Any(b => b.Any(x => StatementWrites(x, symbol, model)))
      || s is InputStmt or ReadStmt or SwapStmt;    // these write through targets this scan does not model
  }

  /// <summary>
  /// Whether a statement can reach USER code, which is the only thing that could rewrite a variable
  /// behind the optimizer's back. A PRINT is a call, but into the DOS runtime, and the runtime does
  /// not write the program's variables - so it is not one of these. Anything not modelled here counts
  /// as a call, because guessing the other way is how a stale value gets reused.
  /// </summary>
  private bool ContainsUserCall(Statement s) {
    if (ChildStatementBlocks(s).Any(b => b.Any(this.ContainsUserCall)))
      return true;
    return s switch {
      CallStmt => true,
      AssignStmt a => !CallFree(a.Value, model)
                      || (a.Target is not NameExpr && !CallFree(a.Target, model)),
      PrintStmt p => p.Items.Any(i => i.Value is { } v && !CallFree(v, model)),
      ForStmt f => !CallFree(f.From, model) || !CallFree(f.To, model)
                   || (f.Step is { } step && !CallFree(step, model)),
      IfStmt i => !CallFree(i.Condition, model) || i.ElseIfs.Any(e => !CallFree(e.Condition, model)),
      DoLoopStmt d => (d.PreCondition is { } pre && !CallFree(pre, model))
                      || (d.PostCondition is { } post && !CallFree(post, model)),
      SelectStmt sel => !CallFree(sel.Subject, model),
      LabelStmt or ExitStmt or IterateStmt => false,
      _ => true,
    };
  }

  private void ScanDivMod(IReadOnlyList<Statement> body) {
    for (var i = 0; i + 1 < body.Count; ++i)
      // DIVIDE first only: this form reuses DX, which holds the remainder - a MOD that ran first
      // leaves the QUOTIENT in AX instead, and that pair is handled by the stashing scan below
      if (this.IsSharedDivModPair(body[i], body[i + 1], out var divideIsFirst) && divideIsFirst)
        (this._remainderReuse ??= new(ReferenceEqualityComparer.Instance)).Add((AssignStmt)body[i + 1]);
    foreach (var s in body)
      foreach (var block in ChildStatementBlocks(s))
        this.ScanDivMod(block);
  }

  /// <summary>
  /// The two statements of a shared divide, in EITHER order. One IDIV produces both answers, so it
  /// does not matter which of them the program asks for first: <c>q = n \ d</c> then <c>m = n MOD d</c>
  /// keeps the remainder out of DX, and <c>m = n MOD d</c> then <c>q = n \ d</c> keeps the quotient
  /// out of AX. The conditions either way are the same.
  /// </summary>
  private bool IsSharedDivModPair(Statement first, Statement second) => this.IsSharedDivModPair(first, second, out _);

  private bool IsSharedDivModPair(Statement first, Statement second, out bool divideIsFirst) {
    divideIsFirst = true;
    if (first is not AssignStmt { Target: NameExpr firstTarget, Value: BinaryExpr { Left: { } firstN, Right: { } firstD } firstValue } firstAssign)
      return false;
    if (second is not AssignStmt { Target: NameExpr secondTarget, Value: BinaryExpr { Left: { } secondN, Right: { } secondD } secondValue } secondAssign)
      return false;
    var firstOp = ((BinaryExpr)firstAssign.Value).Op;
    var secondOp = ((BinaryExpr)secondAssign.Value).Op;
    if (firstOp == BinaryOp.IntegerDivide && secondOp == BinaryOp.Modulo)
      divideIsFirst = true;
    else if (firstOp == BinaryOp.Modulo && secondOp == BinaryOp.IntegerDivide)
      divideIsFirst = false;
    else
      return false;

    var (qName, mName) = divideIsFirst ? (firstTarget, secondTarget) : (secondTarget, firstTarget);
    var (divStmt, modStmt) = divideIsFirst ? (firstAssign, secondAssign) : (secondAssign, firstAssign);
    var (divValue, modValue) = divideIsFirst ? (firstValue, secondValue) : (secondValue, firstValue);
    var (n1, d1) = divideIsFirst ? (firstN, firstD) : (secondN, secondD);
    var (n2, d2) = divideIsFirst ? (secondN, secondD) : (firstN, firstD);
    // neither statement may be SCCP-dead (a skipped divide would leave DX undefined) nor CSE-shared
    // (whose slot define/reload the direct DX reuse would bypass)
    if (this._deadStatements?.Contains(divStmt) == true || this._deadStatements?.Contains(modStmt) == true)
      return false;
    if (this._cseMarks?.ContainsKey(divValue) == true || this._cseMarks?.ContainsKey(modValue) == true)
      return false;
    // 16-bit signed INTEGER throughout: the 16-bit IDIV leaves a 16-bit remainder in DX
    bool IsInt16(Expression e) => model.TypeOf(e) is ScalarType { IsFloat: false, ByteSize: 2, Signed: true };
    if (!IsInt16(qName) || !IsInt16(mName) || !IsInt16(n1) || !IsInt16(d1))
      return false;
    // the same, side-effect-free operands - so the divide computed this exact remainder and nothing
    // (a call) is dropped by not re-evaluating them
    if (!this.SameDivOperand(n1, n2) || !this.SameDivOperand(d1, d2)
        || !this.IsPureDivOperand(n1) || !this.IsPureDivOperand(d1))
      return false;
    // a runtime divisor guarantees a real IDIV (a constant could fold / strength-reduce it away)
    if (this.OptFolder.TryFold(d1) is { Integer: not null })
      return false;
    // plain-scalar targets: their stores never emit address code that could clobber DX
    if (!model.VariableBindings.TryGetValue(qName, out var qSym) || this.TryDirectCell(qSym) is null
        || !model.VariableBindings.TryGetValue(mName, out var mSym) || this.TryDirectCell(mSym) is null)
      return false;
    // whichever statement runs first, its store must not overwrite an operand the other one needs
    foreach (var operand in new[] { n1, d1 })
      if (operand is NameExpr on && model.VariableBindings.TryGetValue(on, out var os)
          && (ReferenceEquals(os, qSym) || ReferenceEquals(os, mSym)))
        return false;
    return true;
  }

  /// <summary>Two divide operands that name the same storage or fold to the same constant.</summary>
  private bool SameDivOperand(Expression a, Expression b)
    => (this.OptFolder.TryFold(a) is { Integer: { } ka } && this.OptFolder.TryFold(b) is { Integer: { } kb } && ka == kb)
       || this.IsSameLvalue(a, b);

  /// <summary>A divide operand with no observable evaluation: a compile-time constant or a plain variable read.</summary>
  private bool IsPureDivOperand(Expression e)
    => this.OptFolder.TryFold(e) is { Integer: not null }
       || (e is NameExpr && model.VariableBindings.ContainsKey(e) && !model.IntrinsicBindings.ContainsKey(e));

  /// <summary>pb36 O3: runs the common-subexpression analysis for a body and reserves its frame slots; call right before <see cref="BeginFrame"/>.</summary>
  private void PrepareCse(IReadOnlyList<Statement> body) {
    this._cseMarks = null;
    this._cseBytes = 0;
    if (!this.Optimize)
      return;
    var result = OptCommonSubexpr.Analyze(body, model);
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
    // O2: assignments whose result SCCP propagated away (or never read) are dead.
    // A read that SCCP proved constant only stops keeping its store alive when the
    // emitter actually folds that read to the constant - which it does NOT do under
    // $ERROR OVERFLOW/NUMERIC (folding a checked op would skip its trap; see the
    // `!CheckOverflow && !CheckNumeric` gate on the proven-read fold). When such
    // checking is enabled anywhere in this body the reads stay as real memory loads,
    // so a store feeding them is NOT dead - run dead-store analysis against an EMPTY
    // proven set (remove only genuinely-unread stores) to avoid dropping a store the
    // un-folded read still loads. The flag form (CheckOverflow/CheckNumeric) is only
    // set later, when the $ERROR meta is emitted, so test the model directly.
    var checkedArithmetic = model.MetaStatements.Any(m =>
      m.Command.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
      && m.Arguments.Count >= 2
      && m.Arguments[0].Text.ToUpperInvariant() is "NUMERIC" or "OVERFLOW" or "ALL"
      && m.Arguments[^1].Text.Equals("ON", StringComparison.OrdinalIgnoreCase));
    var deadProven = checkedArithmetic
      ? (IReadOnlyDictionary<Syntax.Ast.NameExpr, long>)new Dictionary<Syntax.Ast.NameExpr, long>()
      : proven;
    var dead = Ssa.DeadStore.Compute(model, ssa, deadProven);
    // copy propagation: redirect reads of a copy y = x to x and drop the copy
    var (copyReads, deadCopies) = OptCopyProp.Analyze(ssa);
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

    this.EmitBackendDataPool(asm);

    foreach (var (symbol, label) in this._variableSlots) {
      // pb36 O23: a dead global's data slot carries no live value - emit no bytes for it.
      // (its only stores were skipped, so SlotOf was normally never even called for it.)
      if (this._deadGlobals != null && this._deadGlobals.Contains(symbol))
        continue;
      asm.Align(2);
      asm.MarkLabel(label);
      var bytes = symbol.ArrayClass is ArrayClass.Huge or ArrayClass.Virtual or ArrayClass.Ems or ArrayClass.Xms
        ? HvDescriptorBytes                       // dword bounds + EMS handle + page cache (EMS/XMS ride the same paged descriptor)
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
    MbfType => ValueKind.Float,   // MBF cells convert to/from the x87 on load/store
    StringType or FixedStringType or FlexType or AsciizType => ValueKind.Str,
    _ => ValueKind.Int16,
  };

  #endregion

  }
