using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// The program point an ELSEIF condition is judged at.
///
/// The O16 value lattice answers questions about a variable "here", and "here" is
/// <c>_currentStatement</c>. Emitting the THEN arm moves that to the last statement inside it -
/// whose environment was refined by the IF's condition being TRUE - so an ELSEIF condition folded
/// afterwards was being answered in the wrong world. <c>IF i &lt; 0 ... ELSEIF i = 0</c> folded the
/// second test to false for every value, because inside the first arm <c>i</c> really is negative
/// and <c>i = 0</c> really is impossible there; the arm was simply never taken.
///
/// It only showed with an EQUALITY test, only when the value could be negative, and only with the
/// optimizer on - and it printed a plausible answer rather than crashing, which is why nothing had
/// caught it. It was found by rendering the IR back to BASIC and running both programs.
/// </summary>
[TestFixture]
public sealed class ElseIfProgramPointTests {

  private static string Run(string source, bool optimize, bool backend) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = optimize, UseExperimentalBackend = backend };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  /// <summary>Every combination has to agree; the optimizer is not allowed a different answer.</summary>
  private static void AllPathsAgree(string source, string expected) {
    foreach (var optimize in new[] { false, true })
      foreach (var backend in new[] { false, true })
        Assert.That(Run(source, optimize, backend), Is.EqualTo(expected),
          $"optimize={optimize} backend={backend}");
  }

  [Test]
  public void ElseIf_GivenAnEqualityAfterASignTest_ThenTheZeroArmIsReached() =>
    AllPathsAgree("""
      DIM i AS INTEGER
      FOR i = -2 TO 2
        IF i < 0 THEN
          PRINT "neg"
        ELSEIF i = 0 THEN
          PRINT "zero"
        ELSE
          PRINT "pos"
        END IF
      NEXT i
      END
      """, "neg|neg|zero|pos|pos");

  /// <summary>With no ELSE the faulty fold made the value vanish entirely rather than take a wrong arm.</summary>
  [Test]
  public void ElseIf_GivenNoElseArm_ThenTheZeroArmStillRuns() =>
    AllPathsAgree("""
      DIM i AS INTEGER
      FOR i = -2 TO 2
        IF i < 0 THEN
          PRINT "neg"
        ELSEIF i = 0 THEN
          PRINT "zero"
        END IF
      NEXT i
      END
      """, "neg|neg|zero");

  /// <summary>The same shape outside a FOR loop: the counter was never the point, the refinement was.</summary>
  [Test]
  public void ElseIf_GivenADoLoopInsteadOfFor_ThenItBehavesTheSame() =>
    AllPathsAgree("""
      DIM i AS INTEGER
      i = -2
      DO WHILE i <= 2
        IF i < 0 THEN
          PRINT "neg"
        ELSEIF i = 0 THEN
          PRINT "zero"
        ELSE
          PRINT "pos"
        END IF
        i = i + 1
      LOOP
      END
      """, "neg|neg|zero|pos|pos");

  /// <summary>A second ELSEIF is judged at the IF's point too, not at the first ELSEIF's arm.</summary>
  [Test]
  public void ElseIf_GivenAChainOfThem_ThenEveryArmIsReachable() =>
    AllPathsAgree("""
      DIM i AS INTEGER
      FOR i = -1 TO 3
        IF i < 0 THEN
          PRINT "neg"
        ELSEIF i = 0 THEN
          PRINT "zero"
        ELSEIF i = 1 THEN
          PRINT "one"
        ELSEIF i = 2 THEN
          PRINT "two"
        ELSE
          PRINT "big"
        END IF
      NEXT i
      END
      """, "neg|zero|one|two|big");
}
