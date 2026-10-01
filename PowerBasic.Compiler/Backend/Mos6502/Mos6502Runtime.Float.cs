using System.Numerics;
using static PowerBasic.Compiler.Backend.Mos6502.M6502Op;
using Zp = PowerBasic.Compiler.Backend.Mos6502.Mos6502ZeroPage;

namespace PowerBasic.Compiler.Backend.Mos6502;

/// <summary>The IEEE formats a float is stored in.</summary>
public enum M6502FloatFormat { Single, Double, Extended }

/// <summary>
/// The floating-point half of the runtime. Every float is stored in its own IEEE format - four,
/// eight or ten bytes - and worked on unpacked, in the page-zero accumulators <see cref="Zp.FloatA"/>
/// and <see cref="Zp.FloatB"/>: a sign byte, a 16-bit exponent with the x87's bias, and a 72-bit
/// mantissa whose low byte is guard bits with a sticky bit at the bottom. Arithmetic is exact up to
/// that sticky bit, so rounding happens once, to nearest-even, when the result is packed into the
/// format its IR type names - which is what makes a SINGLE sum the correctly rounded SINGLE sum.
///
/// <para>
/// Zeros are exponent zero; a denormal operand reads as zero and a result too small for its format
/// is written as zero. There are no infinities or NaNs: a result too large for its format is BASIC's
/// error 6, overflow, and a zero divisor its error 11.
/// </para>
/// </summary>
public sealed partial class Mos6502Runtime {

  /// <summary>The bias of the unpacked exponent: the x87's.</summary>
  public const int ExponentBias = 16383;

  private static M6502Address Sign(M6502Address accumulator) => accumulator.Plus(Zp.FloatSign);
  private static M6502Address Exponent(M6502Address accumulator, int k = 0) => accumulator.Plus(Zp.FloatExponent + k);
  private static M6502Address Mantissa(M6502Address accumulator, int k) => accumulator.Plus(Zp.FloatMantissa + k);

  /// <summary>The routine that unpacks a <paramref name="format"/> value at <c>(Ptr)</c> into accumulator A or B.</summary>
  public static M6502Routine Unpack(M6502FloatFormat format, bool intoB) => (format, intoB) switch {
    (M6502FloatFormat.Single, false) => M6502Routine.UnpackSingleA,
    (M6502FloatFormat.Double, false) => M6502Routine.UnpackDoubleA,
    (M6502FloatFormat.Extended, false) => M6502Routine.UnpackExtendedA,
    (M6502FloatFormat.Single, true) => M6502Routine.UnpackSingleB,
    (M6502FloatFormat.Double, true) => M6502Routine.UnpackDoubleB,
    _ => M6502Routine.UnpackExtendedB,
  };

  /// <summary>The routine that rounds accumulator A into a <paramref name="format"/> value at <c>(Ptr)</c>.</summary>
  public static M6502Routine Pack(M6502FloatFormat format) => format switch {
    M6502FloatFormat.Single => M6502Routine.PackSingle,
    M6502FloatFormat.Double => M6502Routine.PackDouble,
    _ => M6502Routine.PackExtended,
  };

  private bool EmitFloat(M6502Routine routine) {
    switch (routine) {
      case M6502Routine.UnpackSingleA: this.EmitUnpackSingle(Zp.FloatA); return true;
      case M6502Routine.UnpackSingleB: this.EmitUnpackSingle(Zp.FloatB); return true;
      case M6502Routine.UnpackDoubleA: this.EmitUnpackDouble(Zp.FloatA); return true;
      case M6502Routine.UnpackDoubleB: this.EmitUnpackDouble(Zp.FloatB); return true;
      case M6502Routine.UnpackExtendedA: this.EmitUnpackExtended(Zp.FloatA); return true;
      case M6502Routine.UnpackExtendedB: this.EmitUnpackExtended(Zp.FloatB); return true;
      case M6502Routine.PackSingle: this.EmitPackSingle(); return true;
      case M6502Routine.PackDouble: this.EmitPackDouble(); return true;
      case M6502Routine.PackExtended: this.EmitPackExtended(); return true;
      case M6502Routine.NormalizeA: this.EmitNormalize(Zp.FloatA); return true;
      case M6502Routine.FloatAdd: this.EmitFloatAdd(); return true;
      case M6502Routine.FloatSubtract:
        asm.Memory(Lda, Sign(Zp.FloatB));
        asm.Immediate(Eor, 0x80);
        asm.Memory(Sta, Sign(Zp.FloatB));
        asm.Jump(this.Routine(M6502Routine.FloatAdd));
        return true;
      case M6502Routine.FloatMultiply: this.EmitFloatMultiply(); return true;
      case M6502Routine.CopyBToA: this.EmitCopy(Zp.FloatB, Zp.FloatA); return true;
      case M6502Routine.SwapFloats: this.EmitSwap(); return true;
      case M6502Routine.ShiftRightStickyB: this.EmitShiftRightSticky(Zp.FloatB); return true;
      case M6502Routine.FloatDivide: this.EmitFloatDivide(); return true;
      case M6502Routine.FloatCompare: this.EmitFloatCompare(); return true;
      case M6502Routine.FloatFromSigned: this.EmitFloatFromInteger(signed: true); return true;
      case M6502Routine.FloatFromUnsigned: this.EmitFloatFromInteger(signed: false); return true;
      case M6502Routine.FloatToSignedTruncate: this.EmitFloatToInteger(signed: true, round: false); return true;
      case M6502Routine.FloatToSignedRound: this.EmitFloatToInteger(signed: true, round: true); return true;
      case M6502Routine.FloatToUnsignedTruncate: this.EmitFloatToInteger(signed: false, round: false); return true;
      case M6502Routine.FloatToUnsignedRound: this.EmitFloatToInteger(signed: false, round: true); return true;
      default: return false;
    }
  }

  // --- unpacking --------------------------------------------------------------------------------

  private void ClearMantissa(M6502Address accumulator, int from, int to) {
    asm.Immediate(Lda, 0);
    for (var k = from; k < to; ++k)
      asm.Memory(Sta, Mantissa(accumulator, k));
  }

  private void StoreExponent(M6502Address accumulator, int lowFromTemp, int bias) {
    // exponent (in Temp+lowFromTemp, two bytes) + bias -> the accumulator's exponent
    asm.Emit(Clc);
    asm.Memory(Lda, Zp.Temp.Plus(lowFromTemp));
    asm.Immediate(Adc, bias & 0xFF);
    asm.Memory(Sta, Exponent(accumulator));
    asm.Memory(Lda, Zp.Temp.Plus(lowFromTemp + 1));
    asm.Immediate(Adc, (bias >> 8) & 0xFF);
    asm.Memory(Sta, Exponent(accumulator, 1));
  }

  private void SetZero(M6502Address accumulator) {
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, Sign(accumulator));
    asm.Memory(Sta, Exponent(accumulator));
    asm.Memory(Sta, Exponent(accumulator, 1));
    for (var k = 0; k < Zp.FloatMantissaBytes; ++k)
      asm.Memory(Sta, Mantissa(accumulator, k));
  }

  private void EmitUnpackSingle(M6502Address accumulator) {
    // s eeeeeeee fffffff ffffffff ffffffff; the implicit one made explicit at mantissa bit 71
    var zero = asm.NewLabel("rt.unpackSingle.zero");
    for (var y = 3; y >= 0; --y) {
      asm.Immediate(Ldy, y);
      asm.IndirectY(Lda, Zp.Ptr);
      asm.Memory(Sta, Zp.Temp.Plus(y));
    }
    asm.Memory(Lda, Zp.Temp.Plus(3));
    asm.Immediate(M6502Op.And, 0x80);
    asm.Memory(Sta, Sign(accumulator));
    asm.Memory(Lda, Zp.Temp.Plus(2));
    asm.EmitAccumulator(Asl);
    asm.Memory(Lda, Zp.Temp.Plus(3));
    asm.EmitAccumulator(Rol);
    asm.Branch(Beq, zero);
    asm.Memory(Sta, Zp.Temp.Plus(4));
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, Zp.Temp.Plus(5));
    this.StoreExponent(accumulator, 4, ExponentBias - 127);
    asm.Memory(Lda, Zp.Temp.Plus(2));
    asm.Immediate(Ora, 0x80);
    asm.Memory(Sta, Mantissa(accumulator, 8));
    asm.Memory(Lda, Zp.Temp.Plus(1));
    asm.Memory(Sta, Mantissa(accumulator, 7));
    asm.Memory(Lda, Zp.Temp);
    asm.Memory(Sta, Mantissa(accumulator, 6));
    this.ClearMantissa(accumulator, 0, 6);
    asm.Emit(Rts);
    asm.Bind(zero);
    this.SetZero(accumulator);
    asm.Emit(Rts);
  }

  private void EmitUnpackDouble(M6502Address accumulator) {
    // s eeeeeeeeeee f(52): the eight bytes become the mantissa's top eight, the exponent is cut out
    // and the implicit one set, and the 53 bits are shifted up to the top: one byte and three bits
    var zero = asm.NewLabel("rt.unpackDouble.zero");
    for (var y = 7; y >= 0; --y) {
      asm.Immediate(Ldy, y);
      asm.IndirectY(Lda, Zp.Ptr);
      asm.Memory(Sta, Mantissa(accumulator, y + 1));
    }
    asm.Memory(Lda, Mantissa(accumulator, 8));
    asm.Immediate(M6502Op.And, 0x80);
    asm.Memory(Sta, Sign(accumulator));
    // the exponent: low byte ((b7 & $0F) << 4) | (b6 >> 4), high byte (b7 & $70) >> 4
    asm.Memory(Lda, Mantissa(accumulator, 7));
    for (var i = 0; i < 4; ++i)
      asm.EmitAccumulator(Lsr);
    asm.Memory(Sta, Zp.Temp.Plus(4));
    asm.Memory(Lda, Mantissa(accumulator, 8));
    for (var i = 0; i < 4; ++i)
      asm.EmitAccumulator(Asl);
    asm.Memory(Ora, Zp.Temp.Plus(4));
    asm.Memory(Sta, Zp.Temp.Plus(4));
    asm.Memory(Lda, Mantissa(accumulator, 8));
    asm.Immediate(M6502Op.And, 0x70);
    for (var i = 0; i < 4; ++i)
      asm.EmitAccumulator(Lsr);
    asm.Memory(Sta, Zp.Temp.Plus(5));
    asm.Memory(Ora, Zp.Temp.Plus(4));
    asm.Branch(Beq, zero);
    this.StoreExponent(accumulator, 4, ExponentBias - 1023);
    asm.Memory(Lda, Mantissa(accumulator, 7));
    asm.Immediate(M6502Op.And, 0x0F);
    asm.Immediate(Ora, 0x10);
    asm.Memory(Sta, Mantissa(accumulator, 7));
    // up one byte...
    for (var k = 8; k >= 1; --k) {
      asm.Memory(Lda, Mantissa(accumulator, k - 1));
      asm.Memory(Sta, Mantissa(accumulator, k));
    }
    // ...(the old M0 was never written; the byte now at M1 is zero either way)
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, Mantissa(accumulator, 0));
    asm.Memory(Sta, Mantissa(accumulator, 1));
    // ...and three bits
    for (var bit = 0; bit < 3; ++bit)
      this.ShiftLeft(Mantissa(accumulator, 0), Zp.FloatMantissaBytes);
    asm.Emit(Rts);
    asm.Bind(zero);
    this.SetZero(accumulator);
    asm.Emit(Rts);
  }

  private void EmitUnpackExtended(M6502Address accumulator) {
    // the x87's own layout: an explicit 64-bit significand, then sign and a 15-bit exponent
    var zero = asm.NewLabel("rt.unpackExtended.zero");
    for (var y = 0; y < 8; ++y) {
      asm.Immediate(Ldy, y);
      asm.IndirectY(Lda, Zp.Ptr);
      asm.Memory(Sta, Mantissa(accumulator, y + 1));
    }
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, Mantissa(accumulator, 0));
    asm.Immediate(Ldy, 8);
    asm.IndirectY(Lda, Zp.Ptr);
    asm.Memory(Sta, Exponent(accumulator));
    asm.Emit(Iny);
    asm.IndirectY(Lda, Zp.Ptr);
    asm.Emit(Tax);
    asm.Immediate(M6502Op.And, 0x80);
    asm.Memory(Sta, Sign(accumulator));
    asm.Emit(Txa);
    asm.Immediate(M6502Op.And, 0x7F);
    asm.Memory(Sta, Exponent(accumulator, 1));
    asm.Memory(Ora, Exponent(accumulator));
    asm.Branch(Beq, zero);
    // an unnormal significand is made normal; a zero one is zero
    if (accumulator == Zp.FloatA) {
      asm.Jump(this.Routine(M6502Routine.NormalizeA));
    } else {
      this.EmitNormalizeBody(accumulator, "rt.unpackExtendedB");
      asm.Emit(Rts);
    }
    asm.Bind(zero);
    this.SetZero(accumulator);
    asm.Emit(Rts);
  }

  // --- packing ----------------------------------------------------------------------------------

  /// <summary>
  /// Rounds accumulator A to nearest-even at the bit <paramref name="unitBit"/> of mantissa byte
  /// <paramref name="unitByte"/> - the unit in the last place of the format - carrying into the
  /// exponent when the mantissa rolls over.
  /// </summary>
  private void RoundA(int unitByte, int unitBit, string name) {
    var down = asm.NewLabel($"rt.{name}.down");
    var up = asm.NewLabel($"rt.{name}.up");
    var half = 1 << unitBit >> 1;
    // the bits below the unit: exactly half, more, or less
    if (unitBit == 0) {
      // the whole byte below is the fraction
      asm.Memory(Lda, Mantissa(Zp.FloatA, unitByte - 1));
      asm.Immediate(Cmp, 0x80);
      asm.Branch(Bcc, down);
      asm.Branch(Bne, up);
      for (var k = unitByte - 2; k >= 0; --k) {
        asm.Memory(Lda, Mantissa(Zp.FloatA, k));
        asm.Branch(Bne, up);
      }
    } else {
      asm.Memory(Lda, Mantissa(Zp.FloatA, unitByte));
      asm.Immediate(M6502Op.And, half);
      asm.Branch(Beq, down);
      asm.Memory(Lda, Mantissa(Zp.FloatA, unitByte));
      asm.Immediate(M6502Op.And, half - 1);
      for (var k = unitByte - 1; k >= 0; --k)
        asm.Memory(Ora, Mantissa(Zp.FloatA, k));
      asm.Branch(Bne, up);
    }
    // a tie goes to the even neighbour
    asm.Memory(Lda, Mantissa(Zp.FloatA, unitByte));
    asm.Immediate(M6502Op.And, 1 << unitBit);
    asm.Branch(Beq, down);
    asm.Bind(up);
    asm.Emit(Clc);
    asm.Memory(Lda, Mantissa(Zp.FloatA, unitByte));
    asm.Immediate(Adc, 1 << unitBit);
    asm.Memory(Sta, Mantissa(Zp.FloatA, unitByte));
    for (var k = unitByte + 1; k < Zp.FloatMantissaBytes; ++k) {
      asm.Memory(Lda, Mantissa(Zp.FloatA, k));
      asm.Immediate(Adc, 0);
      asm.Memory(Sta, Mantissa(Zp.FloatA, k));
    }
    asm.Branch(Bcc, down);
    // 1.111...1 rounded up is 10.000...0
    asm.Immediate(Lda, 0x80);
    asm.Memory(Sta, Mantissa(Zp.FloatA, 8));
    asm.Memory(Inc, Exponent(Zp.FloatA));
    asm.Branch(Bne, down);
    asm.Memory(Inc, Exponent(Zp.FloatA, 1));
    asm.Bind(down);
  }

  /// <summary>
  /// The format's exponent into Temp+4/5: the unpacked one minus the bias difference. A result at or
  /// below zero is too small for the format and jumps to <paramref name="underflow"/>; one at or past
  /// <paramref name="limit"/> is error 6.
  /// </summary>
  private void RebiasExponent(int biasDifference, int limit, M6502Label underflow, string name) {
    var fits = asm.NewLabel($"rt.{name}.fits");
    asm.Emit(Sec);
    asm.Memory(Lda, Exponent(Zp.FloatA));
    asm.Immediate(Sbc, biasDifference & 0xFF);
    asm.Memory(Sta, Zp.Temp2.Plus(4));
    asm.Memory(Lda, Exponent(Zp.FloatA, 1));
    asm.Immediate(Sbc, (biasDifference >> 8) & 0xFF);
    asm.Memory(Sta, Zp.Temp2.Plus(5));
    asm.Branch(Bmi, underflow);
    asm.Memory(Ora, Zp.Temp2.Plus(4));
    asm.Branch(Beq, underflow);
    // below the limit?
    asm.Memory(Lda, Zp.Temp2.Plus(4));
    asm.Immediate(Cmp, limit & 0xFF);
    asm.Memory(Lda, Zp.Temp2.Plus(5));
    asm.Immediate(Sbc, (limit >> 8) & 0xFF);
    asm.Branch(Bcc, fits);
    this.RaiseError(6);
    asm.Bind(fits);
  }

  private void WriteZeros(int bytes) {
    asm.Immediate(Lda, 0);
    for (var y = 0; y < bytes; ++y) {
      asm.Immediate(Ldy, y);
      asm.IndirectY(Sta, Zp.Ptr);
    }
    asm.Emit(Rts);
  }

  private void EmitPackSingle() {
    var zero = asm.NewLabel("rt.packSingle.zero");
    asm.Memory(Lda, Exponent(Zp.FloatA));
    asm.Memory(Ora, Exponent(Zp.FloatA, 1));
    asm.Branch(Beq, zero);
    this.RoundA(6, 0, "packSingle");
    this.RebiasExponent(ExponentBias - 127, 0xFF, zero, "packSingle");
    asm.Immediate(Ldy, 0);
    asm.Memory(Lda, Mantissa(Zp.FloatA, 6));
    asm.IndirectY(Sta, Zp.Ptr);
    asm.Emit(Iny);
    asm.Memory(Lda, Mantissa(Zp.FloatA, 7));
    asm.IndirectY(Sta, Zp.Ptr);
    asm.Emit(Iny);
    // e0 into bit 7 over the fraction's top seven bits (the implicit one dropped), then the sign over e7..e1
    asm.Memory(Lda, Mantissa(Zp.FloatA, 8));
    asm.Immediate(M6502Op.And, 0x7F);
    asm.Memory(Sta, Zp.Temp);
    asm.Memory(Lda, Zp.Temp2.Plus(4));
    asm.EmitAccumulator(Lsr);
    asm.Immediate(Lda, 0);
    asm.EmitAccumulator(Ror);
    asm.Memory(Ora, Zp.Temp);
    asm.IndirectY(Sta, Zp.Ptr);
    asm.Emit(Iny);
    asm.Memory(Lda, Zp.Temp2.Plus(4));
    asm.EmitAccumulator(Lsr);
    asm.Memory(Ora, Sign(Zp.FloatA));
    asm.IndirectY(Sta, Zp.Ptr);
    asm.Emit(Rts);
    asm.Bind(zero);
    this.WriteZeros(4);
  }

  private void EmitPackDouble() {
    var zero = asm.NewLabel("rt.packDouble.zero");
    asm.Memory(Lda, Exponent(Zp.FloatA));
    asm.Memory(Ora, Exponent(Zp.FloatA, 1));
    asm.Branch(Beq, zero);
    this.RoundA(2, 3, "packDouble");
    this.RebiasExponent(ExponentBias - 1023, 0x7FF, zero, "packDouble");
    // the 53 bits are mantissa bits 71..19: bytes 2..8 shifted down three
    for (var k = 0; k < 7; ++k) {
      asm.Memory(Lda, Mantissa(Zp.FloatA, k + 2));
      asm.Memory(Sta, Zp.Temp.Plus(k));
    }
    for (var bit = 0; bit < 3; ++bit) {
      asm.Memory(Lsr, Zp.Temp.Plus(6));
      for (var k = 5; k >= 0; --k)
        asm.Memory(Ror, Zp.Temp.Plus(k));
    }
    // byte 6: the exponent's low nibble over the fraction's top four bits (the implicit one dropped)
    asm.Memory(Lda, Zp.Temp2.Plus(4));
    for (var i = 0; i < 4; ++i)
      asm.EmitAccumulator(Asl);
    asm.Memory(Sta, Zp.Temp.Plus(7));
    asm.Memory(Lda, Zp.Temp.Plus(6));
    asm.Immediate(M6502Op.And, 0x0F);
    asm.Memory(Ora, Zp.Temp.Plus(7));
    asm.Memory(Sta, Zp.Temp.Plus(6));
    // byte 7: the sign over the exponent's top seven bits
    asm.Memory(Lda, Zp.Temp2.Plus(4));
    for (var i = 0; i < 4; ++i)
      asm.EmitAccumulator(Lsr);
    asm.Memory(Sta, Zp.Temp.Plus(7));
    asm.Memory(Lda, Zp.Temp2.Plus(5));
    for (var i = 0; i < 4; ++i)
      asm.EmitAccumulator(Asl);
    asm.Memory(Ora, Zp.Temp.Plus(7));
    asm.Memory(Ora, Sign(Zp.FloatA));
    asm.Memory(Sta, Zp.Temp.Plus(7));
    for (var y = 0; y < 8; ++y) {
      asm.Immediate(Ldy, y);
      asm.Memory(Lda, Zp.Temp.Plus(y));
      asm.IndirectY(Sta, Zp.Ptr);
    }
    asm.Emit(Rts);
    asm.Bind(zero);
    this.WriteZeros(8);
  }

  private void EmitPackExtended() {
    var zero = asm.NewLabel("rt.packExtended.zero");
    var fits = asm.NewLabel("rt.packExtended.fits");
    asm.Memory(Lda, Exponent(Zp.FloatA));
    asm.Memory(Ora, Exponent(Zp.FloatA, 1));
    asm.Branch(Beq, zero);
    this.RoundA(1, 0, "packExtended");
    asm.Memory(Lda, Exponent(Zp.FloatA, 1));
    asm.Immediate(Cmp, 0x7F);
    asm.Branch(Bcc, fits);
    asm.Memory(Lda, Exponent(Zp.FloatA));
    asm.Immediate(Cmp, 0xFF);
    asm.Branch(Bcc, fits);
    this.RaiseError(6);
    asm.Bind(fits);
    for (var y = 0; y < 8; ++y) {
      asm.Immediate(Ldy, y);
      asm.Memory(Lda, Mantissa(Zp.FloatA, y + 1));
      asm.IndirectY(Sta, Zp.Ptr);
    }
    asm.Immediate(Ldy, 8);
    asm.Memory(Lda, Exponent(Zp.FloatA));
    asm.IndirectY(Sta, Zp.Ptr);
    asm.Emit(Iny);
    asm.Memory(Lda, Exponent(Zp.FloatA, 1));
    asm.Memory(Ora, Sign(Zp.FloatA));
    asm.IndirectY(Sta, Zp.Ptr);
    asm.Emit(Rts);
    asm.Bind(zero);
    this.WriteZeros(10);
  }

  // --- normalizing and shifting -----------------------------------------------------------------

  private void EmitNormalize(M6502Address accumulator) {
    this.EmitNormalizeBody(accumulator, "rt.normalize");
    asm.Emit(Rts);
  }

  /// <summary>Shifts the mantissa up until its top bit is set, taking from the exponent; a zero mantissa is zero.</summary>
  private void EmitNormalizeBody(M6502Address accumulator, string name) {
    var bytes = asm.NewLabel($"{name}.bytes");
    var bits = asm.NewLabel($"{name}.bits");
    var done = asm.NewLabel($"{name}.done");
    var notZero = asm.NewLabel($"{name}.notZero");
    asm.Memory(Lda, Mantissa(accumulator, 0));
    for (var k = 1; k < Zp.FloatMantissaBytes; ++k)
      asm.Memory(Ora, Mantissa(accumulator, k));
    asm.Branch(Bne, notZero);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, Exponent(accumulator));
    asm.Memory(Sta, Exponent(accumulator, 1));
    asm.Memory(Sta, Sign(accumulator));
    asm.Jump(done);
    asm.Bind(notZero);
    // whole bytes while the top one is empty
    asm.Bind(bytes);
    asm.Memory(Lda, Mantissa(accumulator, 8));
    asm.Branch(Bne, bits);
    for (var k = 8; k >= 1; --k) {
      asm.Memory(Lda, Mantissa(accumulator, k - 1));
      asm.Memory(Sta, Mantissa(accumulator, k));
    }
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, Mantissa(accumulator, 0));
    asm.Emit(Sec);
    asm.Memory(Lda, Exponent(accumulator));
    asm.Immediate(Sbc, 8);
    asm.Memory(Sta, Exponent(accumulator));
    asm.Branch(Bcs, bytes);
    asm.Memory(Dec, Exponent(accumulator, 1));
    asm.Jump(bytes);
    // then single bits
    asm.Bind(bits);
    asm.Branch(Bmi, done);
    this.ShiftLeft(Mantissa(accumulator, 0), Zp.FloatMantissaBytes);
    this.DecrementWord(Exponent(accumulator), $"{name}.exponent");
    asm.Memory(Lda, Mantissa(accumulator, 8));
    asm.Jump(bits);
    asm.Bind(done);
  }

  private void DecrementWord(M6502Address word, string name) {
    var noBorrow = asm.NewLabel($"{name}.noBorrow");
    asm.Memory(Lda, word);
    asm.Branch(Bne, noBorrow);
    asm.Memory(Dec, word.Plus(1));
    asm.Bind(noBorrow);
    asm.Memory(Dec, word);
  }

  private void EmitCopy(M6502Address from, M6502Address to) {
    var loop = asm.NewLabel("rt.copyFloat");
    asm.Immediate(Ldx, Zp.FloatBytes - 1);
    asm.Bind(loop);
    asm.Memory(Lda, from, M6502Index.X);
    asm.Memory(Sta, to, M6502Index.X);
    asm.Emit(Dex);
    asm.Branch(Bpl, loop);
    asm.Emit(Rts);
  }

  private void EmitSwap() {
    var loop = asm.NewLabel("rt.swapFloats");
    asm.Immediate(Ldx, Zp.FloatBytes - 1);
    asm.Bind(loop);
    asm.Memory(Lda, Zp.FloatA, M6502Index.X);
    asm.Memory(Ldy, Zp.FloatB, M6502Index.X);
    asm.Memory(Sta, Zp.FloatB, M6502Index.X);
    asm.Memory(Sty, Zp.FloatA, M6502Index.X);
    asm.Emit(Dex);
    asm.Branch(Bpl, loop);
    asm.Emit(Rts);
  }

  /// <summary>Shifts a mantissa right by X bits (1..71), OR-ing every bit shifted out into bit 0.</summary>
  private void EmitShiftRightSticky(M6502Address accumulator) {
    var bytes = asm.NewLabel("rt.shiftSticky.bytes");
    var bits = asm.NewLabel("rt.shiftSticky.bits");
    var bit = asm.NewLabel("rt.shiftSticky.bit");
    var done = asm.NewLabel("rt.shiftSticky.done");
    var noByteSticky = asm.NewLabel("rt.shiftSticky.noByteSticky");
    var noBitSticky = asm.NewLabel("rt.shiftSticky.noBitSticky");
    asm.Bind(bytes);
    asm.Immediate(Cpx, 8);
    asm.Branch(Bcc, bits);
    asm.Memory(Lda, Mantissa(accumulator, 0));
    asm.Memory(Sta, Zp.Temp2);
    for (var k = 0; k < 8; ++k) {
      asm.Memory(Lda, Mantissa(accumulator, k + 1));
      asm.Memory(Sta, Mantissa(accumulator, k));
    }
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, Mantissa(accumulator, 8));
    asm.Memory(Lda, Zp.Temp2);
    asm.Branch(Beq, noByteSticky);
    this.SetSticky(accumulator);
    asm.Bind(noByteSticky);
    asm.Emit(Txa);
    asm.Emit(Sec);
    asm.Immediate(Sbc, 8);
    asm.Emit(Tax);
    asm.Jump(bytes);
    asm.Bind(bits);
    asm.Immediate(Cpx, 0);
    asm.Branch(Beq, done);
    asm.Bind(bit);
    asm.Memory(Lsr, Mantissa(accumulator, 8));
    for (var k = 7; k >= 0; --k)
      asm.Memory(Ror, Mantissa(accumulator, k));
    asm.Branch(Bcc, noBitSticky);
    this.SetSticky(accumulator);
    asm.Bind(noBitSticky);
    asm.Emit(Dex);
    asm.Branch(Bne, bit);
    asm.Bind(done);
    asm.Emit(Rts);
  }

  private void SetSticky(M6502Address accumulator) {
    asm.Memory(Lda, Mantissa(accumulator, 0));
    asm.Immediate(Ora, 1);
    asm.Memory(Sta, Mantissa(accumulator, 0));
  }

  /// <summary>Branches to <paramref name="zero"/> when the accumulator is zero (exponent zero).</summary>
  private void IfZero(M6502Address accumulator, M6502Label zero) {
    asm.Memory(Lda, Exponent(accumulator));
    asm.Memory(Ora, Exponent(accumulator, 1));
    asm.Branch(Beq, zero);
  }

  /// <summary>
  /// A = A + B. The smaller operand's mantissa is shifted down to the larger's exponent, keeping a
  /// sticky bit; equal signs add (a carry out shifts back up one), different signs subtract (a borrow
  /// flips the sign), and the result is normalized. Exact up to the sticky bit, so it rounds once.
  /// </summary>
  private void EmitFloatAdd() {
    var bNotZero = asm.NewLabel("rt.floatAdd.bNotZero");
    var aNotZero = asm.NewLabel("rt.floatAdd.aNotZero");
    var ordered = asm.NewLabel("rt.floatAdd.ordered");
    var tiny = asm.NewLabel("rt.floatAdd.tiny");
    var shift = asm.NewLabel("rt.floatAdd.shift");
    var aligned = asm.NewLabel("rt.floatAdd.aligned");
    var subtract = asm.NewLabel("rt.floatAdd.subtract");
    var done = asm.NewLabel("rt.floatAdd.done");
    var noSticky = asm.NewLabel("rt.floatAdd.noSticky");
    var positive = asm.NewLabel("rt.floatAdd.positive");
    asm.Memory(Lda, Exponent(Zp.FloatB));
    asm.Memory(Ora, Exponent(Zp.FloatB, 1));
    asm.Branch(Bne, bNotZero);
    asm.Emit(Rts);
    asm.Bind(bNotZero);
    asm.Memory(Lda, Exponent(Zp.FloatA));
    asm.Memory(Ora, Exponent(Zp.FloatA, 1));
    asm.Branch(Bne, aNotZero);
    asm.Jump(this.Routine(M6502Routine.CopyBToA));
    asm.Bind(aNotZero);
    // A must be the larger in exponent: the difference into Temp, swapping if it came out negative
    this.ExponentDifference();
    asm.Branch(Bpl, ordered);
    asm.Call(this.Routine(M6502Routine.SwapFloats));
    this.ExponentDifference();
    asm.Bind(ordered);
    asm.Memory(Lda, Zp.Temp.Plus(1));
    asm.Branch(Bne, tiny);
    asm.Memory(Lda, Zp.Temp);
    asm.Immediate(Cmp, 72);
    asm.Branch(Bcc, shift);
    // B is below A's last guard bit: it only makes the result inexact
    asm.Bind(tiny);
    this.ClearMantissa(Zp.FloatB, 1, Zp.FloatMantissaBytes);
    asm.Immediate(Lda, 1);
    asm.Memory(Sta, Mantissa(Zp.FloatB, 0));
    asm.Jump(aligned);
    asm.Bind(shift);
    asm.Memory(Ldx, Zp.Temp);
    asm.Branch(Beq, aligned);
    asm.Call(this.Routine(M6502Routine.ShiftRightStickyB));
    asm.Bind(aligned);
    asm.Memory(Lda, Sign(Zp.FloatA));
    asm.Memory(Eor, Sign(Zp.FloatB));
    asm.Branch(Bmi, subtract);
    asm.Emit(Clc);
    for (var k = 0; k < Zp.FloatMantissaBytes; ++k) {
      asm.Memory(Lda, Mantissa(Zp.FloatA, k));
      asm.Memory(Adc, Mantissa(Zp.FloatB, k));
      asm.Memory(Sta, Mantissa(Zp.FloatA, k));
    }
    asm.Branch(Bcc, done);
    // the carry becomes the new top bit
    for (var k = 8; k >= 0; --k)
      asm.Memory(Ror, Mantissa(Zp.FloatA, k));
    asm.Branch(Bcc, noSticky);
    this.SetSticky(Zp.FloatA);
    asm.Bind(noSticky);
    asm.Memory(Inc, Exponent(Zp.FloatA));
    asm.Branch(Bne, done);
    asm.Memory(Inc, Exponent(Zp.FloatA, 1));
    asm.Bind(done);
    asm.Emit(Rts);
    asm.Bind(subtract);
    asm.Emit(Sec);
    for (var k = 0; k < Zp.FloatMantissaBytes; ++k) {
      asm.Memory(Lda, Mantissa(Zp.FloatA, k));
      asm.Memory(Sbc, Mantissa(Zp.FloatB, k));
      asm.Memory(Sta, Mantissa(Zp.FloatA, k));
    }
    asm.Branch(Bcs, positive);
    asm.Emit(Sec);
    for (var k = 0; k < Zp.FloatMantissaBytes; ++k) {
      asm.Immediate(Lda, 0);
      asm.Memory(Sbc, Mantissa(Zp.FloatA, k));
      asm.Memory(Sta, Mantissa(Zp.FloatA, k));
    }
    asm.Memory(Lda, Sign(Zp.FloatA));
    asm.Immediate(Eor, 0x80);
    asm.Memory(Sta, Sign(Zp.FloatA));
    asm.Bind(positive);
    asm.Jump(this.Routine(M6502Routine.NormalizeA));
  }

  /// <summary>E(A) - E(B) into Temp and Temp+1, the flags of its high byte left for a sign test.</summary>
  private void ExponentDifference() {
    asm.Emit(Sec);
    asm.Memory(Lda, Exponent(Zp.FloatA));
    asm.Memory(Sbc, Exponent(Zp.FloatB));
    asm.Memory(Sta, Zp.Temp);
    asm.Memory(Lda, Exponent(Zp.FloatA, 1));
    asm.Memory(Sbc, Exponent(Zp.FloatB, 1));
    asm.Memory(Sta, Zp.Temp.Plus(1));
  }

  /// <summary>
  /// A = A * B: the 64-bit mantissas multiplied into a 128-bit product (shift and add, the product
  /// shifted right as the multiplier's bits are used up), whose top 72 bits are the result and the
  /// rest its sticky bit.
  /// </summary>
  private void EmitFloatMultiply() {
    var zero = asm.NewLabel("rt.floatMultiply.zero");
    var loop = asm.NewLabel("rt.floatMultiply.loop");
    var noAdd = asm.NewLabel("rt.floatMultiply.noAdd");
    var shifted = asm.NewLabel("rt.floatMultiply.shifted");
    var top = asm.NewLabel("rt.floatMultiply.top");
    var take = asm.NewLabel("rt.floatMultiply.take");
    var noSticky = asm.NewLabel("rt.floatMultiply.noSticky");
    this.IfZero(Zp.FloatA, zero);
    this.IfZero(Zp.FloatB, zero);
    asm.Memory(Lda, Sign(Zp.FloatA));
    asm.Memory(Eor, Sign(Zp.FloatB));
    asm.Memory(Sta, Sign(Zp.FloatA));
    // E = E(A) + E(B) - bias; one more if the product fills its top bit
    asm.Emit(Clc);
    asm.Memory(Lda, Exponent(Zp.FloatA));
    asm.Memory(Adc, Exponent(Zp.FloatB));
    asm.Memory(Sta, Zp.Temp);
    asm.Memory(Lda, Exponent(Zp.FloatA, 1));
    asm.Memory(Adc, Exponent(Zp.FloatB, 1));
    asm.Memory(Sta, Zp.Temp.Plus(1));
    asm.Emit(Sec);
    asm.Memory(Lda, Zp.Temp);
    asm.Immediate(Sbc, ExponentBias & 0xFF);
    asm.Memory(Sta, Exponent(Zp.FloatA));
    asm.Memory(Lda, Zp.Temp.Plus(1));
    asm.Immediate(Sbc, ExponentBias >> 8);
    asm.Memory(Sta, Exponent(Zp.FloatA, 1));
    asm.Immediate(Lda, 0);
    for (var k = 0; k < 16; ++k)
      asm.Memory(Sta, Zp.FloatWork.Plus(k));
    asm.Immediate(Ldy, 64);
    asm.Bind(loop);
    asm.Memory(Lsr, Mantissa(Zp.FloatA, 8));
    for (var k = 7; k >= 1; --k)
      asm.Memory(Ror, Mantissa(Zp.FloatA, k));
    asm.Branch(Bcc, noAdd);
    asm.Emit(Clc);
    for (var k = 0; k < 8; ++k) {
      asm.Memory(Lda, Zp.FloatWork.Plus(8 + k));
      asm.Memory(Adc, Mantissa(Zp.FloatB, 1 + k));
      asm.Memory(Sta, Zp.FloatWork.Plus(8 + k));
    }
    asm.Jump(shifted);
    asm.Bind(noAdd);
    asm.Emit(Clc);
    asm.Bind(shifted);
    for (var k = 15; k >= 0; --k)
      asm.Memory(Ror, Zp.FloatWork.Plus(k));
    asm.Emit(Dey);
    asm.Branch(Bne, loop);
    asm.Memory(Lda, Zp.FloatWork.Plus(15));
    asm.Branch(Bmi, top);
    asm.Memory(Asl, Zp.FloatWork);
    for (var k = 1; k < 16; ++k)
      asm.Memory(Rol, Zp.FloatWork.Plus(k));
    asm.Jump(take);
    asm.Bind(top);
    asm.Memory(Inc, Exponent(Zp.FloatA));
    asm.Branch(Bne, take);
    asm.Memory(Inc, Exponent(Zp.FloatA, 1));
    asm.Bind(take);
    for (var k = 0; k < Zp.FloatMantissaBytes; ++k) {
      asm.Memory(Lda, Zp.FloatWork.Plus(7 + k));
      asm.Memory(Sta, Mantissa(Zp.FloatA, k));
    }
    asm.Memory(Lda, Zp.FloatWork);
    for (var k = 1; k < 7; ++k)
      asm.Memory(Ora, Zp.FloatWork.Plus(k));
    asm.Branch(Beq, noSticky);
    this.SetSticky(Zp.FloatA);
    asm.Bind(noSticky);
    asm.Emit(Rts);
    asm.Bind(zero);
    this.SetZero(Zp.FloatA);
    asm.Emit(Rts);
  }

  /// <summary>
  /// A = A / B by restoring division: the dividend's mantissa is the running remainder (in
  /// FloatWork, a ninth byte for the bit a shift carries out), 72 quotient bits are developed into
  /// A's mantissa, and a remainder left over is the sticky bit. A zero divisor is error 11.
  /// </summary>
  private void EmitFloatDivide() {
    var divisible = asm.NewLabel("rt.floatDivide.divisible");
    var zero = asm.NewLabel("rt.floatDivide.zero");
    var started = asm.NewLabel("rt.floatDivide.started");
    var loop = asm.NewLabel("rt.floatDivide.loop");
    var noSticky = asm.NewLabel("rt.floatDivide.noSticky");
    asm.Memory(Lda, Exponent(Zp.FloatB));
    asm.Memory(Ora, Exponent(Zp.FloatB, 1));
    asm.Branch(Bne, divisible);
    this.RaiseError(11);
    asm.Bind(divisible);
    this.IfZero(Zp.FloatA, zero);
    asm.Memory(Lda, Sign(Zp.FloatA));
    asm.Memory(Eor, Sign(Zp.FloatB));
    asm.Memory(Sta, Sign(Zp.FloatA));
    // E = E(A) - E(B) + bias
    asm.Emit(Sec);
    asm.Memory(Lda, Exponent(Zp.FloatA));
    asm.Memory(Sbc, Exponent(Zp.FloatB));
    asm.Memory(Sta, Zp.Temp);
    asm.Memory(Lda, Exponent(Zp.FloatA, 1));
    asm.Memory(Sbc, Exponent(Zp.FloatB, 1));
    asm.Memory(Sta, Zp.Temp.Plus(1));
    asm.Emit(Clc);
    asm.Memory(Lda, Zp.Temp);
    asm.Immediate(Adc, ExponentBias & 0xFF);
    asm.Memory(Sta, Exponent(Zp.FloatA));
    asm.Memory(Lda, Zp.Temp.Plus(1));
    asm.Immediate(Adc, ExponentBias >> 8);
    asm.Memory(Sta, Exponent(Zp.FloatA, 1));
    // the remainder starts as A's mantissa; the quotient builds in A
    for (var k = 0; k < 8; ++k) {
      asm.Memory(Lda, Mantissa(Zp.FloatA, k + 1));
      asm.Memory(Sta, Zp.FloatWork.Plus(k));
    }
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, Zp.FloatWork.Plus(8));
    this.ClearMantissa(Zp.FloatA, 0, Zp.FloatMantissaBytes);
    // a dividend mantissa below the divisor's: double it, and the exponent goes down one
    var less = asm.NewLabel("rt.floatDivide.less");
    this.CompareRemainder(less, started);
    asm.Bind(less);
    this.ShiftRemainder();
    this.DecrementWord(Exponent(Zp.FloatA), "rt.floatDivide.exponent");
    asm.Bind(started);
    asm.Immediate(Ldx, 72);
    asm.Bind(loop);
    asm.Memory(Asl, Mantissa(Zp.FloatA, 0));
    for (var k = 1; k < Zp.FloatMantissaBytes; ++k)
      asm.Memory(Rol, Mantissa(Zp.FloatA, k));
    var smaller = asm.NewLabel("rt.floatDivide.smaller");
    var subtract = asm.NewLabel("rt.floatDivide.subtract");
    this.CompareRemainder(smaller, subtract);
    asm.Bind(subtract);
    asm.Emit(Sec);
    for (var k = 0; k < 8; ++k) {
      asm.Memory(Lda, Zp.FloatWork.Plus(k));
      asm.Memory(Sbc, Mantissa(Zp.FloatB, k + 1));
      asm.Memory(Sta, Zp.FloatWork.Plus(k));
    }
    asm.Memory(Lda, Zp.FloatWork.Plus(8));
    asm.Immediate(Sbc, 0);
    asm.Memory(Sta, Zp.FloatWork.Plus(8));
    asm.Memory(Inc, Mantissa(Zp.FloatA, 0));
    asm.Bind(smaller);
    this.ShiftRemainder();
    asm.Emit(Dex);
    asm.Branch(Bne, loop);
    asm.Memory(Lda, Zp.FloatWork);
    for (var k = 1; k < 9; ++k)
      asm.Memory(Ora, Zp.FloatWork.Plus(k));
    asm.Branch(Beq, noSticky);
    this.SetSticky(Zp.FloatA);
    asm.Bind(noSticky);
    asm.Emit(Rts);
    asm.Bind(zero);
    this.SetZero(Zp.FloatA);
    asm.Emit(Rts);
  }

  /// <summary>The remainder against B's mantissa: to <paramref name="less"/> if smaller, else to <paramref name="atLeast"/>.</summary>
  private void CompareRemainder(M6502Label less, M6502Label atLeast) {
    asm.Memory(Lda, Zp.FloatWork.Plus(8));
    asm.Branch(Bne, atLeast);
    for (var k = 7; k >= 0; --k) {
      asm.Memory(Lda, Zp.FloatWork.Plus(k));
      asm.Memory(Cmp, Mantissa(Zp.FloatB, k + 1));
      asm.Branch(Bcc, less);
      asm.Branch(Bne, atLeast);
    }
    asm.Jump(atLeast);
  }

  private void ShiftRemainder() {
    asm.Memory(Asl, Zp.FloatWork);
    for (var k = 1; k < 9; ++k)
      asm.Memory(Rol, Zp.FloatWork.Plus(k));
  }

  /// <summary>Compares A with B into the accumulator: <c>$FF</c> for less, <c>0</c> for equal, <c>1</c> for greater.</summary>
  private void EmitFloatCompare() {
    var sameSign = asm.NewLabel("rt.floatCompare.sameSign");
    var aLess = asm.NewLabel("rt.floatCompare.aLess");
    var aGreater = asm.NewLabel("rt.floatCompare.aGreater");
    var magnitudeLess = asm.NewLabel("rt.floatCompare.magnitudeLess");
    var magnitudeGreater = asm.NewLabel("rt.floatCompare.magnitudeGreater");
    // zeros are unpacked with a clear sign, so the signs alone decide a mixed pair
    asm.Memory(Lda, Sign(Zp.FloatA));
    asm.Memory(Cmp, Sign(Zp.FloatB));
    asm.Branch(Beq, sameSign);
    asm.Memory(Lda, Sign(Zp.FloatA));
    asm.Branch(Bmi, aLess);
    asm.Jump(aGreater);
    asm.Bind(sameSign);
    var place = new List<M6502Address> { Exponent(Zp.FloatA, 1), Exponent(Zp.FloatA) };
    var other = new List<M6502Address> { Exponent(Zp.FloatB, 1), Exponent(Zp.FloatB) };
    for (var k = 8; k >= 0; --k) {
      place.Add(Mantissa(Zp.FloatA, k));
      other.Add(Mantissa(Zp.FloatB, k));
    }
    for (var i = 0; i < place.Count; ++i) {
      asm.Memory(Lda, place[i]);
      asm.Memory(Cmp, other[i]);
      asm.Branch(Bcc, magnitudeLess);
      asm.Branch(Bne, magnitudeGreater);
    }
    asm.Immediate(Lda, 0);
    asm.Emit(Rts);
    // a larger magnitude is the larger number when positive, the smaller when negative
    asm.Bind(magnitudeGreater);
    asm.Memory(Lda, Sign(Zp.FloatA));
    asm.Branch(Bmi, aLess);
    asm.Bind(aGreater);
    asm.Immediate(Lda, 1);
    asm.Emit(Rts);
    asm.Bind(magnitudeLess);
    asm.Memory(Lda, Sign(Zp.FloatA));
    asm.Branch(Bmi, aGreater);
    asm.Bind(aLess);
    asm.Immediate(Lda, 0xFF);
    asm.Emit(Rts);
  }

  /// <summary>The 64-bit integer in Arg into A: its magnitude as the mantissa, 2^63 as the exponent, normalized.</summary>
  private void EmitFloatFromInteger(bool signed) {
    var positive = asm.NewLabel($"rt.floatFrom{(signed ? "Signed" : "Unsigned")}.positive");
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, Sign(Zp.FloatA));
    if (signed) {
      asm.Memory(Lda, Zp.Arg.Plus(7));
      asm.Branch(Bpl, positive);
      asm.Immediate(Lda, 0x80);
      asm.Memory(Sta, Sign(Zp.FloatA));
      this.Negate(Zp.Arg, 8);
      asm.Bind(positive);
    }
    for (var k = 0; k < 8; ++k) {
      asm.Memory(Lda, Zp.Arg.Plus(k));
      asm.Memory(Sta, Mantissa(Zp.FloatA, k + 1));
    }
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, Mantissa(Zp.FloatA, 0));
    asm.Immediate(Lda, (ExponentBias + 63) & 0xFF);
    asm.Memory(Sta, Exponent(Zp.FloatA));
    asm.Immediate(Lda, (ExponentBias + 63) >> 8);
    asm.Memory(Sta, Exponent(Zp.FloatA, 1));
    asm.Jump(this.Routine(M6502Routine.NormalizeA));
  }

  /// <summary>
  /// A into a 64-bit integer in Ret, truncated or rounded to nearest-even, then checked against the
  /// target's X bytes: a value that does not fit is error 6. The integer part is shifted out of the
  /// mantissa's top into Ret, which leaves the first fraction bit at the mantissa's top for rounding.
  /// </summary>
  private void EmitFloatToInteger(bool signed, bool round) {
    var name = $"rt.floatTo{(signed ? "Signed" : "Unsigned")}{(round ? "Round" : "Truncate")}";
    var label = (string what) => asm.NewLabel($"{name}.{what}");
    var (zero, belowOne, whole, bytes, bits, bitLoop, rounded, magnitude, overflow, done) =
      (label("zero"), label("belowOne"), label("whole"), label("bytes"), label("bits"), label("bitLoop"),
       label("rounded"), label("magnitude"), label("overflow"), label("done"));
    asm.Memory(Stx, Zp.Temp2.Plus(3));
    asm.Immediate(Lda, 0);
    for (var k = 0; k < 8; ++k)
      asm.Memory(Sta, Zp.Ret.Plus(k));
    this.IfZero(Zp.FloatA, done);
    // e = E - bias: below zero the value is under one; 64 and up it cannot fit
    asm.Emit(Sec);
    asm.Memory(Lda, Exponent(Zp.FloatA));
    asm.Immediate(Sbc, ExponentBias & 0xFF);
    asm.Memory(Sta, Zp.Temp);
    asm.Memory(Lda, Exponent(Zp.FloatA, 1));
    asm.Immediate(Sbc, ExponentBias >> 8);
    asm.Memory(Sta, Zp.Temp.Plus(1));
    asm.Branch(Bmi, belowOne);
    asm.Branch(Bne, overflow);
    asm.Memory(Lda, Zp.Temp);
    asm.Immediate(Cmp, 64);
    asm.Branch(Bcs, overflow);
    // shift e + 1 bits into Ret: whole bytes first
    asm.Emit(Tax);
    asm.Emit(Inx);
    asm.Bind(bytes);
    asm.Immediate(Cpx, 8);
    asm.Branch(Bcc, bits);
    for (var k = 7; k >= 1; --k) {
      asm.Memory(Lda, Zp.Ret.Plus(k - 1));
      asm.Memory(Sta, Zp.Ret.Plus(k));
    }
    asm.Memory(Lda, Mantissa(Zp.FloatA, 8));
    asm.Memory(Sta, Zp.Ret);
    for (var k = 8; k >= 1; --k) {
      asm.Memory(Lda, Mantissa(Zp.FloatA, k - 1));
      asm.Memory(Sta, Mantissa(Zp.FloatA, k));
    }
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, Mantissa(Zp.FloatA, 0));
    asm.Emit(Txa);
    asm.Emit(Sec);
    asm.Immediate(Sbc, 8);
    asm.Emit(Tax);
    asm.Jump(bytes);
    asm.Bind(bits);
    asm.Immediate(Cpx, 0);
    asm.Branch(Beq, whole);
    asm.Bind(bitLoop);
    asm.Memory(Asl, Mantissa(Zp.FloatA, 0));
    for (var k = 1; k < Zp.FloatMantissaBytes; ++k)
      asm.Memory(Rol, Mantissa(Zp.FloatA, k));
    for (var k = 0; k < 8; ++k)
      asm.Memory(Rol, Zp.Ret.Plus(k));
    asm.Emit(Dex);
    asm.Branch(Bne, bitLoop);
    asm.Bind(whole);
    if (round) {
      // the first fraction bit is the mantissa's top; the rest are sticky; a tie goes to even
      asm.Memory(Lda, Mantissa(Zp.FloatA, 8));
      asm.Branch(Bpl, magnitude);
      asm.Immediate(M6502Op.And, 0x7F);
      for (var k = 0; k < 8; ++k)
        asm.Memory(Ora, Mantissa(Zp.FloatA, k));
      var up = label("up");
      asm.Branch(Bne, up);
      asm.Memory(Lda, Zp.Ret);
      asm.Immediate(M6502Op.And, 1);
      asm.Branch(Beq, magnitude);
      asm.Bind(up);
      for (var k = 0; k < 8; ++k) {
        asm.Memory(Inc, Zp.Ret.Plus(k));
        asm.Branch(Bne, magnitude);
      }
      asm.Jump(overflow);
    }
    asm.Jump(magnitude);
    // under one: truncation gives 0; rounding gives 1 above a half, 0 at or below it
    asm.Bind(belowOne);
    if (round) {
      // only e = -1, a value in [0.5, 1), can round up
      asm.Memory(Lda, Zp.Temp);
      asm.Memory(M6502Op.And, Zp.Temp.Plus(1));
      asm.Immediate(Cmp, 0xFF);
      asm.Branch(Bne, magnitude);
      asm.Memory(Lda, Mantissa(Zp.FloatA, 8));
      asm.Immediate(M6502Op.And, 0x7F);
      for (var k = 0; k < 8; ++k)
        asm.Memory(Ora, Mantissa(Zp.FloatA, k));
      asm.Branch(Beq, magnitude);
      asm.Immediate(Lda, 1);
      asm.Memory(Sta, Zp.Ret);
    }
    asm.Bind(magnitude);
    // the magnitude is in Ret: sign it, then check it fits the target
    var negate = label("negate");
    var check = label("check");
    if (signed) {
      asm.Memory(Lda, Zp.Ret.Plus(7));
      asm.Branch(Bpl, negate);
      // only -2^63 has its top bit set and still fits
      asm.Memory(Lda, Sign(Zp.FloatA));
      asm.Branch(Bpl, overflow);
      asm.Memory(Lda, Zp.Ret.Plus(7));
      asm.Immediate(Cmp, 0x80);
      asm.Branch(Bne, overflow);
      asm.Memory(Lda, Zp.Ret);
      for (var k = 1; k < 7; ++k)
        asm.Memory(Ora, Zp.Ret.Plus(k));
      asm.Branch(Bne, overflow);
      asm.Bind(negate);
      asm.Memory(Lda, Sign(Zp.FloatA));
      asm.Branch(Bpl, check);
      this.Negate(Zp.Ret, 8);
    } else {
      // a negative value converts to an unsigned type only if it rounded to zero
      asm.Memory(Lda, Sign(Zp.FloatA));
      asm.Branch(Bpl, check);
      asm.Memory(Lda, Zp.Ret);
      for (var k = 1; k < 8; ++k)
        asm.Memory(Ora, Zp.Ret.Plus(k));
      asm.Branch(Bne, overflow);
    }
    asm.Bind(check);
    // the bytes above the target's must be the sign extension (signed) or zero (unsigned) of its top
    var fill = label("fill");
    var compare = label("compare");
    asm.Memory(Ldx, Zp.Temp2.Plus(3));
    asm.Immediate(Cpx, 8);
    asm.Branch(Beq, done);
    asm.Immediate(Lda, 0);
    if (signed) {
      asm.Memory(Ldy, Zp.Ret.Plus(-1), M6502Index.X);
      asm.Branch(Bpl, fill);
      asm.Immediate(Lda, 0xFF);
    }
    asm.Bind(fill);
    asm.Bind(compare);
    asm.Memory(Cmp, Zp.Ret, M6502Index.X);
    asm.Branch(Bne, overflow);
    asm.Emit(Inx);
    asm.Immediate(Cpx, 8);
    asm.Branch(Bne, compare);
    asm.Bind(done);
    asm.Emit(Rts);
    asm.Bind(overflow);
    this.RaiseError(6);
  }
}
