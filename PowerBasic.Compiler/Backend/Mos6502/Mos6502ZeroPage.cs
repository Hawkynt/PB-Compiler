namespace PowerBasic.Compiler.Backend.Mos6502;

/// <summary>
/// The page-zero cells the generated code and its runtime work in. They are <c>$02</c>-<c>$2F</c>,
/// which BASIC owns while it runs: start-up copies them aside and the exit path puts them back, so
/// returning to the <c>READY.</c> prompt finds BASIC's pointers where it left them. The KERNAL's
/// screen editor and interrupt handler work above <c>$90</c> and are not disturbed.
/// </summary>
public static class Mos6502ZeroPage {
  public const int First = 0x02;
  public const int Last = 0x2F;

  /// <summary>The pointer <c>(Ptr),Y</c> loads and stores go through.</summary>
  public static readonly M6502Address Ptr = M6502Address.Absolute(0x02);

  /// <summary>A second pointer, for copies.</summary>
  public static readonly M6502Address Ptr2 = M6502Address.Absolute(0x04);

  /// <summary>
  /// Runtime arguments: a routine's operands are laid out here one after another, each at its own
  /// width. The first eight bytes are also the left operand, and the quotient, of arithmetic.
  /// </summary>
  public static readonly M6502Address Arg = M6502Address.Absolute(0x06);

  /// <summary>The right operand of arithmetic: <see cref="Arg"/> + 8.</summary>
  public static readonly M6502Address ArgB = M6502Address.Absolute(0x0E);

  /// <summary>Where a function or runtime routine leaves its result.</summary>
  public static readonly M6502Address Ret = M6502Address.Absolute(0x16);

  /// <summary>The soft stack pointer: recursion saves frames below it.</summary>
  public static readonly M6502Address SoftStack = M6502Address.Absolute(0x1E);

  /// <summary>Runtime scratch; also where division leaves its remainder.</summary>
  public static readonly M6502Address Temp = M6502Address.Absolute(0x20);

  /// <summary>Four more bytes of runtime scratch.</summary>
  public static readonly M6502Address Temp2 = M6502Address.Absolute(0x28);

  /// <summary>The output column <c>PRINT</c>'s zones and <c>TAB</c> count from, kept by the character writer.</summary>
  public static readonly M6502Address Column = M6502Address.Absolute(0x2C);

  /// <summary>Signs a signed division remembers across the unsigned one.</summary>
  public static readonly M6502Address QuotientSign = M6502Address.Absolute(0x2E);
  public static readonly M6502Address RemainderSign = M6502Address.Absolute(0x2F);

  /// <summary>How many bytes <see cref="Arg"/> and <see cref="ArgB"/> hold together.</summary>
  public const int ArgumentBytes = 16;
}
