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
    // The frame size and the zero fill are two numbers in the prologue, and an image-shrinking pass
    // that moved one without the other would zero bytes the frame never allocated - the SUB would run
    // on a corrupted stack. SUB SP,alloc / PUSH DS / POP ES / LEA DI,[BP-d] (or MOV DI,SP: d = alloc)
    // / MOV CX,words / XOR AX,AX / REP STOSW must keep the filled [BP-d, BP-d+2*words) inside
    // [BP-alloc, BP). The SUB takes an opaque argument from two call sites, so its frame survives.
    const string source = """
      DECLARE SUB Grid(BYVAL k%)
      Grid INP(&H60)
      Grid 2
      SUB Grid(BYVAL k%) NOINLINE
        DIM STACK g(1 TO 3, 1 TO 4) AS INTEGER
        g(1, k% AND 3) = 5
        PRINT g(1, 1); g(2, k% AND 3)
      END SUB
      """;
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var image = new CodeGenerator(Binder.Bind(unit, Dialect.Pb36)).EmitExecutable();

    var frames = 0;
    for (var i = 0; i + 16 < image.Length; ++i) {
      int alloc, at;
      if (image[i] == 0x83 && image[i + 1] == 0xEC) { alloc = image[i + 2]; at = i + 3; }
      else if (image[i] == 0x81 && image[i + 1] == 0xEC) { alloc = image[i + 2] | (image[i + 3] << 8); at = i + 4; }
      else continue;                                   // SUB SP,imm8 / SUB SP,imm16
      if (image[at] != 0x1E || image[at + 1] != 0x07)
        continue;                                      // PUSH DS / POP ES
      int depth;
      if (image[at + 2] == 0x89 && image[at + 3] == 0xE7) { depth = alloc; at += 4; }                  // MOV DI,SP
      else if (image[at + 2] == 0x8D && image[at + 3] == 0x7E) { depth = -(sbyte)image[at + 4]; at += 5; } // LEA DI,[BP+d8]
      else continue;
      if (image[at] != 0xB9 || image[at + 3] != 0x31 || image[at + 4] != 0xC0 || image[at + 5] != 0xF3 || image[at + 6] != 0xAB)
        continue;                                      // MOV CX,words / XOR AX,AX / REP STOSW
      var words = image[at + 1] | (image[at + 2] << 8);
      Assert.That(depth, Is.LessThanOrEqualTo(alloc), $"frame at {i:X4}: the fill starts below the {alloc} allocated bytes");
      Assert.That(words * 2, Is.LessThanOrEqualTo(depth), $"frame at {i:X4}: {words} words from BP-{depth} run past BP");
      ++frames;
    }
    Assert.That(frames, Is.GreaterThan(0), "the zero-filled frame prologue must be present to be checked");
  }

  /// <summary>
  /// The behaviour the frame prologue above exists for, asserted where it can be asserted of BOTH
  /// paths: a STACK array starts zeroed, even when the frame it lands in was just written over.
  ///
  /// <para>
  /// One SUB called twice at the same depth is what makes that a test rather than a hope - the two
  /// invocations get the same frame bytes, so the second reads exactly what the first left unless
  /// something clears them.
  /// </para>
  /// </summary>
  [Test]
  public void Execute_GivenAStackArrayReusingAFrame_ThenItStartsZeroed() {
    const string source = """
      SUB Both(BYVAL fill AS INTEGER)
        DIM STACK g(1 TO 8) AS INTEGER
        DIM i AS INTEGER, s AS INTEGER
        IF fill THEN
          FOR i = 1 TO 8
            g(i) = 99
          NEXT i
        END IF
        FOR i = 1 TO 8
          s = s + g(i)
        NEXT i
        PRINT s;
      END SUB
      Both(-1)
      Both(0)
      """;

    Assert.That(Run(source), Is.EqualTo(" 792  0"),
      "the first call fills the frame and the second must still see zeroes");
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
    var basic = PowerBasic.Compiler.Emit.PowerBasic35Emitter.Render(model, unit);
    Assert.That(basic, Does.Not.Contain("STACK"), "pb35 knows no STACK class - the array decompiles as a plain static DIM");
    var unit2 = Parser.Parse(Lexer.Tokenize(basic, "rt.bas", Dialect.Pb35), "rt.bas", Dialect.Pb35);
    var model2 = Binder.Bind(unit2, Dialect.Pb35);
    Assert.That(model2.Errors, Is.Empty, $"pb35 re-bind of:\n{basic}\nerrors: " + string.Join("; ", model2.Errors));
  }
}
