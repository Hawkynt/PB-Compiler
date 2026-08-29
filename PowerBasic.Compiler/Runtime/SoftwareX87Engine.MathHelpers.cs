using System.Numerics;
using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class SoftwareX87Engine {
  private Label? _mathAdd;
  private Label? _mathSub;
  private Label? _mathMul;
  private Label? _mathDiv;
  private bool _mathProceduresEmitted;

  private void EnsureMathProcedures() {
    if (this._mathProceduresEmitted)
      return;
    this._mathProceduresEmitted = true;
    this._mathAdd = this._asm.DefineLabel("swx87_add");
    this._mathSub = this._asm.DefineLabel("swx87_sub");
    this._mathMul = this._asm.DefineLabel("swx87_mul");
    this._mathDiv = this._asm.DefineLabel("swx87_div");
    var over = this._asm.DefineLabel();
    this._asm.Jmp(over);

    this._asm.MarkLabel(this._mathAdd);
    this.EmitCanonicalAdd(Math0, Math1, Math2);
    this._asm.Ret();
    this._asm.MarkLabel(this._mathSub);
    this.EmitCanonicalSubtract(Math0, Math1, Math2);
    this._asm.Ret();
    this._asm.MarkLabel(this._mathMul);
    this.EmitCanonicalMultiply(Math0, Math1, Math2);
    this._asm.Ret();
    this._asm.MarkLabel(this._mathDiv);
    this.EmitCanonicalDivide(Math0, Math1, Math2);
    this._asm.Ret();
    this._asm.MarkLabel(over);
  }

  private void CallMath(Label? procedure, int left, int right, int result) {
    this.EnsureMathProcedures();
    this.CopyScratch(left, Math0);
    this.CopyScratch(right, Math1);
    this._asm.Call(procedure!);
    this.CopyScratch(Math2, result);
  }

  private void MathAdd(int left, int right, int result) => this.CallMath(this._mathAdd, left, right, result);
  private void MathSub(int left, int right, int result) => this.CallMath(this._mathSub, left, right, result);
  private void MathMul(int left, int right, int result) => this.CallMath(this._mathMul, left, right, result);
  private void MathDiv(int left, int right, int result) => this.CallMath(this._mathDiv, left, right, result);

  private void LoadCanonical(int destination, short exponent, ushort meta, ulong significand) {
    this._asm.Mov(this.Scratch(destination + Sig0, OperandSize.Word), (int)(significand & 0xFFFF));
    this._asm.Mov(this.Scratch(destination + Sig1, OperandSize.Word), (int)(significand >> 16 & 0xFFFF));
    this._asm.Mov(this.Scratch(destination + Sig2, OperandSize.Word), (int)(significand >> 32 & 0xFFFF));
    this._asm.Mov(this.Scratch(destination + Sig3, OperandSize.Word), (int)(significand >> 48 & 0xFFFF));
    this._asm.Mov(this.Scratch(destination + Exponent, OperandSize.Word), exponent);
    this._asm.Mov(this.Scratch(destination + Meta, OperandSize.Word), meta);
  }

  private void LoadCanonicalInteger(int destination, long value) {
    if (value == 0) {
      this.LoadCanonical(destination, 0, ClassZero, 0);
      return;
    }
    var negative = value < 0;
    var magnitude = negative ? (ulong)(-(value + 1)) + 1UL : (ulong)value;
    var exponent = 63 - BitOperations.LeadingZeroCount(magnitude);
    var significand = magnitude << (63 - exponent);
    this.LoadCanonical(destination, (short)exponent, (ushort)(negative ? SignMask : 0), significand);
  }

  /// <summary>Builds an exactly rounded 64-bit canonical rational using integer arithmetic only.</summary>
  private void LoadCanonicalRational(int destination, long numerator, long denominator) {
    if (denominator == 0) throw new DivideByZeroException();
    if (numerator == 0) { this.LoadCanonical(destination, 0, ClassZero, 0); return; }
    var negative = (numerator < 0) ^ (denominator < 0);
    var n = BigInteger.Abs(new BigInteger(numerator));
    var d = BigInteger.Abs(new BigInteger(denominator));
    var exponent = 0;
    while (n >= d << 1) { d <<= 1; ++exponent; }
    while (n < d) { n <<= 1; --exponent; }
    var scaled = n << 63;
    var quotient = BigInteger.DivRem(scaled, d, out var remainder);
    var twice = remainder << 1;
    if (twice > d || twice == d && !quotient.IsEven)
      ++quotient;
    if (quotient == BigInteger.One << 64) { quotient >>= 1; ++exponent; }
    this.LoadCanonical(destination, checked((short)exponent), (ushort)(negative ? SignMask : 0), (ulong)quotient);
  }

  private void LoadPi(int destination) => this.LoadCanonical(destination, 1, 0, 0xC90FDAA22168C235UL);
  private void LoadPiOver2(int destination) => this.LoadCanonical(destination, 0, 0, 0xC90FDAA22168C235UL);
  private void LoadPiOver4(int destination) => this.LoadCanonical(destination, -1, 0, 0xC90FDAA22168C235UL);
  private void LoadLn2(int destination) => this.LoadCanonical(destination, -1, 0, 0xB17217F7D1CF79ACUL);
  private void LoadSqrt2(int destination) => this.LoadCanonical(destination, 0, 0, 0xB504F333F9DE6484UL);

  private void NegateCanonical(int value) => this._asm.Xor(this.Scratch(value + Meta, OperandSize.Word), SignMask);
  private void AbsCanonical(int value) => this._asm.And(this.Scratch(value + Meta, OperandSize.Word), unchecked((ushort)~SignMask));
}
