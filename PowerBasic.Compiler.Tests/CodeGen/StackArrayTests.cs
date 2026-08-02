using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 stack arrays: <c>DIM STACK a(1 TO 8) AS INTEGER</c> inside a procedure places the
/// array data in the stack frame ([BP-n]) instead of the data segment - reentrant scratch
/// storage with zero DGROUP footprint, freed on return. Compile-time bounds required.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class StackArrayTests {

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

  [Test]
  public void Parse_GivenStackArrayBelowPb36_WhenParsed_ThenRejected() {
    Assert.Throws<ParserException>(() =>
      Parser.Parse(Lexer.Tokenize("SUB S\n  DIM STACK a%(1 TO 4)\nEND SUB\n", "t.bas", Dialect.Pb35), "t.bas", Dialect.Pb35));
  }

  [Test]
  public void Bind_GivenModuleLevelStackArray_WhenBound_ThenError() {
    var model = Bind("DIM STACK a%(1 TO 4)\n");
    Assert.That(model.Errors, Is.Not.Empty, "a STACK array needs a procedure frame");
  }

  [Test]
  public void Bind_GivenDynamicBoundsStackArray_WhenBound_ThenError() {
    var model = Bind("SUB S(BYVAL n%)\n  DIM STACK a%(1 TO n%)\nEND SUB\n");
    Assert.That(model.Errors, Is.Not.Empty, "STACK arrays need compile-time bounds");
  }

  [Test]
  public void Bind_GivenLocalStackArray_WhenBound_ThenClassAndStorageRecorded() {
    var model = Bind("SUB S\n  DIM STACK a%(1 TO 4)\n  a%(1) = 1\nEND SUB\n");
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var proc = model.Procedures["S"];
    var symbol = proc.Variables.Values.Single(v => v.IsArray);
    Assert.Multiple(() => {
      Assert.That(symbol.ArrayClass, Is.EqualTo(ArrayClass.Stack));
      Assert.That(symbol.Storage, Is.EqualTo(VariableStorage.Local));
    });
  }

  [Test]
  public void Execute_GivenStackArrayFillAndSum_WhenRun_ThenElementAccessWorks() {
    const string source = """
      SUB Work
        DIM STACK a(1 TO 5) AS INTEGER
        DIM STACK b(0 TO 2) AS LONG
        DIM i AS INTEGER
        FOR i = 1 TO 5
          a(i) = i * i
        NEXT
        b(1) = 100000
        PRINT a(1); a(5); b(1); LBOUND(a); UBOUND(a)
      END SUB
      Work
      """;
    Assert.That(Run(source), Is.EqualTo(" 1  25  100000  1  5\n"));
  }

  [Test]
  public void Execute_GivenRecursionWithStackArray_WhenRun_ThenEachLevelHasItsOwnCopy() {
    // the whole point: a DGROUP-resident local array would be smashed by the recursive call
    const string source = """
      DECLARE FUNCTION Deep%(BYVAL n AS INTEGER)
      PRINT Deep%(1)
      FUNCTION Deep%(BYVAL n AS INTEGER)
        DIM STACK a(1 TO 3) AS INTEGER
        a(1) = n
        IF n < 3 THEN
          DIM sink AS INTEGER
          sink = Deep%(n + 1)
        END IF
        Deep% = a(1)
      END FUNCTION
      """;
    Assert.That(Run(source), Is.EqualTo(" 1\n"), "level 1 must see its own a(1) = 1 after the recursion returns");
  }

  [Test]
  public void Emit_GivenStackArrayFrame_WhenOptimized_ThenAllocationAndZeroFillAgree() {
    // the frame size reaches the image as a "constant label" - a pseudo-label whose position IS
    // the byte count. An image-shrinking pass (short-jump relaxation, the peephole) that slid it
    // like a real offset would allocate fewer bytes than the REP STOSW then zeroes, and the SUB
    // would run on a corrupted stack. The two counts must always agree: SUB SP,n / ... / REP
    // STOSW of n/2 words.
    const string source = """
      SUB Grid
        DIM STACK g(1 TO 3, 1 TO 4) AS INTEGER
        g(1, 1) = 5
        PRINT g(1, 1)
      END SUB
      Grid
      """;
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var image = new CodeGenerator(Binder.Bind(unit, Dialect.Pb36)).EmitExecutable();

    // MOV CX,bytes / SUB SP,CX / PUSH DS / POP ES / MOV DI,SP / MOV CX,words / XOR AX,AX / REP STOSW
    var frames = 0;
    for (var i = 3; i + 12 < image.Length; ++i) {
      if (image[i - 3] != 0xB9 || image[i] != 0x29 || image[i + 1] != 0xCC)
        continue;
      if (image[i + 2] != 0x1E || image[i + 3] != 0x07 || image[i + 4] != 0x89 || image[i + 5] != 0xE7
          || image[i + 6] != 0xB9 || image[i + 9] != 0x31 || image[i + 10] != 0xC0
          || image[i + 11] != 0xF3 || image[i + 12] != 0xAB)
        continue;
      var bytes = image[i - 2] | (image[i - 1] << 8);
      var words = image[i + 7] | (image[i + 8] << 8);
      Assert.That(bytes, Is.EqualTo(words * 2), $"frame at {i:X4}: allocates {bytes} bytes but zeroes {words} words");
      ++frames;
    }
    Assert.That(frames, Is.GreaterThan(0), "the zero-filled frame prologue must be present to be checked");
  }

  [Test]
  public void Execute_GivenRank2StackArray_WhenRun_ThenLinearizationCorrect() {
    const string source = """
      SUB Grid
        DIM STACK g(1 TO 3, 1 TO 4) AS INTEGER
        DIM r AS INTEGER, c AS INTEGER
        FOR r = 1 TO 3
          FOR c = 1 TO 4
            g(r, c) = r * 10 + c
          NEXT
        NEXT
        PRINT g(1, 1); g(2, 3); g(3, 4)
      END SUB
      Grid
      """;
    Assert.That(Run(source), Is.EqualTo(" 11  23  34\n"));
  }

  [Test]
  public void Render_GivenStackArray_WhenDecompiled_ThenPlainDimRecompilesUnderPb35() {
    const string source = "SUB S\n  DIM STACK a%(1 TO 4)\n  a%(2) = 7\n  PRINT a%(2)\nEND SUB\nS\n";
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty);
    var basic = PowerBasic.Compiler.Emit.BasicWriter.Render(model, unit);
    Assert.That(basic, Does.Not.Contain("STACK"), "pb35 knows no STACK class - the array decompiles as a plain static DIM");
    var unit2 = Parser.Parse(Lexer.Tokenize(basic, "rt.bas", Dialect.Pb35), "rt.bas", Dialect.Pb35);
    var model2 = Binder.Bind(unit2, Dialect.Pb35);
    Assert.That(model2.Errors, Is.Empty, $"pb35 re-bind of:\n{basic}\nerrors: " + string.Join("; ", model2.Errors));
  }
}
