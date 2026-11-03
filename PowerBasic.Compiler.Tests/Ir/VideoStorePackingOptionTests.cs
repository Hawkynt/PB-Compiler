using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// $OPTION VRAMPACK is a compiler-only selection of the certified VRAM
/// batching pass. It must not disable normal SSA optimization, direct
/// framebuffer lowering, or any other memory-packing policy.
/// </summary>
[TestFixture]
public sealed class VideoStorePackingOptionTests {
  private const string _PIXELS = """
    SCREEN 13
    PSET (10,5),1
    PSET (11,5),2
    PSET (12,5),3
    PSET (13,5),4
    PRINT POINT(10,5);POINT(11,5);POINT(12,5);POINT(13,5)
    END
    """;

  private static SemanticModel Bind(string source, Dialect dialect = Dialect.Pb36) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "PACK.BAS", dialect),
      "PACK.BAS", dialect), dialect);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    return model;
  }

  [TestCase("", 32, 1)]
  [TestCase("$OPTION VRAMPACK ON", 32, 1)]
  [TestCase("$OPTION VRAMPACK OFF", 32, 4)]
  [TestCase("$OPTION VRAMPACK OFF\n$OPTION VRAMPACK ON", 32, 1)]
  [TestCase("$OPTION VRAMPACK ON\n$OPTION VRAMPACK OFF", 32, 4)]
  [TestCase("$OPTION VRAMPACK ON", 16, 2)]
  [TestCase("$OPTION VRAMPACK OFF", 16, 4)]
  public void Optimize_GivenVideoPackingDirective_ThenOnlyTheVramWriteWidthChanges(
      string directive, int cpuBits, int expectedWrites) {
    var model = Bind(directive + "\n" + _PIXELS);
    var module = IrLowering.TryLowerModule(model, out var reason);
    Assert.That(module, Is.Not.Null, reason);
    IrMiddleEndPipeline.RunNativeModule(module!, optimize: true, recoverIntegerArithmetic: true,
      targetCost: new TargetCost(
        cpuBits == 32 ? CpuTier.I80386 : CpuTier.I8086, CostObjective.Balanced),
      packVideoStores: model.PackVideoStores);
    var main = module!.FindFunction("main")!;
    var framebuffer = main.AllInstructions.OfType<IrStore>().Where(store =>
      store.Pointer is IrFarPtr { Segment: IrConstantInt { Value: -24576 } }).ToArray();
    Assert.Multiple(() => {
      Assert.That(framebuffer, Has.Length.EqualTo(expectedWrites));
      Assert.That(framebuffer.Select(store => store.Value.Type.Bits).Distinct(),
        Is.EquivalentTo(new[] { expectedWrites == 4 ? 8 : cpuBits }));
      Assert.That(main.AllInstructions.OfType<IrCall>().Any(call =>
        call.Callee is IrFunction { Name: "rt_pset_mode13_offset" or "rt_point" }),
        Is.False, "VRAMPACK OFF must leave direct pixel stores and POINT specialization active");
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [TestCase("$OPTION VRAMPACK OFF")]
  [TestCase("$OPTION VRAMPACK ON")]
  public void Execute_GivenVramPackingSelection_ThenPreservesPointReadback(string option) {
    var model = Bind(option + "\n" + _PIXELS);
    var generator = new CodeGenerator(model) { Optimize = true, Container = DosContainer.Exe };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    Assert.That(Cpu8086.Run(image).Output, Does.Contain("1  2  3  4"));
  }

  [TestCase("$OPTION VRAMPACK MAYBE", Dialect.Pb36)]
  [TestCase("$OPTION VRAMPACK", Dialect.Pb36)]
  [TestCase("$OPTION VRAMPACK OFF", Dialect.Pb35)]
  public void Parse_GivenUnsupportedVideoPackingDirective_ThenRejectsIt(
      string source, Dialect dialect) =>
    Assert.Throws<ParserException>(() => Parser.Parse(
      Lexer.Tokenize(source + "\nEND", "PACK.BAS", dialect), "PACK.BAS", dialect));
}
