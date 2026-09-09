using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>O0282: whole-program selection of a private calling convention.</summary>
[TestFixture]
[NonParallelizable]
public sealed class InternalCallingConventionSpecializationTests {

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  [Test]
  public void Apply_GivenOneEscapedAndOneOwnedProcedure_ThenOnlyOwnedProcedureIsSpecialized() {
    var model = Bind("""
      DECLARE FUNCTION escaped(BYVAL a AS INTEGER) AS INTEGER
      DECLARE FUNCTION owned(BYVAL a AS INTEGER) AS INTEGER
      DIM p AS INTEGER
      p = CODEPTR(escaped)
      PRINT escaped(1)
      PRINT owned(2)
      FUNCTION escaped(BYVAL a AS INTEGER) AS INTEGER
        escaped = a + 10
      END FUNCTION
      FUNCTION owned(BYVAL a AS INTEGER) AS INTEGER
        owned = a + 20
      END FUNCTION
      """);

    OptRegParm.Apply(model);

    Assert.Multiple(() => {
      Assert.That(model.Procedures["escaped"].CallConv, Is.EqualTo(CallConvention.Basic),
        "the address-taken procedure must retain its source-visible stack ABI");
      Assert.That(model.Procedures["owned"].CallConv, Is.EqualTo(CallConvention.Watcall),
        "an unrelated fully-owned procedure should still get the private register ABI");
    });
  }

  [Test]
  public void Apply_GivenUncalledProcedure_ThenDoesNotPayForPrivateAbi() {
    var model = Bind("""
      FUNCTION unused(BYVAL a AS INTEGER) AS INTEGER
        unused = a + 1
      END FUNCTION
      """);

    OptRegParm.Apply(model);

    Assert.That(model.Procedures["unused"].CallConv, Is.EqualTo(CallConvention.Basic),
      "there is no call traffic to remove, so changing the convention has no benefit");
  }

  [Test]
  public void Execute_GivenOwnedByRefParameter_ThenPrivateRegisterAbiPreservesReferenceSemantics() {
    var model = Bind("""
      DECLARE SUB bump(value AS INTEGER)
      DIM n AS INTEGER
      n = 41
      bump n
      PRINT n
      SUB bump(value AS INTEGER)
        value = value + 1
      END SUB
      """);

    OptRegParm.Apply(model);
    Assert.That(model.Procedures["bump"].CallConv, Is.EqualTo(CallConvention.Watcall),
      "a BYREF near pointer is itself one ABI word and can use the private register slot");

    // Run without the optimizer after applying the policy explicitly: this forces a real WATCALL
    // rather than allowing inlining to erase the call that this test is meant to exercise.
    var generator = new CodeGenerator(model);
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    Assert.That(Exec.Cpu8086.Run(image).Output.Trim(), Is.EqualTo("42"));
  }
}
