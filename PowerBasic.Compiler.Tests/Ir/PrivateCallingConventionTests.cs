using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// O0282 on the IR: <see cref="PrivateCallingConvention"/> gives a procedure the module owns
/// completely the WATCALL register convention, and leaves the BASIC stack ABI wherever something
/// outside the module's direct calls could reach it.
/// </summary>
[TestFixture]
public sealed class PrivateCallingConventionTests {

  private static IrModule Lower(string source, bool owned = true) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, why);
    module!.OwnsProcedureAbi = owned;
    return module;
  }

  private static IrCallConvention ConventionOf(IrModule module, string name)
    => module.Functions.First(function => function.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Convention;

  [Test]
  public void Run_GivenAnOwnedWordProcedure_ThenItAndEveryCallSiteTakeWatcall() {
    var module = Lower("""
      DECLARE FUNCTION addw(BYVAL a AS INTEGER, BYVAL b AS INTEGER) AS INTEGER
      PRINT addw(2, 3); addw(4, 5)
      FUNCTION addw(BYVAL a AS INTEGER, BYVAL b AS INTEGER) AS INTEGER
        addw = a + b
      END FUNCTION
      """);

    Assert.That(PrivateCallingConvention.Run(module), Is.EqualTo(1));
    Assert.Multiple(() => {
      Assert.That(ConventionOf(module, "addw"), Is.EqualTo(IrCallConvention.Watcall));
      Assert.That(IrVerifier.Verify(module), Is.Empty, "the call sites were respecified with the definition");
    });
  }

  [Test]
  public void Run_GivenALongParameter_ThenTheStackConventionStays() {
    var module = Lower("""
      DECLARE FUNCTION f(BYVAL x AS LONG) AS LONG
      PRINT f(1)
      FUNCTION f(BYVAL x AS LONG) AS LONG
        f = x
      END FUNCTION
      """);

    Assert.That(PrivateCallingConvention.Run(module), Is.Zero);
    Assert.That(ConventionOf(module, "f"), Is.EqualTo(IrCallConvention.Basic),
      "a two-word parameter is not one register");
  }

  [Test]
  public void Run_GivenOneEscapedAndOneOwnedProcedure_ThenOnlyTheOwnedOneChanges() {
    var module = Lower("""
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

    PrivateCallingConvention.Run(module);
    Assert.Multiple(() => {
      Assert.That(ConventionOf(module, "escaped"), Is.EqualTo(IrCallConvention.Basic),
        "a procedure whose address is taken keeps the ABI an indirect caller would use");
      Assert.That(ConventionOf(module, "owned"), Is.EqualTo(IrCallConvention.Watcall));
    });
  }

  [Test]
  public void Run_GivenAnExplicitConvention_ThenItIsLeftAlone() {
    var module = Lower("""
      DECLARE FUNCTION g CDECL ALIAS "_g" (BYVAL a AS INTEGER, BYVAL b AS INTEGER) AS INTEGER
      PRINT g(2, 3)
      FUNCTION g CDECL ALIAS "_g" (BYVAL a AS INTEGER, BYVAL b AS INTEGER) AS INTEGER
        g = a + b
      END FUNCTION
      """);

    PrivateCallingConvention.Run(module);
    Assert.That(ConventionOf(module, "g"), Is.EqualTo(IrCallConvention.Cdecl));
  }

  [Test]
  public void Run_GivenAnUncalledProcedure_ThenThereIsNoCallTrafficToRemove() {
    var module = Lower("""
      FUNCTION unused(BYVAL a AS INTEGER) AS INTEGER
        unused = a + 1
      END FUNCTION
      """);

    Assert.That(PrivateCallingConvention.Run(module), Is.Zero);
  }

  [Test]
  public void Run_GivenAModuleThatDoesNotOwnItsCallers_ThenNothingChanges() {
    var module = Lower("""
      DECLARE FUNCTION addw(BYVAL a AS INTEGER, BYVAL b AS INTEGER) AS INTEGER
      PRINT addw(2, 3)
      FUNCTION addw(BYVAL a AS INTEGER, BYVAL b AS INTEGER) AS INTEGER
        addw = a + b
      END FUNCTION
      """, owned: false);

    Assert.That(PrivateCallingConvention.Run(module), Is.Zero, "a unit's or linked object's callers are elsewhere");
  }

  /// <summary>
  /// The register convention end to end, where it can go wrong: a BYREF is a near pointer in a
  /// register, and a callee that wants its arguments in each other's registers needs the entry's
  /// moves ordered - or an exchange - so that none is overwritten before it is read. The optimized
  /// program must print what the unoptimized one prints.
  /// </summary>
  [TestCase("""
    DECLARE SUB bump(value AS INTEGER, BYVAL by AS INTEGER)
    DIM n AS INTEGER
    n = 41
    bump n, 1
    bump n, INP(&H60) + 2
    PRINT n
    SUB bump(value AS INTEGER, BYVAL by AS INTEGER) NOINLINE
      value = value + by
    END SUB
    """)]
  [TestCase("""
    DECLARE FUNCTION mix(BYVAL a AS INTEGER, BYVAL b AS INTEGER, BYVAL c AS INTEGER, BYVAL d AS INTEGER) AS INTEGER
    PRINT mix(1, 2, 3, 4); mix(INP(&H60) + 7, 11, 13, 17)
    FUNCTION mix(BYVAL a AS INTEGER, BYVAL b AS INTEGER, BYVAL c AS INTEGER, BYVAL d AS INTEGER) AS INTEGER NOINLINE
      DIM i AS INTEGER, s AS INTEGER
      FOR i = 1 TO d
        s = s + (d - a) * 3 + (c XOR b) - i
      NEXT i
      mix = s
    END FUNCTION
    """)]
  public void Execute_GivenARegisterConventionCall_ThenItAnswersAsTheStackOneDoes(string source) {
    static string Run(string text) {
      var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(text, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
      Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
      var generator = new CodeGenerator(model);
      var image = generator.EmitExecutable();
      Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
      return Exec.Cpu8086.Run(image).Output;
    }

    Assert.That(Run("$OPTIMIZE SPEED\n" + source), Is.EqualTo(Run("$OPTIMIZE OFF\n" + source)));
  }
}
