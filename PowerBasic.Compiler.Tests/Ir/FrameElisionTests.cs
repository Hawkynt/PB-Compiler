using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class FrameElisionTests {

  [Test]
  public void IsCandidate_GivenScalarLocalPromotedByMem2Reg_ThenBecomesFrameFree() {
    var function = new IrFunction("F", IrType.I16);
    var entry = function.CreateBlock("entry");
    var local = entry.Append(new IrAlloca(IrType.I16));
    entry.Append(new IrStore(new IrConstantInt(IrType.I16, 7), local));
    entry.Append(new IrRet(entry.Append(new IrLoad(IrType.I16, local))));

    Assert.That(FrameElision.IsCandidate(function), Is.False, "the surviving alloca is fixed frame state");

    Mem2Reg.Run(function);

    Assert.Multiple(() => {
      Assert.That(FrameElision.IsCandidate(function), Is.True);
      Assert.That(function.AllInstructions.OfType<IrAlloca>(), Is.Empty);
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  [Test]
  public void IsCandidate_GivenSurvivingArrayStorage_ThenRequiresFrame() {
    var function = new IrFunction("F", IrType.Void);
    var entry = function.CreateBlock("entry");
    entry.Append(new IrAlloca(IrType.I16, 8));
    entry.Append(new IrRet());

    Assert.That(FrameElision.IsCandidate(function), Is.False);
  }

  [Test]
  public void IsCandidate_GivenOnlySsaParameterState_ThenDoesNotInventATargetAbiRestriction() {
    var argument = new IrArgument(IrType.I16, 0);
    var function = new IrFunction("F", IrType.I16, [argument]);
    function.CreateBlock("entry").Append(new IrRet(argument));

    Assert.That(FrameElision.IsCandidate(function), Is.True,
      "whether a parameter needs BP is an ABI/emitter question, not an SSA-frame question");
  }

  [TestCase(true, false)]
  [TestCase(false, true)]
  public void IsCandidate_GivenOpaqueControlFlowState_ThenDeclines(bool hasErrorHandler, bool hasInlineAsm) {
    var function = new IrFunction("F", IrType.Void) {
      HasErrorHandler = hasErrorHandler,
      HasInlineAsm = hasInlineAsm,
    };
    function.CreateBlock("entry").Append(new IrRet());

    Assert.That(FrameElision.IsCandidate(function), Is.False);
  }
}
