using PowerBasic.Compiler.Backend.Mos6502;
using static PowerBasic.Compiler.Backend.Mos6502.M6502Op;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The 6502 back end's code-size work, each piece against the bytes it must and must not remove:
/// the accumulator-load elimination in <see cref="Mos6502Assembler"/> (O0409) on hand-written
/// instruction streams, and the frame packing (O0408) and branch shaping (O0410) on compiled programs
/// that have to keep printing what they printed.
/// </summary>
[TestFixture]
public sealed class Mos6502CodeSizeTests {

  private static byte[] Assemble(Action<Mos6502Assembler> write) {
    var asm = new Mos6502Assembler();
    write(asm);
    return asm.Assemble(0x1000).Bytes;
  }

  private static readonly M6502Address CellA = M6502Address.Absolute(0x20);
  private static readonly M6502Address CellB = M6502Address.Absolute(0x21);

  [Test]
  public void Assemble_GivenTheSameImmediateTwice_ThenTheSecondLoadIsDropped() {
    var bytes = Assemble(asm => {
      asm.Immediate(Lda, 0);
      asm.Memory(Sta, CellA);
      asm.Immediate(Lda, 0);
      asm.Memory(Sta, CellB);
      asm.Immediate(Ldx, 0);                            // sets N and Z itself, so the dropped load's are unread
      asm.Emit(Rts);
    });
    Assert.That(bytes, Is.EqualTo(new byte[] { 0xA9, 0x00, 0x85, 0x20, 0x85, 0x21, 0xA2, 0x00, 0x60 }));
  }

  [Test]
  public void Assemble_GivenALoadOfTheCellJustStored_ThenTheLoadIsDropped() {
    var bytes = Assemble(asm => {
      asm.Memory(Sta, CellA);
      asm.Memory(Lda, CellA);
      asm.Memory(Sta, CellB);
      asm.Immediate(Ldx, 0);
      asm.Emit(Rts);
    });
    Assert.That(bytes, Is.EqualTo(new byte[] { 0x85, 0x20, 0x85, 0x21, 0xA2, 0x00, 0x60 }));
  }

  /// <summary>A routine may hand its flags back: the caller of one ending in a load reads them after RTS.</summary>
  [Test]
  public void Assemble_GivenAReturnAfterTheLoad_ThenTheLoadStays() {
    var bytes = Assemble(asm => {
      asm.Immediate(Lda, 0);
      asm.Memory(Sta, CellA);
      asm.Immediate(Lda, 0);
      asm.Emit(Rts);
    });
    Assert.That(bytes, Has.Length.EqualTo(7));
  }

  [Test]
  public void Assemble_GivenABranchReadingTheLoadsFlags_ThenTheLoadStays() {
    var bytes = Assemble(asm => {
      var skip = asm.NewLabel("skip");
      asm.Immediate(Lda, 0);
      asm.Memory(Sta, CellA);
      asm.Immediate(Lda, 0);
      asm.Branch(Beq, skip);
      asm.Bind(skip);
      asm.Emit(Rts);
    });
    Assert.That(bytes, Has.Length.EqualTo(9), "Z after STA is the first load's, but a branch may be reached otherwise too");
  }

  [Test]
  public void Assemble_GivenALabelBetween_ThenTheLoadStays() {
    var bytes = Assemble(asm => {
      var join = asm.NewLabel("join");
      asm.Immediate(Lda, 1);
      asm.Memory(Sta, CellA);
      asm.Bind(join);
      asm.Immediate(Lda, 1);
      asm.Memory(Sta, CellB);
      asm.Emit(Rts);
    });
    Assert.That(bytes, Has.Length.EqualTo(9), "control may arrive at a label with anything in A");
  }

  [Test]
  public void Assemble_GivenACellTheKernalUpdates_ThenItIsReadAgain() {
    var jiffy = M6502Address.Absolute(0xA2);
    var bytes = Assemble(asm => {
      asm.Memory(Lda, jiffy);
      asm.Memory(Sta, CellA);
      asm.Memory(Lda, jiffy);
      asm.Memory(Sta, CellB);
      asm.Emit(Rts);
    });
    Assert.That(bytes, Has.Length.EqualTo(9), "the interrupt moves the jiffy clock between two reads");
  }

  [Test]
  public void Assemble_GivenTheInstructionABitSkipSwallows_ThenWhatFollowsKnowsNothing() {
    var bytes = Assemble(asm => {
      var holds = asm.NewLabel("holds");
      asm.Immediate(Lda, 0);
      asm.Bytes([0x2C]);
      asm.Bind(holds);
      asm.Immediate(Lda, 1);
      asm.Memory(Sta, CellA);
      asm.Immediate(Lda, 1);
      asm.Memory(Sta, CellB);
      asm.Emit(Rts);
    });
    Assert.That(bytes, Has.Length.EqualTo(12), "past the swallowed LDA #1 A may still be 0");
  }

  /// <summary>Sixteen LONG temporaries computed one after another, each dead before the next: packed, they share a slot.</summary>
  [Test]
  public void Run_GivenManyShortLivedValues_ThenTheyStillComputeTheirOwnAnswers() {
    var lines = string.Concat(Enumerable.Range(1, 16).Select(i => $"t& = t& * 3 + INP({i}) + {i}\nPRINT t&;\n"));
    var output = FlatTargets.Run("6502", "t& = 1\n" + lines);
    var expected = new System.Text.StringBuilder();
    long t = 1;
    for (var i = 1; i <= 16; ++i) {
      t = (int)(t * 3 + i);
      expected.Append(t < 0 ? $"{t} " : $" {t} ");
    }
    Assert.That(output.Replace("\r", "").TrimEnd('\n'), Is.EqualTo(expected.ToString()));
  }

  /// <summary>Comparisons kept as values and branched on both ways: the falling-through and materialized forms agree with DOS.</summary>
  [Test]
  public void Run_GivenComparisonsAsValuesAndBranches_ThenEveryOutcomeIsRight() {
    const string source = """
      FOR i% = -2 TO 2
        a = (i% > 0): b = (i% = 0): c = (i% < 1)
        IF i% > 0 THEN PRINT "p"; ELSE PRINT "n";
        PRINT a; b; c
      NEXT
      """;
    Assert.That(FlatTargets.Run("6502", source).Replace("\r", ""), Is.EqualTo(FlatTargets.Run("x64", source).Replace("\r", "")));
  }
}
