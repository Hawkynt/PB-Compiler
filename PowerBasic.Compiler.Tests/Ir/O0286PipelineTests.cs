using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class O0286PipelineTests {

  [Test]
  public void StandardPipeline_GivenLenOfLeft_WhenRun_ThenO0286EliminatesTheSubstringCall() {
    var module = new IrModule("test");
    var lengthFn = Runtime(module, "rt_str_len", IrType.I32, IrType.Ptr);
    var leftFn = Runtime(module, "rt_str_left", IrType.Ptr, IrType.Ptr, IrType.I32);
    var source = new IrArgument(IrType.Ptr, 0, "source");
    var count = new IrArgument(IrType.I32, 1, "count");
    var function = module.AddFunction(new IrFunction("f", IrType.I32, [source, count]));
    var block = function.CreateBlock("entry");
    var builder = new IrBuilder(block);
    var slice = builder.Call(IrType.Ptr, leftFn, source, count);
    builder.Ret(builder.Call(IrType.I32, lengthFn, slice));

    IrPassManager.Standard().RunOnModule(module);

    Assert.That(function.AllInstructions.OfType<IrCall>()
      .Any(call => call.Callee is IrFunction { Name: "rt_str_left" }), Is.False);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  private static IrFunction Runtime(IrModule module, string name, IrType result, params IrType[] parameters)
    => module.AddFunction(new IrFunction(name, result,
      parameters.Select((type, index) => new IrArgument(type, index)).ToArray()));
}
