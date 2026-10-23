using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class DrawStringSpecializationTests {
  private static (IrModule Module, IrFunction Main, IrCall Draw, IrCall Text) Build(string picture) {
    var module = new IrModule("draw");
    var literal = module.AddFunction(new IrFunction("rt_str_const", IrType.Ptr,
      [new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.I32, 1)]));
    var draw = module.AddFunction(new IrFunction("rt_draw", IrType.Void, [new IrArgument(IrType.Ptr, 0)]));
    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var block = main.CreateBlock("entry");
    var bytes = System.Text.Encoding.Latin1.GetBytes(picture);
    var text = block.Append(new IrCall(IrType.Ptr, literal,
      [module.AddStringConstant(bytes), new IrConstantInt(IrType.I32, bytes.Length)]));
    var call = block.Append(new IrCall(IrType.Void, draw, [text]));
    block.Append(new IrRet());
    return (module, main, call, text);
  }

  [TestCase("C9 R5", 1)]
  [TestCase("C9 NR5 BR2 D3", 2)]
  [TestCase("", 0)]
  [TestCase("R32767", 1)]
  [TestCase("L32767", 1)]
  public void Run_GivenOwnedProvenCommands_ThenExpandsAndConsumesOnlyTheirProducer(string picture, int lines) {
    var (module, main, draw, text) = Build(picture);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(DrawStringSpecialization.Run(module), Is.EqualTo(1));
    Assert.Multiple(() => {
      Assert.That(draw.Parent, Is.Null);
      Assert.That(text.Parent, Is.Null);
      Assert.That(text.Users, Is.Empty);
      Assert.That(main.AllInstructions.OfType<IrCall>().Count(call => call.Callee.Name == "rt_line"),
        Is.EqualTo(lines));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
      Assert.That(DrawStringSpecialization.Run(module), Is.Zero);
    });
  }

  [TestCase("S4 R5")]
  [TestCase("A1 R5")]
  [TestCase("R5 Z")]
  [TestCase("R9999999999999999999999999")]
  [TestCase("R5\u00ff")]
  [TestCase("R32768")]
  [TestCase("L32768")]
  [TestCase("R65536")]
  [TestCase("U65536")]
  [TestCase("M65536,0")]
  [TestCase("C9999999999999999999999999")]
  public void Run_GivenUnmodeledOrInvalidText_ThenDoesNotPartiallyDrawOrConsumeIt(string picture) {
    var (module, _, draw, text) = Build(picture);
    Assert.That(DrawStringSpecialization.Run(module), Is.Zero);
    Assert.That(draw.Parent, Is.Not.Null);
    Assert.That(text.Parent, Is.Not.Null);
    Assert.That(module.FindGlobal("rt_gx1"), Is.Null);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  [TestCase("shared")]
  [TestCase("foreign")]
  [TestCase("length")]
  [TestCase("definition")]
  [TestCase("handler")]
  [TestCase("assembly")]
  [TestCase("cell")]
  [TestCase("line")]
  [TestCase("line-foreign")]
  public void Run_GivenUnprovedOwnershipAbiOrState_ThenLeavesTheCallIntact(string boundary) {
    var (module, main, draw, text) = Build("C9 R5");
    switch (boundary) {
      case "shared":
        var use = module.AddFunction(new IrFunction("other", IrType.Void, [new IrArgument(IrType.Ptr, 0)]));
        draw.Parent!.InsertBefore(new IrCall(IrType.Void, use, [text]), draw);
        break;
      case "foreign":
        draw.SpecializeConvention(IrCallConvention.Cdecl);
        ((IrFunction)draw.Callee).SpecializeConvention(IrCallConvention.Cdecl);
        break;
      case "length": text.SetOperand(2, new IrConstantInt(IrType.I32, 1)); break;
      case "definition": ((IrFunction)draw.Callee).CreateBlock("entry").Append(new IrRet()); break;
      case "handler": main.HasErrorHandler = true; break;
      case "assembly": main.HasInlineAsm = true; break;
      case "cell": module.AddGlobal(new IrGlobalVariable("rt_gx1", IrType.I32)); break;
      case "line": module.AddFunction(new IrFunction("rt_line", IrType.Void)).CreateBlock("entry")
          .Append(new IrRet()); break;
      case "line-foreign": module.AddFunction(new IrFunction("rt_line", IrType.Void) {
          Convention = IrCallConvention.Cdecl,
        }); break;
    }
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(DrawStringSpecialization.Run(module), Is.Zero);
    Assert.That(draw.Parent, Is.Not.Null);
    Assert.That(text.Parent, Is.Not.Null);
  }

  [Test]
  public void Standard_GivenProvenText_ThenRunsTheRegisteredSpecializationAndKeepsObserversInOrder() {
    var (module, main, draw, _) = Build("C9 R5");
    var observer = module.AddFunction(new IrFunction("observe", IrType.Void));
    var before = draw.Parent!.InsertBefore(new IrCall(IrType.Void, observer, []), draw);
    var pipeline = IrMiddleEndPipeline.Standard();
    pipeline.VerifyEachPass = true;
    pipeline.RunOnModule(module);
    var instructions = main.AllInstructions.ToArray();
    Assert.That(draw.Parent, Is.Null);
    Assert.That(Array.IndexOf(instructions, before), Is.LessThan(Array.FindIndex(instructions,
      instruction => instruction is IrCall { Callee.Name: "rt_line" })));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }
}
