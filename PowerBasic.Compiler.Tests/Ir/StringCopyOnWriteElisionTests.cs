using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0293 — statically delayed duplication for non-escaping SSA string ownership lifetimes.</summary>
[TestFixture]
public sealed class StringCopyOnWriteElisionTests {

  [Test]
  public void Run_GivenTheCopyDiesFirst_ThenTheAssignmentCopyAndEarlyReleaseDisappear() {
    var fixture = BuildFixture();
    var source = fixture.Function.Parameters[0];
    var copy = fixture.Entry.Append(new IrCall(IrType.Ptr, fixture.Dup, [source]));
    var copyRead = fixture.Entry.Append(new IrCall(IrType.Ptr, fixture.Dup, [copy]));
    fixture.Entry.Append(new IrCall(IrType.Void, fixture.Sink, [copyRead]));
    var copyFree = fixture.Entry.Append(new IrCall(IrType.Void, fixture.Free, [copy]));
    var sourceRead = fixture.Entry.Append(new IrCall(IrType.Ptr, fixture.Dup, [source]));
    fixture.Entry.Append(new IrCall(IrType.Void, fixture.Sink, [sourceRead]));
    var sourceFree = fixture.Entry.Append(new IrCall(IrType.Void, fixture.Free, [source]));
    fixture.Entry.Append(new IrRet());

    Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
    Assert.That(StringCopyOnWriteElision.Run(fixture.Function), Is.EqualTo(1));

    Assert.Multiple(() => {
      Assert.That(copy.Parent, Is.Null, "the eager assignment duplicate must be gone");
      Assert.That(copyFree.Parent, Is.Null, "the first owner death only drops an alias, not the shared block");
      Assert.That(sourceFree.Parent, Is.SameAs(fixture.Entry), "the last owner still releases the block");
      Assert.That(copyRead.GetOperand(1), Is.SameAs(source));
      Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
    });
  }

  [Test]
  public void Run_GivenTheSourceDiesFirst_ThenTheCopyOwnsTheSharedHandleUntilItsLaterRelease() {
    var fixture = BuildFixture();
    var source = fixture.Function.Parameters[0];
    var copy = fixture.Entry.Append(new IrCall(IrType.Ptr, fixture.Dup, [source]));
    var sourceRead = fixture.Entry.Append(new IrCall(IrType.Ptr, fixture.Dup, [source]));
    fixture.Entry.Append(new IrCall(IrType.Void, fixture.Sink, [sourceRead]));
    var sourceFree = fixture.Entry.Append(new IrCall(IrType.Void, fixture.Free, [source]));
    var copyRead = fixture.Entry.Append(new IrCall(IrType.Ptr, fixture.Dup, [copy]));
    fixture.Entry.Append(new IrCall(IrType.Void, fixture.Sink, [copyRead]));
    var copyFree = fixture.Entry.Append(new IrCall(IrType.Void, fixture.Free, [copy]));
    fixture.Entry.Append(new IrRet());

    Assert.That(StringCopyOnWriteElision.Run(fixture.Function), Is.EqualTo(1));

    Assert.Multiple(() => {
      Assert.That(sourceFree.Parent, Is.Null, "the earlier source death must not free storage still named by the copy");
      Assert.That(copyFree.Parent, Is.SameAs(fixture.Entry));
      Assert.That(copyFree.GetOperand(1), Is.SameAs(source), "the later owner releases the shared handle exactly once");
      Assert.That(copyRead.GetOperand(1), Is.SameAs(source));
      Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
    });
  }

  [Test]
  public void Run_GivenTheCopiedHandleEscapesToAnOpaqueConsumer_ThenItIsNotShared() {
    var fixture = BuildFixture();
    var source = fixture.Function.Parameters[0];
    var copy = fixture.Entry.Append(new IrCall(IrType.Ptr, fixture.Dup, [source]));
    fixture.Entry.Append(new IrCall(IrType.Void, fixture.Sink, [copy]));
    fixture.Entry.Append(new IrCall(IrType.Void, fixture.Free, [copy]));
    fixture.Entry.Append(new IrCall(IrType.Void, fixture.Free, [source]));
    fixture.Entry.Append(new IrRet());

    Assert.Multiple(() => {
      Assert.That(StringCopyOnWriteElision.Run(fixture.Function), Is.Zero);
      Assert.That(copy.Parent, Is.SameAs(fixture.Entry));
      Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
    });
  }

  [Test]
  public void Run_GivenTheTwoOwnershipDeathsAreInAnotherBlock_ThenTheLocalProofDeclines() {
    var fixture = BuildFixture();
    var exit = fixture.Function.CreateBlock("exit");
    var source = fixture.Function.Parameters[0];
    var copy = fixture.Entry.Append(new IrCall(IrType.Ptr, fixture.Dup, [source]));
    fixture.Entry.Append(new IrBr(exit));
    exit.Append(new IrCall(IrType.Void, fixture.Free, [source]));
    exit.Append(new IrCall(IrType.Void, fixture.Free, [copy]));
    exit.Append(new IrRet());

    Assert.Multiple(() => {
      Assert.That(StringCopyOnWriteElision.Run(fixture.Function), Is.Zero,
        "cross-block ownership needs a path-sensitive lifetime proof, not a guessed ordering");
      Assert.That(copy.Parent, Is.SameAs(fixture.Entry));
      Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
    });
  }

  [Test]
  public void LoweredAssignment_GivenTheDestinationMutatesLater_ThenDuplicationIsDelayedToThatMutation() {
    var unit = Parser.Parse(Lexer.Tokenize("""
      a$ = "one"
      b$ = a$
      b$ = b$ + "x"
      PRINT a$
      PRINT b$
      END
      """, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var module = IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35));

    Assert.That(module, Is.Not.Null);
    var main = module!.FindFunction("main")!;
    Assert.That(Mem2Reg.Run(main), Is.GreaterThan(0));
    Assert.That(DupCalls(main), Has.Count.EqualTo(4), "assignment, mutation read and two PRINT reads start owned");

    Assert.That(StringCopyOnWriteElision.Run(main), Is.EqualTo(1));

    Assert.Multiple(() => {
      Assert.That(DupCalls(main), Has.Count.EqualTo(3), "the mutation borrow remains; only the assignment copy is delayed away");
      Assert.That(IrVerifier.Verify(main), Is.Empty);
    });
  }

  private static List<IrCall> DupCalls(IrFunction function)
    => function.AllInstructions.OfType<IrCall>()
      .Where(call => call.Callee is IrFunction { Name: "rt_str_dup" })
      .ToList();

  private static Fixture BuildFixture() {
    var module = new IrModule("O0293");
    var dup = module.AddFunction(new IrFunction("rt_str_dup", IrType.Ptr,
      [new IrArgument(IrType.Ptr, 0, "value")]));
    var free = module.AddFunction(new IrFunction("rt_str_free", IrType.Void,
      [new IrArgument(IrType.Ptr, 0, "value")]));
    var sink = module.AddFunction(new IrFunction("rt_sink", IrType.Void,
      [new IrArgument(IrType.Ptr, 0, "value")]));
    var function = module.AddFunction(new IrFunction("test", IrType.Void,
      [new IrArgument(IrType.Ptr, 0, "source")]));
    var entry = function.CreateBlock("entry");
    return new(module, function, dup, free, sink, entry);
  }

  private sealed record Fixture(
    IrModule Module,
    IrFunction Function,
    IrFunction Dup,
    IrFunction Free,
    IrFunction Sink,
    IrBasicBlock Entry);
}
