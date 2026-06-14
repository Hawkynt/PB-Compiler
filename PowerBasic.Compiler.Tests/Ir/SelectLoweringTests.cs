using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>SELECT CASE lowering: value/list/range/IS arms and CASE ELSE as a comparison chain.</summary>
[TestFixture]
public sealed class SelectLoweringTests {

  private static IrFunction Lower(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    return IrLowering.TryLowerMainBody(Binder.Bind(unit, Dialect.Pb35))!;
  }

  private static IrModule? LowerModule(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    return IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35));
  }

  [Test]
  public void Lower_GivenStringSubject_ComparesViaRuntime() {
    var module = LowerModule(
      "a$ = \"b\" : r% = 0\n" +
      "SELECT CASE a$\n" +
      "CASE \"a\"\n  r% = 1\n" +
      "CASE \"b\", \"c\"\n  r% = 2\n" +
      "CASE \"d\" TO \"f\"\n  r% = 3\n" +
      "CASE ELSE\n  r% = 9\n" +
      "END SELECT\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("@rt_str_compare(ptr"));   // string arms compare through the runtime
    Assert.That(text, Does.Contain("icmp sge i32"));          // the "d" TO "f" range lower bound
  }

  [Test]
  public void Lower_GivenValueListRangeAndIs_BuildsVerifiableComparisonChain() {
    var fn = Lower(
      "n% = 0 : r% = 0\n" +
      "SELECT CASE n%\n" +
      "CASE 1\n  r% = 10\n" +
      "CASE 2, 3\n  r% = 20\n" +
      "CASE 4 TO 6\n  r% = 30\n" +
      "CASE IS > 9\n  r% = 40\n" +
      "CASE ELSE\n  r% = 99\n" +
      "END SELECT");

    Assert.That(fn, Is.Not.Null);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    var text = IrPrinter.Print(fn);
    Assert.That(text, Does.Contain("icmp eq i16"));    // value / list arms
    Assert.That(text, Does.Contain("icmp sge i16"));   // range lower bound
    Assert.That(text, Does.Contain("icmp sle i16"));   // range upper bound
    Assert.That(text, Does.Contain("icmp sgt i16"));   // IS > 9
  }

  [Test]
  public void Pipeline_GivenConstantSubject_FoldsToTheMatchingArm() {
    var fn = Lower(
      "n% = 2 : r% = 0\n" +
      "SELECT CASE n%\n" +
      "CASE 1\n  r% = 10\n" +
      "CASE 2\n  r% = 20\n" +
      "CASE ELSE\n  r% = 99\n" +
      "END SELECT");

    IrPassManager.Standard().RunToFixpoint(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    // subject is constant 2: every comparison is proven and the chain collapses
    Assert.That(IrPrinter.Print(fn), Does.Not.Contain("icmp"));
  }
}
