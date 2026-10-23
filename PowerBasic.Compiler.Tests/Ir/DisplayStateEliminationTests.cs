using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class DisplayStateEliminationTests {

  private static IrModule Optimize(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var reason);
    Assert.That(module, Is.Not.Null, reason);
    var pipeline = IrMiddleEndPipeline.Standard();
    pipeline.VerifyEachPass = true;
    pipeline.RunOnModule(module!);
    return module!;
  }

  private static int CountCalls(IrModule module, string name) => module.Functions
    .SelectMany(function => function.Blocks).SelectMany(block => block.Instructions)
    .OfType<IrCall>().Count(call => call.Callee is IrFunction function && function.Name == name);

  private static Cpu8086 Execute(string source, bool optimize) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    return Cpu8086.Run(image);
  }

  [TestCase("CLS:LOCATE 3,2:CLS", 1, 0, 0, 0)]
  [TestCase("LOCATE 3,2:LOCATE 5,6", 0, 1, 0, 0)]
  [TestCase("SCREEN 12:SCREEN 12", 0, 0, 1, 0)]
  [TestCase("SCREEN 0:SCREEN 0:SCREEN 0", 0, 0, 1, 0)]
  public void Optimize_GivenOverwrittenDisplayState_ThenKeepsOnlyTheFinalEffects(
      string source, int clears, int locates, int modes, int pixels) {
    var module = Optimize(source + "\nEND");
    Assert.Multiple(() => {
      Assert.That(CountCalls(module, "rt_cls"), Is.EqualTo(clears));
      Assert.That(CountCalls(module, "rt_locate"), Is.EqualTo(locates));
      Assert.That(CountCalls(module, "rt_screen_mode"), Is.EqualTo(modes));
      Assert.That(CountCalls(module, "rt_pset"), Is.EqualTo(pixels));
    });
  }

  [TestCase("CLS:PRINT \"seen\":CLS", "rt_cls", 2)]
  [TestCase("LOCATE 3,2:Q%=CSRLIN:CLS:PRINT Q%", "rt_locate", 1)]
  [TestCase("LOCATE 3,2:LOCATE ,5", "rt_locate", 2)]
  [TestCase("LOCATE 99,2:CLS", "rt_locate", 1)]
  [TestCase("LOCATE 3,2:PRINT \"observed\":LOCATE 5,6", "rt_locate", 2)]
  [TestCase("LOCATE 3,2:A%=PEEK(0):CLS:PRINT A%", "rt_locate", 1)]
  // Genuine QB 4.5 keeps the cursor when SCREEN 0 selects its already-active text mode.
  [TestCase("LOCATE 3,2:SCREEN 0", "rt_locate", 1)]
  // A mode can fail (SCREEN 4 raises Error 5 on VGA); it is not an unconditional display kill.
  [TestCase("SCREEN 4:SCREEN 12", "rt_screen_mode", 2)]
  [TestCase("SCREEN 12:SCREEN 0:SCREEN 12", "rt_screen_mode", 3)]
  [TestCase("SCREEN 12:CLS:SCREEN 12", "rt_screen_mode", 2)]
  [TestCase("SCREEN 12:LOCATE 3,2:SCREEN 12", "rt_screen_mode", 2)]
  [TestCase("SCREEN 12:PRINT \"seen\":SCREEN 12", "rt_screen_mode", 2)]
  [TestCase("SCREEN 12:PSET (1,1):SCREEN 12", "rt_screen_mode", 2)]
  [TestCase("SCREEN 12:POKE 0,1:SCREEN 12", "rt_screen_mode", 2)]
  [TestCase("SCREEN 12:CLS:SCREEN 0", "rt_screen_mode", 2)]
  [TestCase("SCREEN 12:CLS:SCREEN 0", "rt_cls", 1)]
  [TestCase("SCREEN 4:PSET (1,1):SCREEN 12", "rt_screen_mode", 2)]
  [TestCase("SCREEN 4:PSET (1,1):SCREEN 12", "rt_pset", 1)]
  [TestCase("SCREEN 12:PSET (1,1):Q%=POINT(1,1):SCREEN 0:PRINT Q%", "rt_screen_mode", 2)]
  public void Optimize_GivenAnObserverOrPartialOverwrite_ThenPreservesTheObservedState(
      string source, string callee, int calls) {
    Assert.That(CountCalls(Optimize(source + "\nEND"), callee), Is.EqualTo(calls));
  }

  [TestCase(false, false)]
  [TestCase(true, false)]
  [TestCase(false, true)]
  public void Run_GivenRepeatedModeAcrossPureArithmetic_ThenKeepsTheFirstCallUnlessControlIsHidden(
      bool hasErrorHandler, bool hasInlineAsm) {
    var function = new IrFunction("test", IrType.Void) {
      HasErrorHandler = hasErrorHandler, HasInlineAsm = hasInlineAsm
    };
    var screen = new IrFunction("rt_screen_mode", IrType.Void, [new IrArgument(IrType.I16, 0)]);
    var block = function.CreateBlock("entry");
    var mode = new IrConstantInt(IrType.I16, 12);
    var first = block.Append(new IrCall(IrType.Void, screen, [mode]));
    block.Append(new IrBinary(IrBinaryOp.Add, mode, mode));
    var second = block.Append(new IrCall(IrType.Void, screen, [mode]));
    block.Append(new IrRet());

    var removed = ConsoleSetterCoalescing.Run(function);

    Assert.Multiple(() => {
      Assert.That(removed, Is.EqualTo(hasErrorHandler || hasInlineAsm ? 0 : 1));
      Assert.That(block.Instructions, Does.Contain(first), "the first potentially failing request must remain");
      Assert.That(block.Instructions.Contains(second), Is.EqualTo(hasErrorHandler || hasInlineAsm));
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  [TestCase("call")]
  [TestCase("dynamic")]
  [TestCase("block")]
  [TestCase("divide")]
  public void Run_GivenAModeKnowledgeBarrier_ThenKeepsBothRequests(string barrier) {
    var argument = new IrArgument(IrType.I16, 0);
    var function = new IrFunction("test", IrType.Void, [argument]);
    var screen = new IrFunction("rt_screen_mode", IrType.Void, [new IrArgument(IrType.I16, 0)]);
    var block = function.CreateBlock("entry");
    var mode = new IrConstantInt(IrType.I16, 12);
    var first = block.Append(new IrCall(IrType.Void, screen, [mode]));
    switch (barrier) {
      case "call":
        block.Append(new IrCall(IrType.Void, new IrFunction("unknown", IrType.Void), []));
        break;
      case "dynamic":
        block.Append(new IrCall(IrType.Void, screen, [argument]));
        break;
      case "block":
        var next = function.CreateBlock("next");
        block.Append(new IrBr(next));
        block = next;
        break;
      case "divide":
        block.Append(new IrBinary(IrBinaryOp.SDiv, mode, argument));
        break;
    }
    var second = block.Append(new IrCall(IrType.Void, screen, [mode]));
    block.Append(new IrRet());

    Assert.Multiple(() => {
      Assert.That(ConsoleSetterCoalescing.Run(function), Is.Zero);
      Assert.That(first.Parent, Is.Not.Null);
      Assert.That(block.Instructions, Does.Contain(second));
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  [TestCase("PRINT \"old\":CLS:LOCATE 3,2:CLS:PRINT \"new\";")]
  [TestCase("LOCATE 3,2:LOCATE 5,6:PRINT \"position\";")]
  [TestCase("LOCATE 3,2:PRINT POS(0);CSRLIN:CLS:PRINT \"home\";")]
  [TestCase("SCREEN 0:SCREEN 0:PRINT \"same mode\";")]
  [TestCase("SCREEN 12:SCREEN 12:SCREEN 0:PRINT \"back\";")]
  public void Execute_GivenCoalescedSetters_ThenPreservesOutputScreenAndCursor(string source) {
    var plain = Execute(source + "\nEND", optimize: false);
    var optimized = Execute(source + "\nEND", optimize: true);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(plain.Output));
      Assert.That(optimized.Screen, Is.EqualTo(plain.Screen));
      Assert.That(optimized.Cursor, Is.EqualTo(plain.Cursor));
    });
  }
}
