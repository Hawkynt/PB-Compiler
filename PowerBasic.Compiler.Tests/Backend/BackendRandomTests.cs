using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// <c>RANDOMIZE</c>, <c>RND</c> and <c>TIMER</c> through the x86-16 back end.
///
/// <para>
/// <b>What is asserted, and why it is not the drawn value.</b> The contract a caller can rely on is
/// DETERMINISM GIVEN A SEED: the same seed replays the same sequence, and the optimized and
/// unoptimized builds draw the same one, because there is one generator and one seed cell. That the
/// sequence matches GENUINE PBC 3.50 is a separate claim, and it is FALSE and known to be: from seed 7
/// the oracle draws <c>.7670898</c> where this compiler draws <c>.5970459</c> (measured with
/// <c>scripts/diff-one.sh … pb35</c>). Reproducing Zale's generator is a fidelity item of its own;
/// these tests measure replay and agreement. <c>tests/diff/DIFF120.BAS</c> carries the half the oracle
/// does agree with - replay, range and bounds.
/// </para>
/// <para>
/// <b>TIMER is nondeterministic by nature, so the SHAPE is the assertion.</b> Under the interpreter
/// the BIOS tick counter advances one tick per read, so what is checked is that the reading MOVES
/// (a back end that folded the call away or answered a constant would fail) and that it is a
/// plausible time of day. Its RANGE against a real clock is checked in
/// DIFF120 under DOSBox, where the counter is the machine's own.
/// </para>
/// <para>
/// <b>The seed comes out of a FILE.</b> A literal seed lets SCCP carry the value into the store and
/// makes the program a constant; taking it from <c>INPUT #</c> leaves the seeding and every draw as
/// real work, which is the only state in which the comparison measures anything.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendRandomTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static (string Output, IReadOnlyList<string> RoutedNames) Run(string source, bool optimize) {
    var generator = new CodeGenerator(Bind(source)) { Optimize = optimize};
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return (Cpu8086.Run(image).Output, generator.BackendRoutedNames.ToList());
  }

  /// <summary>Writes the seed to a file first, so nothing in the program is a compile-time constant.</summary>
  private const string _SeedPrologue = """
    OPEN "SEED.TXT" FOR OUTPUT AS #1
    PRINT #1, "12345"
    CLOSE #1
    DIM s AS LONG
    OPEN "SEED.TXT" FOR INPUT AS #1
    INPUT #1, s
    CLOSE #1

    """;

  /// <summary>
  /// A seeded sequence is the same sequence whatever the optimizer did to the program around it: the
  /// optimized and unoptimized builds share one generator and one seed cell, so they draw alike.
  /// </summary>
  [Test]
  public void Run_GivenASeededSequence_ThenOptimizedAndUnoptimizedDrawTheSameNumbers() {
    const string source = _SeedPrologue + """
      RANDOMIZE s
      PRINT RND; RND; RND
      PRINT RND(1, 1000); RND(1, 1000)
      END
      """;
    var (optimized, names) = Run(source, optimize: true);
    var (unoptimized, unoptimizedNames) = Run(source, optimize: false);

    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("main"), "the module body did not route - nothing was measured");
      Assert.That(unoptimizedNames, Does.Contain("main"), "the unoptimized module body did not route");
      Assert.That(optimized, Is.EqualTo(unoptimized));
      Assert.That(optimized, Does.Not.Contain("0  0  0"), "the generator answered zero three times");
    });
  }

  /// <summary>
  /// Reseeding with the same value replays the sequence. This is the property the whole facility is
  /// for, and it is the one that fails if the routed store lands anywhere but the runtime's own cell:
  /// a seed written to a private copy still produces numbers, and they still repeat within one run.
  /// </summary>
  [TestCase(true, TestName = "Run_GivenTheSameSeedTwice_WhenOptimized_ThenTheSequenceRepeats")]
  [TestCase(false, TestName = "Run_GivenTheSameSeedTwice_WhenUnoptimized_ThenTheSequenceRepeats")]
  public void Run_GivenTheSameSeedTwice_ThenTheSequenceRepeats(bool optimize) {
    var (routed, names) = Run(_SeedPrologue + """
      RANDOMIZE s
      a! = RND
      b! = RND
      RANDOMIZE s
      c! = RND
      d! = RND
      PRINT (a! = c!); (b! = d!); (a! <> b!)
      END
      """, optimize);

    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("main"), "the module body did not route - nothing was measured");
      Assert.That(routed.Trim(), Is.EqualTo("-1 -1 -1"),
        "reseeding with the same value must replay the sequence, and two draws must differ");
    });
  }

  /// <summary>
  /// <c>RANDOMIZE</c> with no argument seeds from the BIOS tick counter.
  ///
  /// <para>
  /// The control matters here more than usual. "Two consecutive draws differ" says nothing at all -
  /// they differ whether or not the statement did anything, which is what an unseeded generator is
  /// for. So the program draws the seeded pair TWICE and interposes the bare <c>RANDOMIZE</c> before
  /// the second half of the second run: the first draw must still match (nothing before it changed)
  /// and the second must NOT, which can only be true if the statement wrote the seed cell.
  /// </para>
  /// </summary>
  [TestCase(true, TestName = "Run_GivenAClockSeededGenerator_WhenOptimized_ThenTheSeedChanged")]
  [TestCase(false, TestName = "Run_GivenAClockSeededGenerator_WhenUnoptimized_ThenTheSeedChanged")]
  public void Run_GivenAClockSeededGenerator_ThenTheSeedChanged(bool optimize) {
    var (routed, names) = Run(_SeedPrologue + """
      RANDOMIZE s
      a! = RND
      b! = RND
      RANDOMIZE s
      c! = RND
      RANDOMIZE
      d! = RND
      PRINT (a! = c!); (b! <> d!)
      END
      """, optimize);

    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("main"), "the module body did not route - nothing was measured");
      Assert.That(routed.Trim(), Is.EqualTo("-1 -1"),
        "RANDOMIZE with no argument left the seed cell as it found it");
    });
  }

  /// <summary>
  /// TIMER's shape: two readings of an advancing counter differ, and the reading is a plausible time
  /// of day.
  /// </summary>
  [TestCase(true, TestName = "Run_GivenTimer_WhenOptimized_ThenItAdvancesWithinADay")]
  [TestCase(false, TestName = "Run_GivenTimer_WhenUnoptimized_ThenItAdvancesWithinADay")]
  public void Run_GivenTimer_ThenItAdvancesWithinADay(bool optimize) {
    var (routed, names) = Run("""
      t1! = TIMER
      t2! = TIMER
      PRINT (t2! > t1!); (t1! >= 0); (t1! < 86400!)
      END
      """, optimize);

    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("main"), "the module body did not route - nothing was measured");
      Assert.That(routed.Trim(), Is.EqualTo("-1 -1 -1"),
        "TIMER must advance and answer a time of day - a folded-away call answers neither");
    });
  }
}
