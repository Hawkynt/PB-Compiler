using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Analysis;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class PixelRuntimeInliningTests {
  // PIXELS.TXT captured with QB 4.50 BC.EXE + BCOM45.LIB, DOSBox Staging 0.83.
  // BC.EXE SHA-256: DE21556C94B588BFCA06388A45F6E22362A7C9E153571F5A27C4F95B6740F5FA
  // BCOM45.LIB SHA-256: 5B1C7A6FBB102E3E47ACAA38349EFA9BF1DAE674E57F4D86D170E920086C8996
  private const string _ORACLE_SOURCE = """
    SCREEN 13
    FOR y%=0 TO 199:PSET (3,y%),7:NEXT
    FOR x%=0 TO 319:PSET (x%,199),9:NEXT
    OPEN "PIXELS.TXT" FOR OUTPUT AS #1
    PRINT #1,POINT(3,0);POINT(3,198);POINT(0,199);POINT(319,199)
    CLOSE #1
    SCREEN 0
    END
    """;

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36),
      Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    return model;
  }

  [TestCase("SCREEN 13:PSET (3,2),7", 1)]
  [TestCase("SCREEN 12:PSET (3,2),7", 0)]
  [TestCase("PSET (3,2),7", 0)]
  [TestCase("SCREEN 13:SCREEN VAL(COMMAND$):PSET (3,2),7", 0)]
  [TestCase("SCREEN 13:OUT &H3C4,4:PSET (3,2),7", 0)]
  [TestCase("SCREEN 13:FOR y%=0 TO 199:PSET (3,y%),7:NEXT", 1)]
  public void Optimize_GivenAProvenPackedMode_ThenInlinesItsAddressCalculation(string source, int writes) {
    var module = IrLowering.TryLowerModule(Bind(source + "\nEND"), out var reason);
    Assert.That(module, Is.Not.Null, reason);
    IrMiddleEndPipeline.RunNativeModule(module!, optimize: true, recoverIntegerArithmetic: true);
    var calls = module!.Functions.Where(function => function.Name == "main")
      .SelectMany(function => function.AllInstructions).OfType<IrCall>().ToArray();
    Assert.Multiple(() => {
      Assert.That(calls.Any(call => call.Callee is IrFunction { Name: "rt_pset_mode13_offset" }),
        Is.False, "the final lowering must remove the opaque pixel helper call");
      Assert.That(module.FindFunction("main")!.AllInstructions.OfType<IrStore>().Count(store =>
        store.Pointer is IrFarPtr { Segment: IrConstantInt { Value: -24576 } }),
        Is.EqualTo(writes), "each proven packed pixel must become exactly one segmented store");
      Assert.That(calls.Any(call => call.Callee is IrFunction { Name: "rt_pset_mode13" }), Is.False,
        "the ordinary inliner must expose the runtime body to loop optimization");
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [TestCase(true)]
  [TestCase(false)]
  public void Analyze_GivenAJoin_ThenRequiresAllPredecessorsToAgree(bool agrees) {
    var flag = new IrArgument(IrType.I1, 0);
    var function = new IrFunction("join", IrType.Void, [flag]);
    var screen = new IrFunction("rt_screen_mode", IrType.Void, [new IrArgument(IrType.I16, 0)]);
    var entry = function.CreateBlock("entry");
    var left = function.CreateBlock("left");
    var right = function.CreateBlock("right");
    var join = function.CreateBlock("join");
    entry.Append(new IrCondBr(flag, left, right));
    left.Append(new IrCall(IrType.Void, screen, [new IrConstantInt(IrType.I16, 13)]));
    left.Append(new IrBr(join));
    right.Append(new IrCall(IrType.Void, screen, [new IrConstantInt(IrType.I16, agrees ? 13 : 12)]));
    right.Append(new IrBr(join));
    var ret = join.Append(new IrRet());

    Assert.That(new IrAnalysisManager(function).Get(IrAnalyses.ScreenModes).Before(ret),
      Is.EqualTo(agrees ? (long?)13 : null));
  }

  [Test]
  public void Inline_GivenANonRuntimeCallingConvention_ThenPreservesTheOriginalCallAbi() {
    var module = new IrModule("abi");
    var function = module.AddFunction(new IrFunction("main", IrType.Void));
    var screen = module.AddFunction(new IrFunction("rt_screen_mode", IrType.Void, [new IrArgument(IrType.I16, 0)]));
    var pixel = module.AddFunction(new IrFunction("rt_pset", IrType.Void,
      Enumerable.Range(0, 3).Select(index => new IrArgument(IrType.I16, index))) { Convention = IrCallConvention.Cdecl });
    var block = function.CreateBlock("entry");
    block.Append(new IrCall(IrType.Void, screen, [new IrConstantInt(IrType.I16, 13)]));
    var call = block.Append(new IrCall(IrType.Void, pixel,
      [new IrConstantInt(IrType.I16, 1), new IrConstantInt(IrType.I16, 2), new IrConstantInt(IrType.I16, 3)],
      IrCallConvention.Cdecl));
    block.Append(new IrRet());

    Assert.Multiple(() => {
      Assert.That(PixelRuntimeInlining.Run(module), Is.Zero);
      Assert.That(call.Callee, Is.SameAs(pixel));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [TestCase(true)]
  [TestCase(false)]
  public void Analyze_GivenALoopBackedge_ThenWaitsForConvergenceBeforePublishingFacts(bool invalidates) {
    var flag = new IrArgument(IrType.I1, 0);
    var function = new IrFunction("loop", IrType.Void, [flag]);
    var screen = new IrFunction("rt_screen_mode", IrType.Void, [new IrArgument(IrType.I16, 0)]);
    var entry = function.CreateBlock("entry");
    var header = function.CreateBlock("header");
    var body = function.CreateBlock("body");
    var exit = function.CreateBlock("exit");
    entry.Append(new IrCall(IrType.Void, screen, [new IrConstantInt(IrType.I16, 13)]));
    entry.Append(new IrBr(header));
    var test = header.Append(new IrCondBr(flag, body, exit));
    if (invalidates)
      body.Append(new IrCall(IrType.Void, new IrFunction("opaque", IrType.Void), []));
    body.Append(new IrBr(header));
    exit.Append(new IrRet());

    Assert.That(new IrAnalysisManager(function).Get(IrAnalyses.ScreenModes).Before(test),
      Is.EqualTo(invalidates ? null : (long?)13));
  }

  [Test]
  public void Optimize_GivenAVerticalPixelLoop_ThenCarriesTheOffsetInsteadOfRecomputingItsScale() {
    var module = IrLowering.TryLowerModule(Bind("SCREEN 13\nFOR y%=0 TO 199:PSET (3,y%),7:NEXT\nEND"), out var reason);
    Assert.That(module, Is.Not.Null, reason);
    IrMiddleEndPipeline.RunNativeModule(module!, optimize: true, recoverIntegerArithmetic: true);
    var function = module!.FindFunction("main")!;
    var loops = new IrAnalysisManager(function).Get(IrAnalyses.Loops).Loops;
    Assert.That(loops, Is.Not.Empty, "do not hide the address calculation by fully unrolling 200 pixels");
    Assert.That(loops.SelectMany(loop => loop.Blocks).SelectMany(block => block.Instructions).OfType<IrBinary>()
      .Any(binary => binary.Op is IrBinaryOp.Mul or IrBinaryOp.Shl), Is.False, IrPrinter.Print(function));
  }

  [TestCase(false, false)]
  [TestCase(true, false)]
  [TestCase(false, true)]
  [TestCase(true, true)]
  public void Execute_GivenPackedPixelLoops_ThenMatchesTheGenuineQuickBasicCapture(bool optimize, bool com) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(_ORACLE_SOURCE, "T.BAS", Dialect.Qb45),
      "T.BAS", Dialect.Qb45), Dialect.Qb45);
    Assert.That(model.Errors, Is.Empty);
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = com ? generator.EmitCom() : generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    Assert.That(image.AsSpan(0, 2).SequenceEqual("MZ"u8), Is.EqualTo(!com));
    Assert.That(Cpu8086.Run(image).FileBytes("PIXELS.TXT"),
      Is.EqualTo(System.Text.Encoding.ASCII.GetBytes(" 7  7  9  9 \r\n")));
  }

  [TestCase(0, 16)]
  [TestCase(2, 32)]
  public void Optimize_GivenAdjacentConstantPixels_ThenPacksByteWritesForTheTarget(
      int cpuTier, int expectedWidth) {
    const string source = """
      SCREEN 13
      PSET (10,5), 1
      PSET (11,5), 2
      PSET (12,5), 3
      PSET (13,5), 4
      END
      """;
    var module = IrLowering.TryLowerModule(Bind(source), out var reason);
    Assert.That(module, Is.Not.Null, reason);
    IrMiddleEndPipeline.RunNativeModule(module!, optimize: true, recoverIntegerArithmetic: true,
      targetCost: new TargetCost((CpuTier)cpuTier, CostObjective.Balanced));
    var videoStores = module!.FindFunction("main")!.AllInstructions.OfType<IrStore>()
      .Where(store => store.Pointer is IrFarPtr { Segment: IrConstantInt { Value: -24576 } })
      .ToArray();
    Assert.Multiple(() => {
      Assert.That(videoStores.Select(store => store.Value.Type.Bits),
        Is.EqualTo(expectedWidth == 32 ? new[] { 32 } : new[] { 16, 16 }));
      Assert.That(videoStores.All(store => store.Value is IrConstantInt), Is.True);
      Assert.That(videoStores[0].Value is IrConstantInt value && value.Value ==
        (expectedWidth == 32 ? 0x04030201L : 0x0201L), Is.True);
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void Optimize_GivenPixelsAcrossARowBoundary_ThenKeepsSeparateByteStores() {
    var module = IrLowering.TryLowerModule(Bind("""
      SCREEN 13
      PSET (319,0),1
      PSET (0,1),2
      END
      """), out var reason);
    Assert.That(module, Is.Not.Null, reason);
    IrMiddleEndPipeline.RunNativeModule(module!, optimize: true, recoverIntegerArithmetic: true,
      targetCost: new TargetCost(CpuTier.I80386, CostObjective.Balanced));
    Assert.That(module!.FindFunction("main")!.AllInstructions.OfType<IrStore>().Count(store =>
      store.Pointer is IrFarPtr { Segment: IrConstantInt { Value: -24576 } }),
      Is.EqualTo(2));
  }

  [Test]
  public void Optimize_GivenAnObservablePointReadBetweenPixels_ThenDoesNotFuseWrites() {
    var module = IrLowering.TryLowerModule(Bind("""
      SCREEN 13
      PSET (10,5),1
      PRINT POINT(10,5)
      PSET (11,5),2
      END
      """), out var reason);
    Assert.That(module, Is.Not.Null, reason);
    IrMiddleEndPipeline.RunNativeModule(module!, optimize: true, recoverIntegerArithmetic: true,
      targetCost: new TargetCost(CpuTier.I80386, CostObjective.Balanced));
    Assert.That(module!.FindFunction("main")!.AllInstructions.OfType<IrStore>().Count(store =>
      store.Pointer is IrFarPtr { Segment: IrConstantInt { Value: -24576 } }),
      Is.EqualTo(2));
  }

  [Test]
  public void Optimize_GivenAProvenDefSeg_ThenPokeAndPsetShareThePackedWordWriter() {
    var module = IrLowering.TryLowerModule(Bind("""
      SCREEN 13
      DEF SEG = &HA000
      POKE 32160,1
      PSET (161,100),2
      END
      """), out var reason);
    Assert.That(module, Is.Not.Null, reason);
    IrMiddleEndPipeline.RunNativeModule(module!, optimize: true, recoverIntegerArithmetic: true,
      targetCost: new TargetCost(CpuTier.I8086, CostObjective.Balanced));
    var main = module!.FindFunction("main")!;
    var videoStores = main.AllInstructions.OfType<IrStore>()
      .Where(store => store.Pointer is IrFarPtr { Segment: IrConstantInt { Value: -24576 } })
      .ToArray();
    Assert.Multiple(() => {
      Assert.That(main.AllInstructions.OfType<IrCall>().Any(call =>
        call.Callee is IrFunction { Name: "rt_poke" }), Is.False);
      Assert.That(videoStores.Length, Is.EqualTo(1));
      Assert.That(videoStores[0].Value is IrConstantInt pixel && pixel.Value == 0x0201L,
        Is.True);
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [TestCase("&HB800", 32160)]
  [TestCase("&HA000", -536)]
  public void Optimize_GivenUncertifiedPoke_ThenPreservesRawRuntimeCall(
      string segment, int offset) {
    var module = IrLowering.TryLowerModule(Bind($"""
      SCREEN 13
      DEF SEG = {segment}
      POKE {offset},1
      END
      """), out var reason);
    Assert.That(module, Is.Not.Null, reason);
    IrMiddleEndPipeline.RunNativeModule(module!, optimize: true, recoverIntegerArithmetic: true);
    var main = module!.FindFunction("main")!;
    Assert.Multiple(() => {
      Assert.That(main.AllInstructions.OfType<IrCall>().Any(call =>
        call.Callee is IrFunction { Name: "rt_poke" }), Is.True);
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenProvenPokeAndPset_ThenPreservesPointReadback(bool optimize) {
    const string source = """
      SCREEN 13
      DEF SEG = &HA000
      POKE 32160,1
      PSET (161,100),2
      PRINT POINT(160,100);POINT(161,100)
      END
      """;
    var generator = new CodeGenerator(Bind(source)) { Optimize = optimize, Container = DosContainer.Exe };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    Assert.That(Cpu8086.Run(image).Output, Does.Contain("1  2"));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenPackedPixelLoops_ThenMatchesTheGenericRuntime(bool com) {
    const string source = """
      SCREEN 13
      FOR y%=0 TO 199:PSET (3,y%),7:NEXT
      FOR x%=0 TO 319:PSET (x%,199),9:NEXT
      PRINT POINT(3,0);POINT(3,198);POINT(0,199);POINT(319,199)
      END
      """;
    string Execute(bool optimize) {
      var generator = new CodeGenerator(Bind(source)) { Optimize = optimize, Container = DosContainer.Exe };
      var image = com ? generator.EmitCom() : generator.EmitExecutable();
      Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
      Assert.That(generator.BackendDeclines, Is.Empty);
      Assert.That(image.AsSpan(0, 2).SequenceEqual("MZ"u8), Is.EqualTo(!com));
      return Cpu8086.Run(image).Output;
    }
    Assert.That(Execute(true), Is.EqualTo(Execute(false)).And.Contain("7  7  9  9"));
  }
}
