using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0272 profile-guided loop policy: distribution-aware small-trip peeling with fallback.</summary>
[TestFixture]
public sealed class ProfileGuidedLoopOptimizationTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static IrModule Lowered(string source) {
    var module = IrLowering.TryLowerModule(Bind(source), out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    foreach (var fn in module!.Functions)
      if (!fn.IsDeclaration)
        Mem2Reg.Run(fn);
    return module;
  }

  private static string Run(string source) {
    var cg = new CodeGenerator(Bind(source)) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  private static IrBasicBlock LoopHeader(IrFunction fn) => fn.Blocks.Single(block =>
    block.Phis.Any() && block.Terminator is IrCondBr { Condition: IrCmp });

  private static string RuntimeBoundProgram(int bound) => $$"""
    DIM i AS INTEGER
    DIM s AS INTEGER
    s = 0
    FOR i = 1 TO Bound%()
      s = s + i
    NEXT i
    PRINT s
    END
    FUNCTION Bound%()
      Bound% = {{bound}}
    END FUNCTION
    """;

  private static void Attach(IrBasicBlock header, params (long Trips, ulong Samples)[] histogram)
    => IrProfileMetadata.SetLoopTripCounts(header, new(histogram));

  [Test]
  public void Profile_GivenDominantSmallTripCount_ThenThePrefixIsPeeledAndTheLoopRemainsAsFallback() {
    var module = Lowered(RuntimeBoundProgram(3));
    var main = module.FindFunction("main")!;
    var header = LoopHeader(main);
    Attach(header, (3, 95), (3000, 5));

    Assert.That(ProfileGuidedLoopOptimization.Run(main), Is.EqualTo(1));

    Assert.That(main.Blocks.Any(block => block.Label.StartsWith("pgo.peel0.", StringComparison.Ordinal)), Is.True);
    Assert.That(main.Blocks.Contains(header), Is.True, "the original loop is the correctness fallback");
    Assert.That(IrProfileMetadata.TryGetLoopTripCounts(header, out _), Is.False,
      "consuming the profile prevents another fixpoint sweep from peeling it again");
    Assert.That(IrVerifier.Verify(main), Is.Empty);
  }

  [Test]
  public void Profile_GivenBimodalDistribution_ThenItDoesNotOptimizeTheAverage() {
    var module = Lowered(RuntimeBoundProgram(4));
    var main = module.FindFunction("main")!;
    var header = LoopHeader(main);
    Attach(header, (0, 50), (8, 50)); // average 4, but almost nobody actually runs four trips

    Assert.That(ProfileGuidedLoopOptimization.Run(main), Is.Zero);
    Assert.That(main.Blocks.Any(block => block.Label.StartsWith("pgo.peel", StringComparison.Ordinal)), Is.False);
    Assert.That(IrVerifier.Verify(main), Is.Empty);
  }

  [Test]
  public void Profile_GivenTooFewSamples_ThenItDeclines() {
    var module = Lowered(RuntimeBoundProgram(3));
    var main = module.FindFunction("main")!;
    var header = LoopHeader(main);
    Attach(header, (3, 31));

    Assert.That(ProfileGuidedLoopOptimization.Run(main), Is.Zero);
    Assert.That(IrVerifier.Verify(main), Is.Empty);
  }

  [Test]
  public void Profile_GivenZeroTripMode_ThenItDoesNotCloneAColdBody() {
    var module = Lowered(RuntimeBoundProgram(0));
    var main = module.FindFunction("main")!;
    var header = LoopHeader(main);
    Attach(header, (0, 95), (1, 5));

    Assert.That(ProfileGuidedLoopOptimization.Run(main), Is.Zero);
    Assert.That(IrVerifier.Verify(main), Is.Empty);
  }

  [Test]
  public void Pipeline_GivenAProfiledRuntimeBound_ThenTheEarlyLoopSlotConsumesO0272() {
    var module = Lowered(RuntimeBoundProgram(3));
    var main = module.FindFunction("main")!;
    var header = LoopHeader(main);
    Attach(header, (3, 100));

    Assert.That(LoopUnroll.Run(main), Is.GreaterThanOrEqualTo(1));
    Assert.That(IrProfileMetadata.TryGetLoopTripCounts(header, out _), Is.False);
    Assert.That(IrVerifier.Verify(main), Is.Empty);
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(3)]
  [TestCase(12)]
  public void Profile_GivenAStaleOrAccurateHistogram_ThenObservableBehaviourIsStillExact(int actualBound) {
    var source = RuntimeBoundProgram(actualBound);
    var expected = Run(source);
    var module = Lowered(source);
    var main = module.FindFunction("main")!;
    var header = LoopHeader(main);

    // Deliberately claim three trips for every test case. 0/1/12 are stale profiles; the runtime
    // guards and original-loop fallback must still reproduce the source exactly.
    Attach(header, (3, 100));
    Assert.That(ProfileGuidedLoopOptimization.Run(main), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(main), Is.Empty);

    var got = Run(IrBasicWriter.Write(module));
    Assert.That(got, Is.EqualTo(expected));
  }
}
