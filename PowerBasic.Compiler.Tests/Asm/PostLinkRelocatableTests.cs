using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

[TestFixture]
public sealed class PostLinkRelocatableTests {

  [Test]
  public void Image_GivenInternalControlTransfers_ThenTheirSemanticFixupsSurviveAssembly() {
    var asm = new Assembler { EnableJumpRelaxation = true };
    var entry = asm.DefineLabel("entry");
    var taken = asm.DefineLabel("taken");
    var callee = asm.DefineLabel("callee");

    asm.MarkLabel(entry);
    asm.Je(taken);
    asm.Call(callee);
    asm.Jmp(taken);
    asm.Nop();
    asm.MarkLabel(taken);
    asm.Ret();
    asm.MarkLabel(callee);
    asm.Ret();

    var image = asm.ToPostLinkRelocatable();

    Assert.That(image.RelativeFixups.Select(fixup => fixup.Kind),
      Is.EquivalentTo(new[] {
        AsmRelativeFixupKind.Conditional,
        AsmRelativeFixupKind.Call,
        AsmRelativeFixupKind.Jump,
      }));
    var conditional = image.RelativeFixups.Single(fixup => fixup.Kind == AsmRelativeFixupKind.Conditional);
    Assert.Multiple(() => {
      Assert.That(conditional.Condition, Is.EqualTo((byte)Condition.Equal));
      Assert.That(conditional.TargetOffset, Is.EqualTo(taken.Position));
      Assert.That(conditional.EncodedLength, Is.EqualTo(2), "the first assembler relaxation should be visible in metadata");
      Assert.That(image.RelativeFixups.Single(fixup => fixup.Kind == AsmRelativeFixupKind.Call).TargetOffset,
        Is.EqualTo(callee.Position));
    });
  }

  [Test]
  public void Image_GivenAnonymousAndNamedLabels_ThenCompleteBoundLabelMapSurvives() {
    var asm = new Assembler();
    var anonymous = asm.DefineLabel();
    var named = asm.DefineLabel("body");
    asm.MarkLabel(anonymous);
    asm.Nop();
    asm.MarkLabel(named);
    asm.Ret();

    var image = asm.ToPostLinkRelocatable();

    Assert.That(image.AllBoundLabels, Does.Contain(new AsmBoundLabel(null, anonymous.Position)));
    Assert.That(image.AllBoundLabels, Does.Contain(new AsmBoundLabel("body", named.Position)));
  }

  [Test]
  public void OrdinaryRelocatable_GivenInternalJump_ThenHistoricalMetadataContractStaysEmpty() {
    var asm = new Assembler();
    var target = asm.DefineLabel();
    asm.Jmp(target);
    asm.Nop();
    asm.MarkLabel(target);
    asm.Ret();

    var image = asm.ToRelocatable();

    Assert.That(image.RelativeFixups, Is.Empty);
    Assert.That(image.AllBoundLabels, Is.Empty);
  }
}
