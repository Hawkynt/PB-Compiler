using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// <c>EXIT FAR</c> compiled by the x86-16 back end, and executed.
///
/// The keyword misleads: this is not a far RETURN and nothing about it is a return at all.
/// <c>EXIT FAR AT label</c> records an unwind point - a target offset plus the SP and BP of the frame
/// that has to be back in place when control lands there - and a bare <c>EXIT FAR</c> anywhere
/// afterwards, at any call depth, restores that frame and jumps. Every procedure frame and GOSUB in
/// between is abandoned without being popped, and the procedure that fired it never returns to its
/// caller. It is PB's other non-local jump, the same shape as <c>ON ERROR</c> and armed the same way:
/// inline code, because a CALL would capture its own frame instead of the one being marked.
///
/// Everything here RUNS both images rather than inspecting either. A landing point is reached by an
/// edge no instruction chose, and "the stack is where it was" is not a property static inspection can
/// report - the failure mode of getting it wrong is a program that runs and then returns to nowhere,
/// which only shows up when something executes.
/// </summary>
[TestFixture]
public sealed class BackendExitFarTests {

  private static (string Output, IEnumerable<string> Routed) Run(string source, bool routed) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true, UseExperimentalBackend = routed };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return (Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|"), cg.BackendRoutedNames.ToList());
  }

  /// <summary>
  /// Both emitters, and the exact text. The routing assertion is not decoration: a program the back
  /// end declined would be emitted by the DIRECT path under both settings and agree with itself
  /// perfectly, which is the one way this fixture could pass while testing nothing.
  /// </summary>
  private static void BothPathsAgree(string source, string expected, params string[] mustRoute) {
    var (routed, names) = Run(source, routed: true);
    foreach (var name in mustRoute.DefaultIfEmpty("main"))
      Assert.That(names, Does.Contain(name), $"the back end has to have taken {name}");
    Assert.That(routed, Is.EqualTo(Run(source, routed: false).Output), "the two emitters disagree");
    Assert.That(routed, Is.EqualTo(expected));
  }

  /// <summary>
  /// The base case: a SUB fires the unwind, so neither the rest of the SUB nor the rest of the caller's
  /// statements run - control resumes at the armed label. Both PRINTs that are skipped would have
  /// printed, which is what makes the absence of their text evidence rather than a coincidence.
  /// </summary>
  [Test]
  public void Route_GivenABareExitFarInASub_ThenTheRestOfTheSubAndTheCallerAreSkipped() =>
    BothPathsAgree("""
      DECLARE SUB Leave()
      PRINT "before"
      EXIT FAR AT Home
      Leave
      PRINT "after the call"
      Home:
      PRINT "landed"
      END
      SUB Leave()
        PRINT "in sub"
        EXIT FAR
        PRINT "past the exit"
      END SUB
      """, "before|in sub|landed", "main", "Leave");

  /// <summary>
  /// Three frames deep, all abandoned at once - the shape DIFF14 uses. BYVAL keeps this case focused
  /// on the non-local jump; near numeric BYREF procedures have their own routed ABI coverage.
  /// </summary>
  [Test]
  public void Route_GivenNestedCalls_ThenEveryFrameBetweenIsAbandonedAtOnce() =>
    BothPathsAgree("""
      DECLARE SUB Noisy(BYVAL n%)
      EXIT FAR AT Unwound
      Noisy 3
      PRINT "not reached"
      Unwound:
      PRINT "unwound"
      END
      SUB Noisy(BYVAL n%)
        IF n% = 0 THEN
          EXIT FAR
        END IF
        Noisy n% - 1
        PRINT "after"; n%
      END SUB
      """, "unwound", "main", "Noisy");

  /// <summary>
  /// Fired from inside a loop inside the procedure. The loop counter lives in the abandoned frame and
  /// the FOR has an unfinished iteration, so nothing may unwind through it - the jump leaves both
  /// where they are.
  /// </summary>
  [Test]
  public void Route_GivenAnExitFarInsideALoop_ThenTheLoopIsLeftUnfinished() =>
    BothPathsAgree("""
      DECLARE SUB Counter()
      EXIT FAR AT Done
      Counter
      PRINT "not reached"
      Done:
      PRINT "done"
      END
      SUB Counter()
        FOR i% = 1 TO 10
          PRINT "i="; i%
          IF i% = 3 THEN EXIT FAR
        NEXT i%
        PRINT "loop finished"
      END SUB
      """, "i= 1 |i= 2 |i= 3 |done", "main", "Counter");

  /// <summary>
  /// A FUNCTION that assigns its result and then unwinds: the caller never sees the value, because
  /// the call never returns. The assignment target keeps whatever it already held, which is the
  /// difference between an unwind and the EXIT FUNCTION it is easy to mistake this for - and the
  /// reason the test prints the target rather than asserting on nothing.
  /// </summary>
  [Test]
  public void Route_GivenAFunctionThatAssignsItsResultThenExitsFar_ThenTheCallerNeverReceivesIt() =>
    BothPathsAgree("""
      DECLARE FUNCTION Give%()
      r% = 111
      EXIT FAR AT Landed
      r% = Give%()
      PRINT "not reached"; r%
      Landed:
      PRINT "r="; r%
      END
      FUNCTION Give%()
        Give% = 777
        EXIT FAR
        Give% = 999
      END FUNCTION
      """, "r= 111", "main", "Give");

  /// <summary>
  /// The same procedure both ways in one program: called once where it unwinds and once where it
  /// runs off its own end and returns normally. A restore sequence that damaged the ordinary return
  /// would show here and nowhere else.
  /// </summary>
  [Test]
  public void Route_GivenAProcedureThatAlsoReturnsNormally_ThenTheOrdinaryReturnStillWorks() =>
    BothPathsAgree("""
      DECLARE SUB Maybe(BYVAL n%)
      EXIT FAR AT Landed
      Maybe 1
      PRINT "back from 1"
      Maybe 2
      PRINT "back from 2"
      Maybe 0
      PRINT "not reached"
      Landed:
      PRINT "landed"
      END
      SUB Maybe(BYVAL n%)
        IF n% = 0 THEN EXIT FAR
        PRINT "ran"; n%
      END SUB
      """, "ran 1 |back from 1|ran 2 |back from 2|landed", "main", "Maybe");

  /// <summary>
  /// The unwind point is a CELL, not a scope: arming twice leaves the second one armed, and the first
  /// label is never reached. Arming is a statement that runs, not a region that nests.
  /// </summary>
  [Test]
  public void Route_GivenTwoArmedPoints_ThenTheMostRecentOneWins() =>
    BothPathsAgree("""
      DECLARE SUB Leave()
      EXIT FAR AT First
      PRINT "armed first"
      EXIT FAR AT Second
      PRINT "armed second"
      Leave
      PRINT "not reached"
      First:
      PRINT "first"
      GOTO Fin
      Second:
      PRINT "second"
      Fin:
      END
      SUB Leave()
        EXIT FAR
      END SUB
      """, "armed first|armed second|second", "main", "Leave");

  /// <summary>
  /// Arming and never firing. The three cell stores are all the statement does, so the program has to
  /// run exactly as though the line were absent - including falling through the label it named.
  /// </summary>
  [Test]
  public void Route_GivenAnArmThatNeverFires_ThenTheProgramRunsUnchanged() =>
    BothPathsAgree("""
      DECLARE SUB Quiet()
      a% = 5
      EXIT FAR AT Never
      Quiet
      a% = a% + 2
      Never:
      PRINT "a="; a%
      END
      SUB Quiet()
        PRINT "quiet"
      END SUB
      """, "quiet|a= 7", "main", "Quiet");

  /// <summary>
  /// Fired from the module body itself, with no procedure in between. There is no frame to abandon,
  /// so this is the degenerate case - and the one that proves the restore is a plain jump when SP and
  /// BP already hold what was recorded, rather than something that only works after a CALL.
  /// </summary>
  [Test]
  public void Route_GivenAnExitFarInTheModuleBody_ThenItJumpsToTheArmedLabel() =>
    BothPathsAgree("""
      PRINT "start"
      EXIT FAR AT Target
      EXIT FAR
      PRINT "not reached"
      Target:
      PRINT "target"
      END
      """, "start|target");

  /// <summary>
  /// The unwind point armed inside a PROCEDURE rather than the module body, and fired from one called
  /// deeper still. The landing frame is then a procedure's own, so the arming procedure resumes in the
  /// middle of itself and afterwards returns to its caller normally - which is the case where getting
  /// the restore wrong returns to nowhere rather than merely skipping a PRINT.
  /// </summary>
  [Test]
  public void Route_GivenAProcedureThatArmsTheUnwindPoint_ThenItResumesInsideItselfAndStillReturns() =>
    BothPathsAgree("""
      DECLARE SUB Outer()
      DECLARE SUB Inner()
      PRINT "start"
      Outer
      PRINT "back in main"
      END
      SUB Outer()
        EXIT FAR AT Cleanup
        Inner
        PRINT "not reached"
        Cleanup:
        PRINT "cleanup"
      END SUB
      SUB Inner()
        PRINT "inner"
        EXIT FAR
      END SUB
      """, "start|inner|cleanup|back in main", "main", "Outer", "Inner");

  /// <summary>
  /// The frame really is restored, not merely the instruction pointer: a local of the module body
  /// written before the arm is read after the landing, and a procedure that ran in between put its own
  /// frame over the same stack region. A jump that reinstated only the target offset would read that
  /// procedure's leftovers.
  /// </summary>
  [Test]
  public void Route_GivenLocalsWrittenBeforeTheArm_ThenTheyStillReadCorrectlyAfterLanding() =>
    BothPathsAgree("""
      DECLARE SUB Churn(BYVAL n%)
      x% = 1234
      y% = -9
      EXIT FAR AT Back
      Churn 6
      x% = 0
      y% = 0
      Back:
      PRINT "x="; x%; " y="; y%
      END
      SUB Churn(BYVAL n%)
        DIM junk%(1:20)
        FOR i% = 1 TO 20
          junk%(i%) = i% * 7
        NEXT i%
        IF n% > 0 THEN Churn n% - 1
        EXIT FAR
      END SUB
      """, "x= 1234  y=-9", "main", "Churn");
}
