using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Runtime;

[TestFixture]
public sealed class SoftwareX87RuntimeIntegrationTests {

  private static byte[] Compile(string source, bool optimize, bool com) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = com ? generator.EmitCom() : generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    Assert.That(image.AsSpan(0, 2).SequenceEqual("MZ"u8), Is.EqualTo(!com));
    return image;
  }

  [TestCase(false, false)]
  [TestCase(true, false)]
  [TestCase(false, true)]
  [TestCase(true, true)]
  public void Encode_GivenSoftwareFpuActivation_ThenRuntimeSharesTheCallersStack(bool optimize, bool com) {
    const string source = """
      $COMPILE EXE
      $FLOAT EMULATE
      DIM saved AS WORD
      x# = 1.25#
      ! FNSTCW saved
      OPEN "VALUE.BIN" FOR OUTPUT AS #1
      PRINT #1, MKD$(x#);
      CLOSE #1
      END
      """;
    var image = Compile(source, optimize, com);
    Assert.That(Cpu8086.Run(image, exactFloatingPoint: true).FileBytes("VALUE.BIN"),
      Is.EqualTo(new byte[] { 0, 0, 0, 0, 0, 0, 244, 63 }));
  }

  [TestCase("FCHS")]
  [TestCase("FLDZ")]
  [TestCase("FADDP")]
  [TestCase("FRNDINT")]
  public void Execute_GivenSoftwareX87_ThenPreservesTheIntegerCarryFlag(string instruction) {
    var source = "$FLOAT EMULATE\nDIM flags AS WORD\n! FLD1\n! FLD1\n! STC\n! " + instruction
      + "\n! PUSHF\n! POP AX\n! MOV flags, AX\nPRINT flags AND 1\nEND";
    var image = Compile(source, true, false);
    Assert.That(Cpu8086.Run(image, exactFloatingPoint: true).Output.Trim(), Is.EqualTo("1"));
  }

  [TestCase("$FLOAT PROCEDURE")]
  [TestCase("$ISA X87 EMULATE")]
  [TestCase("$FPU EMULATE")]
  [TestCase("$X87 EMULATE")]
  [TestCase("$ISA DEFAULT EMULATE")]
  public void Encode_GivenAnExplicitSoftwarePolicy_ThenDoesNotNeedAnInlineInstructionToSelectIt(string policy) {
    var source = policy + "\nx# = 1.25#\nOPEN \"VALUE.BIN\" FOR OUTPUT AS #1\nPRINT #1, MKD$(x#);\nCLOSE #1\nEND";
    var image = Compile(source, true, true);
    Assert.That(Cpu8086.Run(image, exactFloatingPoint: true).FileBytes("VALUE.BIN"),
      Is.EqualTo(new byte[] { 0, 0, 0, 0, 0, 0, 244, 63 }));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Encode_GivenAByReferenceProcedure_ThenSharedHelpersReadTheCurrentFrame(bool optimize) {
    const string source = """
      $FLOAT PROCEDURE
      DIM x AS DOUBLE
      OPEN "VALUE.BIN" FOR OUTPUT AS #1
      x = 0.5#: CALL WRITEVALUE(x)
      x = -1.25#: CALL WRITEVALUE(x)
      CLOSE #1
      END
      SUB WRITEVALUE(z#) STATIC
        PRINT #1, MKD$(z#);
      END SUB
      """;
    var image = Compile(source, optimize, true);
    Assert.That(Cpu8086.Run(image, exactFloatingPoint: true).FileBytes("VALUE.BIN"), Is.EqualTo(new byte[] {
      0, 0, 0, 0, 0, 0, 224, 63, 0, 0, 0, 0, 0, 0, 244, 191,
    }));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Print_GivenSoftwareArithmetic_ThenTheNumericRuntimeConsumesTheSameStack(bool optimize) {
    var image = Compile("$FLOAT PROCEDURE\nx# = VAL(\"1.25\")\nPRINT x# * 2#\nEND", optimize, true);
    Assert.That(Cpu8086.Run(image, exactFloatingPoint: true).Output.Trim(), Is.EqualTo("2.5"));
  }
}
