using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Regression tests for O0291 handle ownership elision.</summary>
[TestFixture]
public sealed class O0291HandleOwnershipElisionTests {

  [Test]
  public void Run_GivenAnUnusedOwnedCopyAndMatchingRelease_WhenRun_ThenBothDisappear() {
    var (dup, free, _) = Runtime();
    var source = new IrArgument(IrType.Ptr, 0, "source");
    var fn = new IrFunction("f", IrType.Void, [source]);
    var entry = fn.CreateBlock("entry");
    var b = new IrBuilder(entry);
    var copy = b.Call(IrType.Ptr, dup, source);
    var release = b.Call(IrType.Void, free, copy);
    b.Call(IrType.Void, free, source);
    b.Ret();

    var changed = HandleOwnershipElision.Run(fn);

    Assert.Multiple(() => {
      Assert.That(changed, Is.EqualTo(1));
      Assert.That(copy.Parent, Is.Null);
      Assert.That(release.Parent, Is.Null);
      Assert.That(entry.Instructions.OfType<IrCall>().Select(CallName), Is.EqualTo(new[] { "rt_str_free" }));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void Run_GivenBorrowReadsInsideANestedOwnershipLifetime_WhenRun_ThenTheyBorrowTheOuterOwner() {
    var (dup, free, consume) = Runtime();
    var source = new IrArgument(IrType.Ptr, 0, "source");
    var fn = new IrFunction("f", IrType.Void, [source]);
    var entry = fn.CreateBlock("entry");
    var b = new IrBuilder(entry);
    var copy = b.Call(IrType.Ptr, dup, source);
    var borrow = b.Call(IrType.Ptr, dup, copy);
    b.Call(IrType.I32, consume, borrow);
    var release = b.Call(IrType.Void, free, copy);
    b.Call(IrType.Void, free, source);
    b.Ret();

    var changed = HandleOwnershipElision.Run(fn);

    Assert.Multiple(() => {
      Assert.That(changed, Is.EqualTo(1));
      Assert.That(borrow.GetOperand(1), Is.SameAs(source));
      Assert.That(copy.Parent, Is.Null);
      Assert.That(release.Parent, Is.Null);
      Assert.That(entry.Instructions.OfType<IrCall>().Select(CallName),
        Is.EqualTo(new[] { "rt_str_dup", "rt_str_len", "rt_str_free" }));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void Run_GivenTheSourceDiesBeforeTheCopiedOwner_WhenRun_ThenThePairIsKept() {
    var (dup, free, consume) = Runtime();
    var source = new IrArgument(IrType.Ptr, 0, "source");
    var fn = new IrFunction("f", IrType.Void, [source]);
    var entry = fn.CreateBlock("entry");
    var b = new IrBuilder(entry);
    var copy = b.Call(IrType.Ptr, dup, source);
    var borrow = b.Call(IrType.Ptr, dup, copy);
    b.Call(IrType.I32, consume, borrow);
    b.Call(IrType.Void, free, source);
    b.Call(IrType.Void, free, copy);
    b.Ret();

    Assert.That(HandleOwnershipElision.Run(fn), Is.Zero);
    Assert.That(borrow.GetOperand(1), Is.SameAs(copy));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Run_GivenTheCopiedOwnerHasAnOpaqueUse_WhenRun_ThenThePairIsKept() {
    var (dup, free, _) = Runtime();
    var observer = new IrFunction("observe", IrType.Void, [new IrArgument(IrType.Ptr, 0)]);
    var source = new IrArgument(IrType.Ptr, 0, "source");
    var fn = new IrFunction("f", IrType.Void, [source]);
    var entry = fn.CreateBlock("entry");
    var b = new IrBuilder(entry);
    var copy = b.Call(IrType.Ptr, dup, source);
    b.Call(IrType.Void, observer, copy);
    b.Call(IrType.Void, free, copy);
    b.Call(IrType.Void, free, source);
    b.Ret();

    Assert.That(HandleOwnershipElision.Run(fn), Is.Zero);
    Assert.That(copy.Parent, Is.SameAs(entry));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Run_GivenTheSourceWasLoadedFromAliasableStorage_WhenRun_ThenThePairIsKept() {
    var (dup, free, consume) = Runtime();
    var fn = new IrFunction("f", IrType.Void, []);
    var entry = fn.CreateBlock("entry");
    var b = new IrBuilder(entry);
    var slot = b.Alloca(IrType.Ptr);
    var source = b.Load(IrType.Ptr, slot);
    var copy = b.Call(IrType.Ptr, dup, source);
    var borrow = b.Call(IrType.Ptr, dup, copy);
    b.Call(IrType.I32, consume, borrow);
    b.Call(IrType.Void, free, copy);
    b.Call(IrType.Void, free, source);
    b.Ret();

    Assert.That(HandleOwnershipElision.Run(fn), Is.Zero);
    Assert.That(borrow.GetOperand(1), Is.SameAs(copy));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Run_GivenTheMatchingReleaseIsInAnotherBlock_WhenRun_ThenThePairIsKept() {
    var (dup, free, _) = Runtime();
    var source = new IrArgument(IrType.Ptr, 0, "source");
    var fn = new IrFunction("f", IrType.Void, [source]);
    var entry = fn.CreateBlock("entry");
    var exit = fn.CreateBlock("exit");
    var b = new IrBuilder(entry);
    var copy = b.Call(IrType.Ptr, dup, source);
    b.Br(exit);
    b.Position(exit);
    b.Call(IrType.Void, free, copy);
    b.Call(IrType.Void, free, source);
    b.Ret();

    Assert.That(HandleOwnershipElision.Run(fn), Is.Zero);
    Assert.That(copy.Parent, Is.SameAs(entry));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Run_GivenTheCatalogueAssignmentShapeAfterLowering_WhenRun_ThenOneOwnershipLayerDisappears() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize("""
      DIM a AS STRING
      DIM b AS STRING
      b = "source"
      a = b
      PRINT a
      END
      """, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    var main = module!.FindFunction("main")!;
    Mem2Reg.Run(main);
    var before = main.AllInstructions.OfType<IrCall>().Count(call => CallName(call) == "rt_str_dup");

    var changed = HandleOwnershipElision.Run(main);
    var after = main.AllInstructions.OfType<IrCall>().Count(call => CallName(call) == "rt_str_dup");

    Assert.Multiple(() => {
      Assert.That(changed, Is.GreaterThanOrEqualTo(1));
      Assert.That(after, Is.EqualTo(before - 1));
      Assert.That(IrVerifier.Verify(main), Is.Empty);
    });
  }

  [Test]
  public void Standard_GivenAnEligibleOwnershipLifetime_WhenRun_ThenO0291IsEnabled() {
    var (dup, free, consume) = Runtime();
    var source = new IrArgument(IrType.Ptr, 0, "source");
    var fn = new IrFunction("f", IrType.Void, [source]);
    var entry = fn.CreateBlock("entry");
    var b = new IrBuilder(entry);
    var copy = b.Call(IrType.Ptr, dup, source);
    var borrow = b.Call(IrType.Ptr, dup, copy);
    b.Call(IrType.I32, consume, borrow);
    b.Call(IrType.Void, free, copy);
    b.Call(IrType.Void, free, source);
    b.Ret();

    var passes = IrPassManager.Standard(includeModulePasses: false);
    passes.VerifyEachPass = true;
    passes.RunToFixpoint(fn);

    Assert.That(copy.Parent, Is.Null);
    Assert.That(borrow.GetOperand(1), Is.SameAs(source));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  private static (IrFunction Dup, IrFunction Free, IrFunction Consume) Runtime() => (
    new IrFunction("rt_str_dup", IrType.Ptr, [new IrArgument(IrType.Ptr, 0)]),
    new IrFunction("rt_str_free", IrType.Void, [new IrArgument(IrType.Ptr, 0)]),
    new IrFunction("rt_str_len", IrType.I32, [new IrArgument(IrType.Ptr, 0)])
  );

  private static string CallName(IrCall call) => ((IrFunction)call.Callee).Name;
}
