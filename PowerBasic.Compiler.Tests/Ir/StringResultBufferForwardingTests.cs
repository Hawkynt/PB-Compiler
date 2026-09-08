using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// O0295 is intentionally represented as owned-handle forwarding rather than an sret-style hidden
/// destination. A dynamic STRING result is already the allocation handle the caller wants to own, so
/// copying it into a second result buffer would add work instead of removing it. These tests pin that
/// contract and the alias-sensitive evaluation order around it.
/// </summary>
[TestFixture]
public sealed class StringResultBufferForwardingTests {

  private static IrModule Lower(string source, bool optimize = false) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));

    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    if (optimize)
      IrPassManager.Standard().RunOnModule(module!);

    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    return module!;
  }

  private static string? CalleeName(IrCall call) => (call.Callee as IrFunction)?.Name;

  [Test]
  public void Lower_GivenAssignedStringFunctionResult_ThenCallerAdoptsTheOwnedHandleWithoutCopyingIt() {
    var module = Lower("""
      DECLARE FUNCTION Make$()
      DIM dst AS STRING
      dst = "old"
      dst = Make$()
      PRINT dst
      END
      FUNCTION Make$()
        Make$ = "made"
      END FUNCTION
      """);

    var main = module.FindFunction("main")!;
    var make = module.FindFunction("Make")!;
    var call = main.AllInstructions.OfType<IrCall>().Single(candidate => CalleeName(candidate) == "Make");
    var store = call.Users.OfType<IrStore>().Single();

    Assert.Multiple(() => {
      Assert.That(call.Type, Is.EqualTo(IrType.Ptr), "a dynamic STRING result is its owned runtime handle");
      Assert.That(make.ReturnType, Is.EqualTo(IrType.Ptr));
      Assert.That(make.Parameters, Is.Empty, "O0295 must not materialize an sret destination parameter");
      Assert.That(call.Users, Has.Count.EqualTo(1), "the returned ownership must flow straight to the assignment");
      Assert.That(store.Value, Is.SameAs(call));
      Assert.That(store.Pointer, Is.TypeOf<IrAlloca>());
      Assert.That(main.AllInstructions.OfType<IrCall>()
        .Where(candidate => CalleeName(candidate) == "rt_str_dup")
        .SelectMany(candidate => candidate.Args), Does.Not.Contain(call),
        "duplicating an owned function result would recreate the handoff O0295 is meant to remove");
    });
  }

  [Test]
  public void Optimize_GivenStringFunctionResult_ThenMem2RegEliminatesBothHandleCells() {
    var module = Lower("""
      DECLARE FUNCTION Make$()
      DIM dst AS STRING
      dst = Make$()
      PRINT dst
      END
      FUNCTION Make$()
        Make$ = "made"
      END FUNCTION
      """, optimize: true);

    var main = module.FindFunction("main")!;
    var make = module.FindFunction("Make")!;

    Assert.Multiple(() => {
      Assert.That(make.Parameters, Is.Empty, "forwarding the handle must not make the caller destination address escape");
      Assert.That(make.AllInstructions.OfType<IrAlloca>(), Is.Empty,
        "the syntactic function-result cell should promote to the returned SSA handle");
      Assert.That(main.AllInstructions.OfType<IrAlloca>(), Is.Empty,
        "the caller destination should remain promotable instead of becoming an address-taken sret cell");
      Assert.That(make.AllInstructions.OfType<IrRet>(), Has.All.Matches<IrRet>(ret => ret.Value is not IrLoad),
        "the optimized return should carry the producer value directly, not reload a result buffer");
    });
  }

  [Test]
  public void Lower_GivenDestinationAlsoUsedAsArgument_ThenReadPrecedesCallAndReplacementFreeFollowsIt() {
    var module = Lower("""
      DECLARE FUNCTION Echo$(BYVAL value$)
      DIM dst AS STRING
      dst = "old"
      dst = Echo$(dst)
      PRINT dst
      END
      FUNCTION Echo$(BYVAL value$)
        Echo$ = value$
      END FUNCTION
      """);

    var calls = module.FindFunction("main")!.AllInstructions.OfType<IrCall>().ToList();
    var echoIndex = calls.FindIndex(call => CalleeName(call) == "Echo");

    Assert.That(echoIndex, Is.GreaterThan(0));
    Assert.Multiple(() => {
      Assert.That(CalleeName(calls[echoIndex - 1]), Is.EqualTo("rt_str_dup"),
        "the argument must own a snapshot of dst before the call can replace dst");
      Assert.That(echoIndex + 1, Is.LessThan(calls.Count));
      Assert.That(CalleeName(calls[echoIndex + 1]), Is.EqualTo("rt_str_free"),
        "the old destination may be released only after every aliased argument has been evaluated");
    });
  }
}
