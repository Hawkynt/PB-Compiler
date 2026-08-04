using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 array slices: <c>b() = a(lo TO hi)</c> copies the slice into a dynamic array
/// (REDIM 0-based + element loop), and <c>FOR EACH v IN a(lo TO hi)</c> iterates it.
/// Bounds are runtime expressions, omissible (LBOUND/UBOUND) or from-end (<c>^n</c>).
/// Lowered entirely in the binder - pb35 sees plain loops.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ArraySliceTests {

  private static SemanticModel Bind(string source, Dialect dialect = Dialect.Pb36) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", dialect), "t.bas", dialect);
    return Binder.Bind(unit, dialect);
  }

  private static string Run(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return DosBoxRunner.Normalize(DosBoxRunner.Run(exe));
  }

  private const string _FILLED = """
    DIM a(1 TO 8) AS INTEGER
    DIM i AS INTEGER
    FOR i = 1 TO 8
      a(i) = i * 10
    NEXT

    """;

  [Test]
  public void Execute_GivenSliceCopy_WhenRun_ThenDynamicTargetHoldsTheWindow() {
    const string source = _FILLED + """
      DIM b() AS INTEGER
      b() = a(3 TO 6)
      PRINT LBOUND(b); UBOUND(b); b(0); b(3)
      """;
    Assert.That(Run(source), Is.EqualTo(" 0  3  30  60\n"));
  }

  [Test]
  public void Execute_GivenOmittedAndFromEndBounds_WhenRun_ThenDefaultsApply() {
    const string source = _FILLED + """
      DIM b() AS INTEGER, c() AS INTEGER, d() AS INTEGER
      b() = a(TO 3)
      c() = a(6 TO)
      d() = a(^3 TO ^2)
      PRINT b(0); UBOUND(b); c(0); UBOUND(c); d(0); d(1); UBOUND(d)
      """;
    // a(TO 3) = 10,20,30; a(6 TO) = 60,70,80; a(^3 TO ^2) = elements 6..7 = 60,70
    Assert.That(Run(source), Is.EqualTo(" 10  2  60  2  60  70  1\n"));
  }

  [Test]
  public void Execute_GivenRuntimeBoundsAndForEach_WhenRun_ThenSliceIterates() {
    const string source = _FILLED + """
      DIM lo AS INTEGER, v AS INTEGER, total AS LONG
      lo = 5
      FOR EACH v IN a(lo TO 7)
        total = total + v
      NEXT
      PRINT total
      """;
    Assert.That(Run(source), Is.EqualTo(" 180\n"));
  }

  [Test]
  public void Bind_GivenSliceInExpressionContext_WhenBound_ThenError() {
    var model = Bind(_FILLED + "PRINT a(2 TO 4)\n");
    Assert.That(model.Errors.Any(e => e.Message.Contains("slice")), Is.True, string.Join("; ", model.Errors));
  }

  [Test]
  public void Bind_GivenSliceIntoStaticArray_WhenBound_ThenError() {
    var model = Bind(_FILLED + "DIM s(3) AS INTEGER\ns() = a(2 TO 4)\n");
    Assert.That(model.Errors.Any(e => e.Message.Contains("dynamic")), Is.True, string.Join("; ", model.Errors));
  }

  [Test]
  public void Parse_GivenSliceBelowPb36_WhenParsed_ThenRejected() {
    Assert.Throws<ParserException>(() =>
      Parser.Parse(Lexer.Tokenize("b() = a(2 TO 4)\n", "t.bas", Dialect.Pb35), "t.bas", Dialect.Pb35));
  }

  [Test]
  public void Render_GivenSlices_WhenDecompiled_ThenPlainLoopsRecompileUnderPb35() {
    var source = _FILLED + "DIM b() AS INTEGER\nb() = a(2 TO ^2)\nPRINT b(0); UBOUND(b)\n";
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var basic = PowerBasic.Compiler.Emit.PowerBasic35Emitter.Render(model, unit);
    Assert.That(basic, Does.Contain("REDIM").And.Not.Contain(" TO ^"), $"slice lowers to REDIM + loop:\n{basic}");
    var unit2 = Parser.Parse(Lexer.Tokenize(basic, "rt.bas", Dialect.Pb35), "rt.bas", Dialect.Pb35);
    var model2 = Binder.Bind(unit2, Dialect.Pb35);
    Assert.That(model2.Errors, Is.Empty, $"pb35 re-bind of:\n{basic}\nerrors: " + string.Join("; ", model2.Errors));
  }
}
