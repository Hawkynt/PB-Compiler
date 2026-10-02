using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// WRITE and SETEOF on the IR path.
///
/// <para>
/// The two got here by opposite routes. WRITE is composed entirely of runtime calls the IR could
/// already make - there is no inline anything in it - so it only ever needed writing down, item for
/// item. SETEOF is a bare DOS interrupt, so it needed a routine to call before it could be said at
/// all.
/// </para>
/// <para>
/// WRITE's formatting is the part worth testing rather than asserting. Quotes around strings, commas
/// between items, and numbers with the leading sign column stripped - each of those is a decision that
/// a plausible implementation gets wrong in a way that still compiles and still prints something.
/// </para>
/// </summary>
[TestFixture]
public sealed class WriteSetEofLoweringTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static (string Output, string? File) Run(string source) {
    var cg = new CodeGenerator(Bind(source)) { Optimize = true};
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    var cpu = Cpu8086.Run(image);
    return (cpu.Output, cpu.FileContent("OUT.TXT"));
  }

  private static readonly (string Name, string Source, string Output, string? File)[] _programs = [
    // WRITE to the console: the shape DIFF14 and DIFF57 use, with every item kind in one statement
    ("console", """
      WRITE 42, "hello", 3.5
      WRITE -7, "world", 2.25
      WRITE
      END
      """, "42,\"hello\",3.5\r\n-7,\"world\",2.25\r\n\r\n", null),
    ("to a file", """
      OPEN "OUT.TXT" FOR OUTPUT AS #1
      WRITE #1, 1, "two", 3.5
      WRITE #1, -7, "world", 2.25
      CLOSE #1
      PRINT "written"
      END
      """, "written\r\n", "1,\"two\",3.5\r\n-7,\"world\",2.25\r\n"),
    // a string with a comma in it must stay one field - the quotes are what make that readable back
    ("comma inside a string", """
      WRITE "a,b", 1
      END
      """, "\"a,b\",1\r\n", null),
    ("every numeric width", """
      DIM i AS INTEGER
      DIM l AS LONG
      DIM s AS SINGLE
      DIM d AS DOUBLE
      i = -32768
      l = 2000000000
      s = 1.5
      d = 1.25
      WRITE i, l, s, d
      END
      """, "-32768,2000000000,1.5,1.25\r\n", null),
    // SETEOF: write four lines, rewind two, truncate - the file must end where it was cut
    ("seteof truncates", """
      OPEN "OUT.TXT" FOR BINARY AS #1
      PUT$ #1, "abcdefghij"
      SEEK #1, 5
      SETEOF #1
      CLOSE #1
      OPEN "OUT.TXT" FOR BINARY AS #1
      PRINT LOF(1)
      CLOSE #1
      END
      """, " 5 \r\n", "abcde"),
  ];

  [Test]
  public void Lowering_GivenWriteAndSetEof_ThenTheModuleLowers() {
    foreach (var (name, source, _, _) in _programs) {
      var module = IrLowering.TryLowerModule(Bind(source), out var why);
      Assert.That(module, Is.Not.Null, $"'{name}' declined: {why}");
    }
  }

  /// <summary>
  /// The program must print - and leave behind - exactly the expected text. The file content is
  /// checked as well as the console, because for WRITE # the file IS the output.
  /// </summary>
  [Test]
  public void Routed_GivenWriteAndSetEof_ThenTheConsoleAndTheFileHoldTheExpectedText() {
    foreach (var (name, source, expectedOutput, expectedFile) in _programs) {
      var (output, file) = Run(source);
      Assert.That(output, Is.EqualTo(expectedOutput), $"'{name}' console output");
      Assert.That(file, Is.EqualTo(expectedFile), $"'{name}' file content");
    }
  }

  /// <summary>
  /// WRITE's format, stated rather than merely compared: quotes around strings, a comma between
  /// items, no sign column on a number - the statement whose whole purpose is its punctuation.
  /// </summary>
  [Test]
  public void Write_GivenMixedItems_ThenTheFormatIsPowerBasics() {
    var (output, _) = Run("""
      WRITE 42, "hello", -7
      END
      """);
    Assert.That(output.Trim(), Is.EqualTo("42,\"hello\",-7"));
  }
}
