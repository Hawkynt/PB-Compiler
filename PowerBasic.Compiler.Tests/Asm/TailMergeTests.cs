using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

/// <summary>
/// S3 identical-code folding: procedure regions with byte- and fixup-identical content fold
/// to one copy; the duplicate's entry label re-binds to the survivor and callers land there.
/// </summary>
[TestFixture]
public sealed class TailMergeTests {

  [Test]
  public void Merge_GivenIdenticalRegions_WhenAssembled_ThenSecondEntryFoldsToFirst() {
    var asm = new Assembler { EnableTailMerge = true };
    var p1 = asm.DefineLabel();
    var p2 = asm.DefineLabel();

    asm.Call(p1);            // 0: E8 xx xx
    asm.Call(p2);            // 3: E8 xx xx
    asm.Ret();               // 6: C3

    asm.BeginFoldRegion(p1);
    asm.MarkLabel(p1);       // 7: identical body #1
    asm.Mov(Reg.AX, 7);
    asm.Ret();
    asm.EndFoldRegion();

    asm.BeginFoldRegion(p2);
    asm.MarkLabel(p2);       // identical body #2 - must fold away
    asm.Mov(Reg.AX, 7);
    asm.Ret();
    asm.EndFoldRegion();

    var image = asm.ToArray();
    Assert.Multiple(() => {
      Assert.That(image.Length, Is.EqualTo(7 + 4), "one 4-byte body remains (MOV AX,imm16 + RET)");
      var call1 = image[1] | image[2] << 8;
      var call2 = image[4] | image[5] << 8;
      Assert.That(3 + call1, Is.EqualTo(7 - 0), "first CALL lands on the surviving body");
      Assert.That(6 + call2, Is.EqualTo(7), "second CALL folds onto the SAME body");
    });
  }

  [Test]
  public void Merge_GivenDifferentBodies_WhenAssembled_ThenBothKept() {
    var asm = new Assembler { EnableTailMerge = true };
    var p1 = asm.DefineLabel();
    var p2 = asm.DefineLabel();
    asm.Call(p1);
    asm.Call(p2);
    asm.Ret();
    asm.BeginFoldRegion(p1);
    asm.MarkLabel(p1);
    asm.Mov(Reg.AX, 7);
    asm.Ret();
    asm.EndFoldRegion();
    asm.BeginFoldRegion(p2);
    asm.MarkLabel(p2);
    asm.Mov(Reg.AX, 8);      // different immediate - not congruent
    asm.Ret();
    asm.EndFoldRegion();
    var image = asm.ToArray();
    Assert.That(image.Length, Is.EqualTo(7 + 4 + 4), "no fold when bodies differ");
  }

  [Test]
  public void Merge_GivenIdenticalBodiesCallingSameExternal_WhenAssembled_ThenFold() {
    // internal fixups to the SAME external target are congruent; to different targets not
    var asm = new Assembler { EnableTailMerge = true };
    var shared = asm.DefineLabel();
    var p1 = asm.DefineLabel();
    var p2 = asm.DefineLabel();
    asm.Call(p1);
    asm.Call(p2);
    asm.Ret();
    asm.MarkLabel(shared);
    asm.Ret();
    asm.BeginFoldRegion(p1);
    asm.MarkLabel(p1);
    asm.Call(shared);
    asm.Ret();
    asm.EndFoldRegion();
    asm.BeginFoldRegion(p2);
    asm.MarkLabel(p2);
    asm.Call(shared);
    asm.Ret();
    asm.EndFoldRegion();
    var image = asm.ToArray();
    Assert.That(image.Length, Is.EqualTo(7 + 1 + 4), "the two shared-callers fold to one");
  }

  [Test]
  public void Merge_GivenGateOff_WhenAssembled_ThenNothingFolds() {
    var asm = new Assembler();
    var p1 = asm.DefineLabel();
    var p2 = asm.DefineLabel();
    asm.Call(p1);
    asm.Call(p2);
    asm.Ret();
    asm.BeginFoldRegion(p1);
    asm.MarkLabel(p1);
    asm.Mov(Reg.AX, 7);
    asm.Ret();
    asm.EndFoldRegion();
    asm.BeginFoldRegion(p2);
    asm.MarkLabel(p2);
    asm.Mov(Reg.AX, 7);
    asm.Ret();
    asm.EndFoldRegion();
    var image = asm.ToArray();
    Assert.That(image.Length, Is.EqualTo(7 + 4 + 4));
  }
}
