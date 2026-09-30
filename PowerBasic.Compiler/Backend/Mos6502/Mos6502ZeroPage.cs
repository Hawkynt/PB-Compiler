namespace PowerBasic.Compiler.Backend.Mos6502;

/// <summary>
/// The page-zero cells the generated code and its runtime work in, allocated one after another from
/// <c>$02</c> so no two can overlap. The range is BASIC's own while it runs: start-up copies it aside
/// and the exit path puts it back, so returning to the <c>READY.</c> prompt finds BASIC's pointers
/// where it left them. The KERNAL's screen editor and interrupt handler work above <c>$90</c> and are
/// not disturbed.
/// </summary>
public static class Mos6502ZeroPage {
  public const int First = 0x02;

  private static int _next = First;

  private static M6502Address Take(int bytes) {
    var cell = M6502Address.Absolute(_next);
    _next += bytes;
    return cell;
  }

  /// <summary>The pointer <c>(Ptr),Y</c> loads and stores go through.</summary>
  public static readonly M6502Address Ptr = Take(2);

  /// <summary>A second pointer, for copies.</summary>
  public static readonly M6502Address Ptr2 = Take(2);

  /// <summary>
  /// Runtime arguments: a routine's operands are laid out here one after another, each at its own
  /// width. The first eight bytes are also the left operand, and the quotient, of integer arithmetic.
  /// </summary>
  public static readonly M6502Address Arg = Take(ArgumentBytes);

  /// <summary>The right operand of integer arithmetic: <see cref="Arg"/> + 8.</summary>
  public static readonly M6502Address ArgB = Arg.Plus(8);

  /// <summary>Where a function or runtime routine leaves its result: wide enough for an 80-bit float.</summary>
  public static readonly M6502Address Ret = Take(10);

  /// <summary>The soft stack pointer: recursion saves frames below it.</summary>
  public static readonly M6502Address SoftStack = Take(2);

  /// <summary>Runtime scratch; also where integer division leaves its remainder.</summary>
  public static readonly M6502Address Temp = Take(8);

  /// <summary>Eight more bytes of runtime scratch: a 64-bit division's trial difference.</summary>
  public static readonly M6502Address Temp2 = Take(8);

  /// <summary>Signs a signed division remembers across the unsigned one.</summary>
  public static readonly M6502Address QuotientSign = Take(1);
  public static readonly M6502Address RemainderSign = Take(1);

  /// <summary>A digit count the number printers keep.</summary>
  public static readonly M6502Address DigitCount = Take(1);

  /// <summary>
  /// The floating-point accumulators, unpacked: <see cref="FloatSign"/> offset, <see cref="FloatExponent"/>
  /// offset (a 16-bit biased exponent, x87's bias) and <see cref="FloatMantissa"/> offset (nine bytes,
  /// least significant first; the leading one explicit at bit 7 of the last, the first a guard byte
  /// whose bit 0 is sticky).
  /// </summary>
  public static readonly M6502Address FloatA = Take(FloatBytes);
  public static readonly M6502Address FloatB = Take(FloatBytes);

  /// <summary>Scratch for a float product or a division's remainder: sixteen bytes.</summary>
  public static readonly M6502Address FloatWork = Take(16);

  /// <summary>The last cell allocated: start-up saves <see cref="First"/> through this one.</summary>
  public static readonly int Last = _next - 1;

  /// <summary>How many bytes <see cref="Arg"/> holds: two eight-byte operands.</summary>
  public const int ArgumentBytes = 16;

  public const int FloatSign = 0;
  public const int FloatExponent = 1;
  public const int FloatMantissa = 3;
  public const int FloatMantissaBytes = 9;
  public const int FloatBytes = FloatMantissa + FloatMantissaBytes;
}
