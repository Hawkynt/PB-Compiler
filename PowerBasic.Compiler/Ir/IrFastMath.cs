namespace PowerBasic.Compiler.Ir;

/// <summary>
/// Numerical freedoms granted to one floating-point IR instruction. The vocabulary and bit layout
/// intentionally match LLVM's fast-math flags so a hosted backend can carry the exact contract rather
/// than translate one vague switch into target-specific guesses.
/// </summary>
[Flags]
public enum IrFastMathFlags {
  None = 0,
  Reassociate = 1 << 0,
  NoNaNs = 1 << 1,
  NoInfs = 1 << 2,
  NoSignedZeros = 1 << 3,
  AllowReciprocal = 1 << 4,
  AllowContract = 1 << 5,
  ApproxFunc = 1 << 6,

  /// <summary>
  /// All relaxed floating-point semantics selected by <c>$OPTIMIZE SPEED</c>. Ordinary optimization
  /// remains strict; SPEED is the explicit objective that permits a faster non-bit-identical answer.
  /// </summary>
  Fast = Reassociate | NoNaNs | NoInfs | NoSignedZeros | AllowReciprocal | AllowContract | ApproxFunc,
}
