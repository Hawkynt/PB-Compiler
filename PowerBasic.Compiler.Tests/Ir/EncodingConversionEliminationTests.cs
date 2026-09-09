using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0301 lossless representation-round-trip elimination.</summary>
[TestFixture]
public sealed class EncodingConversionEliminationTests {

  [TestCase("rt_str_from_i8", 8, true, IrCastOp.SIToFP)]
  [TestCase("rt_str_from_u8", 8, false, IrCastOp.UIToFP)]
  [TestCase("rt_str_from_i16", 16, true, IrCastOp.SIToFP)]
  [TestCase("rt_str_from_u16", 16, false, IrCastOp.UIToFP)]
  [TestCase("rt_str_from_i32", 32, true, IrCastOp.SIToFP)]
  [TestCase("rt_str_from_u32", 32, false, IrCastOp.UIToFP)]
  [TestCase("rt_str_from_i64", 64, true, IrCastOp.SIToFP)]
  [TestCase("rt_str_from_u64", 64, false, IrCastOp.UIToFP)]
  public void IntegerRoundTrip_GivenPrivateFormattedText_WhenFolded_ThenItBecomesTheMatchingNumericConversion(
      string formatterName, int bits, bool signed, IrCastOp expectedConversion) {
    var module = new IrModule("test");
    var integerType = IrType.Integer(bits, signed);
    var value = new IrArgument(integerType, 0, "value");
    var function = module.AddFunction(new IrFunction("f", IrType.F64, [value]));
    var formatter = module.AddFunction(new IrFunction(formatterName, IrType.Ptr,
      [new IrArgument(integerType, 0)]));
    var val = module.AddFunction(new IrFunction("rt_str_val", IrType.F64,
      [new IrArgument(IrType.Ptr, 0)]));
    var builder = new IrBuilder(function.CreateBlock("entry"));
    var text = builder.Call(IrType.Ptr, formatter, value);
    var parsed = builder.Call(IrType.F64, val, text);
    builder.Ret(parsed);

    var changed = StringConstantFold.Run(module);

    Assert.That(changed, Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(function), Is.Empty);
    Assert.That(function.AllInstructions.OfType<IrCall>(), Is.Empty);
    var conversion = function.AllInstructions.OfType<IrCast>().Single();
    Assert.That(conversion.Op, Is.EqualTo(expectedConversion));
    Assert.That(conversion.Value, Is.SameAs(value));
    Assert.That(conversion.Type, Is.EqualTo(IrType.F64));
  }

  [Test]
  public void IntegerRoundTrip_GivenFormattedTextWithAnotherReader_WhenFolded_ThenOwnershipPreventsElimination() {
    var module = new IrModule("test");
    var value = new IrArgument(IrType.I32, 0, "value");
    var function = module.AddFunction(new IrFunction("f", IrType.F64, [value]));
    var formatter = module.AddFunction(new IrFunction("rt_str_from_i32", IrType.Ptr,
      [new IrArgument(IrType.I32, 0)]));
    var val = module.AddFunction(new IrFunction("rt_str_val", IrType.F64,
      [new IrArgument(IrType.Ptr, 0)]));
    var observe = module.AddFunction(new IrFunction("observe", IrType.Void,
      [new IrArgument(IrType.Ptr, 0)]));
    var builder = new IrBuilder(function.CreateBlock("entry"));
    var text = builder.Call(IrType.Ptr, formatter, value);
    var parsed = builder.Call(IrType.F64, val, text);
    builder.Call(IrType.Void, observe, text);
    builder.Ret(parsed);

    var changed = StringConstantFold.Run(module);

    Assert.That(changed, Is.Zero);
    Assert.That(function.AllInstructions.OfType<IrCall>().Select(c => ((IrFunction)c.Callee).Name),
      Does.Contain("rt_str_from_i32").And.Contain("rt_str_val"));
    Assert.That(IrVerifier.Verify(function), Is.Empty);
  }

  [Test]
  public void IntegerRoundTrip_GivenFormatterSignednessThatDoesNotMatchItsOperand_WhenFolded_ThenBitsAreNotReinterpreted() {
    var module = new IrModule("test");
    var value = new IrArgument(IrType.I32, 0, "value");
    var function = module.AddFunction(new IrFunction("f", IrType.F64, [value]));
    var formatter = module.AddFunction(new IrFunction("rt_str_from_u32", IrType.Ptr,
      [new IrArgument(IrType.I32, 0)]));
    var val = module.AddFunction(new IrFunction("rt_str_val", IrType.F64,
      [new IrArgument(IrType.Ptr, 0)]));
    var builder = new IrBuilder(function.CreateBlock("entry"));
    var text = builder.Call(IrType.Ptr, formatter, value);
    builder.Ret(builder.Call(IrType.F64, val, text));

    var changed = StringConstantFold.Run(module);

    Assert.That(changed, Is.Zero);
    Assert.That(function.AllInstructions.OfType<IrCast>(), Is.Empty);
    Assert.That(IrVerifier.Verify(function), Is.Empty);
  }

  [Test]
  public void FloatingRoundTrip_GivenValOfFormattedSingle_WhenFolded_ThenPrecisionSensitiveFormattingIsPreserved() {
    var module = new IrModule("test");
    var value = new IrArgument(IrType.F80, 0, "value");
    var function = module.AddFunction(new IrFunction("f", IrType.F64, [value]));
    var formatter = module.AddFunction(new IrFunction("rt_str_from_single", IrType.Ptr,
      [new IrArgument(IrType.F80, 0)]));
    var val = module.AddFunction(new IrFunction("rt_str_val", IrType.F64,
      [new IrArgument(IrType.Ptr, 0)]));
    var builder = new IrBuilder(function.CreateBlock("entry"));
    var text = builder.Call(IrType.Ptr, formatter, value);
    builder.Ret(builder.Call(IrType.F64, val, text));

    var changed = StringConstantFold.Run(module);

    Assert.That(changed, Is.Zero);
    Assert.That(function.AllInstructions.OfType<IrCall>().Select(c => ((IrFunction)c.Callee).Name),
      Does.Contain("rt_str_from_single").And.Contain("rt_str_val"));
    Assert.That(IrVerifier.Verify(function), Is.Empty);
  }

  [Test]
  public void ReverseRoundTrip_GivenStrOfVal_WhenFolded_ThenNormalizedFormattingIsNotReplacedByTheSourceString() {
    var module = new IrModule("test");
    var source = new IrArgument(IrType.Ptr, 0, "source");
    var function = module.AddFunction(new IrFunction("f", IrType.Ptr, [source]));
    var val = module.AddFunction(new IrFunction("rt_str_val", IrType.F64,
      [new IrArgument(IrType.Ptr, 0)]));
    var formatter = module.AddFunction(new IrFunction("rt_str_from_double", IrType.Ptr,
      [new IrArgument(IrType.F80, 0)]));
    var builder = new IrBuilder(function.CreateBlock("entry"));
    var numeric = builder.Call(IrType.F64, val, source);
    var widened = builder.Cast(IrCastOp.FPExt, numeric, IrType.F80);
    builder.Ret(builder.Call(IrType.Ptr, formatter, widened));

    var changed = StringConstantFold.Run(module);

    Assert.That(changed, Is.Zero);
    Assert.That(function.AllInstructions.OfType<IrCall>().Select(c => ((IrFunction)c.Callee).Name),
      Does.Contain("rt_str_val").And.Contain("rt_str_from_double"));
    Assert.That(IrVerifier.Verify(function), Is.Empty);
  }
}
