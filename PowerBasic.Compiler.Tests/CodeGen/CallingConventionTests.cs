using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Register calling conventions (docs/LINKER.md): WATCALL (Watcom: args in AX,DX,BX,CX,
/// callee-clean overflow, name <c>name_</c>) and FASTCALL (Microsoft/Borland: AX,DX,BX,
/// callee-clean overflow, name <c>@name</c>). These round-trip tests define a procedure
/// with the convention and call it - exercising both the call-site register loading and
/// the define-side register-spill prologue in one program (run unoptimized so the call is
/// real, not inlined). DOSBox-gated; the real-foreign-object proofs live in CInteropTests.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class CallingConventionTests {

  private static string Run(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return DosBoxRunner.Normalize(DosBoxRunner.Run(exe));
  }

  [Test]
  public void Execute_GivenWatcallRoundTrip_WhenTwoArgs_ThenRegistersAxDx() {
    const string source = """
      DECLARE FUNCTION subw WATCALL (BYVAL a AS INTEGER, BYVAL b AS INTEGER) AS INTEGER
      PRINT subw(20, 7)
      FUNCTION subw WATCALL (BYVAL a AS INTEGER, BYVAL b AS INTEGER) AS INTEGER
        subw = a - b
      END FUNCTION
      """;
    Assert.That(Run(source), Is.EqualTo(" 13\n"));
  }

  [Test]
  public void Execute_GivenFastcallRoundTrip_WhenTwoArgs_ThenRegistersAxDx() {
    const string source = """
      DECLARE FUNCTION subf FASTCALL (BYVAL a AS INTEGER, BYVAL b AS INTEGER) AS INTEGER
      PRINT subf(20, 7)
      FUNCTION subf FASTCALL (BYVAL a AS INTEGER, BYVAL b AS INTEGER) AS INTEGER
        subf = a - b
      END FUNCTION
      """;
    Assert.That(Run(source), Is.EqualTo(" 13\n"));
  }

  [Test]
  public void Execute_GivenWatcallRoundTrip_WhenFiveArgs_ThenFourRegistersPlusStackOverflow() {
    // a,b,c,d -> AX,DX,BX,CX ; e -> stack ; callee cleans the one overflow word (RET 2)
    const string source = """
      DECLARE FUNCTION calc WATCALL (BYVAL a AS INTEGER, BYVAL b AS INTEGER, BYVAL c AS INTEGER, BYVAL d AS INTEGER, BYVAL e AS INTEGER) AS INTEGER
      PRINT calc(50, 8, 4, 2, 1)
      FUNCTION calc WATCALL (BYVAL a AS INTEGER, BYVAL b AS INTEGER, BYVAL c AS INTEGER, BYVAL d AS INTEGER, BYVAL e AS INTEGER) AS INTEGER
        calc = a - b - c - d - e
      END FUNCTION
      """;
    Assert.That(Run(source), Is.EqualTo(" 35\n"));
  }

  [Test]
  public void Execute_GivenFastcallRoundTrip_WhenFiveArgs_ThenThreeRegistersPlusStackOverflow() {
    // a,b,c -> AX,DX,BX ; d,e -> stack ; callee cleans the two overflow words (RET 4)
    const string source = """
      DECLARE FUNCTION calc FASTCALL (BYVAL a AS INTEGER, BYVAL b AS INTEGER, BYVAL c AS INTEGER, BYVAL d AS INTEGER, BYVAL e AS INTEGER) AS INTEGER
      PRINT calc(50, 8, 4, 2, 1)
      FUNCTION calc FASTCALL (BYVAL a AS INTEGER, BYVAL b AS INTEGER, BYVAL c AS INTEGER, BYVAL d AS INTEGER, BYVAL e AS INTEGER) AS INTEGER
        calc = a - b - c - d - e
      END FUNCTION
      """;
    Assert.That(Run(source), Is.EqualTo(" 35\n"));
  }

  [Test]
  public void Execute_GivenWatcallSub_WhenCalled_ThenRegisterArgsReachBody() {
    // a SUB (no return) with register args, observed via its side effect
    const string source = """
      DECLARE SUB show WATCALL (BYVAL a AS INTEGER, BYVAL b AS INTEGER)
      show 9, 4
      SUB show WATCALL (BYVAL a AS INTEGER, BYVAL b AS INTEGER)
        PRINT a * b
      END SUB
      """;
    Assert.That(Run(source), Is.EqualTo(" 36\n"));
  }

  [Test]
  public void Compile_GivenRegisterConventionWithLongParam_ThenDiagnostic() {
    // a LONG does not fit the common-case word model; reject rather than silently miscompile
    const string source = """
      DECLARE FUNCTION f WATCALL (BYVAL x AS LONG) AS LONG
      PRINT f(1)
      FUNCTION f WATCALL (BYVAL x AS LONG) AS LONG
        f = x
      END FUNCTION
      """;
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    var generator = new CodeGenerator(model);
    generator.EmitExecutable();
    Assert.That(generator.Errors.Select(e => e.Message), Has.Some.Contains("word-sized"),
      "expected a diagnostic rejecting the non-word register-convention parameter");
  }
}
