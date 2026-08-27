using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Who owns a string handle, on the retargetable path - the rule <c>IrLowering</c> states and the two
/// places that broke it. A runtime entry CONSUMES its handle arguments, so a value handed to one twice
/// is read after it was released, and a value handed to none is leaked.
///
/// <para>
/// Both faults need a program that DEFEATS constant folding, which is why every subject here comes
/// back out of a file rather than being written down: with a literal subject the comparison folds and
/// the loop unrolls, and what is left measures nothing. The corpus has string <c>SELECT</c>s and
/// <c>MID$</c> statements already and none of them noticed, for exactly that reason.
/// </para>
/// <para>
/// The leak additionally needs SCALE. One leaked block is invisible; the DOS runtime's compacting heap
/// is 64 KiB, so the count and the string length here are chosen to cross it - and the assertion is
/// that the program FINISHES, which is the only thing that tells a leak from a correct build.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendStringLifetimeTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static (string Direct, string Routed, IEnumerable<string> RoutedNames) RunBothWays(string source, bool optimize) {
    var direct = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = true };
    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();
    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));

    string Execute(byte[] image, string which) {
      try {
        return Cpu8086.Run(image).Output;
      } catch (Cpu8086Exception e) {
        Assert.Ignore($"the interpreter cannot run the {which} image: {e.Message}");
        return "";
      }
    }

    return (Execute(directImage, "direct"), Execute(routedImage, "routed"), routed.BackendRoutedNames);
  }

  /// <summary>
  /// A <c>SELECT CASE</c> over a string tested the SAME borrowed handle in every arm, and
  /// <c>rt_str_compare</c> consumes its arguments - so the second arm compared a handle the first had
  /// already released, and <c>rt_str_const</c> had meanwhile been handed the descriptor back.
  ///
  /// <para>
  /// The subject is "gamma" and matches no arm, so the answer is CASE ELSE. Routed it answered the
  /// SECOND arm - not a random value, which is what made it look like a dispatch bug rather than a
  /// lifetime one: the freed descriptor held the very literal that arm compares against.
  /// </para>
  /// <para>
  /// TWO named arms are load-bearing. With one, the handle is consumed once and there is no later use
  /// to be wrong, which is why <c>tests/</c> and the differential corpus never caught this.
  /// </para>
  /// </summary>
  [TestCase(true, TestName = "Run_GivenAStringSelectWithTwoArms_WhenOptimized_ThenTheSubjectSurvivesEveryComparison")]
  [TestCase(false, TestName = "Run_GivenAStringSelectWithTwoArms_WhenUnoptimized_ThenTheSubjectSurvivesEveryComparison")]
  public void Run_GivenAStringSelectWithTwoArms_ThenTheSubjectSurvivesEveryComparison(bool optimize) {
    var (direct, routed, names) = RunBothWays("""
      OPEN "D.TXT" FOR OUTPUT AS #1
      PRINT #1, "gamma"
      CLOSE #1
      OPEN "D.TXT" FOR INPUT AS #1
      LINE INPUT #1, g$
      CLOSE #1
      SELECT CASE g$
        CASE "alpha" : PRINT "A"
        CASE "beta"  : PRINT "B"
        CASE ELSE    : PRINT "?"
      END SELECT
      END
      """, optimize);

    Assert.That(names, Does.Contain("main"), "the back end did not take the module body under test");
    Assert.That(direct.Trim(), Is.EqualTo("?"), "gamma matches neither arm, so CASE ELSE is the answer");
    Assert.That(routed, Is.EqualTo(direct));
  }

  /// <summary>
  /// The same handle discipline with more arms and a value list, plus the SELECT inside a loop - which
  /// is where a subject released once too FEW rather than once too many would show, as a leak.
  /// </summary>
  [Test]
  public void Run_GivenAStringSelectInALoop_ThenEveryIterationAgreesAndTheHeapHolds() {
    var (direct, routed, names) = RunBothWays("""
      OPEN "D.TXT" FOR OUTPUT AS #1
      PRINT #1, "0123456789012345678901234567890123456789"
      PRINT #1, "400"
      CLOSE #1
      OPEN "D.TXT" FOR INPUT AS #1
      LINE INPUT #1, u$
      INPUT #1, n%
      CLOSE #1
      p$ = u$ + u$ + u$ + u$ + u$
      hits% = 0
      FOR i% = 1 TO n%
        SELECT CASE p$
          CASE "no1" : hits% = hits% + 1
          CASE "no2" : hits% = hits% + 2
          CASE "no3" : hits% = hits% + 3
          CASE ELSE  : hits% = hits% + 10
        END SELECT
      NEXT i%
      PRINT hits%; LEN(p$)
      PRINT "done"
      END
      """, optimize: true);

    Assert.That(names, Does.Contain("main"));
    Assert.That(direct, Does.Contain("done"), "the direct build finishes, so the comparison is about the routed one");
    Assert.That(direct, Does.Contain("4000"), "no arm matches, so every iteration takes CASE ELSE");
    Assert.That(routed, Is.EqualTo(direct));
  }

  /// <summary>
  /// <c>MID$(s$, i, n) = v$</c> read the target as a borrowed COPY, handed that to the runtime, and
  /// stored the edited copy back - leaving the handle the variable HELD released by nobody. One per
  /// statement, so it takes a churning loop to see it: 600 edits of a 120-byte string exhaust the
  /// 64 KiB heap and the routed build says OUT OF STRING SPACE where the direct one prints its answer.
  ///
  /// <c>REPLACE</c> next door already freed through the cell; this is that same call, which is what
  /// makes the fix a one-liner rather than a design.
  /// </summary>
  [Test]
  public void Run_GivenMidStatementInAChurningLoop_ThenTheReplacedHandleIsReleased() {
    var (direct, routed, names) = RunBothWays("""
      OPEN "D.TXT" FOR OUTPUT AS #1
      PRINT #1, "0123456789012345678901234567890123456789"
      PRINT #1, "600"
      CLOSE #1
      OPEN "D.TXT" FOR INPUT AS #1
      LINE INPUT #1, u$
      INPUT #1, n%
      CLOSE #1
      s$ = u$ + u$ + u$
      FOR i% = 1 TO n%
        MID$(s$, 5, 3) = "ZZZ"
      NEXT i%
      PRINT LEN(s$); "["; MID$(s$, 1, 12); "]"
      PRINT "done"
      END
      """, optimize: true);

    Assert.That(names, Does.Contain("main"));
    Assert.That(direct, Does.Contain("done"));
    Assert.That(routed, Does.Not.Contain("OUT OF STRING SPACE"), "the routed build leaked one block per MID$ statement");
    Assert.That(routed, Is.EqualTo(direct));
  }

  /// <summary>
  /// <c>ASC(s$, i) = code</c> is the same sequence as the MID$ statement and had the same omission.
  /// It gets its own case because the two are separate lowerings - the first fix left this one leaking,
  /// and only running it said so.
  /// </summary>
  [Test]
  public void Run_GivenAscAssignmentInAChurningLoop_ThenTheReplacedHandleIsReleased() {
    var (direct, routed, names) = RunBothWays("""
      OPEN "D.TXT" FOR OUTPUT AS #1
      PRINT #1, "0123456789012345678901234567890123456789"
      PRINT #1, "600"
      CLOSE #1
      OPEN "D.TXT" FOR INPUT AS #1
      LINE INPUT #1, u$
      INPUT #1, n%
      CLOSE #1
      s$ = u$ + u$ + u$
      FOR i% = 1 TO n%
        ASC(s$, 5) = 90
        ASC(s$, 5) = 65
      NEXT i%
      PRINT LEN(s$); "["; MID$(s$, 1, 12); "]"
      PRINT "done"
      END
      """, optimize: true);

    Assert.That(names, Does.Contain("main"));
    Assert.That(direct, Does.Contain("done"));
    Assert.That(routed, Does.Not.Contain("OUT OF STRING SPACE"), "the routed build leaked one block per ASC assignment");
    Assert.That(routed, Is.EqualTo(direct));
  }

  [TestCase(true, TestName = "Run_GivenDynamicStringSwap_WhenOptimized_ThenHandlesTransferWithoutFallback")]
  [TestCase(false, TestName = "Run_GivenDynamicStringSwap_WhenUnoptimized_ThenHandlesTransferWithoutFallback")]
  public void Run_GivenDynamicStringSwap_WhenCompiled_ThenHandlesTransferWithoutFallback(bool optimize) {
    var (direct, routed, names) = RunBothWays("""
      DECLARE SUB Exchange()
      Exchange
      END
      SUB Exchange() NOINLINE
        DIM left$, right$
        left$ = "left" : right$ = "right"
        SWAP left$, right$
        PRINT left$; ":"; right$
        DIM empty$, filled$
        filled$ = "full"
        SWAP empty$, filled$
        SWAP empty$, empty$
        PRINT "["; empty$; "]["; filled$; "]"
        DIM items$()
        REDIM items$(1 TO 2)
        items$(1) = "one" : items$(2) = "two"
        SWAP items$(1), items$(2)
        PRINT items$(1); ":"; items$(2)
      END SUB
      """, optimize);

    Assert.That(names, Is.SupersetOf(new[] { "Exchange", "main" }),
      "the string handle exchange and its caller must stay on the routed path");
    Assert.That(direct, Does.Contain("right:left"));
    Assert.That(direct, Does.Contain("[full][]"));
    Assert.That(direct, Does.Contain("two:one"));
    Assert.That(routed, Is.EqualTo(direct));
  }
}
