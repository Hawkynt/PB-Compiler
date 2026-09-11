using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Analysis;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class IrFloat80ConstantTests {

  private static IrConstantFloat ExtraPrecisionOne()
    => IrConstantFloat.FromFloat80Bits(0x3fff, 0x8000_0000_0000_0001UL);

  [Test]
  public void FromDouble_GivenExactlyRepresentableValue_ThenProducesArchitecturalX87Bits() {
    var value = new IrConstantFloat(IrType.F80, 1.5);

    Assert.Multiple(() => {
      Assert.That(value.Float80.SignExponent, Is.EqualTo(0x3fff));
      Assert.That(value.Float80.Significand, Is.EqualTo(0xc000_0000_0000_0000UL));
      Assert.That(value.TryGetDoubleExact(out var roundTrip), Is.True);
      Assert.That(roundTrip, Is.EqualTo(1.5));
      Assert.That(value.Float80.ToLlvmHexString(), Is.EqualTo("0xK3FFFC000000000000000"));
    });
  }

  [Test]
  public void ExactBits_GivenOneExtraSignificandBit_ThenHostDoubleConversionDeclinesInsteadOfRounding() {
    var value = ExtraPrecisionOne();

    Assert.Multiple(() => {
      Assert.That(value.TryGetDoubleExact(out _), Is.False);
      Assert.That(() => _ = value.Value, Throws.InvalidOperationException);
      Assert.That(value.Float80.ToLlvmHexString(), Is.EqualTo("0xK3FFF8000000000000001"));
      Assert.That(value.Clone().SameBits(value), Is.True);
    });
  }

  [Test]
  public void ExactBits_GivenExponentOutsideBinary64_ThenValueRemainsFiniteF80WithoutFakeDoubleRange() {
    var value = IrConstantFloat.FromFloat80Bits(0x47cf, 0x8000_0000_0000_0000UL); // 2^2000
    var fn = new IrFunction("huge", IrType.F80);
    var entry = fn.CreateBlock("entry");
    entry.Append(new IrRet(value));
    var domain = FpDomainAnalysis.Build(fn)!.DomainAt(value, entry);

    Assert.Multiple(() => {
      Assert.That(value.Float80.IsFinite, Is.True);
      Assert.That(value.TryGetDoubleExact(out _), Is.False);
      Assert.That(domain.NonNaN, Is.True);
      Assert.That(domain.Finite, Is.True);
      Assert.That(domain.IsKnown, Is.False, "binary64 endpoints must not approximate an out-of-range F80 value");
    });
  }

  [TestCase(0x7fff, 0x8000000000000000UL, "0xK7FFF8000000000000000", TestName = "LLVM preserves F80 infinity")]
  [TestCase(0x0000, 0x0000000000000001UL, "0xK00000000000000000001", TestName = "LLVM preserves F80 subnormal")]
  [TestCase(0x7fff, 0xc000000000000123UL, "0xK7FFFC000000000000123", TestName = "LLVM preserves F80 NaN payload")]
  public void Emit_GivenSpecialF80Bits_ThenLLVMAndDebugPrinterPreserveAllBits(
      int signExponent, ulong significand, string expected) {
    var constant = IrConstantFloat.FromFloat80Bits((ushort)signExponent, significand);
    var fn = new IrFunction("bits", IrType.F80);
    var entry = fn.CreateBlock("entry");
    entry.Append(new IrRet(constant));

    Assert.Multiple(() => {
      Assert.That(LlvmEmitter.Emit(fn), Does.Contain($"ret x86_fp80 {expected}"));
      Assert.That(IrPrinter.Print(fn), Does.Contain($"ret f80 {expected}"));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void ConstFold_GivenF80OperandOutsideExactBinary64Subset_ThenLeavesArithmeticToTarget() {
    var left = ExtraPrecisionOne();
    var right = new IrConstantFloat(IrType.F80, 1.0);
    var add = new IrBinary(IrBinaryOp.FAdd, left, right);

    Assert.That(IrConstFold.TryFold(add), Is.Null);
  }

  [Test]
  public void SCCP_GivenSameExactF80BitsFromBothExecutableEdges_ThenClonePreservesThem() {
    var condition = new IrArgument(IrType.I1, 0, "condition");
    var fn = new IrFunction("same", IrType.F80, [condition]);
    var entry = fn.CreateBlock("entry");
    var left = fn.CreateBlock("left");
    var right = fn.CreateBlock("right");
    var join = fn.CreateBlock("join");
    entry.Append(new IrCondBr(condition, left, right));
    left.Append(new IrBr(join));
    right.Append(new IrBr(join));
    var phi = join.AppendPhi(new IrPhi(IrType.F80));
    phi.AddIncoming(ExtraPrecisionOne(), left);
    phi.AddIncoming(ExtraPrecisionOne(), right);
    var ret = join.Append(new IrRet(phi));

    Assert.That(Sccp.Run(fn), Is.GreaterThanOrEqualTo(1));
    var folded = ret.Value as IrConstantFloat;

    Assert.Multiple(() => {
      Assert.That(folded, Is.Not.Null);
      Assert.That(folded!.SameBits(ExtraPrecisionOne()), Is.True);
      Assert.That(folded.TryGetDoubleExact(out _), Is.False);
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void SCCP_GivenF80ConstantsDifferingOnlyInExtraPrecisionBit_ThenDoesNotMergeThem() {
    var condition = new IrArgument(IrType.I1, 0, "condition");
    var fn = new IrFunction("different", IrType.F80, [condition]);
    var entry = fn.CreateBlock("entry");
    var left = fn.CreateBlock("left");
    var right = fn.CreateBlock("right");
    var join = fn.CreateBlock("join");
    entry.Append(new IrCondBr(condition, left, right));
    left.Append(new IrBr(join));
    right.Append(new IrBr(join));
    var phi = join.AppendPhi(new IrPhi(IrType.F80));
    phi.AddIncoming(IrConstantFloat.FromFloat80Bits(0x3fff, 0x8000_0000_0000_0000UL), left);
    phi.AddIncoming(ExtraPrecisionOne(), right);
    var ret = join.Append(new IrRet(phi));

    Sccp.Run(fn);

    Assert.Multiple(() => {
      Assert.That(ret.Value, Is.SameAs(phi));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void GVN_GivenSeparateConstantsWithSameF80Bits_ThenCSEsButDifferentLowBitDoesNot() {
    var x = new IrArgument(IrType.F80, 0, "x");
    var fn = new IrFunction("gvn", IrType.F80, [x]);
    var entry = fn.CreateBlock("entry");
    var first = entry.Append(new IrBinary(IrBinaryOp.FAdd, x, ExtraPrecisionOne()));
    var same = entry.Append(new IrBinary(IrBinaryOp.FAdd, x, ExtraPrecisionOne()));
    var different = entry.Append(new IrBinary(IrBinaryOp.FAdd, x,
      IrConstantFloat.FromFloat80Bits(0x3fff, 0x8000_0000_0000_0002UL)));
    var total = entry.Append(new IrBinary(IrBinaryOp.FAdd, same, different));
    entry.Append(new IrRet(total));

    Assert.That(Gvn.Run(fn), Is.EqualTo(1));

    Assert.Multiple(() => {
      Assert.That(first.Parent, Is.SameAs(entry));
      Assert.That(same.Parent, Is.Null, "the bit-identical expression should be commoned");
      Assert.That(different.Parent, Is.SameAs(entry), "the extra low significand bit is part of the value identity");
      Assert.That(total.Lhs, Is.SameAs(first));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void FromDouble_GivenSignedZeroInfinityAndNaN_ThenExactRoundTripKeepsBinary64Bits() {
    var values = new[] {
      -0.0,
      double.PositiveInfinity,
      double.NegativeInfinity,
      BitConverter.Int64BitsToDouble(unchecked((long)0xfff8_0000_0000_0123UL)),
    };

    Assert.Multiple(() => {
      foreach (var source in values) {
        var constant = new IrConstantFloat(IrType.F80, source);
        Assert.That(constant.TryGetDoubleExact(out var roundTrip), Is.True);
        Assert.That(BitConverter.DoubleToInt64Bits(roundTrip), Is.EqualTo(BitConverter.DoubleToInt64Bits(source)));
      }
    });
  }
}
