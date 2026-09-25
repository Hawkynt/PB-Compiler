using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// O0025: a call to a pure integer FUNCTION with constant arguments is answered at compile time by
/// <see cref="PureCallEvaluation"/> - and every way that could go wrong leaves the call alone.
/// </summary>
[TestFixture]
public sealed class PureCallEvaluationTests {

  private static IrModule Lower(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, why);
    // what the pass sees in the pipeline: SSA values, and PB's float-promoted integer arithmetic
    // recovered as integers
    foreach (var function in module!.Functions.Where(function => !function.IsDeclaration)) {
      Mem2Reg.Run(function);
      IntegerRecovery.Run(function);
      Dce.Run(function);
    }
    return module;
  }

  private static int CallsTo(IrModule module, string name)
    => module.Functions.Where(function => !function.IsDeclaration).SelectMany(function => function.AllInstructions)
      .OfType<IrCall>().Count(call => call.Callee is IrFunction callee && callee.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

  [Test]
  public void Run_GivenARecursivePureFunctionOfConstants_ThenTheCallIsItsAnswer() {
    var module = Lower("""
      DECLARE FUNCTION fact%(BYVAL n%)
      PRINT fact%(7)
      END
      FUNCTION fact%(BYVAL n%)
        IF n% <= 1 THEN fact% = 1 ELSE fact% = n% * fact%(n% - 1)
      END FUNCTION
      """);

    Assert.That(PureCallEvaluation.Run(module), Is.EqualTo(1));
    var main = module.Functions.First(function => function.Name == "main");
    Assert.Multiple(() => {
      Assert.That(CallsTo(module, "fact") - module.Functions.First(f => f.Name == "fact").AllInstructions.OfType<IrCall>().Count(),
        Is.Zero, "main no longer calls fact");
      Assert.That(main.AllInstructions.SelectMany(instruction => instruction.Operands).OfType<IrConstantInt>()
        .Any(constant => constant.Value == 5040), Is.True, "and prints 5040 instead");
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void Run_GivenRecursionThatNeverEnds_ThenTheBudgetStopsItAndTheCallStays() {
    var module = Lower("""
      DECLARE FUNCTION spin%(BYVAL n%)
      PRINT spin%(1)
      END
      FUNCTION spin%(BYVAL n%)
        spin% = spin%(n% + 1)
      END FUNCTION
      """);

    Assert.That(PureCallEvaluation.Run(module), Is.Zero);
  }

  [Test]
  public void Run_GivenADivisionByZero_ThenTheCallStaysForTheProgramToRaise() {
    var module = Lower("""
      DECLARE FUNCTION q%(BYVAL a%, BYVAL b%)
      PRINT q%(7, 0)
      END
      FUNCTION q%(BYVAL a%, BYVAL b%)
        q% = a% \ b%
      END FUNCTION
      """);

    PureCallEvaluation.Run(module);
    Assert.That(CallsTo(module, "q"), Is.EqualTo(1), "a trap is the program's to take, not the compiler's");
  }

  [Test]
  public void Run_GivenAFunctionThatReadsAGlobal_ThenItIsNotPure() {
    var module = Lower("""
      DECLARE FUNCTION g%(BYVAL a%)
      k% = INP(&H60)
      PRINT g%(3)
      END
      FUNCTION g%(BYVAL a%)
        SHARED k%
        g% = a% + k%
      END FUNCTION
      """);

    PureCallEvaluation.Run(module);
    Assert.That(CallsTo(module, "g"), Is.EqualTo(1));
  }

  [Test]
  public void Execute_GivenPureCallsFoldedAtCompileTime_ThenTheProgramPrintsWhatItWouldHaveComputed() {
    const string source = """
      DECLARE FUNCTION fact%(BYVAL n%)
      DECLARE FUNCTION fib%(BYVAL n%)
      PRINT fact%(7); fib%(20); fact%(0); fib%(1)
      END
      FUNCTION fact%(BYVAL n%)
        IF n% <= 1 THEN fact% = 1 ELSE fact% = n% * fact%(n% - 1)
      END FUNCTION
      FUNCTION fib%(BYVAL n%)
        IF n% < 2 THEN fib% = n% ELSE fib% = fib%(n% - 1) + fib%(n% - 2)
      END FUNCTION
      """;
    static string Run(string text) {
      var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(text, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
      var generator = new CodeGenerator(model);
      var image = generator.EmitExecutable();
      Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
      return Exec.Cpu8086.Run(image, maxSteps: 50_000_000).Output.Trim();
    }

    Assert.Multiple(() => {
      Assert.That(Run("$OPTIMIZE SPEED\n" + source), Is.EqualTo("5040  6765  1  1"));
      Assert.That(Run("$OPTIMIZE OFF\n" + source), Is.EqualTo("5040  6765  1  1"), "the calls, made at run time");
    });
  }
}
