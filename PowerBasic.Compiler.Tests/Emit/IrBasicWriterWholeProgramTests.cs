using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Emit;

/// <summary>
/// Whole programs round-tripped through the IR: source → IR → source → compile → run, compared
/// against the original compiled and run.
///
/// This is the shape that makes the writer useful rather than merely correct. An optimization pass
/// can be checked by rendering the IR before and after it and running both programs - a pass that
/// changes behaviour stops being an argument about instruction counts and becomes two programs that
/// print different things.
/// </summary>
[TestFixture]
public sealed class IrBasicWriterWholeProgramTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static (string Output, string? File) Run(string source) {
    var cg = new CodeGenerator(Bind(source)) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    var cpu = Cpu8086.Run(image);
    return (cpu.Output.Trim().Replace("\r\n", "|"), cpu.FileContent("RESULT.TXT")?.Replace("\r\n", "|"));
  }

  /// <summary>Renders the whole module and checks the rendered program behaves like the original.</summary>
  private static void RoundTrips(string source) {
    var before = Run(source);

    var module = IrLowering.TryLowerModule(Bind(source), out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    IrPassManager.Standard().RunOnModule(module!);
    var rendered = IrBasicWriter.Write(module!);

    var after = Run(rendered);
    Assert.That(after.Output, Is.EqualTo(before.Output), $"the rendered program prints something else:\n{rendered}");
    Assert.That(after.File, Is.EqualTo(before.File), $"the rendered program writes a different file:\n{rendered}");
  }

  [Test]
  public void RoundTrip_GivenPrintingAndArithmetic_ThenTheRenderedProgramBehavesTheSame() =>
    RoundTrips("""
      x% = 7
      y% = x% * 3 + 1
      PRINT "y="; y%
      PRINT y% - x%
      END
      """);

  [Test]
  public void RoundTrip_GivenALoopWithOutput_ThenEveryIterationSurvives() =>
    RoundTrips("""
      DIM i AS INTEGER
      DIM s AS INTEGER
      s = 0
      FOR i = 1 TO 5
        s = s + i
        PRINT i; s
      NEXT i
      PRINT "total"; s
      END
      """);

  [Test]
  public void RoundTrip_GivenAProcedureCall_ThenBothTheCallerAndCalleeSurvive() =>
    RoundTrips("""
      PRINT Twice%(21)
      CALL Announce(3)
      END
      FUNCTION Twice%(BYVAL n%)
        Twice% = n% * 2
      END FUNCTION
      SUB Announce(BYVAL n%)
        PRINT "n="; n%
      END SUB
      """);

  [Test]
  public void RoundTrip_GivenFileOutput_ThenTheRenderedProgramWritesTheSameFile() =>
    RoundTrips("""
      OPEN "RESULT.TXT" FOR OUTPUT AS #1
      DIM i AS INTEGER
      FOR i = 1 TO 3
        PRINT #1, "row"; i
      NEXT i
      CLOSE #1
      END
      """);

  [Test]
  public void RoundTrip_GivenBranchesAndComparisons_ThenEveryArmIsReached() =>
    RoundTrips("""
      DIM i AS INTEGER
      FOR i = -2 TO 2
        IF i < 0 THEN
          PRINT "neg"; i
        ELSEIF i = 0 THEN
          PRINT "zero"
        ELSE
          PRINT "pos"; i
        END IF
      NEXT i
      END
      """);

  /// <summary>
  /// An array: the writer has to recover the DIM from the alloca and the subscript from the byte
  /// offset the lowering multiplied out. Getting the stride wrong reads the wrong element, which is
  /// the kind of thing that looks plausible and is not.
  /// </summary>
  [Test]
  public void RoundTrip_GivenAnArray_ThenTheSubscriptsAddressTheSameElements() =>
    RoundTrips("""
      DIM a(0 TO 9) AS INTEGER
      DIM i AS INTEGER
      FOR i = 0 TO 9
        a(i) = i * i
      NEXT i
      FOR i = 0 TO 9
        PRINT a(i);
      NEXT i
      PRINT
      END
      """);

  [Test]
  public void RoundTrip_GivenAnArrayWithALowerBound_ThenTheOffsetIsPreserved() =>
    RoundTrips("""
      DIM a(5 TO 8) AS INTEGER
      a(5) = 50
      a(8) = 80
      PRINT a(5); a(8)
      END
      """);

  [Test]
  public void RoundTrip_GivenALongArray_ThenTheWiderStrideIsRecovered() =>
    RoundTrips("""
      DIM a(0 TO 4) AS LONG
      DIM i AS INTEGER
      FOR i = 0 TO 4
        a(i) = i * 100000
      NEXT i
      PRINT a(0); a(3); a(4)
      END
      """);

  [Test]
  public void RoundTrip_GivenLongArithmetic_ThenTheWidthIsPreserved() =>
    RoundTrips("""
      DIM a AS LONG
      a = 100000
      PRINT a * 20
      PRINT a \ 7
      PRINT a MOD 7
      END
      """);
}
