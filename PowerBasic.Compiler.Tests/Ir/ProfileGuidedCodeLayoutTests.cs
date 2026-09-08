using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0274 — profile-guided code layout over current IR CFG edge counts.</summary>
[TestFixture]
public sealed class ProfileGuidedCodeLayoutTests {

  [Test]
  public void Diamond_GivenHotTruePath_ThenHotPathBecomesContiguous() {
    var (function, entry, cold, hot, join) = Diamond();

    var changes = ProfileGuidedCodeLayout.Run(function, [
      new(entry, hot, 1000),
      new(entry, cold, 10),
      new(hot, join, 1000),
      new(cold, join, 10),
    ]);

    Assert.Multiple(() => {
      Assert.That(changes, Is.GreaterThan(0));
      Assert.That(function.Blocks.Select(block => block.Label), Is.EqualTo(new[] { "entry", "hot", "join", "cold" }));
      Assert.That(function.Entry, Is.SameAs(entry), "layout may never move the function entry");
      Assert.That(IrVerifier.Verify(function), Is.Empty, "layout changes physical order, not CFG semantics");
    });
  }

  [Test]
  public void Loop_GivenHotBackedgeRegion_ThenDominatorsStillPrecedeTheirChildren() {
    var condition = new IrArgument(IrType.I1, 0, "again");
    var function = new IrFunction("loop", IrType.Void, [condition]);
    var entry = function.CreateBlock("entry");
    var exit = function.CreateBlock("exit");
    var body = function.CreateBlock("body");
    var header = function.CreateBlock("header");
    entry.Append(new IrBr(header));
    header.Append(new IrCondBr(condition, body, exit));
    body.Append(new IrBr(header));
    exit.Append(new IrRet());

    ProfileGuidedCodeLayout.Run(function, [
      new(entry, header, 100),
      new(header, body, 1000),
      new(body, header, 900),
      new(header, exit, 100),
    ]);

    Assert.Multiple(() => {
      Assert.That(function.Blocks.Select(block => block.Label), Is.EqualTo(new[] { "entry", "header", "body", "exit" }));
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  [Test]
  public void Function_GivenUnreachableBlocks_ThenTheirRelativeOrderIsPreserved() {
    var (function, entry, cold, hot, join) = Diamond();
    var deadFirst = function.CreateBlock("dead.first");
    var deadSecond = function.CreateBlock("dead.second");
    deadFirst.Append(new IrRet());
    deadSecond.Append(new IrRet());

    ProfileGuidedCodeLayout.Run(function, [
      new(entry, hot, 1000),
      new(entry, cold, 1),
      new(hot, join, 1000),
      new(cold, join, 1),
    ]);

    Assert.Multiple(() => {
      Assert.That(function.Blocks.Select(block => block.Label),
        Is.EqualTo(new[] { "entry", "hot", "join", "cold", "dead.first", "dead.second" }));
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  [Test]
  public void Function_GivenNoObservedEdges_ThenLayoutIsUnchanged() {
    var (function, _, _, _, _) = Diamond();
    var before = function.Blocks.ToArray();

    var changes = ProfileGuidedCodeLayout.Run(function, []);

    Assert.Multiple(() => {
      Assert.That(changes, Is.Zero);
      Assert.That(function.Blocks, Is.EqualTo(before));
    });
  }

  [Test]
  public void Function_GivenOnlyZeroCounts_ThenLayoutIsUnchanged() {
    var (function, entry, cold, hot, join) = Diamond();
    var before = function.Blocks.ToArray();

    var changes = ProfileGuidedCodeLayout.Run(function, [
      new(entry, hot, 0),
      new(entry, cold, 0),
      new(hot, join, 0),
    ]);

    Assert.Multiple(() => {
      Assert.That(changes, Is.Zero);
      Assert.That(function.Blocks, Is.EqualTo(before));
    });
  }

  [TestCase(true, false)]
  [TestCase(false, true)]
  public void Function_GivenOpaqueControlFlow_ThenLayoutIsUnchanged(bool hasErrorHandler, bool hasInlineAsm) {
    var (function, entry, cold, hot, join) = Diamond();
    function.HasErrorHandler = hasErrorHandler;
    function.HasInlineAsm = hasInlineAsm;
    var before = function.Blocks.ToArray();

    var changes = ProfileGuidedCodeLayout.Run(function, [
      new(entry, hot, 1000),
      new(entry, cold, 1),
      new(hot, join, 1000),
      new(cold, join, 1),
    ]);

    Assert.Multiple(() => {
      Assert.That(changes, Is.Zero);
      Assert.That(function.Blocks, Is.EqualTo(before));
    });
  }

  [Test]
  public void Function_GivenDuplicateEdgeSamples_ThenCountsAreAccumulatedWithoutOverflow() {
    var (function, entry, cold, hot, join) = Diamond();

    ProfileGuidedCodeLayout.Run(function, [
      new(entry, hot, ulong.MaxValue),
      new(entry, hot, 1),
      new(entry, cold, 2),
      new(hot, join, ulong.MaxValue),
      new(cold, join, 2),
    ]);

    Assert.That(function.Blocks.Select(block => block.Label), Is.EqualTo(new[] { "entry", "hot", "join", "cold" }));
  }

  [Test]
  public void Function_GivenAProfileEdgeOutsideTheCurrentCfg_ThenItIsRejected() {
    var (function, entry, _, hot, join) = Diamond();

    Assert.That(
      () => ProfileGuidedCodeLayout.Run(function, [new(entry, join, 1)]),
      Throws.ArgumentException.With.Message.Contains("current CFG"));
    Assert.That(hot.Parent, Is.SameAs(function));
  }

  [Test]
  public void LlvmEmitter_GivenProfileLayout_ThenItEmitsThatPhysicalBlockOrder() {
    var (function, entry, cold, hot, join) = Diamond();
    ProfileGuidedCodeLayout.Run(function, [
      new(entry, hot, 1000),
      new(entry, cold, 1),
      new(hot, join, 1000),
      new(cold, join, 1),
    ]);

    var llvm = LlvmEmitter.Emit(function);

    Assert.Multiple(() => {
      Assert.That(llvm.IndexOf("hot:\n", StringComparison.Ordinal), Is.LessThan(llvm.IndexOf("join:\n", StringComparison.Ordinal)));
      Assert.That(llvm.IndexOf("join:\n", StringComparison.Ordinal), Is.LessThan(llvm.IndexOf("cold:\n", StringComparison.Ordinal)));
    });
  }

  private static (IrFunction Function, IrBasicBlock Entry, IrBasicBlock Cold, IrBasicBlock Hot, IrBasicBlock Join) Diamond() {
    var condition = new IrArgument(IrType.I1, 0, "condition");
    var function = new IrFunction("diamond", IrType.Void, [condition]);
    var entry = function.CreateBlock("entry");
    var cold = function.CreateBlock("cold");
    var hot = function.CreateBlock("hot");
    var join = function.CreateBlock("join");
    entry.Append(new IrCondBr(condition, hot, cold));
    cold.Append(new IrBr(join));
    hot.Append(new IrBr(join));
    join.Append(new IrRet());
    return (function, entry, cold, hot, join);
  }
}
