namespace PowerBasic.Compiler.Backend;

/// <summary>
/// What the instruction selector is compiling FOR: the instruction set it may assume and the
/// objective it is trading against. Both are properties of the whole compilation rather than of the
/// function, and both are already decided by the directives the front end read
/// (<c>$CPU 80386</c>, <c>$OPTIMIZE SIZE|SPEED|OFF</c>) - this is only how they reach the back end.
///
/// <para>
/// It exists because the IR is deliberately target-independent and objective-free, and a few
/// decisions genuinely are neither. A dispatch is the clearest case: whether a <c>SELECT CASE</c>
/// becomes a word jump table, the byte-index table that is smaller and one load slower, a membership
/// mask that needs a 32-bit shift, or the compare chain that beats all of them for three arms is not
/// a question about the program - the IR is the same either way - but about which of those the
/// machine can encode and which the user asked for. Answering it in a pass would mean the pass
/// deciding an encoding it cannot see.
/// </para>
///
/// <para>
/// <see cref="Cpu386"/> mirrors the direct emitter's <c>_rt.Cpu386</c> and MUST be given the same
/// answer: the two paths emit into one image, so a routed function that assumes a 386 while a
/// directly-emitted one does not is a program that runs on two different machines.
/// </para>
/// </summary>
/// <param name="Cpu386">the declared target is an 80386 or later (<c>$CPU 80386</c>)</param>
/// <param name="Optimize">optimization is on at all - with it off nothing here may change the code the selector would otherwise write</param>
/// <param name="OptimizeSpeed">favour speed over size (<c>$OPTIMIZE SPEED</c>)</param>
/// <param name="OptimizeSize">favour size over speed (<c>$OPTIMIZE SIZE</c>)</param>
public readonly record struct SelectionTarget(
  bool Cpu386 = false,
  bool Optimize = false,
  bool OptimizeSpeed = false,
  bool OptimizeSize = false) {

  /// <summary>An 8086 with no optimization - what a hand-built test function is selected for.</summary>
  public static SelectionTarget Baseline { get; } = new();
}
