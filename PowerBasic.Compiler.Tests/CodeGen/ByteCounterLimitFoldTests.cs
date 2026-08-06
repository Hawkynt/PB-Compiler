using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// O0113 on a BYTE counter: a constant <c>FOR</c> limit folds into the compare as an immediate.
///
/// Word counters already did this; byte counters were excluded outright, so <c>FOR b?? = 1 TO 10</c>
/// still spent a temp cell and a memory read every iteration to compare against a constant. The
/// compare happens in <c>AL</c>, so the folded form is <c>CMP AL, imm8</c>.
///
/// What the exclusion was really protecting is the RANGE: the limit has to fit the COUNTER's width,
/// not a word's. A byte counter compared against an immediate truncated from 300 would be comparing
/// against 44 and would stop early, so the bound now comes from the counter — 0..255 unsigned,
/// -128..127 signed — and an out-of-range constant keeps the temp instead of folding into something
/// narrower than itself.
///
/// These tests assert BEHAVIOUR only. A byte-pattern detector was written first and thrown away
/// after measuring it: the faithful build of the same program contains <c>3C 0A</c> (CMP AL, 10)
/// four times over inside its untrimmed runtime, entirely by coincidence, so both "absent without
/// --optimize" and "more occurrences with it" are false. That the fold fires was confirmed by
/// building the same source with and without the emitter change and diffing the images, which a
/// test cannot do; what a test can do is prove the loops still count correctly and that the
/// optimizer changes nothing observable, and that is what these do.
///
/// Note on the top of the range: <c>FOR b?? = 250 TO 255</c> never terminates, in the faithful
/// build exactly as in the optimized one — 255 incremented wraps to 0, which is still &lt;= 255.
/// That is the counter's arithmetic, not a fold artefact, which is why the boundary case below
/// stops at 254.
/// </summary>
[TestFixture]
public sealed class ByteCounterLimitFoldTests {

  private static byte[] Compile(string source, bool optimize) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return image;
  }

  private static string Run(string source, bool optimize) =>
    Cpu8086.Run(Compile(source, optimize)).Output.Trim().Replace("\r\n", "|");

  private const string Counted = """
    DIM b AS BYTE, s AS INTEGER
    FOR b = 1 TO 10
      s = s + 1
    NEXT b
    PRINT s
    END
    """;

  [TestCase(true)]
  [TestCase(false)]
  public void Run_GivenAByteCounter_ThenTheLoopRunsTheRightNumberOfTimes(bool optimize) =>
    Assert.That(Run(Counted, optimize), Is.EqualTo("10"));

  /// <summary>
  /// The high end of the byte range, where a limit truncated to a narrower width would show up as
  /// an early exit. 250 to 254 is five iterations; 255 is deliberately avoided because the counter
  /// wraps there and the loop never ends either way.
  /// </summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Run_GivenAByteCounterNearTheTopOfItsRange_ThenItStillCountsRight(bool optimize) =>
    Assert.That(Run("""
      DIM b AS BYTE, s AS INTEGER
      FOR b = 250 TO 254
        s = s + 1
      NEXT b
      PRINT s
      END
      """, optimize), Is.EqualTo("5"));

  /// <summary>A STEP the fold must not disturb - the immediate is the limit, not the increment.</summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Run_GivenAByteCounterWithAStep_ThenItStillCountsRight(bool optimize) =>
    Assert.That(Run("""
      DIM b AS BYTE, s AS INTEGER
      FOR b = 2 TO 11 STEP 3
        s = s + 1
      NEXT b
      PRINT s
      END
      """, optimize), Is.EqualTo("4"));

  /// <summary>A zero-iteration loop still runs zero times: the guard compares before entering.</summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Run_GivenAByteCounterWhoseLimitIsBelowItsStart_ThenTheBodyNeverRuns(bool optimize) =>
    Assert.That(Run("""
      DIM b AS BYTE, s AS INTEGER
      FOR b = 9 TO 4
        s = s + 1
      NEXT b
      PRINT s
      END
      """, optimize), Is.EqualTo("0"));

  /// <summary>
  /// The optimizer may not change what the program prints - asserted directly, because this rewrites
  /// a loop's termination test.
  /// </summary>
  [Test]
  public void Run_WhenOptimized_ThenIdenticalToTheUnoptimizedRun() =>
    Assert.That(Cpu8086.Run(Compile(Counted, optimize: true)).Output,
      Is.EqualTo(Cpu8086.Run(Compile(Counted, optimize: false)).Output));
}
