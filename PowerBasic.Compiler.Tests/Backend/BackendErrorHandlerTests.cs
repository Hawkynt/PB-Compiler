using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// <c>ON ERROR</c> compiled by the x86-16 back end, and executed.
///
/// This is the construct the IR path could not emit for longest, and the reason is worth keeping in
/// view: arming a handler captures the CURRENT frame - the <c>BP</c> and <c>SP</c> that
/// <c>rt_raise</c> restores before it jumps - so it cannot be a call, which would capture its own.
/// The lowering therefore emits intrinsics and the selector expands them inline, and a handler is
/// named by the offset of its own basic block (the machine form of LLVM's <c>blockaddress</c>).
///
/// Everything here compares the routed program against the directly-emitted one by RUNNING both. A
/// handler is entered by a jump the CFG does not show, from a point no instruction here chose, and
/// static inspection cannot tell you whether that landed anywhere sensible.
///
/// Faults raised from INSIDE a runtime routine rather than by an ERROR statement are covered by the
/// corpus differential instead, where real programs divide and open files; pinning one here would
/// mean asserting divide-by-zero semantics under the optimizer that nothing has checked against the
/// genuine compiler.
/// </summary>
[TestFixture]
public sealed class BackendErrorHandlerTests {

  private static (string Output, IEnumerable<string> Routed) Run(
      string source, bool routed, bool optimize = true) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = optimize, UseExperimentalBackend = routed };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return (Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|"), cg.BackendRoutedNames.ToList());
  }

  private static void BothPathsAgree(string source, string expected, bool optimize = true) {
    var (routed, names) = Run(source, routed: true, optimize);
    Assert.That(names, Does.Contain("main"), "the back end has to have taken the module body");
    Assert.That(routed, Is.EqualTo(Run(source, routed: false, optimize).Output), "the two emitters disagree");
    Assert.That(routed, Is.EqualTo(expected));
  }

  /// <summary>A raise reaches the handler, and RESUME to a label carries on from there.</summary>
  [Test]
  public void Route_GivenOnErrorGotoAndResumeLabel_ThenTheHandlerRunsAndControlContinues() =>
    BothPathsAgree("""
      ON ERROR GOTO Trap
      PRINT "before"
      ERROR 7
      PRINT "not reached"
      Trap:
      PRINT "trapped"
      RESUME Done
      Done:
      PRINT "done"
      END
      """, "before|trapped|done");

  /// <summary>ERR is a runtime cell, and the routed code has to read the same one the raise wrote.</summary>
  [Test]
  public void Route_GivenAHandlerReadingErr_ThenItSeesTheRaisedCode() =>
    BothPathsAgree("""
      ON ERROR GOTO Trap
      ERROR 11
      Trap:
      PRINT "err"; ERR
      RESUME Done
      Done:
      END
      """, "err 11");

  /// <summary>Disarming means the next fault is fatal rather than caught - so nothing after it runs.</summary>
  [Test]
  public void Route_GivenOnErrorGotoZero_ThenTheHandlerIsNoLongerArmed() =>
    BothPathsAgree("""
      ON ERROR GOTO Trap
      ERROR 5
      PRINT "unreachable"
      Trap:
      PRINT "caught"
      ON ERROR GOTO 0
      RESUME Done
      Done:
      PRINT "done"
      END
      """, "caught|done");

  /// <summary>
  /// RESUME NEXT is the form that needs every statement to publish its own boundaries, because it
  /// returns to the statement AFTER the one that faulted - which the fault chose, not the source.
  /// </summary>
  [Test]
  public void Route_GivenResumeNext_ThenExecutionContinuesAfterTheFaultingStatement() =>
    BothPathsAgree("""
      ON ERROR GOTO Trap
      PRINT "one"
      ERROR 6
      PRINT "two"
      END
      Trap:
      RESUME NEXT
      """, "one|two");

  [Test]
  public void Route_GivenOnErrorResumeNext_ThenTheInlineModeSkipsTheFaultingStatement() =>
    BothPathsAgree("""
      ON ERROR RESUME NEXT
      PRINT "one"
      ERROR 6
      PRINT "two"
      ON ERROR GOTO 0
      END
      """, "one|two");

  [Test]
  public void Route_GivenUnoptimizedHandlerAndDirectCalleeRaise_ThenUnwindsToRoutedMain() =>
    BothPathsAgree("""
      DECLARE SUB Boom(v%)
      ON ERROR GOTO Trap
      DIM value AS INTEGER
      Boom value
      PRINT "not reached"
      END

      SUB Boom(v%)
        ERROR 7
      END SUB

      Trap:
      PRINT "err"; ERR
      RESUME Done
      Done:
      PRINT "done"
      END
      """, "err 7 |done", optimize: false);

  [Test]
  public void Route_GivenUnoptimizedDirectCalleeStackTrap_ThenUnwindsToRoutedMain() =>
    BothPathsAgree("""
      $ERROR STACK ON
      DECLARE SUB Recurse(d%)
      ON ERROR GOTO Trap
      Recurse 1
      PRINT "not reached"
      END

      SUB Recurse(d%)
        Recurse d% + 1
      END SUB

      Trap:
      PRINT "err"; ERR
      RESUME Done
      Done:
      PRINT "done"
      END
      """, "err 201 |done", optimize: false);

  /// <summary>Two handlers in sequence: the second arming has to replace the first, not stack on it.</summary>
  [Test]
  public void Route_GivenAReArmedHandler_ThenTheLatestOneTakesTheFault() =>
    BothPathsAgree("""
      ON ERROR GOTO First
      ON ERROR GOTO Second
      ERROR 9
      END
      First:
      PRINT "first"
      RESUME Done
      Second:
      PRINT "second"
      RESUME Done
      Done:
      PRINT "done"
      END
      """, "second|done");

  /// <summary>
  /// ERL is the last NUMERIC line label that ran - PB counts those and not alphabetic ones, and the
  /// distinction is the whole test: 100 and 200 must be reported, and passing through <c>Middle:</c>
  /// must not reset or change what ERL says. The lowering used to record neither, so every handler
  /// asking read a zero the direct emitter never gives it.
  /// </summary>
  [Test]
  public void Route_GivenNumericLineLabels_ThenErlReportsTheLastOneBeforeTheFault() =>
    BothPathsAgree("""
      ON ERROR GOTO Trap
      100 PRINT "one"
      Middle:
      ERROR 5
      After:
      PRINT "erl"; ERL
      200 ERROR 6
      Later:
      PRINT "erl"; ERL
      ON ERROR GOTO 0
      END
      Trap:
      RESUME NEXT
      """, "one|erl 100 |erl 200");

}
