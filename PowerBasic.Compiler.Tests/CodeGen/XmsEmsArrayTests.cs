using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 external-memory arrays: <c>DIM EMS/XMS a(...)</c> stores the data outside conventional
/// memory through the paged (VIRTUAL) machinery - scalars AND UDT elements, including member
/// access and whole-element copies through the far paged window. Behavioral tests run under
/// DOSBox (EMS enabled) and are skipped when it is unavailable.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class XmsEmsArrayTests {

  private static string Run(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return DosBoxRunner.Normalize(DosBoxRunner.Run(exe));
  }

  [Test]
  public void Execute_GivenXmsLongArray_WhenFilledAcrossPages_ThenValuesReadBack() {
    // 5000 LONGs = 20000 bytes: spans more than one 16 KiB EMS page, so the
    // page-window remapping is exercised, not just offset arithmetic
    const string source = """
      DIM XMS a&(1 TO 5000)
      DIM i&
      FOR i& = 1 TO 5000
        a&(i&) = i& * 3
      NEXT
      PRINT a&(1); a&(4096); a&(5000)
      PRINT LBOUND(a&); UBOUND(a&)
      """;
    Assert.That(Run(source), Is.EqualTo(" 3  12288  15000\n 1  5000\n"));
  }

  [Test]
  public void Execute_GivenXmsUdtArray_WhenMembersWrittenAndElementCopied_ThenValuesReadBack() {
    const string source = """
      TYPE Point
        X AS INTEGER
        Y AS LONG
        Tag AS STRING * 3
      END TYPE
      DIM XMS p(1 TO 3000) AS Point
      p(1).X = 11
      p(1).Y = 100000
      p(1).Tag = "abc"
      p(2500).X = 42
      p(2500).Y = 987654
      p(2500).Tag = "xyz"
      DIM q AS Point
      q = p(2500)
      p(7) = q
      PRINT p(1).X; p(1).Y; p(1).Tag
      PRINT p(7).X; p(7).Y; p(7).Tag
      """;
    Assert.That(Run(source), Is.EqualTo(" 11  100000 abc\n 42  987654 xyz\n"));
  }

  [Test]
  public void Execute_GivenTwoXmsArrays_WhenWritesInterleave_ThenNoCrossTalk() {
    // both arrays live behind the same EMS page frame: every access must map ITS handle's
    // page pair, not reuse whatever window the last allocation/zeroing left behind
    const string source = """
      DIM XMS a&(1 TO 4000)
      DIM XMS b&(1 TO 4000)
      DIM i&
      FOR i& = 1 TO 4000
        a&(i&) = i&
        b&(i&) = -i&
      NEXT
      PRINT a&(1); a&(4000); b&(1); b&(4000)
      """;
    Assert.That(Run(source), Is.EqualTo(" 1  4000 -1 -4000\n"));
  }

  [Test]
  public void Execute_GivenXmsArrayBeyond64K_WhenFilled_ThenNoWraparound() {
    // 30000 LONGs = 120000 bytes > the 64 KiB page frame: offsets must page-map, not wrap
    const string source = """
      DIM XMS a&(1 TO 30000)
      a&(1) = 111
      a&(17000) = 222
      a&(29999) = 333
      PRINT a&(1); a&(17000); a&(29999)
      """;
    Assert.That(Run(source), Is.EqualTo(" 111  222  333\n"));
  }

  [Test]
  public void Execute_GivenCpu386SpeedBuild_WhenZeroFillsWiden_ThenFreshArraysReadZeroAndValuesSurvive() {
    // R3: the BSS entry zero and the EMS zero-fill store DWORDs under $CPU 80386 -
    // fresh elements must still read 0 and stored values survive across pages
    const string source = """
      $CPU 80386
      $OPTIMIZE SPEED
      DIM XMS a&(1 TO 5000)
      DIM untouched AS LONG
      a&(4999) = 123456
      PRINT untouched; a&(1); a&(4999)
      """;
    Assert.That(Run(source), Is.EqualTo(" 0  0  123456\n"));
  }

  [Test]
  public void Execute_GivenOptimizedHugeArray_WhenUmbAvailable_ThenAllocatesHighWithCorrectValues() {
    // C6: on DOS 5+ the entry stub links UMBs and prefers high memory, so a HUGE
    // array that fits lands above 0x9FFF (DOSBox provides UMBs by default) while
    // element access stays correct; too-large blocks fall back to conventional
    const string source = """
      $OPTIMIZE SPEED
      DIM HUGE h(1 TO 20000) AS LONG
      h(1) = 42
      h(20000) = 77
      PRINT h(1); h(20000)
      IF VARSEG(h(1)) > &H9FFF THEN PRINT "HIGH" ELSE PRINT "LOW"
      """;
    Assert.That(Run(source), Is.EqualTo(" 42  77\nHIGH\n"));
  }

  [Test]
  public void Execute_GivenEmsUdtArray_WhenMemberWritten_ThenReadsBack() {
    const string source = """
      TYPE Pair
        A AS LONG
        B AS LONG
      END TYPE
      DIM EMS e(1 TO 2000) AS Pair
      e(1999).A = 123456
      e(1999).B = 654321
      PRINT e(1999).A; e(1999).B
      """;
    Assert.That(Run(source), Is.EqualTo(" 123456  654321\n"));
  }
}
