using PowerBasic.Compiler.Numerics;
using PowerBasic.Compiler.Semantics;

namespace PowerBasic.Compiler.Ir;

public sealed partial class IrLowering {

  // Converted from Microsoft's MIT-licensed MATH1.ASM ($SIN/$COS/RR/$SINCN) and
  // MATH2.ASM ($POLYX/$POLY), revision edf82c2ebf6bfe099c2054e0ae125c3efe5769c4.
  // See Numerics/THIRD-PARTY-NOTICE.GW-BASIC.txt. The coefficients are raw MBF, not decimal estimates.
  private static readonly uint[] _INTERPRETER_SINE_COEFFICIENTS = [
    0x861ED7FB, 0x87992665, 0x87233458, 0x86A55DE1, 0x83490FDB,
  ];

  private static IrConstantFloat CreateMbfConstant(ulong bits) {
    var bytes = Mbf64.FromBytes(BitConverter.GetBytes(bits)).ToExtended80().ToBytes();
    return IrConstantFloat.FromFloat80Bits(BitConverter.ToUInt16(bytes, 8), BitConverter.ToUInt64(bytes));
  }

  private static IrConstantFloat CreateMbfConstant(uint bits) => CreateMbfConstant((ulong)bits << 32);

  private IrValue LowerInterpreterTrig(IrValue argument, string function) {
    if (function == "cos")
      return this.LowerInterpreterSinCos(argument, cosine: true);
    var sine = this.LowerInterpreterSinCos(argument, cosine: false);
    if (function == "sin")
      return sine;
    var cosine = this.LowerInterpreterSinCos(argument, cosine: true);
    this.RaiseWhen(this._b.Cmp(IrCmpPred.Foeq, cosine, new IrConstantFloat(IrType.F80, 0)), 11, "tan.zero");
    return this.RoundInterpreterFloat(this._b.Binary(IrBinaryOp.FDiv, sine, cosine), PbType.Single);
  }

  private IrValue LowerInterpreterSinCos(IrValue argument, bool cosine) {
    var zero = new IrConstantFloat(IrType.F80, 0);
    var magnitude = this._b.Call(IrType.F80, this.RuntimeFn("llvm.fabs.f80", IrType.F80, IrType.F80), argument);
    // RR uses this deliberately adjusted 56-bit reciprocal, not a rounded host-double 1/(2*pi).
    var turns = this.RoundInterpreterFloat(
      this._b.Binary(IrBinaryOp.FMul, magnitude, CreateMbfConstant(0x7E22F9836E4E440BUL)), PbType.Double);
    var fraction = this.InterpreterFraction(turns);
    if (cosine)
      fraction = this.InterpreterFraction(this.RoundInterpreterFloat(
        this._b.Binary(IrBinaryOp.FAdd, fraction, new IrConstantFloat(IrType.F80, 0.25)), PbType.Double));
    var quadrants = this._b.Binary(IrBinaryOp.FMul, fraction, new IrConstantFloat(IrType.F80, 4));
    var quadrant = this._b.Cast(IrCastOp.FPToSI, quadrants, IrType.I16);
    IrValue reduced = this._b.Binary(IrBinaryOp.FSub, quadrants, this._b.Cast(IrCastOp.SIToFP, quadrant, IrType.F80));
    var odd = this._b.Cmp(IrCmpPred.Ne,
      this._b.And(quadrant, new IrConstantInt(IrType.I16, 1)), new IrConstantInt(IrType.I16, 0));
    reduced = this._b.Select(odd, this.RoundInterpreterFloat(
      this._b.Binary(IrBinaryOp.FSub, new IrConstantFloat(IrType.F80, 1), reduced), PbType.Double), reduced);
    reduced = this.RoundMbfSingleConversion(
      this._b.Binary(IrBinaryOp.FMul, reduced, new IrConstantFloat(IrType.F80, 0.25)), PbType.Double);
    var result = this.EvaluateInterpreterSine(reduced);
    var negative = this._b.Cmp(IrCmpPred.Sge, quadrant, new IrConstantInt(IrType.I16, 2));
    if (!cosine)
      negative = this._b.Cmp(IrCmpPred.Ne,
        this._b.Binary(IrBinaryOp.Xor, negative, this._b.Cmp(IrCmpPred.Folt, argument, zero)),
        new IrConstantInt(IrType.I1, 0));
    result = this._b.Select(negative,
      this._b.Binary(IrBinaryOp.FMul, result, new IrConstantFloat(IrType.F80, -1)), result);
    return cosine ? result : this._b.Select(
      this._b.Cmp(IrCmpPred.Folt, magnitude, new IrConstantFloat(IrType.F80, Math.ScaleB(1, -10))), argument, result);
  }

  private IrValue InterpreterFraction(IrValue positive) {
    // MBF64 has no fractional bits at or above 2^55. Clamp before FPToSI so even the largest
    // valid interpreter argument never crosses an out-of-range integer conversion.
    var safe = this._b.Select(
      this._b.Cmp(IrCmpPred.Foge, positive, new IrConstantFloat(IrType.F80, Math.ScaleB(1, 55))),
      new IrConstantFloat(IrType.F80, 0), positive);
    var integer = this._b.Cast(IrCastOp.SIToFP, this._b.Cast(IrCastOp.FPToSI, safe, IrType.I64), IrType.F80);
    return this.RoundInterpreterFloat(this._b.Binary(IrBinaryOp.FSub, safe, integer), PbType.Double);
  }

  private IrValue EvaluateInterpreterSine(IrValue reduced) {
    var square = this.RoundInterpreterSingleArithmetic(this._b.Binary(IrBinaryOp.FMul, reduced, reduced));
    IrValue polynomial = CreateMbfConstant(_INTERPRETER_SINE_COEFFICIENTS[0]);
    foreach (var coefficient in _INTERPRETER_SINE_COEFFICIENTS.Skip(1)) {
      polynomial = this.RoundInterpreterSingleArithmetic(this._b.Binary(IrBinaryOp.FMul, polynomial, square));
      polynomial = this.RoundInterpreterSingleArithmetic(
        this._b.Binary(IrBinaryOp.FAdd, polynomial, CreateMbfConstant(coefficient)));
    }
    var result = this.RoundInterpreterSingleArithmetic(this._b.Binary(IrBinaryOp.FMul, reduced, polynomial));
    var small = this.RoundInterpreterSingleArithmetic(
      this._b.Binary(IrBinaryOp.FMul, reduced, CreateMbfConstant(0x83490FDBU)));
    return this._b.Select(
      this._b.Cmp(IrCmpPred.Folt, reduced, new IrConstantFloat(IrType.F80, Math.ScaleB(1, -13))), small, result);
  }
}
