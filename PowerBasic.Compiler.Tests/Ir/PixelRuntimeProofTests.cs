using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Analysis;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class PixelRuntimeProofTests {
  [Test]
  public void Run_GivenOrdinaryFarVideoStores_ThenDoesNotInferPackedLayoutFromTheirAddress() {
    var module = new IrModule("unproved-video");
    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var builder = new IrBuilder(main.CreateBlock("entry"));
    foreach (var offset in new[] { 10, 11 }) {
      var pointer = builder.FarPtr(new IrConstantInt(IrType.I16, -24576), new IrConstantInt(IrType.I16, offset));
      builder.Store(new IrConstantInt(IrType.U8, offset), pointer);
    }
    builder.Ret();
    Assert.That(PixelRuntimeStoreLowering.Run(module), Is.Zero);
    Assert.That(main.AllInstructions.OfType<IrStore>().Select(store => store.Value.Type.Bits), Is.EqualTo(new[] { 8, 8 }));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  [Test]
  public void Run_GivenAnUnusedPointResult_ThenFinalCleanupKeepsTheDeviceRead() {
    const string source = """
      SCREEN 13
      PSET (10,5),1
      unused%=POINT(10,5)
      PSET (11,5),2
      END
      """;
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36),
      Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty);
    var module = IrLowering.TryLowerModule(model, out var reason);
    Assert.That(module, Is.Not.Null, reason);
    IrMiddleEndPipeline.RunNativeModule(module!, optimize: true, recoverIntegerArithmetic: true);
    var main = module!.FindFunction("main")!;
    var reads = main.AllInstructions.OfType<IrLoad>().Where(load => load.Pointer is IrFarPtr).ToArray();
    Assert.That(reads, Has.Length.EqualTo(1), "POINT is an observation even when its result is unused");
    Assert.That(IrEffects.ForInstruction(reads[0]).CanDiscard, Is.False);
    Dce.Run(main);
    Assert.That(reads[0].Parent, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }
}
