using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Regression coverage for O0303 formatted-print specialization in the IR middle end.</summary>
[TestFixture]
public sealed class O0303MiddleEndTests {

  [TestCase(314, (5 << 8) | 2, " 3.14")]
  [TestCase(-1250, (7 << 8) | 2, " -12.50")]
  [TestCase(1234567, (11 << 8) | 0x80, "  1,234,567")]
  [TestCase(5, (4 << 8) | 2, "0.05")]
  [TestCase(12345, 2 << 8, "12345")]
  [TestCase(int.MinValue, 11 << 8, "-2147483648")]
  public void FormattedPrint_GivenAConstantScaledField_WhenRun_ThenItBecomesAnExactLiteral(
      int scaled, int spec, string expected) {
    var module = new IrModule("test");
    var formatter = module.AddFunction(new IrFunction("rt_using_field", IrType.Void, [
      new IrArgument(IrType.I32, 0), new IrArgument(IrType.I32, 1),
    ]));
    var fn = module.AddFunction(new IrFunction("f", IrType.Void, []));
    var block = fn.CreateBlock("entry");
    var builder = new IrBuilder(block);
    builder.Call(IrType.Void, formatter,
      new IrConstantInt(IrType.I32, scaled), new IrConstantInt(IrType.I32, spec));
    builder.Ret();

    var changed = FormattedPrintSpecialization.Run(module);

    Assert.That(changed, Is.EqualTo(1));
    var call = fn.AllInstructions.OfType<IrCall>().Single();
    Assert.That((call.Callee as IrFunction)?.Name, Is.EqualTo("rt_print_str"));
    var args = call.Args.ToArray();
    Assert.That(args[0], Is.TypeOf<IrGlobalVariable>());
    Assert.That(System.Text.Encoding.ASCII.GetString(((IrGlobalVariable)args[0]).Bytes!), Is.EqualTo(expected));
    Assert.That(((IrConstantInt)args[1]).Value, Is.EqualTo(expected.Length));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void FormattedPrint_GivenAFileField_WhenRun_ThenTheFileNumberIsPreserved() {
    var file = new IrArgument(IrType.I32, 0, "file");
    var module = new IrModule("test");
    var formatter = module.AddFunction(new IrFunction("rt_fusing_field", IrType.Void, [
      new IrArgument(IrType.I32, 0), new IrArgument(IrType.I32, 1), new IrArgument(IrType.I32, 2),
    ]));
    var fn = module.AddFunction(new IrFunction("f", IrType.Void, [file]));
    var block = fn.CreateBlock("entry");
    var builder = new IrBuilder(block);
    builder.Call(IrType.Void, formatter, file,
      new IrConstantInt(IrType.I32, 42), new IrConstantInt(IrType.I32, 4 << 8));
    builder.Ret();

    Assert.That(FormattedPrintSpecialization.Run(module), Is.EqualTo(1));

    var call = fn.AllInstructions.OfType<IrCall>().Single();
    Assert.That((call.Callee as IrFunction)?.Name, Is.EqualTo("rt_fprint_str"));
    var args = call.Args.ToArray();
    Assert.That(args[0], Is.SameAs(file));
    Assert.That(System.Text.Encoding.ASCII.GetString(((IrGlobalVariable)args[1]).Bytes!), Is.EqualTo("  42"));
    Assert.That(((IrConstantInt)args[2]).Value, Is.EqualTo(4));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void FormattedPrint_GivenADynamicScaledField_WhenRun_ThenTheGenericFormatterRemains() {
    var value = new IrArgument(IrType.I32, 0, "value");
    var module = new IrModule("test");
    var formatter = module.AddFunction(new IrFunction("rt_using_field", IrType.Void, [
      new IrArgument(IrType.I32, 0), new IrArgument(IrType.I32, 1),
    ]));
    var fn = module.AddFunction(new IrFunction("f", IrType.Void, [value]));
    var block = fn.CreateBlock("entry");
    var builder = new IrBuilder(block);
    builder.Call(IrType.Void, formatter, value, new IrConstantInt(IrType.I32, (5 << 8) | 2));
    builder.Ret();

    Assert.That(FormattedPrintSpecialization.Run(module), Is.Zero);
    Assert.That((fn.AllInstructions.OfType<IrCall>().Single().Callee as IrFunction)?.Name,
      Is.EqualTo("rt_using_field"));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void StandardPipeline_GivenAConstantFormattedField_WhenRun_ThenO0303IsRegistered() {
    var module = new IrModule("test");
    var formatter = module.AddFunction(new IrFunction("rt_using_field", IrType.Void, [
      new IrArgument(IrType.I32, 0), new IrArgument(IrType.I32, 1),
    ]));
    var fn = module.AddFunction(new IrFunction("f", IrType.Void, []));
    var block = fn.CreateBlock("entry");
    var builder = new IrBuilder(block);
    builder.Call(IrType.Void, formatter,
      new IrConstantInt(IrType.I32, 7), new IrConstantInt(IrType.I32, 2 << 8));
    builder.Ret();

    IrPassManager.Standard().RunOnModule(module);

    Assert.That(fn.AllInstructions.OfType<IrCall>()
      .Any(call => call.Callee is IrFunction { Name: "rt_using_field" }), Is.False);
    Assert.That(fn.AllInstructions.OfType<IrCall>()
      .Any(call => call.Callee is IrFunction { Name: "rt_print_str" }), Is.True);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }
}
