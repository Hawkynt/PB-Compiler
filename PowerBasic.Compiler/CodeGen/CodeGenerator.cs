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
  /// Opt-in: compile eligible pure-INTEGER functions through the in-house x86-16 back end
  /// (docs/X86-BACKEND.md) - it owns the whole function via its SSA IR (no shared cells), so it never
  /// reads an optimizer-stale cell. Default off; enabled for verification via PBC_X_BACKEND / --x-backend.
  /// </summary>
  public bool UseExperimentalBackend { get; set; } = System.Environment.GetEnvironmentVariable("PBC_X_BACKEND") != null;

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
  public byte[] EmitExecutable(IReadOnlyList<PbuFile> units, IReadOnlyList<PblFile> libraries, IReadOnlyList<Emit.Omf.OmfLibrary>? omfLibraries = null) {
    ArgumentNullException.ThrowIfNull(units);
    ArgumentNullException.ThrowIfNull(libraries);
    omfLibraries ??= [];
    this._allowExternalCalls = units.Count > 0 || libraries.Count > 0 || omfLibraries.Count > 0;

    // pb36 O2/O10: drop unreachable statements and redundant DEF SEGs first -
    // dead code also vanishes from the trivial-lowering analysis below
    if (this.Optimize && !this._isUnit) {
      OptPruner.Prune(model);
      OptLoopFusion.Fuse(model);   // O0062: merge adjacent same-bound FOR loops (after pruning makes them adjacent)
      OptFloatDemotion.Apply(model);
      this._ipcp = OptIpcp.Analyze(model); // O18: constants into callee bodies
      this._pureFold = OptPureFold.Analyze(model); // O25: compile-time-evaluate pure-function calls with constant args
      this.ScheduleInlineAsmBlocks(); // reorder inline-asm runs to group memory/ALU ops (dependency-preserving)
      // $OPTIMIZE SPEED: pass internal parameters in registers (AX,DX,BX,CX) instead of on
      // the stack when we own every call site. Self-contained programs only (a separately
      // compiled unit could otherwise call a converted procedure with the stack convention).
      // Gated on the optimizer flags, not the dialect - the optimizer is dialect-agnostic, so
      // any dialect compiled with the optimizer + SPEED gets it; it merely defaults on for pb36.
      if (this.OptimizeSpeed && !this._allowExternalCalls)
        OptRegParm.Apply(model, this.IsBackendRouted);   // back-end functions stay on the stack convention
    }

    // P7: programs whose only effect is printing compile-time text lower to a
    // raw COM-style image of a few dozen bytes (docs/PB36.md) - a lean-output
    // optimization, available to any dialect under the optimizer flag
    if (this.Optimize && !this._allowExternalCalls && !this._isUnit
        && this.TryLowerTrivialProgram() is { } trivial)
      return trivial;

    var asm = this._asm;
    // peephole / scheduler: record the instruction stream of a standalone program image so a
    // post-emit pass can rewrite it (units/libraries keep the faithful stream). The two passes both
    // rewrite by recorded byte position, so they are mutually exclusive: an optimized standalone under
    // $OPTIMIZE SPEED gets the instruction scheduler (reorders the FINAL stream - after
    // unrolling/inlining/const-fold - to group memory/ALU ops), every other optimized standalone keeps
    // the peephole (staging coalesce, CMP->TEST). Gated on the optimizer flags, not the dialect (the
    // optimizer is dialect-agnostic; SPEED merely defaults on for pb36).
    // $OPTIMIZE SIZE|SPEED - one per module (PBC -OZF preselects SPEED). Resolved BEFORE the
    // post-emit pass gates below so a metastatement (not just the CLI flag) arms them.
    var optimizeMetas = model.MetaStatements.Where(m => m.Command.Equals("OPTIMIZE", StringComparison.OrdinalIgnoreCase)).ToList();
    if (optimizeMetas.Count > 1)
      this.Errors.Add(new(optimizeMetas[1].Position, "only one $OPTIMIZE per module"));
    if (optimizeMetas.Count > 0 && optimizeMetas[0].Arguments is [{ } optMode, ..]) {
      this.OptimizeSpeed = optMode.Text.Equals("SPEED", StringComparison.OrdinalIgnoreCase);
      this.OptimizeSize = optMode.Text.Equals("SIZE", StringComparison.OrdinalIgnoreCase);
    }

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
    asm.Allow386Jcc = this.Cpu386;
    // S3 SIZE: identical procedures fold to one copy (entry labels re-bound to the survivor)
    asm.EnableTailMerge = standalone && this.OptimizeSize;
    var userMain = asm.DefineLabel("user_main");
    this._scratch = asm.DefineLabel("cg_scratch");

    this._rt.EnableBss = this.Optimize && !this._allowExternalCalls && !this._isUnit;
    this._rt.EnableUmb = this.Optimize && !this._allowExternalCalls && !this._isUnit;   // C6: HUGE-array heap prefers upper memory
    this._rt.EnableFastVideo = model.FastVideo;   // R1: $OPTION VIDEO direct-video console PRINT
    this._rt.Cpu386 = this.Optimize && this.Cpu386;
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

    // pb36 O23 whole-program data tree-shaking: solve which module globals nothing reachable
    // reads (dead), the pure stores to them, and - cascading through CODEPTR - which procedures
    // a now-dead global pointer kept alive. Self-contained main only (a unit or a foreign-linked
    // main could export/observe a global), so the pb35/unoptimized golden output is untouched.
    var dataShake = this.Optimize && !this._isUnit && !this._allowExternalCalls
      ? OptDeadGlobals.Analyze(model, this.IsFullyOwned, this.NumericCheckingPossible())
      : null;
    if (dataShake != null) {
      this._deadGlobals = dataShake.DeadGlobals;
      this._deadGlobalStores = dataShake.DeadStores;
    }

    this.PrepareCse(model.MainBody);
    this.PrepareSccp(model.MainBody);
    this.PrepareDivMod(model.MainBody);
    this.PrepareArrayFill(model.MainBody);
    if (this.Optimize)
      this._intervalPoints = IntervalRangeAnalysis.AnalyzeProgramPoints(model.MainBody, model);
    if (this.BackendMain() is not null) {
      // the x86-16 back end owns the whole module body: its own prologue, its own frame, and the
      // implicit END emitted at its return sites (docs/X86-BACKEND.md)
      this.EmitBackendMain();
    } else {
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
    }

    // pb36 O22 dead procedure elimination: under optimization, only emit the procedures
    // something references (directly, via CODEPTR, or as a lambda) - the rest are
    // unreachable code. Only fully-owned procedures may be dropped: a nested procedure is
    // private to its container, and in a self-contained main every procedure is ours; a
    // procedure that a linked foreign object could call by name is kept regardless.
    // O23 feeds its cascaded live set back here: a procedure kept alive only by a CODEPTR in a
    // now-dead global's store is dropped too. Without data shaking, plain reachability applies.
    var liveProcs = dataShake?.LiveProcedures
      ?? (this.Optimize ? OptReachability.LiveProcedures(model, model.MainBody) : null);
    // pb36 O6: a procedure inlined at EVERY call site has no surviving real CALL, so it
    // is purged. FullyInlinedProcedures guarantees every reference inlines (one that does
    // not poisons it out of the set), and an inlinable leaf makes no calls of its own, so
    // removing it from the live set cannot strand a still-needed callee. Self-contained
    // main only (matching O22/O23 ownership), so pb35/unoptimized output is untouched.
    // $OPTIMIZE SIZE keeps every body: it emits a real CALL at each site instead of inlining
    // (see TryEmitInlinedFunction), so purging on the strength of "it would inline everywhere"
    // strands those calls on a label nothing ever binds.
    if (this.Optimize && !this.OptimizeSize && !this._isUnit && liveProcs != null) {
      var hasErrorHandling = ContainsErrorHandling(model.MainBody)
        || model.ProcedureList.Any(p => p.Body is { } b && ContainsErrorHandling(b));
      var inlinedAway = OptInlining.FullyInlinedProcedures(model, p => this.AnalyzeInlinableLeaf(p) != null && !this.IsBackendRouted(p), this.IsFullyOwned, this.IsNearLValue, hasErrorHandling);
      foreach (var proc in inlinedAway)
        liveProcs.Remove(proc);
    }
    foreach (var proc in model.ProcedureList)
      if (!proc.IsExternal && (liveProcs is null || liveProcs.Contains(proc) || !this.IsFullyOwned(proc))) {
        if (this.IsBackendRouted(proc))
          this.EmitBackendFunction(proc);   // x86-16 back end owns this whole function (docs/X86-BACKEND.md)
        else
          this.EmitProcedure(proc);
      }

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

    var image = this._allowExternalCalls ? this.LinkImage(units, libraries, omfLibraries) : asm.ToArray();
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

    this.EmitLiteralPool(asm);

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

  #region statements

  private bool _trackResume;

  /// <summary>
  /// Emits one statement; inside scopes containing ON ERROR/RESUME every
  /// statement additionally records its own start and successor offsets so
  /// RESUME / RESUME NEXT can re-enter after an error unwound the stack.
  /// </summary>
  private void EmitStatement(Statement statement) {
    // pb36: a member-call statement / property-set assignment is emitted as the bound call it desugars to
    if (model.DesugaredStatements.TryGetValue(statement, out var desugaredStatement)) {
      this.EmitStatement(desugaredStatement);
      return;
    }
    // O16: the current program point - lets IndexRangeOf query the interval lattice at this use
    this._currentStatement = statement;
    // pb36 O2: a dead store (pure RHS, value never really read) is not emitted
    if (this._deadStatements != null && this._deadStatements.Contains(statement))
      return;
    // pb36 O23: a pure store to a dead global (its value is never read anywhere) is not emitted
    if (this._deadGlobalStores != null && this._deadGlobalStores.Contains(statement))
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
        // a MOD whose quotient a later divide wants must stash AX between the IDIV and the MOV AX,DX
        // that replaces it with the remainder - so the emitter is told before the statement, not after
        this._stashQuotientSlot = this._quotientStash?.TryGetValue(a, out var quotientSlot) == true ? quotientSlot : null;
        this.EmitAssign(a);
        this._stashQuotientSlot = null;
        // O0079 separated form: the IDIV just left the remainder in DX and a later MOD wants it.
        // The quotient store is a plain-scalar move (the pair test insists on it), so DX is intact.
        if (this._remainderStash?.TryGetValue(a, out var stashSlot) == true)
          this._asm.Mov(this.CseSlot(stashSlot), Reg.DX);
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

      case TryStmt t:
        this.EmitTry(t);
        break;

      // pb36 generator-in-TRY handler plumbing (synthesized only)
      case HandlerSaveStmt hs:
        this.EmitHandlerSave(hs);
        break;
      case HandlerRestoreStmt hr:
        this.EmitHandlerRestore(hr);
        break;
      case HandlerArmStmt ha:
        this.EmitHandlerArm(ha);
        break;
      case HandlerReraiseStmt:
        this.EmitHandlerReraise();
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

      case LineStmt ln:
        this.EmitLineStatement(ln);
        break;

      case CircleStmt ci:
        this.EmitCircleStatement(ci);
        break;

      case GetPutGraphicsStmt gg:
        this.EmitGetPutGraphics(gg);
        break;

      // R2 direct-video pixel write (mode 13h): PSET (x,y)[,c] / PRESET (x,y)[,c]
      case PsetStmt ps: {
        var asm3 = this._asm;
        this.EmitInt16Argument(ps.Point.X);
        asm3.Push(Reg.AX);
        this.EmitInt16Argument(ps.Point.Y);
        asm3.Push(Reg.AX);
        if (ps.Color is { } col)
          this.EmitInt16Argument(col);
        else
          asm3.Mov(Reg.AX, ps.IsPreset ? 0 : 15);   // PRESET erases (background), PSET defaults to white
        asm3.Mov(Reg.DX, Reg.AX);
        asm3.Pop(Reg.BX);                            // y
        asm3.Pop(Reg.AX);                            // x
        asm3.Call(this._rt.Pset);
        // PSET sets the LAST POINT REFERENCED, like every other graphics statement. Without this
        // `PSET (10,10) : LINE -(20,20)` draws from wherever the previous statement finished - or
        // from the origin in a program whose first graphics statement it is - and DRAW, whose whole
        // notion of position is this cell, starts its picture in the top-left corner.
        asm3.Mov(Mem.Word(asm3.Lbl("rt_gx1")), Reg.AX);
        asm3.Mov(Mem.Word(asm3.Lbl("rt_gy1")), Reg.BX);
        break;
      }

      case EquateStmt or DefTypeStmt or DataStmt or StaticAssertStmt or ResourceStmt:
        break; // declarations & module bookkeeping - nothing to execute ($ASSERT bound, $RESOURCE baked into data)

      // pb36 contract: violation prints the message (when given) and raises error 5.
      // $OPTIMIZE SPEED is the release mode - it compiles the checks out entirely.
      case RequireStmt rq: {
        if (this.OptimizeSpeed)
          break;
        var asm2 = this._asm;
        var ok = asm2.DefineLabel();
        this.EmitCondition(rq.Condition);   // truth in AX / ZF
        asm2.Jnz(ok);
        if (rq.Message is { Length: > 0 } msg) {
          asm2.Mov(Reg.SI, Imm.OffsetOf(this.LiteralOf(msg)));
          asm2.Mov(Reg.CX, msg.Length);
          asm2.Call(this._rt.PrintStr);
          asm2.Call(this._rt.PrintNewLine);
        }
        asm2.Mov(Reg.AX, 5);
        asm2.Call(this._rt.Raise);
        asm2.MarkLabel(ok);
        break;
      }

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
      case "ENVIRON" when cmd.Arguments is [{ } setting]:
        this.EmitExpression(setting);
        asm.Call(this._rt.SetEnviron);
        break;

      case "KILL" when cmd.Arguments is [{ } name]:
        this.EmitExpression(name);
        asm.Call(this._rt.Kill);
        break;

      // BSAVE name$, offset, length - the numbers go into their cells first because evaluating the
      // name leaves its string handle in AX, which is where the runtime wants it
      case "BSAVE" when cmd.Arguments is [{ } saveName, { } saveOffset, { } saveLength]:
        this.EmitInt16Argument(saveOffset);
        asm.Mov(Mem.Word(asm.Lbl("rt_bofs")), Reg.AX);
        this.EmitInt16Argument(saveLength);
        asm.Mov(Mem.Word(asm.Lbl("rt_blen")), Reg.AX);
        this.EmitExpression(saveName);
        asm.Call(this._rt.BSave);
        break;

      // BLOAD name$ [, offset] - with no offset the block goes back where BSAVE recorded it
      case "BLOAD" when cmd.Arguments is [{ } loadName, ..] && cmd.Arguments.Count <= 2:
        if (cmd.Arguments.Count == 2 && cmd.Arguments[1] is { } loadOffset) {
          this.EmitInt16Argument(loadOffset);
          asm.Mov(Mem.Word(asm.Lbl("rt_bofs")), Reg.AX);
          asm.Mov(Mem.Word(asm.Lbl("rt_bhasofs")), (Imm)1);
        } else
          asm.Mov(Mem.Word(asm.Lbl("rt_bhasofs")), (Imm)0);
        this.EmitExpression(loadName);
        asm.Call(this._rt.BLoad);
        break;

      case "MKDIR" when cmd.Arguments is [{ } path]:
        this.EmitExpression(path);
        asm.Call(this._rt.MkDir);
        break;

      case "RMDIR" when cmd.Arguments is [{ } path]:
        this.EmitExpression(path);
        asm.Call(this._rt.RmDir);
        break;

      case "CHDIR" when cmd.Arguments is [{ } path]:
        this.EmitExpression(path);
        asm.Call(this._rt.ChDir);
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

      // DRAW with a written-down string is expanded into its moves. A computed one declines, as it
      // did before - the point of doing this at compile time is that the picture is knowable.
      case "DRAW" when cmd.Arguments is [StringLiteralExpr picture]:
        this.EmitDrawStatement(cmd, picture.Value);
        break;

      case "PCOPY" when cmd.Arguments is [{ } fromPage, { } toPage]:
        this.EmitInt16Argument(fromPage);
        asm.Push(Reg.AX);
        this.EmitInt16Argument(toPage);
        asm.Mov(Reg.DX, Reg.AX);
        asm.Pop(Reg.AX);
        asm.Call(this._rt.PCopy);
        break;

      case "PAINT":
        this.EmitPaintStatement(cmd);
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
    if (write.FileNumber != null) {
      asm.Mov(Mem.Word(asm.Lbl("rt_curout")), 1);
      asm.Mov(Mem.Word(asm.Lbl("rt_colptr")), Imm.OffsetOf(asm.Lbl("rt_col")));
    }
  }

  /// <summary>
  /// Emits <paramref name="condition"/> and branches to <paramref name="target"/> on the wanted
  /// truth value. A comparison that IS the whole condition branches on the CMP's own flags (see
  /// <see cref="TryEmitCompareAsBranch"/>); anything else keeps the value path and tests it.
  /// </summary>
  private void EmitConditionalBranch(Expression condition, Label target, bool whenFalse) {
    var asm = this._asm;

    // NOT flips the sense of the branch rather than materializing a truth value to invert
    if (this.Optimize && condition is UnaryExpr { Op: UnaryOp.Not, Operand: { } inner }
        && this.IsShortCircuitBoolean(inner)) {
      this.EmitConditionalBranch(inner, target, !whenFalse);
      return;
    }

    // O0099: `IF k = 1 OR k = 3 OR k = 5 [OR ...]` is membership in a small constant set - a bit-mask
    // test rather than a compare per value. Tried before the short-circuit lowering, which would
    // otherwise emit the compare chain.
    if (this.Optimize && this.OptimizeSpeed && this.TryEmitOrChainBitMask(condition, target, whenFalse))
      return;

    // range check: `x >= lo AND x <= hi` over a 16-bit signed variable and constant bounds is one
    // unsigned compare (x - lo) <=u (hi - lo) - one branch and one subtract instead of two signed
    // compares. Also tried before the short-circuit lowering.
    if (this.Optimize && this.TryEmitRangeCheckBranch(condition, target, whenFalse))
      return;

    // O0081: `(x AND mask) = 0` / `<> 0` is a bit test - `test ax, mask` sets ZF, no AND materialized.
    if (this.Optimize && this.TryEmitBitTestCompareBranch(condition, target, whenFalse))
      return;

    // Short-circuit a condition that is an AND/OR of pure comparisons into conditional branches,
    // instead of materializing each comparison as -1/0, bitwise-combining them and testing. PB's
    // AND/OR are bitwise, but over comparison results (always -1 or 0) that equals the logical
    // operator, and pure operands have no side effect or trap to skip - so `IF x>0 AND x<100` is
    // `CMP x,0 / JLE else / CMP x,100 / JGE else`, exactly what a person writes by hand.
    if (this.Optimize && condition is BinaryExpr { Op: BinaryOp.And or BinaryOp.Or } logic
        && this.IsShortCircuitBoolean(logic)) {
      this.EmitShortCircuitBranch(logic, target, whenFalse);
      return;
    }

    if (this.Optimize && this.TryEmitBitTestBranch(condition)) {
      // O0081: `IF x AND mask` is a bit test - `test ax, mask` set ZF directly; fall to the jz/jnz below.
    } else if (this.Optimize && condition is BinaryExpr {
          Op: BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.Greater
            or BinaryOp.LessEqual or BinaryOp.GreaterEqual } comparison
        && KindOf(model.TypeOf(condition)) == ValueKind.Int16
        && this._cseMarks?.ContainsKey(condition) != true) {   // a CSE slot still wants the value
      this._compareBranch = (comparison, target, whenFalse);
      this._compareBranchTaken = false;
      this.EmitExpression(condition);
      var fused = this._compareBranchTaken;
      this._compareBranch = null;
      this._compareBranchTaken = false;
      if (fused)
        return;                          // the branch IS the comparison - nothing else to emit
      this.EmitTruthTest(condition);     // a folded/strength-reduced comparison left its value in AX
    } else
      this.EmitCondition(condition);
    if (whenFalse)
      asm.Jz(target);
    else
      asm.Jnz(target);
  }

  private void EmitCondition(Expression condition) {
    // leaves truth in AX (0 / nonzero) and sets ZF accordingly
    this.EmitExpression(condition);
    this.EmitTruthTest(condition);
  }

  /// <summary>
  /// O0081: an <c>x AND mask</c> subexpression used as a branch condition is a bit test - the AND's truth is
  /// only its zero-ness, which <c>TEST ax, mask</c> answers directly, without materializing the AND result and
  /// separately testing it. Only for an int16 AND whose other operand folds to a constant, and only when the
  /// value is not also wanted for CSE. The non-constant operand is left evaluated in AX (unmodified), and the
  /// caller's jz/jnz reads the ZF this sets. Runtime-identical to <c>and ax,mask; test ax,ax</c>.
  /// </summary>
  private bool TryEmitBitTestBranch(Expression condition) {
    if (condition is not BinaryExpr { Op: BinaryOp.And, Left: { } l, Right: { } r })
      return false;
    if (KindOf(model.TypeOf(condition)) != ValueKind.Int16 || this._cseMarks?.ContainsKey(condition) == true)
      return false;
    Expression variable;
    long mask;
    if (this.OptFolder.TryFold(r) is { Integer: { } rc }) { variable = l; mask = rc; }
    else if (this.OptFolder.TryFold(l) is { Integer: { } lc }) { variable = r; mask = lc; }
    else
      return false;
    if (KindOf(model.TypeOf(variable)) != ValueKind.Int16)
      return false;

    this.EmitExpression(variable);
    this.Coerce(model.TypeOf(variable), PbType.Integer, variable);
    this._asm.Test(Reg.AX, (Imm)(int)(short)(mask & 0xFFFF));   // ZF = (AX AND mask) == 0
    return true;
  }

  /// <summary>
  /// O0081: the explicit comparison forms of the bit test - <c>(x AND mask) = 0</c> and
  /// <c>(x AND mask) &lt;&gt; 0</c> - emit <c>TEST ax, mask</c> and branch on the resulting ZF directly, with no
  /// AND materialized and no separate compare. Emits the whole branch (so the caller returns). The jump sense
  /// combines the `= 0` vs `&lt;&gt; 0` polarity with the caller's <paramref name="whenFalse"/>.
  /// </summary>
  private bool TryEmitBitTestCompareBranch(Expression condition, Label target, bool whenFalse) {
    if (condition is not BinaryExpr { Op: BinaryOp.Equal or BinaryOp.NotEqual, Left: { } cl, Right: { } cr } cmp)
      return false;
    if (this._cseMarks?.ContainsKey(condition) == true)
      return false;
    bool IsZero(Expression e) => this.OptFolder.TryFold(e) is { Integer: 0 };
    var andExpr =
      cl is BinaryExpr { Op: BinaryOp.And } al && IsZero(cr) ? al :
      cr is BinaryExpr { Op: BinaryOp.And } ar && IsZero(cl) ? ar : null;
    if (andExpr is null || KindOf(model.TypeOf(andExpr)) != ValueKind.Int16)
      return false;
    Expression variable;
    long mask;
    if (this.OptFolder.TryFold(andExpr.Right) is { Integer: { } rc }) { variable = andExpr.Left; mask = rc; }
    else if (this.OptFolder.TryFold(andExpr.Left) is { Integer: { } lc }) { variable = andExpr.Right; mask = lc; }
    else
      return false;
    if (KindOf(model.TypeOf(variable)) != ValueKind.Int16)
      return false;

    this.EmitExpression(variable);
    this.Coerce(model.TypeOf(variable), PbType.Integer, variable);
    this._asm.Test(Reg.AX, (Imm)(int)(short)(mask & 0xFFFF));   // ZF = (x AND mask) == 0
    // `= 0` is true when ZF set; `<> 0` when ZF clear. Branch to target when that truth == !whenFalse.
    var isEqualZero = cmp.Op == BinaryOp.Equal;
    if (isEqualZero != whenFalse)
      this._asm.Jz(target);
    else
      this._asm.Jnz(target);
    return true;
  }

  /// <summary>
  /// True when a condition is an AND/OR/NOT tree of comparisons whose operands are all side-effect
  /// free and cannot trap - the shape a branch may short-circuit. Both sides must be comparisons
  /// (PB's AND/OR are bitwise; only over -1/0 comparison results do they equal logical operators),
  /// and IsPure excludes calls, array indexing (a bounds trap the skipped side would raise) and
  /// intrinsics, so skipping the second operand is observationally invisible.
  /// </summary>
  private bool IsShortCircuitBoolean(Expression e) => e switch {
    BinaryExpr { Op: BinaryOp.And or BinaryOp.Or } b => this.IsShortCircuitBoolean(b.Left) && this.IsShortCircuitBoolean(b.Right),
    UnaryExpr { Op: UnaryOp.Not } u => this.IsShortCircuitBoolean(u.Operand),
    BinaryExpr { Op: BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.Greater
      or BinaryOp.LessEqual or BinaryOp.GreaterEqual } c => OptCommonSubexpr.IsConditionOperandPure(c.Left, model)
        && OptCommonSubexpr.IsConditionOperandPure(c.Right, model),
    _ => false,
  };

  /// <summary>Emits short-circuit branches for an AND/OR of pure comparisons: jump to <paramref name="target"/> when the whole expression's truth value is <c>!whenFalse</c>.</summary>
  private void EmitShortCircuitBranch(BinaryExpr logic, Label target, bool whenFalse) {
    var asm = this._asm;
    var isAnd = logic.Op == BinaryOp.And;
    // AND is false as soon as one side is false; OR is true as soon as one side is true. When the
    // wanted outcome matches that "decides early" polarity, both sides branch to the target; when
    // it is the opposite, the first side jumps PAST the second (to a fall-through skip) on an early
    // decision and the second side carries the branch.
    if (isAnd == whenFalse) {   // AND-when-false, or OR-when-true: each side branches to target
      this.EmitConditionalBranch(logic.Left, target, whenFalse);
      this.EmitConditionalBranch(logic.Right, target, whenFalse);
    } else {
      var skip = asm.DefineLabel();
      this.EmitConditionalBranch(logic.Left, skip, !whenFalse);   // early decision: skip the second side
      this.EmitConditionalBranch(logic.Right, target, whenFalse);
      asm.MarkLabel(skip);
    }
  }

  /// <summary>
  /// A one-sided bound `v OP const` on a 16-bit variable, normalized to a lower (<c>v &gt;= Value</c>) or
  /// upper (<c>v &lt;= Value</c>) inclusive bound. Accepts the constant on either side (flipping the
  /// operator for `const OP v`). Null for anything else.
  /// </summary>
  private (Expression Var, int Value, bool IsLower)? BoundOf(Expression e) {
    if (e is not BinaryExpr { Op: { } op, Left: { } l, Right: { } r })
      return null;
    Expression varE;
    int c;
    if (this.OptFolder.TryFold(r) is { Integer: { } rc } && rc is >= short.MinValue and <= short.MaxValue) {
      varE = l; c = (int)rc;
    } else if (this.OptFolder.TryFold(l) is { Integer: { } lc } && lc is >= short.MinValue and <= short.MaxValue) {
      varE = r; c = (int)lc;
      op = op switch {                                // `const OP v` -> `v FLIP(OP) const`
        BinaryOp.Less => BinaryOp.Greater, BinaryOp.Greater => BinaryOp.Less,
        BinaryOp.LessEqual => BinaryOp.GreaterEqual, BinaryOp.GreaterEqual => BinaryOp.LessEqual, _ => op };
    } else
      return null;
    return op switch {
      BinaryOp.GreaterEqual => (varE, c, true),       // v >= c
      BinaryOp.Greater => (varE, c + 1, true),        // v > c  ==  v >= c+1
      BinaryOp.LessEqual => (varE, c, false),         // v <= c
      BinaryOp.Less => (varE, c - 1, false),          // v < c  ==  v <= c-1
      _ => ((Expression, int, bool)?)null,
    };
  }

  /// <summary>
  /// Range check: <c>x &gt;= lo AND x &lt;= hi</c> over a 16-bit signed variable becomes the single
  /// unsigned test <c>(x - lo) &lt;=u (hi - lo)</c> - one subtract and one compare instead of two signed
  /// compares and two branches. x is evaluated once. Branches to <paramref name="target"/> on the
  /// requested truth value. Declines when the two sides are not a lower+upper bound on the same
  /// variable, the bounds are out of range, or the range is empty.
  /// </summary>
  private bool TryEmitRangeCheckBranch(Expression condition, Label target, bool whenFalse) {
    if (condition is not BinaryExpr { Op: BinaryOp.And or BinaryOp.Or } logic
        || this.BoundOf(logic.Left) is not { } a || this.BoundOf(logic.Right) is not { } b
        || a.IsLower == b.IsLower)                    // need exactly one lower and one upper bound
      return false;
    var lower = a.IsLower ? a : b;                    // x >= lower.Value
    var upper = a.IsLower ? b : a;                    // x <= upper.Value
    if (lower.Var is not NameExpr lv || upper.Var is not NameExpr uv
        || !model.VariableBindings.TryGetValue(lv, out var lsym) || !model.VariableBindings.TryGetValue(uv, out var usym)
        || !ReferenceEquals(lsym, usym)
        || model.TypeOf(lv) is not ScalarType { IsFloat: false, ByteSize: 2, Signed: true })
      return false;

    // AND (x >= L AND x <= U) is true INSIDE [L, U]; OR (x >= L OR x <= U) is true OUTSIDE the gap
    // (U, L), i.e. outside [U+1, L-1]. Either way the region is one contiguous window [lo, hi].
    var isAnd = logic.Op == BinaryOp.And;
    int lo = isAnd ? lower.Value : upper.Value + 1;
    int hi = isAnd ? upper.Value : lower.Value - 1;
    if (lo > hi || lo is < short.MinValue or > short.MaxValue || hi is < short.MinValue or > short.MaxValue)
      return false;                                   // empty window, or a tautology / contradiction

    var asm = this._asm;
    this.EmitExpression(lower.Var);                   // x -> AX (evaluated once)
    if (lo != 0)
      asm.Sub(Reg.AX, (Imm)lo);                       // normalize: 0 <= x-lo <= hi-lo when inside
    asm.Cmp(Reg.AX, (Imm)(hi - lo));
    // AND is true inside the window, OR is true outside it
    if (isAnd ? !whenFalse : whenFalse)
      asm.Jbe(target);                                // within [lo, hi]
    else
      asm.Ja(target);                                 // unsigned above the window -> outside [lo, hi]
    return true;
  }

  /// <summary>Sets ZF from the value in AX (or DX:AX, or st(0)) according to its type.</summary>
  private void EmitTruthTest(Expression condition) {
    var asm = this._asm;
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
    // O16: a constant from SCCP/proven reads, else the interval lattice may prove the comparison
    // invariant over a variable's range - then the unreachable arm is not emitted at all
    return this.OptFolder.TryFold(folded)?.Integer ?? this.FoldComparisonViaRange(condition);
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

    // O0067: an IF/ELSEIF chain testing one pure integer variable against >= 4 dense compile-time
    // constants IS the SELECT CASE dispatch - synthesize the equivalent SelectStmt and reuse the
    // jump-table fast path (O(1) dispatch, first-match-wins identical to the top-to-bottom chain).
    // Declines (falling back to the compare chain below, nothing emitted) for any non-equality
    // condition, a mixed or side-effecting subject, or a value set too small/sparse to tabulate.
    if (this.Optimize && this.TryEmitIfChainJumpTable(i))
      return;

    // O0249: the explicit abs idiom `IF x < 0 THEN x = -x` is the branchless cwd/xor/sub, same as
    // the ABS() intrinsic - no branch and one store.
    if (this.Optimize && this.TryEmitBranchlessAbsIf(i))
      return;

    // O0248/O0108: the min/max diamond `IF a > b THEN m = a ELSE m = b` folds to the same integer
    // CMP/keep-larger the MAX% intrinsic emits - one store, no re-evaluated arm.
    if (this.Optimize && this.TryEmitMinMaxIf(i))
      return;

    var elseLabel = asm.DefineLabel();
    var endLabel = asm.DefineLabel();

    // O16 soundness: every condition in this chain is judged at the IF's OWN program point. Emitting
    // the THEN arm moves _currentStatement to the last statement inside it, whose interval
    // environment was refined by this condition being TRUE - and an ELSEIF condition folded against
    // that environment is answering a different question. `IF i < 0 ... ELSEIF i = 0` folded the
    // second test to false for every i, because inside the first arm i is proven negative, so the
    // arm was never taken and i = 0 fell through to the ELSE. The IF's own entry environment is the
    // sound point: the real one is that refined by the earlier conditions being FALSE, and this is a
    // superset of it.
    var atIf = this._currentStatement;

    this.EmitConditionalBranch(i.Condition, elseLabel, whenFalse: true);
    foreach (var s in i.Then)
      this.EmitStatement(s);
    asm.Jmp(endLabel);

    asm.MarkLabel(elseLabel);
    foreach (var (condition, body) in i.ElseIfs) {
      var next = asm.DefineLabel();
      this._currentStatement = atIf;
      this.EmitConditionalBranch(condition, next, whenFalse: true);
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

  /// <summary>
  /// O0067: recognizes an IF/ELSEIF (+ optional ELSE) chain that is nothing but equality tests of a
  /// single pure integer variable against compile-time constants - <c>IF x = 1 … ELSEIF x = 2 …</c> -
  /// and hands the equivalent SELECT to <see cref="TryEmitSelectJumpTable"/>. The subject and
  /// constant nodes are reused from the original conditions, so the model's type/fold queries still
  /// resolve; only the CaseArm/SelectStmt wrappers are synthetic. Returns false (emitting nothing)
  /// whenever any condition is not such an equality, references a different variable, or the
  /// jump-table path itself declines - the caller then emits the ordinary compare chain.
  /// </summary>
  private bool TryEmitIfChainJumpTable(IfStmt i) {
    VariableSymbol? subjectSym = null;
    Expression? subjectExpr = null;
    var arms = new List<CaseArm>();

    // one side of an equality is the subject variable (a pure read), the other a foldable constant
    bool TryEquality(Expression cond, out Expression subj, out Expression konst) {
      subj = konst = null!;
      if (cond is not BinaryExpr { Op: BinaryOp.Equal, Left: { } l, Right: { } r })
        return false;
      bool IsSubject(Expression e) => e is NameExpr && model.VariableBindings.ContainsKey(e) && !model.IntrinsicBindings.ContainsKey(e);
      bool IsConst(Expression e) => this.OptFolder.TryFold(e) is { Integer: not null };
      if (IsSubject(l) && IsConst(r)) { subj = l; konst = r; return true; }
      if (IsSubject(r) && IsConst(l)) { subj = r; konst = l; return true; }
      return false;
    }

    bool AddArm(Expression cond, IReadOnlyList<Statement> body) {
      if (!TryEquality(cond, out var subj, out var konst) || !model.VariableBindings.TryGetValue(subj, out var sym))
        return false;
      if (subjectSym == null) {
        subjectSym = sym;
        subjectExpr = subj;
      } else if (!ReferenceEquals(sym, subjectSym))
        return false;   // a different variable: not a single-subject dispatch
      arms.Add(new CaseArm(cond.Position, [new CaseSelector(cond.Position, konst, null, null)], body));
      return true;
    }

    if (!AddArm(i.Condition, i.Then))
      return false;
    foreach (var (cond, body) in i.ElseIfs)
      if (!AddArm(cond, body))
        return false;
    if (i.Else != null)
      arms.Add(new CaseArm(i.Position, [], i.Else));   // the trailing ELSE is CASE ELSE

    return this.TryEmitSelectJumpTable(new SelectStmt(i.Position, subjectExpr!, arms));
  }

  /// <summary>
  /// O0249: recognizes <c>IF x &lt; 0 THEN x = -x</c> (equivalently <c>0 &gt; x</c>, no ELSE) over a
  /// 16-bit signed variable and emits the branchless <c>cwd; xor ax,dx; sub ax,dx</c> - the same
  /// sequence as the ABS() intrinsic, bit-identical to the branch for every input (MININT included).
  /// Off under <c>$ERROR OVERFLOW</c> (the negation trap must survive) and declines a register-resident x.
  /// </summary>
  private bool TryEmitBranchlessAbsIf(IfStmt i) {
    if (this.CheckOverflow || i.ElseIfs.Count > 0 || i.Else is { Count: > 0 })
      return false;
    var subject = i.Condition switch {
      BinaryExpr { Op: BinaryOp.Less, Left: { } l, Right: { } r } when this.OptFolder.TryFold(r) is { Integer: 0 } => l,
      BinaryExpr { Op: BinaryOp.Greater, Left: { } l, Right: { } r } when this.OptFolder.TryFold(l) is { Integer: 0 } => r,
      _ => (Expression?)null,
    };
    if (subject is not NameExpr x || model.TypeOf(x) is not ScalarType { IsFloat: false, ByteSize: 2, Signed: true })
      return false;
    if (i.Then is not [AssignStmt { Target: { } t, Value: UnaryExpr { Op: UnaryOp.Negate, Operand: { } neg } }])
      return false;
    if (!this.IsSameLvalue(x, t) || !this.IsSameLvalue(x, neg))
      return false;
    if (!model.VariableBindings.TryGetValue(x, out var sym) || this.ResidentRegOf(sym) != null
        || this.TryDirectCell(sym) is not { } cell)
      return false;

    var asm = this._asm;
    var word = cell.WithSize(OperandSize.Word);
    asm.Mov(Reg.AX, word);
    asm.Cwd();
    asm.Xor(Reg.AX, Reg.DX);
    asm.Sub(Reg.AX, Reg.DX);
    asm.Mov(word, Reg.AX);
    return true;
  }

  /// <summary>
  /// O0248/O0108: recognizes the INTEGER min/max idioms and folds them to the integer <c>CMP</c>/keep the
  /// <c>MAX%</c>/<c>MIN%</c> intrinsic emits, storing the result once. Two shapes:
  /// <list type="bullet">
  /// <item>the diamond <c>IF a REL b THEN m = X ELSE m = Y</c>, where the compared operands are exactly the two
  ///   assigned values;</item>
  /// <item>the one-armed clamp <c>IF x REL bound THEN x = bound</c> (no ELSE), where the assigned variable is one
  ///   compared operand and the assigned value is the other - the saturating form real code writes most.</item>
  /// </list>
  /// The compared operands must be pure (a variable read or a constant): the branch form evaluates the taken
  /// operand a second time in its assignment, the fold once, so a side effect would differ. A numeric tie is
  /// irrelevant - the operands hold the same value, so either choice stores it. Declines a register-resident
  /// target. Sound under every $ERROR mode: it only ever replaces a branch computing the same integer.
  /// </summary>
  private bool TryEmitMinMaxIf(IfStmt i) {
    if (i.ElseIfs.Count > 0)
      return false;
    if (i.Condition is not BinaryExpr { Op: var op, Left: { } left, Right: { } right }
        || op is not (BinaryOp.Greater or BinaryOp.GreaterEqual or BinaryOp.Less or BinaryOp.LessEqual))
      return false;
    if (i.Then is not [AssignStmt { Target: { } thenTarget, Value: { } thenValue }])
      return false;
    if (thenTarget is not NameExpr m || model.TypeOf(m) is not ScalarType { IsFloat: false, ByteSize: 2 or 4 } mType)
      return false;
    var kind = KindOf(mType);   // Int16 or Int32 - the fold and store follow the width

    // an operand is foldable to this width when it is a pure read (a variable, or a constant) of the same kind
    bool IsPure(Expression e) =>
      KindOf(model.TypeOf(e)) == kind
      && (this.OptFolder.TryFold(e) is { Integer: not null }
          || (e is NameExpr && model.VariableBindings.ContainsKey(e) && !model.IntrinsicBindings.ContainsKey(e)));
    bool SameOperand(Expression a, Expression b) {
      if (this.OptFolder.TryFold(a) is { Integer: { } av } && this.OptFolder.TryFold(b) is { Integer: { } bv })
        return av == bv;
      return a is NameExpr && b is NameExpr && this.IsSameLvalue(a, b);
    }
    if (!IsPure(left) || !IsPure(right))
      return false;

    var relationKeepsLarger = op is BinaryOp.Greater or BinaryOp.GreaterEqual;
    bool wantMax;
    if (i.Else is [AssignStmt { Target: { } elseTarget, Value: { } elseValue }]) {
      // diamond: both arms assign m, the two assigned values being the two operands
      if (!this.IsSameLvalue(m, elseTarget))
        return false;
      bool thenIsLeft;
      if (SameOperand(thenValue, left) && SameOperand(elseValue, right))
        thenIsLeft = true;              // IF L REL R THEN m = L ELSE m = R : keep L when relation holds
      else if (SameOperand(thenValue, right) && SameOperand(elseValue, left))
        thenIsLeft = false;
      else
        return false;
      wantMax = thenIsLeft == relationKeepsLarger;
    } else if (i.Else is null or { Count: 0 }) {
      // clamp: IF this REL other THEN this = other - when the relation holds, m takes the OTHER operand
      bool mIsLeft;
      if (SameOperand(m, left) && SameOperand(thenValue, right))
        mIsLeft = true;
      else if (SameOperand(m, right) && SameOperand(thenValue, left))
        mIsLeft = false;
      else
        return false;
      // assigning the other operand when the relation holds keeps the opposite extreme from the diamond
      wantMax = mIsLeft ? !relationKeepsLarger : relationKeepsLarger;
    } else
      return false;

    // the target must be an addressable, non-resident cell (mirrors the abs idiom)
    if (!model.VariableBindings.TryGetValue(m, out var sym) || this.ResidentRegOf(sym) != null
        || this.TryDirectCell(sym) is not { } cell)
      return false;

    if (kind == ValueKind.Int16) {
      this.EmitIntegerMinMaxFold([left, right], wantMax);   // result in AX
      this._asm.Mov(cell.WithSize(OperandSize.Word), Reg.AX);
    } else {
      this.EmitLongMinMaxFold([left, right], wantMax);       // result in DX:AX
      this._asm.Mov(Adjust(cell, 0, OperandSize.Word), Reg.AX);
      this._asm.Mov(Adjust(cell, 2, OperandSize.Word), Reg.DX);
    }
    return true;
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

    // pb36 O5 / C1 ($CPU 80386): a LONG counter over an SI-clean leaf body lives in ESI for the loop
    if (kind == ValueKind.Int32 && constantStep is { } longStep
        && this.TryEmitForLongCounterInRegister(f, counter, slot, longStep))
      return;

    if (kind == ValueKind.Int16 && constantStep is { } fastStep) {
      // pb36 R4 auto-vectorisation ($CPU 80586 MMX): c(i)=a(i) OP b(i) runs four lanes/iteration through MMX
      if (this.TryEmitVectorizedFor(f, counter, slot.WithSize(OperandSize.Word), fastStep))
        return;
      // pb36 O6b: a single-statement array store a%(i%)=expr steps a pointer
      // instead of recomputing (i-lbound)*2 with IMUL on every iteration
      if (this.TryEmitForArrayStore(f, counter, slot.WithSize(OperandSize.Word), fastStep))
        return;
      // pb36 O6b: a single-statement a%(i%) read replaces the per-iteration subscript scale
      // with a stepped pointer. It is tried BEFORE register residency because it is strictly
      // better for that shape - stepping a pointer removes the scale entirely, where a resident
      // counter still has to compute the address from it every iteration.
      if (this.TryEmitForArrayIvsr(f, counter, slot, fastStep))
        return;
      // pb36 O5 (nested): an inner FOR under an SI-resident outer loop keeps its
      // counter in DI - the second (and last) safe index register
      if (this.TryEmitNestedForCounterInRegister(f, counter, slot.WithSize(OperandSize.Word), fastStep))
        return;
      // pb36 O5: an SI-clean body keeps the counter in SI - no per-iteration
      // cell traffic for the compare, increment or counter reads
      if (this.TryEmitForCounterInRegister(f, counter, slot.WithSize(OperandSize.Word), fastStep))
        return;
      this.EmitForInt16Fast(f, slot.WithSize(OperandSize.Word), fastStep);
      return;
    }

    var counterPlace = new Place(slot, false);
    var slotBytes = kind switch { ValueKind.Int16 => 2, ValueKind.Int32 => 4, _ => 8 };
    var limitType = kind switch { ValueKind.Int16 => PbType.Integer, ValueKind.Int32 => PbType.Long, _ => PbType.Double };

    // counter = from (the counter is a direct cell, not a temp)
    this.EmitExpression(f.From);
    this.Coerce(model.TypeOf(f.From), counter.Type, f.From);
    this.EmitStorePlace(counterPlace, counter.Type, f.From);

    // pb36 LICM: hoist loop-invariant pure subexpressions into the preheader. This
    // MUST run before the limit/step temps are allocated: EmitLicmPreheader grows
    // _cseBytes, and AllocTemp fixes a temp's BP offset from the _cseBytes value at
    // alloc time (-(frameLocal + cseBytes + tempBytes)). Allocating limit/step first
    // and growing _cseBytes afterwards would place the new CSE slot exactly on top of
    // the limit slot, so a hoisted invariant (e.g. a constant divisor) would overwrite
    // the loop bound - the loop would then never terminate.
    this.EmitLicmPreheader(f, counter);

    // limit and step into per-invocation stack temps (allocated after LICM so they sit
    // above the now-final CSE region)
    var limit = this.AllocTemp(slotBytes);
    var step = this.AllocTemp(slotBytes);

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

    var top = asm.DefineLabel();
    var negative = asm.DefineLabel();
    var body = asm.DefineLabel();
    var done = asm.DefineLabel();
    var continueLabel = asm.DefineLabel();
    this._exitFor.Push(done);
    this._iterateFor.Push(continueLabel);
    this._iterateAny.Push(continueLabel);
    this.AlignLoopTop();   // C2: cache-line-align the loop top (fetch-ahead win; output-invariant)
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

    // pb36 LICM: hoist loop-invariant pure subexpressions into the preheader. Must run
    // before the limit temp is allocated - LICM grows _cseBytes and AllocTemp fixes a
    // temp's offset from _cseBytes at alloc time, so growing it afterwards would place
    // the new CSE slot on top of the limit slot (see EmitFor for the full rationale).
    if (f.Variable is NameExpr nameVar && model.VariableBindings.TryGetValue(nameVar, out var counterSym))
      this.EmitLicmPreheader(f, counterSym);

    // O0113: a constant limit folds into the compare as an immediate under --optimize - no temp
    // cell, no per-iteration memory read. Gated on Optimize so the faithful path keeps the cmp-against-
    // memory form byte-identical to genuine. Non-constant / out-of-range limits keep the temp.
    // (TryFold only folds pure constants, so skipping the To evaluation drops no side effect.)
    //
    // The range that counts is the COUNTER's, not the word's: a byte counter compares in AL, so its
    // limit has to fit a byte or the immediate would be truncated into a different comparison.
    int? constLimit = this.Optimize
        && this.OptFolder.TryFold(f.To) is { Integer: { } toVal }
        && (unsigned
              ? toVal >= 0 && toVal <= (isByte ? 0xFF : 0xFFFF)
              : toVal >= (isByte ? sbyte.MinValue : short.MinValue)
                && toVal <= (isByte ? sbyte.MaxValue : short.MaxValue))
      ? (int)toVal : null;
    Mem? limit = null;
    if (constLimit is null) {
      limit = this.AllocTemp(2);
      this.EmitExpression(f.To);
      this.Coerce(model.TypeOf(f.To), counterType, f.To);
      asm.Mov(limit.Value, Reg.AX);
    }

    var top = asm.DefineLabel();
    var done = asm.DefineLabel();
    var continueLabel = asm.DefineLabel();
    this._exitFor.Push(done);
    this._iterateFor.Push(continueLabel);
    this._iterateAny.Push(continueLabel);
    var magnitude = (int)Math.Abs(step);

    // compare the just-loaded (or resident) counter against the limit - immediate when constant-folded
    void CmpAgainstLimit() {
      if (constLimit is { } cl)
        asm.Cmp(isByte ? Reg.AL : Reg.AX, (Imm)cl);
      else if (isByte)
        asm.Cmp(Reg.AL, limit!.Value.WithSize(OperandSize.Byte));
      else
        asm.Cmp(Reg.AX, limit!.Value);
    }
    // load the counter into AL/AX and compare it to the limit
    void Compare() {
      asm.Mov(isByte ? Reg.AL : Reg.AX, cell);
      CmpAgainstLimit();
    }
    // jump when the counter is PAST the limit (loop should stop)
    void JumpIfPast(Label t) {
      if (step >= 0)
        (unsigned ? (Action<Label>)asm.Ja : asm.Jg)(t);
      else
        (unsigned ? (Action<Label>)asm.Jb : asm.Jl)(t);
    }
    // increment the counter (leaving the new value in AL/AX), with the checked-wrap trap
    void Increment() {
      if (isByte) {
        asm.Mov(Reg.AL, cell);
        if (step >= 0) asm.Add(Reg.AL, (Imm)magnitude); else asm.Sub(Reg.AL, (Imm)magnitude);
        if (this.CheckNumeric)
          this.EmitRaiseWhen(asm.Jnc, 6);     // byte counters are unsigned: carry = wrap
        asm.Mov(cell, Reg.AL);
      } else {
        asm.Mov(Reg.AX, cell);
        if (step >= 0) asm.Add(Reg.AX, magnitude); else asm.Sub(Reg.AX, magnitude);
        if (this.CheckNumeric)
          this.EmitRaiseWhen(unsigned ? asm.Jnc : asm.Jno, 6);
        asm.Mov(cell, Reg.AX);
      }
    }

    // O0062 loop rotation ($OPTIMIZE SPEED): one entry guard plus a bottom test, dropping the
    // per-iteration JMP. The increment leaves the new counter in AL/AX, so the bottom re-tests it
    // in place with the inverse condition (stop-if-past becomes continue-if-not-past). The compare
    // runs the same N+1 times and the counter wraps identically, so the increment-then-test end
    // value (QUIRK 2.28) and every trip count are unchanged.
    if (this.Optimize && this.OptimizeSpeed) {
      Compare();
      JumpIfPast(done);                       // enter only if not already past
      this.AlignLoopTop();
      asm.MarkLabel(top);
      foreach (var s in f.Body)
        this.EmitStatement(s);
      asm.MarkLabel(continueLabel);
      Increment();
      CmpAgainstLimit();                       // AL/AX already holds the incremented counter
      if (step >= 0)
        (unsigned ? (Action<Label>)asm.Jbe : asm.Jle)(top);   // repeat while not past
      else
        (unsigned ? (Action<Label>)asm.Jae : asm.Jge)(top);
      asm.MarkLabel(done);
    } else {
      this.AlignLoopTop();   // C2: cache-line-align the loop top (fetch-ahead win; output-invariant)
      asm.MarkLabel(top);
      Compare();
      JumpIfPast(done);
      foreach (var s in f.Body)
        this.EmitStatement(s);
      asm.MarkLabel(continueLabel);
      Increment();
      asm.Jmp(top);
      asm.MarkLabel(done);
    }

    this._exitFor.Pop();
    this._iterateFor.Pop();
    this._iterateAny.Pop();
    if (limit != null) this.ReleaseTemp(2);
  }

  private void EmitDoLoop(DoLoopStmt d) {
    // pb36 O5 (beyond the FOR shape): an SI/DI-clean DO/LOOP keeps a hot accumulator in SI
    if (this.TryEmitDoLoopInRegister(d))
      return;

    var asm = this._asm;
    var top = asm.DefineLabel();
    var done = asm.DefineLabel();
    var continueLabel = asm.DefineLabel();
    this._exitDo.Push(done);
    this._iterateDo.Push(continueLabel);
    this._iterateAny.Push(continueLabel);

    // O-LICM: hoist loop-invariant subexpressions of a flat body into the preheader (run
    // once before the loop). No counter - invariance is "not written in the body". The gate
    // forbids checked arithmetic, so a zero-trip pre-test loop never traps on the hoist.
    // The pre/post test is re-evaluated per iteration, so a LEN(s$) in the condition of a
    // string scan (`WHILE i <= LEN(s$)`) hoists too when the body never writes s$.
    Expression?[] conds = [d.PreCondition, d.PostCondition];
    this.EmitLicmPreheader(d.Body, null, conds.Where(static c => c != null).ToArray()!);

    this.EmitDoLoopControl(d, top, continueLabel, done);
    this._exitDo.Pop();
    this._iterateDo.Pop();
    this._iterateAny.Pop();
  }

  /// <summary>
  /// Emits a DO loop's test/body/test control flow between the given labels (shared by the plain and
  /// the SI/DI-register-resident paths). O0062 loop rotation: under $OPTIMIZE SPEED a pre-tested loop
  /// (a PreCondition, no PostCondition) becomes one entry guard plus a bottom test, dropping the
  /// per-iteration unconditional JMP. The condition is evaluated the same N+1 times - one entry plus
  /// one after each body pass - so any side effect is preserved exactly; only the jump disappears.
  /// Every other shape keeps the top test and the jump-back.
  /// </summary>
  private void EmitDoLoopControl(DoLoopStmt d, Label top, Label cont, Label done) {
    var asm = this._asm;
    if (this.Optimize && this.OptimizeSpeed && d.PreCondition != null && d.PostCondition == null) {
      // enter the loop only if the condition holds (While: skip when false; Until: skip when true)
      this.EmitConditionalBranch(d.PreCondition, done, whenFalse: d.PreTest == LoopTestKind.While);
      this.AlignLoopTop();
      asm.MarkLabel(top);
      foreach (var s in d.Body)
        this.EmitStatement(s);
      asm.MarkLabel(cont);
      // repeat while the condition still holds (While: loop when true; Until: loop when false)
      this.EmitConditionalBranch(d.PreCondition, top, whenFalse: d.PreTest != LoopTestKind.While);
      asm.MarkLabel(done);
      return;
    }

    this.AlignLoopTop();   // C2: cache-line-align the loop top (fetch-ahead win; output-invariant)
    asm.MarkLabel(top);
    if (d.PreCondition != null) {
      // While: leave when false; Until: leave when true
      this.EmitConditionalBranch(d.PreCondition, done, whenFalse: d.PreTest == LoopTestKind.While);
    }

    foreach (var s in d.Body)
      this.EmitStatement(s);

    asm.MarkLabel(cont);
    if (d.PostCondition != null) {
      // While: repeat while true; Until: repeat while false
      this.EmitConditionalBranch(d.PostCondition, top, whenFalse: d.PostTest != LoopTestKind.While);
    } else
      asm.Jmp(top);

    asm.MarkLabel(done);
  }

  private void EmitSelect(SelectStmt s) {
    // pb36: a dense integer SELECT (all single-value constant cases) jumps through a
    // table instead of a compare chain - O(1) dispatch, same arm runs (output-identical)
    if (this.Optimize && this.TryEmitSelectJumpTable(s))
      return;
    // pb36 O0100: a sparse integer SELECT whose values have distinct low bits under some mask width
    // dispatches through a perfect hash - AND the subject, index a key+jump table pair, verify the
    // key (the hash is injective only on the case values) and jump. O(1), no compares. Tried before
    // the tree because it is constant time where the tree is O(log n); declines when no mask works.
    if (this.Optimize && this.OptimizeSpeed && this.TryEmitSelectPerfectHash(s))
      return;
    // pb36 O0098: a SPARSE integer SELECT the table declined (span too wide for a dense table) but
    // with many single-constant cases dispatches through a balanced binary decision tree - O(log n)
    // signed compares instead of the linear chain's O(n). The same arm runs, so output is identical.
    if (this.Optimize && this.OptimizeSpeed && this.TryEmitSelectDecisionTree(s))
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
        // O0099: an arm listing several point values in a <=16-wide window (CASE 1, 3, 5, 9) tests
        // membership with one shift + bit-0 test instead of a compare per value; declines otherwise.
        if (!(kind == ValueKind.Int16 && this.Optimize && this.OptimizeSpeed
              && this.TryEmitArmBitMask(arm, subject, armBody)))
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
          if (this.OptFolder.TryFold(sel.Value) is not { Integer: { } v } || v is < short.MinValue or > short.MaxValue)
            return false;
          byValue.TryAdd(v, i);
        } else {
          // Int32: values must be compile-time constants in LONG range
          if (this.OptFolder.TryFold(sel.Value) is not { Integer: { } v } || v is < int.MinValue or > int.MaxValue)
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

    // O0101: the general table is one word per span entry. When the span is wide but the distinct
    // targets are few, a byte index table into a small address table is smaller (span + 2*K bytes vs
    // 2*span). Build the compressed form and use it under $OPTIMIZE SIZE when it actually shrinks;
    // otherwise emit the plain word table. (The index fits a byte because the span is <= 256.)
    var slotOf = new Dictionary<Label, int>();
    var targets = new List<Label>();
    var indexBytes = new byte[span];
    for (var v = min; v <= max; ++v) {
      var lbl = byValue.TryGetValue(v, out var arm) ? armLabels[arm] : defaultLabel;
      if (!slotOf.TryGetValue(lbl, out var slot)) {
        slot = targets.Count;
        slotOf[lbl] = slot;
        targets.Add(lbl);
      }
      indexBytes[v - min] = (byte)slot;
    }

    if (this.OptimizeSize && targets.Count <= 256 && span > 2L * targets.Count) {
      var byteTable = asm.DefineLabel();
      var addrTable = asm.DefineLabel();
      asm.Mov(Reg.BX, Reg.AX);                      // BX = 0-based index (<= 255, the span is <= 256)
      asm.Mov(Reg.BL, Mem.Byte(Reg.BX, byteTable)); // BL = the target slot; BH stays 0 (index <= 255)
      asm.Shl(Reg.BX, 1);
      asm.Jmp(Mem.Word(Reg.BX, addrTable));         // JMP [addrTable + slot*2]
      asm.MarkLabel(byteTable);
      asm.Db(indexBytes);
      asm.MarkLabel(addrTable);
      foreach (var t in targets)
        asm.Dw(t);
    } else {
      var table = asm.DefineLabel();
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 1);                           // word-sized entries
      asm.Jmp(Mem.Word(Reg.BX, table));             // JMP [table + index*2]
      asm.MarkLabel(table);                          // data: only reached via the indexed jump above
      foreach (var t in indexBytes)
        asm.Dw(targets[t]);
    }

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

  /// <summary>
  /// pb36 O0100: a sparse INTEGER SELECT whose case values become collision-free under some low-bit
  /// mask (<c>value AND (2^k - 1)</c> distinct for all values, k &lt;= 8) dispatches through a perfect
  /// hash: mask the subject to an index into a key table and a parallel jump table, verify the key
  /// (the hash is injective only on the case values, so any other input must be rejected) and take the
  /// indexed jump - constant time, no compare per value and no bounds guard (the mask bounds the
  /// index). Empty slots route to the default regardless of the verify, so a colliding non-member is
  /// safe. First-match-wins is preserved by keying each value to its first arm. Same arm runs as the
  /// chain (output-identical). Tried before the tree (O(1) beats O(log n)); INTEGER subjects, gated on
  /// $OPTIMIZE SPEED. Declines when no mask within 8 bits separates the values.
  /// </summary>
  private bool TryEmitSelectPerfectHash(SelectStmt s) {
    if (KindOf(model.TypeOf(s.Subject)) != ValueKind.Int16)
      return false;
    var byValue = new Dictionary<int, int>();     // case value -> first matching arm
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
        if (this.OptFolder.TryFold(sel.Value) is not { Integer: { } v } || v is < short.MinValue or > short.MaxValue)
          return false;
        byValue.TryAdd((short)v, i);
      }
    }
    if (byValue.Count < 8)
      return false;                               // below this the tree/chain is smaller and fast enough

    var values = byValue.Keys.ToList();
    var k = -1;
    for (var w = 3; w <= 8; ++w) {                // smallest table 2^w whose low w bits separate the values
      var mask = (1 << w) - 1;
      var seen = new HashSet<int>();
      var distinct = true;
      foreach (var v in values)
        if (!seen.Add(v & mask)) { distinct = false; break; }
      if (distinct) { k = w; break; }
    }
    if (k < 0)
      return false;                               // no perfect AND-mask within 8 bits: fall to the tree

    var maskVal = (1 << k) - 1;
    var size = 1 << k;
    var slotValue = new int?[size];
    foreach (var v in values)
      slotValue[v & maskVal] = v;

    var asm = this._asm;
    var end = asm.DefineLabel();
    var keyTable = asm.DefineLabel();
    var jumpTable = asm.DefineLabel();
    var armLabels = s.Arms.Select(_ => asm.DefineLabel()).ToList();
    var defaultLabel = elseArm is { } e ? armLabels[e] : end;

    this._exitSelect.Push(end);
    this.EmitExpression(s.Subject);
    this.Coerce(model.TypeOf(s.Subject), PbType.Integer, s.Subject);   // subject -> AX
    // SELECT dispatch must preserve SI/DI (a resident FOR counter / accumulator); it may use AX, BX,
    // CX, DX - so the index lives in BX (as the jump table does) and the original in CX for the verify.
    asm.Mov(Reg.CX, Reg.AX);                      // CX keeps the original value for the verify
    asm.And(Reg.AX, (Imm)maskVal);                // AX = perfect hash
    asm.Shl(Reg.AX, 1);                           // word index
    asm.Mov(Reg.BX, Reg.AX);
    asm.Cmp(Reg.CX, Mem.Word(Reg.BX, keyTable));  // verify: is this actually the value keyed at the slot?
    asm.Jne(defaultLabel);
    asm.Jmp(Mem.Word(Reg.BX, jumpTable));         // indexed indirect jump to the arm

    asm.MarkLabel(keyTable);                       // data: the value keyed at each slot (0 for an empty slot)
    for (var i = 0; i < size; ++i)
      asm.Dw((ushort)(slotValue[i] ?? 0));
    asm.MarkLabel(jumpTable);                      // data: the arm for each slot, default for empties
    for (var i = 0; i < size; ++i)
      asm.Dw(slotValue[i] is { } sv ? armLabels[byValue[sv]] : defaultLabel);

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

  /// <summary>
  /// pb36 O0098: a sparse INTEGER SELECT (all single-constant point cases, no ranges / IS, that the
  /// dense jump table declined) dispatches through a balanced binary search tree over the sorted case
  /// values - each internal node is one signed CMP against the median, so a match is found in
  /// O(log n) compares instead of the linear chain's O(n). First-match-wins is preserved by mapping
  /// each value to its FIRST arm; a value in no arm falls to CASE ELSE (or the end). The same arm body
  /// runs for every subject, so runtime output is identical to the chain. Gated on $OPTIMIZE SPEED
  /// (the tree's extra JL/JG branches can be larger than the chain) and INTEGER subjects only.
  /// </summary>
  private bool TryEmitSelectDecisionTree(SelectStmt s) {
    if (KindOf(model.TypeOf(s.Subject)) != ValueKind.Int16)
      return false;
    var byValue = new Dictionary<short, int>();   // case value -> first matching arm (first match wins)
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
          return false;                             // a range / IS arm cannot be a tree point
        if (this.OptFolder.TryFold(sel.Value) is not { Integer: { } v } || v is < short.MinValue or > short.MaxValue)
          return false;
        byValue.TryAdd((short)v, i);                // first arm to name this value wins
      }
    }
    if (byValue.Count < 8)
      return false;                                 // below this the linear chain is as fast and smaller

    var asm = this._asm;
    var end = asm.DefineLabel();
    var armLabels = s.Arms.Select(_ => asm.DefineLabel()).ToList();
    var defaultLabel = elseArm is { } e ? armLabels[e] : end;
    var points = byValue.Select(kv => (Value: kv.Key, Arm: kv.Value)).OrderBy(p => p.Value).ToList();

    this._exitSelect.Push(end);
    this.EmitExpression(s.Subject);
    this.Coerce(model.TypeOf(s.Subject), PbType.Integer, s.Subject);   // subject -> AX, held for every compare

    void Tree(int lo, int hi) {
      var mid = (lo + hi) / 2;
      var (value, arm) = points[mid];
      asm.Cmp(Reg.AX, (Imm)(int)value);
      asm.Je(armLabels[arm]);
      var hasLeft = lo <= mid - 1;                  // values < value (sorted below mid)
      var hasRight = mid + 1 <= hi;                 // values > value (sorted above mid)
      if (hasLeft && hasRight) {
        var right = asm.DefineLabel();
        asm.Jg(right);                              // signed: AX > value -> right subtree
        Tree(lo, mid - 1);                          // AX < value -> left subtree (fall through)
        asm.MarkLabel(right);
        Tree(mid + 1, hi);
      } else if (hasLeft) {
        asm.Jg(defaultLabel);                       // AX > value but nothing larger matches
        Tree(lo, mid - 1);
      } else if (hasRight) {
        asm.Jl(defaultLabel);                       // AX < value but nothing smaller matches
        Tree(mid + 1, hi);
      } else {
        asm.Jmp(defaultLabel);                      // leaf, not equal -> no match
      }
    }
    Tree(0, points.Count - 1);

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

  /// <summary>
  /// pb36 O0099: a SELECT arm listing several single-constant point values whose span fits a 16-bit
  /// mask (max-min &lt;= 15) tests set membership with one unsigned range guard, one variable shift and
  /// one bit-0 test - constant time, no per-value compare. The values are normalized to the minimum
  /// (so a window not starting at zero, or with negative values, still fits), a mask with a bit per
  /// value is built at compile time, and `SHR mask, CL` brings the subject's bit to position 0.
  /// Jumps to <paramref name="armBody"/> on membership and falls through otherwise; declines (false)
  /// for ranges, IS-relations, fewer than 3 values, or a span wider than 15.
  /// </summary>
  private bool TryEmitArmBitMask(CaseArm arm, Mem subject, Label armBody) {
    if (arm.Selectors.Count < 3)
      return false;                                 // below three values the compare chain is already small
    var values = new List<int>();
    foreach (var sel in arm.Selectors) {
      if (sel.Value == null || sel.RangeUpper != null || sel.IsComparison != null)
        return false;
      if (this.OptFolder.TryFold(sel.Value) is not { Integer: { } v } || v is < short.MinValue or > short.MaxValue)
        return false;
      values.Add((int)v);
    }
    if (this.MaskFor(values) is not { } m)
      return false;

    var asm = this._asm;
    var skip = asm.DefineLabel();
    asm.Mov(Reg.AX, subject.WithSize(OperandSize.Word));
    this.EmitMaskMembership(m, skip);               // sets ZF: bit clear (not a member) or the range guard jumped
    asm.Jnz(armBody);                               // bit set -> the subject is in the set
    asm.MarkLabel(skip);
    return true;
  }

  /// <summary>
  /// The membership window for a value set (O0099): the minimum, the span, the compile-time bit mask
  /// (one bit per value, normalized to the minimum) and whether a 32-bit mask is needed. A span up to
  /// 15 fits a native 16-bit mask; 16..31 needs the 32-bit form and so <c>$CPU 80386</c>. Returns null
  /// for a wider span or when 386 is not available.
  /// </summary>
  private (int Min, int Span, long Mask, bool Wide)? MaskFor(List<int> values) {
    int min = values.Min(), max = values.Max();
    var span = max - min;
    if (span > 31 || (span > 15 && !this.Cpu386))
      return null;
    long mask = 0;
    foreach (var v in values)
      mask |= 1L << (v - min);
    return (min, span, mask, span > 15);
  }

  /// <summary>
  /// Emits the O0099 membership test for a subject already in AX: normalize to the window minimum, an
  /// unsigned range guard to <paramref name="notMember"/>, then <c>SHR mask, CL</c> and a bit-0 <c>TEST</c>.
  /// On return ZF is set iff the subject is NOT in the set (bit 0 clear), so the caller finishes with a
  /// single JZ/JNZ. The 32-bit form (386, span 16..31) shifts a dword mask in EAX.
  /// </summary>
  private void EmitMaskMembership((int Min, int Span, long Mask, bool Wide) m, Label notMember) {
    var asm = this._asm;
    if (m.Min != 0)
      asm.Sub(Reg.AX, (Imm)m.Min);                  // normalize to 0-based (min<0 subtracts a negative = adds)
    asm.Cmp(Reg.AX, (Imm)m.Span);
    asm.Ja(notMember);                              // unsigned: below min (wrapped) or above max -> not a member
    asm.Mov(Reg.CX, Reg.AX);                        // CL = the bit index (0..31)
    if (m.Wide) {
      asm.Mov(Reg.EAX, (Imm)unchecked((int)m.Mask));
      asm.Shr(Reg.EAX, Reg.CL);
      asm.Test(Reg.EAX, (Imm)1);
    } else {
      asm.Mov(Reg.AX, (Imm)(int)(short)m.Mask);
      asm.Shr(Reg.AX, Reg.CL);
      asm.Test(Reg.AX, (Imm)1);
    }
  }

  /// <summary>
  /// pb36 O0099: the <c>IF k = 1 OR k = 3 OR k = 5 THEN</c> spelling of a small-set membership test.
  /// Flattens the OR tree; every leaf must be <c>k = const</c> for the SAME 16-bit integer variable,
  /// the values must fit a 16-bit mask (<c>max - min &lt;= 15</c>) and number at least 3. Emits the same
  /// normalize / range-guard / <c>SHR</c> / bit-0 test the SELECT arm form uses, branching to
  /// <paramref name="target"/> on the requested truth value. Evaluates <c>k</c> once (a bare variable,
  /// no side effect), so it is equivalent to the short-circuited compare chain it replaces. Declines
  /// (false) for any non-conforming leaf, a mixed variable, a wide window or fewer than 3 values.
  /// </summary>
  private bool TryEmitOrChainBitMask(Expression condition, Label target, bool whenFalse) {
    // Two complementary spellings of small-set membership: `k = a OR k = b OR …` is TRUE when k is IN
    // the set; its De Morgan complement `k <> a AND k <> b AND …` is TRUE when k is NOT in it.
    bool member;
    BinaryOp treeOp, leafOp;
    if (condition is BinaryExpr { Op: BinaryOp.Or }) { member = true; treeOp = BinaryOp.Or; leafOp = BinaryOp.Equal; }
    else if (condition is BinaryExpr { Op: BinaryOp.And }) { member = false; treeOp = BinaryOp.And; leafOp = BinaryOp.NotEqual; }
    else
      return false;

    NameExpr? keyVar = null;
    var values = new List<int>();

    bool Collect(Expression e) {
      if (e is BinaryExpr eb && eb.Op == treeOp)
        return Collect(eb.Left) && Collect(eb.Right);
      if (e is BinaryExpr leaf && leaf.Op == leafOp) {
        var (name, valueExpr) = leaf.Left is NameExpr ? (leaf.Left, leaf.Right) : leaf.Right is NameExpr ? (leaf.Right, leaf.Left) : (null, null);
        if (name is not NameExpr n || model.IntrinsicBindings.ContainsKey(n)
            || model.TypeOf(n) is not ScalarType { IsFloat: false, ByteSize: 2 }
            || !model.VariableBindings.TryGetValue(n, out var nsym))
          return false;
        if (keyVar == null)
          keyVar = n;
        else if (!model.VariableBindings.TryGetValue(keyVar, out var ksym) || !ReferenceEquals(ksym, nsym))
          return false;                               // a different variable: not one set membership
        if (this.OptFolder.TryFold(valueExpr) is not { Integer: { } v } || v is < short.MinValue or > short.MaxValue)
          return false;
        values.Add((int)v);
        return true;
      }
      return false;
    }

    if (!Collect(condition) || keyVar == null || values.Count < 3 || this.MaskFor(values) is not { } m)
      return false;

    var asm = this._asm;
    this.EmitExpression(keyVar);                      // k -> AX (bare 16-bit variable read)
    // The condition is TRUE when (k in set) == member. Jump to target when the condition's truth is
    // !whenFalse; EmitMaskMembership sets ZF (bit clear = NOT in set) and jumps to notMember when out
    // of range. `member == whenFalse` is exactly when we must branch on the NOT-in-set outcome.
    if (member == whenFalse) {
      this.EmitMaskMembership(m, target);             // out of range -> target
      asm.Jz(target);                                 // bit clear (not in set) -> target
    } else {
      var skip = asm.DefineLabel();
      this.EmitMaskMembership(m, skip);               // out of range -> skip (in-set outcome not reached)
      asm.Jnz(target);                                // bit set (in set) -> target
      asm.MarkLabel(skip);
    }
    return true;
  }

  private void EmitSelectorInt16(SelectStmt s, Mem subject, CaseSelector selector, Label armBody) {
    var asm = this._asm;
    // O0032 range fold: a constant `CASE lo TO hi` is one unsigned compare (subject - lo) <=u (hi - lo)
    // instead of two signed compares. Jumps to the arm when in range and falls through otherwise.
    if (selector.RangeUpper != null && this.Optimize
        && this.OptFolder.TryFold(selector.Value!) is { Integer: { } loV } && loV is >= short.MinValue and <= short.MaxValue
        && this.OptFolder.TryFold(selector.RangeUpper) is { Integer: { } hiV } && hiV is >= short.MinValue and <= short.MaxValue
        && loV <= hiV) {
      asm.Mov(Reg.AX, subject.WithSize(OperandSize.Word));
      if (loV != 0)
        asm.Sub(Reg.AX, (Imm)(int)loV);
      asm.Cmp(Reg.AX, (Imm)(int)(hiV - loV));
      asm.Jbe(armBody);
      return;
    }
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
    var relation = selector.IsComparison ?? CaseComparison.Equal;
    asm.Mov(Reg.AX, subject.WithSize(OperandSize.Word));
    asm.Call(this._rt.StrDup);                  // compare consumes - keep the subject alive
    asm.Push(Reg.AX);
    this.EmitExpression(selector.Value!);
    asm.Mov(Reg.DX, Reg.AX);
    asm.Pop(Reg.AX);
    // O0298: an equality arm (CASE "quit") only needs the length-guarded compare; ordering arms
    // (CASE IS < ...) need the three-way StrCmp.
    asm.Call(this.Optimize && relation is CaseComparison.Equal or CaseComparison.NotEqual ? this._rt.StrCmpEq : this._rt.StrCmp);
    asm.Test(Reg.AX, Reg.AX);
    switch (relation) {
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

    // pb36 O8: INCR/DECR of a non-resident 2-byte integer direct cell updates memory in place
    // without parking the amount across the (empty) address computation - a constant amount folds
    // into one immediate (INC/DEC for +/-1), a cell amount loads into AX then ADD/SUB [cell],AX.
    if (this.Optimize && !this.CheckOverflow && !this.CheckNumeric
        && id.Amount != null && kind == ValueKind.Int16
        && this.TryInt16MemOperand(id.Target, PbType.Integer) is { } directCell) {
      if (this.TryModularFoldConst(id.Amount, out var amt)) {
        var net = (short)((id.Increment ? amt : -amt) & 0xFFFF);
        if (net == 1)
          asm.Inc(directCell);
        else if (net == -1)
          asm.Dec(directCell);
        else if (net != 0)
          asm.Add(directCell, (Imm)net);     // signed immediate: a negative net subtracts (modular)
        return;
      }
      this.EmitExpression(id.Amount);
      this.Coerce(model.TypeOf(id.Amount), targetType, id.Amount);
      if (id.Increment)
        asm.Add(directCell, Reg.AX);
      else
        asm.Sub(directCell, Reg.AX);
      return;
    }

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
