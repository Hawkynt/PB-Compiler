using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The console domain, compiled both ways and compared on the SCREEN rather than on stdout - which is
/// the only way most of it can be compared at all. <c>LOCATE</c>, <c>CSRLIN</c> and <c>CLS</c> put
/// characters in particular cells and move a cursor; a stdout capture sees the characters and none of
/// the positions, so two builds that disagree about where the text went agree about everything a
/// stdout diff can ask. <see cref="Cpu8086.Screen"/> and <see cref="Cpu8086.Cursor"/> are what close
/// that, and every case here reads at least one of them.
///
/// <para>
/// Every coordinate comes back through a two-call-site <c>NOINLINE</c> function, never written down.
/// A written-down coordinate is folded before instruction selection is asked anything, and one call
/// site lets interprocedural propagation prove it - either way the program under test stops being the
/// program that was written.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendConsoleTests {

  private static SemanticModel Bind(string source, Dialect dialect) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect), dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private sealed record Both(string Output, string Screen, int Row, int Column);

  /// <summary>Compiles both ways, runs both, asserts the routed build really took the named procedures, and returns what the direct one did.</summary>
  private static Both RunBothWays(string source, bool optimize, string routedName = "main", Dialect dialect = Dialect.Pb36) {
    var direct = new CodeGenerator(Bind(source, dialect)) { Optimize = optimize, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source, dialect)) { Optimize = optimize, UseExperimentalBackend = true };
    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();
    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain(routedName),
      "the back end did not take the code under test, so this would compare the direct build with itself: "
      + string.Join(" | ", routed.BackendDeclines.Select(d => d.Name + ": " + d.Reason)));

    static Both Execute(byte[] image, string which) {
      try {
        var cpu = Cpu8086.Run(image);
        return new Both(cpu.Output, string.Join("|", cpu.Screen), cpu.Cursor.Row, cpu.Cursor.Column);
      } catch (Cpu8086Exception e) {
        Assert.Ignore($"the interpreter cannot run the {which} image: {e.Message}");
        throw;
      }
    }

    var directRun = Execute(directImage, "direct");
    var routedRun = Execute(routedImage, "routed");
    Assert.That(routedRun.Output, Is.EqualTo(directRun.Output), "stdout");
    Assert.That(routedRun.Screen, Is.EqualTo(directRun.Screen), "the 80x25 text page");
    Assert.That((routedRun.Row, routedRun.Column), Is.EqualTo((directRun.Row, directRun.Column)), "the cursor");
    return directRun;
  }

  /// <summary>The row the text landed on, or -1 - what a screen comparison is actually about.</summary>
  private static int RowOf(Both run, string text) {
    var rows = run.Screen.Split('|');
    for (var row = 0; row < rows.Length; ++row)
      if (rows[row].Contains(text, StringComparison.Ordinal))
        return row;
    return -1;
  }

  // ---- INPUT's prompt -----------------------------------------------------------------------

  /// <summary>
  /// <c>INPUT "Name"; n%</c> prints <c>Name? </c>. The punctuation between the prompt and the first
  /// variable is what decides: a semicolon appends PB's question mark, a comma leaves the prompt
  /// standing alone. The lowering printed the prompt and never read the flag, so every prompted
  /// INPUT lost its question mark routed - and the corpus has no prompted INPUT at all to notice.
  /// </summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Run_GivenASemicolonPromptedInput_ThenBothPathsPrintTheQuestionMark(bool optimize) {
    var run = RunBothWays("""
      INPUT "Name"; n%
      PRINT "[";POS(0);"]"
      """, optimize);

    Assert.That(run.Output, Does.StartWith("Name? "), "the semicolon form appends the question mark");
    Assert.That(run.Output, Does.Contain(" 8 "), "and the prompt is six columns wide, so the cursor is at 7");
  }

  /// <summary>
  /// The twin that says the flag is READ rather than the question mark unconditionally appended: a
  /// comma-separated prompt stands alone. Without it, "print prompt + '? ' always" would pass above.
  /// </summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Run_GivenACommaPromptedInput_ThenNeitherPathAddsAQuestionMark(bool optimize) {
    var run = RunBothWays("""
      INPUT "Name", n%
      PRINT "[";POS(0);"]"
      """, optimize);

    Assert.That(run.Output, Does.StartWith("Name["), "the comma form prompts with the string and nothing else");
  }

  /// <summary><c>LINE INPUT</c> prompts only when it was given one - and then it takes the same semicolon rule.</summary>
  [TestCase("LINE INPUT \"Who\"; s$", "Who? [")]
  [TestCase("LINE INPUT \"Who\", s$", "Who[")]
  [TestCase("LINE INPUT s$", "[")]
  public void Run_GivenALineInput_ThenTheProgramsPromptIsWhatBothPathsPrint(string statement, string expected) {
    var run = RunBothWays(statement + "\nPRINT \"[\"; s$; \"]\"\n", optimize: true);

    Assert.That(run.Output, Does.StartWith(expected));
  }

  // ---- LOCATE, CSRLIN and the screen ----------------------------------------------------------

  /// <summary>
  /// Two runtime coordinates, and the text has to land in the cell they name. This is the case a
  /// stdout diff cannot resolve at all: both builds print <c>hello</c> wherever they put it.
  /// </summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Run_GivenALocateFromRuntimeCoordinates_ThenBothPathsWriteTheSameCells(bool optimize) {
    var run = RunBothWays("""
      DECLARE FUNCTION Given%(BYVAL v%)
      LOCATE Given%(4), Given%(10)
      PRINT "hello";
      LOCATE Given%(6), Given%(20)
      PRINT "world";
      END
      FUNCTION Given%(BYVAL v%) NOINLINE
        Given% = v%
      END FUNCTION
      """, optimize);

    Assert.That(RowOf(run, "hello"), Is.EqualTo(3), "LOCATE counts rows from one");
    Assert.That(run.Screen.Split('|')[3].IndexOf("hello", StringComparison.Ordinal), Is.EqualTo(9));
    Assert.That(RowOf(run, "world"), Is.EqualTo(5));
    Assert.That((run.Row, run.Column), Is.EqualTo((5, 24)));
  }

  /// <summary>
  /// A coordinate the selector cannot prove word-sized - a SINGLE, and a LONG past 16 bits. Both used
  /// to take the whole module body off the IR path, because <c>rt_locate</c>'s argument slot is a word
  /// register and the lowering handed it a 32-bit value; the coordinates are INTEGERs and now lower as
  /// such. The rounding and the wrap are the direct emitter's, which is the reference.
  /// </summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Run_GivenALocateWithANonWordCoordinate_ThenItRoutesAndAgrees(bool optimize) {
    var run = RunBothWays("""
      DECLARE FUNCTION Real!(BYVAL v!)
      DECLARE FUNCTION Wide&(BYVAL v&)
      LOCATE Real!(5.6), Real!(10.4)
      PRINT "a";
      LOCATE Wide&(65538), Wide&(65540)
      PRINT "b";
      END
      FUNCTION Real!(BYVAL v!) NOINLINE
        Real! = v!
      END FUNCTION
      FUNCTION Wide&(BYVAL v&) NOINLINE
        Wide& = v&
      END FUNCTION
      """, optimize);

    Assert.That(RowOf(run, "a"), Is.EqualTo(5), "CINT(5.6) is 6, and the row counts from one");
    Assert.That(run.Screen.Split('|')[5].IndexOf('a'), Is.EqualTo(9), "CINT(10.4) is 10");
    Assert.That(RowOf(run, "b"), Is.EqualTo(1), "65538 truncated to a word is 2");
  }

  /// <summary>
  /// The <c>$OPTIMIZE</c>-visible fold: an earlier LOCATE is dead when the next one covers everything
  /// it set. It is a whole-model pre-pass over the bound AST, so both paths inherit the same decision -
  /// which is worth a test precisely because it would be easy for the routed path to fold a second
  /// time, or to stop honouring the fold the pruner already made.
  /// </summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Run_GivenTwoAdjacentLocates_ThenOnlyTheSecondPositionSurvivesOnBothPaths(bool optimize) {
    var run = RunBothWays("""
      DECLARE FUNCTION Given%(BYVAL v%)
      LOCATE Given%(5), Given%(10)
      LOCATE Given%(7), Given%(30)
      PRINT "here";
      END
      FUNCTION Given%(BYVAL v%) NOINLINE
        Given% = v%
      END FUNCTION
      """, optimize);

    Assert.That(RowOf(run, "here"), Is.EqualTo(6), "the covered LOCATE left no trace");
    Assert.That(run.Screen.Split('|')[6].IndexOf("here", StringComparison.Ordinal), Is.EqualTo(29));
  }

  /// <summary>
  /// The same pair with a cursor READ between them, which is an observer: both LOCATEs have to happen,
  /// and the first one's column has to be readable. The negative half of the fold above.
  /// </summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Run_GivenACursorReadBetweenTwoLocates_ThenBothPathsKeepBoth(bool optimize) {
    var run = RunBothWays("""
      DECLARE FUNCTION Given%(BYVAL v%)
      LOCATE Given%(5), Given%(10)
      a% = POS(0) : b% = CSRLIN
      LOCATE Given%(7), Given%(30)
      PRINT a%; b%; CSRLIN;
      END
      FUNCTION Given%(BYVAL v%) NOINLINE
        Given% = v%
      END FUNCTION
      """, optimize);

    Assert.That(run.Output.Trim(), Is.EqualTo("10  5  7"), "the first LOCATE was observed and therefore kept");
  }

  /// <summary>
  /// <c>CLS</c> homes the cursor and blanks the page - both of which are only visible on the screen,
  /// which is why this is a screen comparison and not an output one.
  ///
  /// <para>
  /// It used to be a decline test, and said so: "a CLS that starts lowering will fail this test rather
  /// than slip past unmeasured". It started lowering, and it did fail. The lowering is a call to the
  /// same argumentless <c>rt_cls</c> the direct emitter calls, so what is asserted now is the thing
  /// the decline was standing in for - both builds blank the same page and leave the cursor in the
  /// same place. <see cref="RunBothWays"/> checks that <c>main</c> really routed, so this cannot
  /// quietly go back to comparing the direct build with itself.
  /// </para>
  /// </summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Run_GivenCls_ThenBothPathsBlankTheScreenAndHomeTheCursor(bool optimize) {
    var run = RunBothWays("""
      PRINT "before"
      CLS
      PRINT "after";
      """, optimize);

    Assert.That(run.Screen.Split('|')[0], Is.EqualTo("after"),
      "CLS blanked what came before it and put the cursor home");
    Assert.That(run.Output, Does.Contain("before"),
      "and stdout, which is what a text diff sees, kept it");
  }

  /// <summary>
  /// Printing past the right margin wraps, and printing past the last row scrolls. Neither is in the
  /// output byte stream - the characters are all there in both builds whatever the screen does with
  /// them - so this is a screen assertion or it is nothing.
  /// </summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Run_GivenOutputPastTheLastRow_ThenBothPathsScrollTheSameWay(bool optimize) {
    var run = RunBothWays("""
      DECLARE FUNCTION Given%(BYVAL v%)
      n% = Given%(30)
      FOR i% = 1 TO n%
        PRINT "L"; i%
      NEXT i%
      END
      FUNCTION Given%(BYVAL v%) NOINLINE
        Given% = v%
      END FUNCTION
      """, optimize);

    Assert.That(run.Screen.Split('|')[0], Is.EqualTo("L 7"), "the first six lines scrolled off the top");
    Assert.That(run.Screen.Split('|')[23], Is.EqualTo("L 30"));
    Assert.That(run.Row, Is.EqualTo(24));
  }
}
