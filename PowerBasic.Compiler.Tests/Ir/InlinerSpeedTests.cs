using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class InlinerSpeedTests {

  [Test]
  public void Run_GivenCalleeAboveNormalBudget_ThenOnlySpeedObjectiveInlinesIt() {
    var module = BuildModule(bodyInstructions: 80);
    var main = module.FindFunction("main")!;

    Assert.That(Inliner.Run(module), Is.EqualTo(0));
    Assert.That(main.AllInstructions.OfType<IrCall>().Count(), Is.EqualTo(1));

    Assert.That(Inliner.Run(module, optimizeForSpeed: true), Is.EqualTo(1));
    Assert.That(main.AllInstructions.OfType<IrCall>(), Is.Empty);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  [Test]
  public void Run_GivenCalleeAboveSpeedBudget_ThenItRemainsACall() {
    var module = BuildModule(bodyInstructions: 300);
    var main = module.FindFunction("main")!;

    Assert.That(Inliner.Run(module, optimizeForSpeed: true), Is.EqualTo(0));
    Assert.That(main.AllInstructions.OfType<IrCall>().Count(), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  [Test]
  public void Run_GivenInlineAsmCaller_ThenModuleInlinerDoesNotChangeItsFrame() {
    var module = BuildModule(bodyInstructions: 1);
    var main = module.FindFunction("main")!;
    main.HasInlineAsm = true;

    Assert.That(Inliner.Run(module, optimizeForSpeed: true), Is.EqualTo(0));
    Assert.That(main.AllInstructions.OfType<IrCall>().Count(), Is.EqualTo(1));
  }

  private static IrModule BuildModule(int bodyInstructions) {
    var module = new IrModule("T");
    var n = new IrArgument(IrType.I32, 0, "n");
    var callee = new IrFunction("abstraction", IrType.I32, [n]);
    var calleeBuilder = new IrBuilder(callee.CreateBlock("entry"));
    IrValue value = n;
    for (var i = 0; i < bodyInstructions; ++i)
      value = calleeBuilder.Add(value, new IrConstantInt(IrType.I32, i + 1));
    calleeBuilder.Ret(value);
    module.AddFunction(callee);

    var x = new IrArgument(IrType.I32, 0, "x");
    var main = new IrFunction("main", IrType.I32, [x]);
    var mainBuilder = new IrBuilder(main.CreateBlock("entry"));
    mainBuilder.Ret(mainBuilder.Call(IrType.I32, callee, x));
    module.AddFunction(main);
    return module;
  }
}
