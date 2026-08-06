using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// O0298: the equal-length half of a string <c>=</c> / <c>&lt;&gt;</c> compares a WORD at a time.
///
/// <c>rt_strcmpeq</c> already answered unequal lengths without touching a byte; what it then did with
/// equal lengths was a <c>REPE CMPSB</c>. Since the lengths are known equal by that point, the scan
/// can run <c>REPE CMPSW</c> over <c>length &gt;&gt; 1</c> words and finish an odd length with the one
/// trailing byte - half the iterations.
///
/// Only EQUALITY may be widened. <c>CMPSW</c> compares little-endian 16-bit values, so on a mismatch
/// the sign of the result says which word is larger as a number, which is not which string sorts
/// first: "ba" is the word 0x6162 and "ab" is 0x6261, ordering them backwards. The <c>&lt;</c> and
/// <c>&gt;</c> forms keep the byte compare, and the assertion below holds them there.
///
/// The risk the widening introduces is entirely at the odd/even boundary, so the data below is chosen
/// to sit on it: length 1 (no whole word, the tail byte is the whole comparison), length 3 differing
/// only in the tail byte (the case a word loop alone would miss), length 2 differing in the high byte
/// of the single word, and length 4 differing in the last byte of the second word.
/// </summary>
[TestFixture]
public sealed class StringCompareWideningTests {

  /// <summary>
  /// READ/DATA rather than literals: a folded comparison would never reach the runtime routine, and a
  /// test that silently stopped exercising it would still pass.
  /// </summary>
  private const string Program = """
    DIM a AS STRING, b AS STRING
    DIM i AS INTEGER
    FOR i = 1 TO 8
      READ a
      READ b
      IF a = b THEN PRINT "T"; ELSE PRINT "F";
    NEXT i
    PRINT
    DATA "","", "a","a", "a","b", "ab","ab"
    DATA "ab","ax", "abc","abc", "abc","abX", "abcd","abcE"
    END
    """;

  private static byte[] Compile(string source, bool optimize) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return image;
  }


  /// <summary>
  /// Empty, equal, and unequal at the first byte, at a word's high byte, and at an odd tail byte.
  /// "abc" vs "abX" is the one that matters: it agrees on the whole first word and differs only in
  /// the byte a word loop would leave uncompared.
  /// </summary>
  [TestCase(true)]
  [TestCase(false)]
  public void StringEquality_GivenLengthsAcrossTheOddEvenBoundary_ThenEachComparisonIsRight(bool optimize) =>
    Assert.That(Cpu8086.Run(Compile(Program, optimize)).Output.Trim(), Is.EqualTo("TTFTFTFF"));

  /// <summary>
  /// The optimizer must not change what the program prints - the same assertion the whole battery
  /// rests on, made directly here because this pass rewrites a comparison's inner loop.
  /// </summary>
  [Test]
  public void StringEquality_WhenOptimized_ThenIdenticalToTheUnoptimizedRun() =>
    Assert.That(Cpu8086.Run(Compile(Program, optimize: true)).Output,
      Is.EqualTo(Cpu8086.Run(Compile(Program, optimize: false)).Output));

  /// <summary>
  /// And that the widening is actually in the image. Without this the tests above would keep passing
  /// if the pass silently stopped being emitted - they prove the answers are right, not that the
  /// faster path produced them.
  ///
  /// A bare search for REPE CMPSW (F3 A7) will not do: the emitter already uses it elsewhere
  /// (CodeGenerator.Extras.cs), so it is present either way and the search would pass without the
  /// pass. The loop's own shape is what identifies it - SHR CX,1 (D1 E9), the JZ over the word loop
  /// for a length below two (74 xx), then REPE CMPSW.
  ///
  /// Nor can absence be asserted of the unoptimized build. O0298's page says rt_strcmpeq "lives in
  /// its own trimmed section ... so the faithful build keeps the full three-way compare", but the
  /// bytes measurably ARE in a --dialect pb35 image: dead-code trimming is a Tier 3 pass that only
  /// runs under --optimize, so every faithful image carries the whole runtime including the routines
  /// nothing calls. What the faithful build genuinely keeps is the CALL - its comparisons go to
  /// rt_strcmp - and that is what the self-differential test above pins.
  /// </summary>
  [Test]
  public void StringEquality_WhenOptimized_ThenTheContentScanIsWordWide() {
    var image = Compile(Program, optimize: true);
    var found = false;
    for (var i = 0; i + 6 <= image.Length; ++i)
      if (image[i] == 0xD1 && image[i + 1] == 0xE9 && image[i + 2] == 0x74 && image[i + 4] == 0xF3 && image[i + 5] == 0xA7) {
        found = true;
        break;
      }
    Assert.That(found, Is.True, "expected SHR CX,1 / JZ / REPE CMPSW - the widened equal-length scan");
  }

  /// <summary>
  /// Ordering keeps the byte compare. A word compare would sort "ba" before "ab", because as
  /// little-endian words they are 0x6162 and 0x6261 - the widening is sound only for equality, and
  /// this pins that it was not applied where it is unsound.
  /// </summary>
  [Test]
  public void StringOrdering_WhenOptimized_ThenStillCorrectAndByteWise() =>
    Assert.That(Cpu8086.Run(Compile("""
      DIM a AS STRING, b AS STRING
      DIM i AS INTEGER
      FOR i = 1 TO 4
        READ a
        READ b
        IF a < b THEN PRINT "T"; ELSE PRINT "F";
      NEXT i
      PRINT
      DATA "ab","ba", "ba","ab", "abc","abd", "abd","abc"
      END
      """, optimize: true)).Output.Trim(), Is.EqualTo("TFTF"));
}
