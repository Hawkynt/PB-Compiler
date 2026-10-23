using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Analysis;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class IrFNegTests {

  private static IrModule CreateModule() {
    var module = new IrModule("negation");
    var argument = new IrArgument(IrType.F64, 0, "value");
    var function = module.AddFunction(new IrFunction("NegValue", IrType.F64, [argument]));
    var builder = new IrBuilder(function.CreateBlock("entry"));
    builder.Ret(builder.FNeg(argument));
    return module;
  }

  [TestCase(16, IrCastOp.FPToSI)]
  [TestCase(16, IrCastOp.FPToSIRound)]
  [TestCase(32, IrCastOp.FPToSI)]
  [TestCase(32, IrCastOp.FPToSIRound)]
  public void Recover_GivenNegatedIntegerTree_ThenPreservesIntegerRecovery(int width, IrCastOp conversion) {
    var type = width == 16 ? IrType.I16 : IrType.I32;
    var value = new IrArgument(type, 0);
    var function = new IrFunction("NegInt", type, [value]);
    var block = function.CreateBlock("entry");
    var widened = block.Append(new IrCast(IrCastOp.SIToFP, value, IrType.F64));
    var negated = block.Append(new IrFNeg(widened));
    var narrowed = block.Append(new IrCast(conversion, negated, type));
    var returned = block.Append(new IrRet(narrowed));
    Assert.That(IntegerRecovery.Run(function), Is.EqualTo(1));
    Assert.That(returned.Value, Is.InstanceOf<IrBinary>());
    var integer = (IrBinary)returned.Value!;
    Assert.Multiple(() => {
      Assert.That(integer.Op, Is.EqualTo(IrBinaryOp.Sub));
      Assert.That(integer.Lhs, Is.InstanceOf<IrConstantInt>().And.Property("Value").EqualTo(0));
      Assert.That(integer.Rhs, Is.SameAs(value));
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  [TestCase(32, 0x00000000UL)]
  [TestCase(32, 0x80000000UL)]
  [TestCase(32, 0x00000001UL)]
  [TestCase(32, 0x7F7FFFFFUL)]
  [TestCase(32, 0x7F800000UL)]
  [TestCase(32, 0x7FC12345UL)]
  [TestCase(64, 0x0000000000000000UL)]
  [TestCase(64, 0x8000000000000000UL)]
  [TestCase(64, 0x0000000000000001UL)]
  [TestCase(64, 0x7FEFFFFFFFFFFFFFUL)]
  [TestCase(64, 0x7FF0000000000000UL)]
  [TestCase(64, 0x7FF8123456789ABCUL)]
  [TestCase(64, 0x7FF0000000000001UL)]
  public void Fold_GivenIeeeBits_ThenChangesOnlyTheSignBit(int width, ulong bits) {
    var type = width == 32 ? IrType.F32 : IrType.F64;
    var value = width == 32
      ? BitConverter.Int32BitsToSingle(unchecked((int)bits))
      : BitConverter.Int64BitsToDouble(unchecked((long)bits));
    var result = IrConstFold.TryFold(new IrFNeg(new IrConstantFloat(type, value))) as IrConstantFloat;
    Assert.That(result, Is.Not.Null);
    Assert.That(result!.BitPatternKey(), Is.EqualTo(
      (bits ^ (1UL << (width - 1))).ToString(width == 32 ? "X8" : "X16")));
  }

  [TestCase(0x0000, 0x0000000000000000UL)]
  [TestCase(0x8000, 0x0000000000000000UL)]
  [TestCase(0x0000, 0x0000000000000001UL)]
  [TestCase(0x0001, 0x8000000000000000UL)]
  [TestCase(0x3FFF, 0x8000000000000001UL)]
  [TestCase(0x7FFE, 0xFFFFFFFFFFFFFFFFUL)]
  [TestCase(0x7FFF, 0x8000000000000000UL)]
  [TestCase(0x7FFF, 0xC000000000000123UL)]
  [TestCase(0x7FFF, 0x8000000000000001UL)]
  public void Fold_GivenExtendedBits_ThenRetainsEveryPayloadAndExponentBit(int signExponent, ulong significand) {
    var value = IrConstantFloat.FromFloat80Bits((ushort)signExponent, significand);
    var result = (IrConstantFloat)IrConstFold.TryFold(new IrFNeg(value))!;
    Assert.Multiple(() => {
      Assert.That(result.Float80.SignExponent, Is.EqualTo((ushort)(signExponent ^ 0x8000)));
      Assert.That(result.Float80.Significand, Is.EqualTo(significand));
    });
  }

  [Test]
  public void Simplify_GivenTwoSignChanges_ThenReturnsTheOriginalValue() {
    var argument = new IrArgument(IrType.F80, 0);
    Assert.That(InstCombine.Simplify(new IrFNeg(new IrFNeg(argument))), Is.SameAs(argument));
  }

  [Test]
  public void Fold_GivenZeroSubtraction_ThenDoesNotConfuseItWithNegation() {
    var zero = new IrConstantFloat(IrType.F64, 0);
    var subtraction = (IrConstantFloat)IrConstFold.TryFold(new IrBinary(IrBinaryOp.FSub, zero, zero))!;
    var negation = (IrConstantFloat)IrConstFold.TryFold(new IrFNeg(zero))!;
    Assert.That(subtraction.SameBits(negation), Is.False);
  }

  [Test]
  public void CloneAndSerialize_GivenFloatingNegation_ThenPreservesItsOperandAndSemanticContract() {
    var module = CreateModule();
    var source = module.Functions.Single();
    var destination = module.AddFunction(new IrFunction("Clone", IrType.F64, [new IrArgument(IrType.F64, 0)]));
    var values = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance) {
      [source.Parameters[0]] = destination.Parameters[0],
    };
    IrCloner.Clone(destination, source.Blocks, values, "copy.");
    var cloned = destination.AllInstructions.OfType<IrFNeg>().Single();
    var bytes = IrUnitFile.Write(module);
    var restored = IrUnitFile.Read(bytes);
    Assert.Multiple(() => {
      Assert.That(cloned.Value, Is.SameAs(destination.Parameters[0]));
      Assert.That(IrEffects.ForInstruction(cloned).CanCse, Is.True);
      Assert.That(IrRepresentationContract.Verify(module, IrRepresentationStage.LowIr), Is.Empty);
      Assert.That(IrVerifier.Verify(restored), Is.Empty);
      Assert.That(IrPrinter.Print(restored), Is.EqualTo(IrPrinter.Print(module)));
      Assert.That(IrUnitFile.Write(restored), Is.EqualTo(bytes));
    });
  }

  [Test]
  public void Emit_GivenFloatingNegation_ThenWritersUseUnaryNegation() {
    var module = CreateModule();
    Assert.Multiple(() => {
      Assert.That(CEmitter.Emit(module), Does.Contain("-(p0)"));
      Assert.That(LlvmEmitter.Emit(module), Does.Contain("fneg double"));
      Assert.That(IrPrinter.Print(module), Does.Contain("fneg f64"));
      Assert.That(IrBasicWriter.Write(module), Does.Contain("-(value)"));
    });
  }

  [Test]
  public void Verify_GivenATaintedOperand_ThenRejectsTheTypeMismatch() {
    var module = CreateModule();
    var negation = module.Functions.Single().AllInstructions.OfType<IrFNeg>().Single();
    negation.SetOperand(0, new IrConstantInt(IrType.I64, 0));
    Assert.That(IrVerifier.Verify(module), Is.Not.Empty);
  }

  [Test]
  public void Construct_GivenAnInvalidOperand_ThenRejectsItExplicitly() {
    Assert.Multiple(() => {
      Assert.Throws<ArgumentNullException>(() => new IrFNeg(null!));
      Assert.Throws<ArgumentException>(() => new IrFNeg(new IrConstantInt(IrType.I32, 0)));
      Assert.Throws<ArgumentException>(() => new IrFNeg(new IrUndef(IrType.Mbf32)));
      Assert.Throws<ArgumentException>(() => new IrFNeg(new IrUndef(new(IrTypeKind.Float, 16))));
    });
  }
}
