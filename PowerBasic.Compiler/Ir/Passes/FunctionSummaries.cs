namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0161 — per-procedure mod/ref summaries, computed once over the call graph so every other pass can
/// ask "does calling this touch memory?" instead of assuming the worst.
///
/// <para>
/// Today a call is a wall: <see cref="Dce"/> treats every call as side-effecting, so a call whose
/// result nothing uses survives even when the callee does nothing but arithmetic. That is the right
/// default and the wrong answer for the many small BASIC FUNCTIONs that only compute.
/// </para>
/// <para>
/// The summary is deliberately coarse — two bits, reads and writes — because that is what the
/// consumers actually need and because a coarse fact computed correctly beats a precise one computed
/// optimistically. It is a fixpoint over the call graph: a function writes memory if it stores, or
/// calls something that writes. Anything it cannot see through makes it maximally impure: an external
/// declaration, an indirect call, an armed error handler, inline assembly. The one exception is the
/// short checked list in <see cref="IsPureExternal"/> - externals whose contract is known rather than
/// guessed - which is also what lets <see cref="Gvn"/> number a call and <see cref="Licm"/> hoist one.
/// </para>
/// <para>
/// Recursion is why the fixpoint starts from "pure" and only ever adds impurity. Starting from
/// "impure" and removing would need a proof about the cycle before entering it; starting clean and
/// propagating outward reaches the same answer and terminates, because each round can only ever set
/// bits.
/// </para>
/// </summary>
public sealed class FunctionSummaries {

  /// <summary>What calling a function may do to memory.</summary>
  public readonly record struct Summary(bool ReadsMemory, bool WritesMemory) {
    /// <summary>True when the call can be removed if its result is unused.</summary>
    public bool IsPure => !this.WritesMemory;
  }

  /// <summary>
  /// The floating-point math intrinsics, named as <see cref="Ir.IrLowering"/> emits them
  /// (<c>llvm.sqrt.f80</c>) and keyed by the bare function so one row covers all three widths.
  /// One row per PB intrinsic that reaches the IR this way - SQR, SIN, COS, TAN, ATN, LOG, EXP and
  /// <c>^</c> - and nothing speculative beyond them: a row for a name the lowering never emits could
  /// not be checked against anything. This is the whole list on purpose, see <see cref="IsPureExternal"/>.
  /// </summary>
  private static readonly HashSet<string> _pureMathIntrinsics = new(StringComparer.Ordinal) {
    "sqrt", "sin", "cos", "tan", "atan", "log", "exp", "pow",
  };

  /// <summary>
  /// Whether a call to this EXTERNAL is pure: no observable effect, no dependence on mutable state,
  /// and the same answer for the same arguments - so two of them may be merged and one may be moved.
  ///
  /// <para>
  /// Everything on the list is a float math intrinsic, and the argument is the same for each row.
  /// They take floats by value and answer with one: no pointer reaches them, so there is no memory
  /// they could read or write and nothing they could allocate or free. They are deterministic - the
  /// x86-16 back end lowers them to bare x87 instructions or to <c>rt_sin</c>/<c>rt_cos</c>/
  /// <c>rt_tan</c>/<c>rt_pow</c>, which read only read-only constant cells and raise no runtime error;
  /// the C back end lowers them to <c>&lt;math.h&gt;</c>, which is a function of its argument too. The
  /// residue is the x87 status word and C's <c>errno</c>, and neither is observable here: no PB
  /// construct exposes them, and the IR path does not model <c>$ERROR NUMERIC</c> at all, so nothing
  /// in an emitted image reads either one.
  /// </para>
  /// <para>
  /// What is deliberately NOT on the list, because the temptation is real:
  /// </para>
  /// <list type="bullet">
  ///   <item><c>rt_str_len</c> looks like a pure read and is not one - it CONSUMES its argument. The
  ///   DOS <c>rt_len</c> frees the handle before returning, which is why the lowering puts an
  ///   <c>rt_str_dup</c> on every read of a string variable. Merging two of them would free one block
  ///   twice; hoisting one out of a loop would leave the body reading freed memory.</item>
  ///   <item><c>rt_str_dup</c> allocates, and the allocation is observed - by whoever frees it. Two
  ///   borrows merged into one give two consumers the same handle to release. "Nothing can observe
  ///   the allocation" is exactly the condition it fails.</item>
  ///   <item>Everything else in the string ABI either consumes a handle, allocates one, or reads
  ///   bytes that another statement can have written. A length read is only redundant in the absence
  ///   of an intervening write, and that is a memory-dependence fact - this GVN does not number even
  ///   a plain load, so it is in no position to number a call that behaves like one.</item>
  /// </list>
  /// </summary>
  public static bool IsPureExternal(string name) {
    ArgumentNullException.ThrowIfNull(name);
    if (!name.StartsWith("llvm.", StringComparison.Ordinal))
      return false;                              // every rt_* entry either consumes, allocates or does I/O
    var bare = name[5..];
    var width = bare.IndexOf(".f", StringComparison.Ordinal);
    return _pureMathIntrinsics.Contains(width > 0 ? bare[..width] : bare);
  }

  /// <summary>
  /// Whether a call to this external may additionally be executed on a path the original program
  /// would not have taken - what LICM needs before it may hoist one into a preheader.
  ///
  /// <para>
  /// Same list, and the extra requirement is trap-freedom. None of these faults on any input: the
  /// runtime installs the x87 with its exceptions masked, so <c>SQR(-1)</c> and <c>LOG(0)</c> answer
  /// with an indefinite or an infinity rather than raising #MF, and IEEE says the same of the C
  /// library. That is already the position the pass takes on <c>FMul</c>, which sets the very same
  /// status bits and is hoisted today; integer and float division stay excluded there and no
  /// division appears here.
  /// </para>
  /// </summary>
  public static bool IsSpeculatableExternal(string name) => IsPureExternal(name);

  private readonly Dictionary<IrFunction, Summary> _summaries = new(ReferenceEqualityComparer.Instance);

  /// <summary>The summary for a function - maximally impure for anything not in the module.</summary>
  public Summary For(IrFunction function)
    => this._summaries.TryGetValue(function, out var summary) ? summary : new(true, true);

  /// <summary>Computes summaries for every function in <paramref name="module"/>.</summary>
  public static FunctionSummaries Compute(IrModule module) {
    var result = new FunctionSummaries();
    foreach (var function in module.Functions) {
      var opaque = function.IsDeclaration
        ? !IsPureExternal(function.Name)         // a declaration is a wall unless it is on the list
        : function.HasErrorHandler || function.HasInlineAsm;
      result._summaries[function] = opaque
        ? new(true, true)                        // nothing here can be seen through
        : new(false, false);                     // optimistic: only ever made worse below
    }

    for (var changed = true; changed;) {
      changed = false;
      foreach (var function in module.Functions) {
        if (function.IsDeclaration)
          continue;
        var current = result._summaries[function];
        if (current is { ReadsMemory: true, WritesMemory: true })
          continue;

        var merged = current;
        foreach (var instruction in function.AllInstructions)
          merged = Merge(merged, instruction, result);
        if (merged == current)
          continue;
        result._summaries[function] = merged;
        changed = true;
      }
    }
    return result;
  }

  private static Summary Merge(Summary current, IrInstruction instruction, FunctionSummaries known) => instruction switch {
    IrLoad => current with { ReadsMemory = true },
    IrStore => current with { WritesMemory = true },
    IrInlineAsm => new(true, true),
    // An indirect call could be anything. A direct one contributes its callee's summary, which for a
    // recursive cycle is whatever has been established so far - correct because the fixpoint only ever
    // adds, so a cycle settles at the union of everything reachable round it.
    IrCall call => call.Callee is IrFunction callee
      ? Union(current, known.For(callee))
      : new(true, true),
    _ => current,
  };

  private static Summary Union(Summary a, Summary b)
    => new(a.ReadsMemory || b.ReadsMemory, a.WritesMemory || b.WritesMemory);

  /// <summary>
  /// Removes calls whose result nothing uses and whose callee writes no memory. Returns how many went.
  ///
  /// This is the first consumer of the summaries and the reason they exist: a BASIC FUNCTION that only
  /// computes is exactly the shape the optimizer leaves behind after propagating its result away, and
  /// without a summary there is no way to tell it from one that prints.
  ///
  /// <para>
  /// A <c>NOINLINE</c> callee is exempt. Dropping the call is SOUND - nothing observable changes - but
  /// the modifier's contract is that the call survives, and the shape it most often guards is a
  /// procedure that exists only to be a barrier: an empty <c>SUB</c> taking a variable BYREF so the
  /// optimizer cannot know its value. Removing that call removes the barrier, and everything the
  /// programmer wanted to inspect folds away behind it.
  /// </para>
  /// </summary>
  public static int RemoveDeadPureCalls(IrModule module) {
    var summaries = Compute(module);
    var removed = 0;
    foreach (var function in module.Functions) {
      if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
        continue;
      foreach (var instruction in function.AllInstructions.ToList())
        if (instruction is IrCall { Callee: IrFunction callee } call
            && call.HasNoUsers
            && !callee.NoInline
            && summaries.For(callee).IsPure) {
          call.EraseFromParent();
          ++removed;
        }
    }
    return removed;
  }
}
