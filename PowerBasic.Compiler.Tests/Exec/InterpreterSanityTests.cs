using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Exec;

/// <summary>
/// The interpreter checked against the ONE path already known to be right: the direct emitter, whose
/// bytes the golden battery holds to PBC 3.50. If a program compiled by it prints the wrong number
/// here, the interpreter is wrong - and an interpreter that is wrong turns a differential comparison
/// into noise, so these come before any conclusion drawn from one.
/// </summary>
[TestFixture]
public sealed class InterpreterSanityTests {

  private static string Run(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var codegen = new CodeGenerator(model) { Optimize = true, UseExperimentalBackend = false };
    var image = codegen.EmitExecutable();
    Assert.That(codegen.Errors, Is.Empty, string.Join("; ", codegen.Errors));
    return Cpu8086.Run(image).Output;
  }

  private static string RunUnoptimized(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var codegen = new CodeGenerator(model) { Optimize = false, UseExperimentalBackend = false };
    return Cpu8086.Run(codegen.EmitExecutable()).Output;
  }

  [TestCase("PRINT 100 - 1", " 99 ")]
  [TestCase("PRINT 1 - 100", "-99 ")]
  [TestCase("PRINT 100 + 1", " 101 ")]
  [TestCase("PRINT -5", "-5 ")]
  [TestCase("PRINT 7 * 6", " 42 ")]
  [TestCase(@"PRINT 250 \ 10", " 25 ")]
  [TestCase("PRINT 7 MOD 4", " 3 ")]
  public void Run_GivenConstantArithmetic_ThenPrintsWhatBasicPrints(string source, string expected) {
    Assert.That(Run(source).TrimEnd('\r', '\n'), Is.EqualTo(expected));
  }

  [Test]
  public void Run_GivenAVariableWithNoArithmetic_ThenPrintsIt() {
    Assert.That(Run("x% = 99" + "\n" + "PRINT x%").TrimEnd(), Is.EqualTo(" 99"));
  }

  [Test]
  public void Run_GivenTwoVariablesSubtracted_ThenTheSignIsRight() {
    Assert.That(Run("x% = 100" + "\n" + "y% = 1" + "\n" + "PRINT x% - y%").TrimEnd(), Is.EqualTo(" 99"));
  }

  [Test]
  public void Run_GivenAVariableSubtraction_ThenTheSignIsRight() {
    // the immediate-folding path: v - c is emitted as an ADD of -c, and a sign error there would make
    // every differential comparison meaningless
    Assert.That(Run("""
      x% = 100
      PRINT x% - 1; x% - 200
      """).TrimEnd('\r', '\n'), Is.EqualTo(" 99 -100 "));
  }

  [Test]
  public void Run_GivenALoop_ThenTheCounterAdvancesAndStops() {
    Assert.That(Run("""
      FOR i% = 1 TO 3
        PRINT i%;
      NEXT i%
      """).TrimEnd('\r', '\n'), Is.EqualTo(" 1  2  3 "));
  }
}
