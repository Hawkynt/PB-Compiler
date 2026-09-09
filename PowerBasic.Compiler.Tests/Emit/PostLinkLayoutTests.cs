using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Emit;

namespace PowerBasic.Compiler.Tests.Emit;

[TestFixture]
public sealed class PostLinkLayoutTests {

  [Test]
  public void Rewrite_GivenHotDiamondOrder_ThenControlIsRebuiltForNewFallthroughs() {
    var unit = DiamondUnit();
    var layouts = new Dictionary<string, IReadOnlyList<int>> { ["Work"] = [0, 2, 1, 3] };

    var rewritten = PostLinkLayoutRewriter.Rewrite(unit, layouts);

    Assert.Multiple(() => {
      Assert.That(rewritten.Code, Is.EqualTo(new byte[] { 0x90, 0x75, 0x03, 0x92, 0xEB, 0x01, 0x91, 0xC3 }));
      Assert.That(rewritten.Fragments.OrderBy(fragment => fragment.Offset).Select(fragment => fragment.BlockId),
        Is.EqualTo(new[] { 0, 2, 1, 3 }));
      Assert.That(rewritten.RelativeFixups.OrderBy(fixup => fixup.InstructionOffset), Is.EqualTo(new[] {
        new PbuRelativeFixup(1, 2, PbuRelativeFixupKind.Conditional, (byte)Condition.NotEqual, 6),
        new PbuRelativeFixup(4, 2, PbuRelativeFixupKind.Jump, 0, 7),
      }));
    });
  }

  [Test]
  public void Rewrite_GivenMovedAbsoluteCodeTarget_ThenExportAndNearCodeFixupAreRemapped() {
    var unit = new PbuFile { Name = "ABS" };
    unit.Code = [0xB8, 0x04, 0x00, 0x91, 0xC3];
    unit.Exports.Add(new("Entry", PbuExportKind.Sub, 0, 0));
    unit.Exports.Add(new("Target", PbuExportKind.Sub, 0, 4));
    unit.Fixups.Add(new(1, PbuFixupKind.NearCode, 0));
    unit.Fragments.Add(new("Entry", 0, 0, 3, 3, PbuFragmentControlKind.Unconditional, 1, -1, 0, [1]));
    unit.Fragments.Add(new("Entry", 1, 3, 1, 1, PbuFragmentControlKind.Unconditional, 2, -1, 0, [2]));
    unit.Fragments.Add(new("Entry", 2, 4, 1, 1, PbuFragmentControlKind.Preserve, -1, -1, 0, []));

    var rewritten = PostLinkLayoutRewriter.Rewrite(unit,
      new Dictionary<string, IReadOnlyList<int>> { ["Entry"] = [0, 2, 1] });

    Assert.Multiple(() => {
      Assert.That(rewritten.Exports.Single(export => export.Name == "Target").CodeOffset, Is.EqualTo(5));
      Assert.That(rewritten.Code[1] | rewritten.Code[2] << 8, Is.EqualTo(5));
      Assert.That(rewritten.Fixups.Single().Offset, Is.EqualTo(1));
      Assert.That(rewritten.Fragments.OrderBy(fragment => fragment.Offset).Select(fragment => fragment.BlockId),
        Is.EqualTo(new[] { 0, 2, 1 }));
    });
  }

  [Test]
  public void Rewrite_GivenFarConditionalOn8086_ThenLongPairIsMaterialized() {
    var unit = FarConditionalUnit(needs386: false);

    var rewritten = PostLinkLayoutRewriter.Rewrite(unit,
      new Dictionary<string, IReadOnlyList<int>> { ["Far"] = [0, 2, 3, 1] });

    Assert.Multiple(() => {
      Assert.That(rewritten.Code[..6], Is.EqualTo(new byte[] { 0x90, 0x75, 0x03, 0xE9, 0xC9, 0x00 }));
      Assert.That(rewritten.Fragments.Single(fragment => fragment.BlockId == 0).Length, Is.EqualTo(6));
      Assert.That(rewritten.RelativeFixups.Single(),
        Is.EqualTo(new PbuRelativeFixup(1, 5, PbuRelativeFixupKind.Conditional, (byte)Condition.Equal, 207)));
    });
  }

  [Test]
  public void Rewrite_GivenFarConditionalOn386_ThenNearJccIsMaterialized() {
    var unit = FarConditionalUnit(needs386: true);

    var rewritten = PostLinkLayoutRewriter.Rewrite(unit,
      new Dictionary<string, IReadOnlyList<int>> { ["Far"] = [0, 2, 3, 1] });

    Assert.Multiple(() => {
      Assert.That(rewritten.Code[..5], Is.EqualTo(new byte[] { 0x90, 0x0F, 0x84, 0xC9, 0x00 }));
      Assert.That(rewritten.Fragments.Single(fragment => fragment.BlockId == 0).Length, Is.EqualTo(5));
      Assert.That(rewritten.RelativeFixups.Single(),
        Is.EqualTo(new PbuRelativeFixup(1, 4, PbuRelativeFixupKind.Conditional, (byte)Condition.Equal, 206)));
    });
  }

  [Test]
  public void Link_GivenSampledBaselineProfile_ThenFinalExecutableUsesHotBlockOrder() {
    var baselineLinker = new Linker();
    baselineLinker.AddUnit(DiamondUnit());
    var baseline = baselineLinker.Link(new PbuFile { Name = "MAIN", Code = [0xC3] });
    var baselineFragments = baseline.Fragments.Where(fragment => fragment.Function == "Work")
      .ToDictionary(fragment => fragment.BlockId);
    var profile = PostLinkSampleAttribution.Attribute(baseline, [
      new(baselineFragments[0].Offset, 101),
      new(baselineFragments[1].Offset, 1),
      new(baselineFragments[2].Offset, 100),
      new(baselineFragments[3].Offset, 101),
    ]);

    var optimizedLinker = new Linker();
    optimizedLinker.AddUnit(DiamondUnit());
    optimizedLinker.UsePostLinkProfile(profile);
    var optimized = optimizedLinker.Link(new PbuFile { Name = "MAIN", Code = [0xC3] });

    Assert.Multiple(() => {
      Assert.That(profile.BlockCounts[new("Work", 2)], Is.EqualTo(100));
      Assert.That(profile.EdgeCounts[new(new("Work", 0), new("Work", 2))], Is.EqualTo(100));
      Assert.That(optimized.Fragments.Where(fragment => fragment.Function == "Work")
          .OrderBy(fragment => fragment.Offset).Select(fragment => fragment.BlockId),
        Is.EqualTo(new[] { 0, 2, 3, 1 }));
      Assert.That(optimized.ResolvedExports["Work"], Is.EqualTo(2));
    });
  }

  [Test]
  public void Link_GivenStaleSampledEdge_ThenFailsClosed() {
    var profile = new PostLinkProfile();
    profile.AddEdgeCount(new("Work", 0), new("Work", 99), 10);
    var linker = new Linker();
    linker.AddUnit(DiamondUnit());
    linker.UsePostLinkProfile(profile);

    var error = Assert.Throws<LinkException>(() => linker.Link(new PbuFile { Name = "MAIN", Code = [0xC3] }));

    Assert.That(error!.Message, Does.Contain("stale post-link profile edge"));
  }

  [Test]
  public void Attribute_GivenSampleOutsideCode_ThenRejectsMalformedProfileInput() {
    var image = new Linker().Link(new PbuFile { Name = "MAIN", Code = [0xC3] });

    Assert.Throws<ArgumentOutOfRangeException>(() =>
      PostLinkSampleAttribution.Attribute(image, [new((uint)image.Code.Length, 1)]));
  }

  private static PbuFile DiamondUnit() {
    var unit = new PbuFile { Name = "DIAMOND" };
    unit.Code = [0x90, 0x74, 0x03, 0x91, 0xEB, 0x01, 0x92, 0xC3];
    unit.Exports.Add(new("Work", PbuExportKind.Sub, 0, 0));
    unit.Fragments.Add(new("Work", 0, 0, 3, 1, PbuFragmentControlKind.Conditional,
      2, 1, (byte)Condition.Equal, [1, 2]));
    unit.Fragments.Add(new("Work", 1, 3, 3, 1, PbuFragmentControlKind.Unconditional,
      3, -1, 0, [3]));
    unit.Fragments.Add(new("Work", 2, 6, 1, 1, PbuFragmentControlKind.Unconditional,
      3, -1, 0, [3]));
    unit.Fragments.Add(new("Work", 3, 7, 1, 1, PbuFragmentControlKind.Preserve,
      -1, -1, 0, []));
    unit.RelativeFixups.Add(new(1, 2, PbuRelativeFixupKind.Conditional, (byte)Condition.Equal, 6));
    unit.RelativeFixups.Add(new(4, 2, PbuRelativeFixupKind.Jump, 0, 7));
    return unit;
  }

  private static PbuFile FarConditionalUnit(bool needs386) {
    var unit = new PbuFile {
      Name = "FAR",
      CpuFlags = needs386 ? PbuCpuFlags.Needs386 : PbuCpuFlags.None,
      Code = new byte[205],
    };
    unit.Code[0] = 0x90;
    unit.Code[3] = 0xC3;
    Array.Fill(unit.Code, (byte)0x90, 4, 200);
    unit.Code[204] = 0xC3;
    unit.Exports.Add(new("Far", PbuExportKind.Sub, 0, 0));
    unit.Fragments.Add(new("Far", 0, 0, 3, 1, PbuFragmentControlKind.Conditional,
      1, 2, (byte)Condition.Equal, [1, 2]));
    unit.Fragments.Add(new("Far", 1, 3, 1, 1, PbuFragmentControlKind.Preserve,
      -1, -1, 0, []));
    unit.Fragments.Add(new("Far", 2, 4, 200, 200, PbuFragmentControlKind.Preserve,
      -1, -1, 0, []));
    unit.Fragments.Add(new("Far", 3, 204, 1, 1, PbuFragmentControlKind.Preserve,
      -1, -1, 0, []));
    return unit;
  }
}
