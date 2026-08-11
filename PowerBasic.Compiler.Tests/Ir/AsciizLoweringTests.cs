using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// <c>ASCIIZ * n</c> on the IR path.
///
/// <para>
/// It stores like a fixed string and behaves like nothing else: the buffer is n bytes, but the VALUE
/// is whatever precedes the first NUL. So an assignment truncates to n-1 characters and terminates
/// rather than padding to n, and <c>LEN</c> counts to the NUL while <c>SIZEOF</c> reports the
/// capacity. Every one of those is a place where treating it as a fixed string would compile, run,
/// and answer wrongly only for values that do not happen to fill the buffer.
/// </para>
/// </summary>
[TestFixture]
public sealed class AsciizLoweringTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static string Run(string source, bool routed) {
    var cg = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = routed };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output;
  }

  private static IEnumerable<string> RoutedNames(string source) {
    var cg = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };
    cg.EmitExecutable();
    return cg.BackendRoutedNames.ToList();
  }

  /// <summary>DIFF07's own shapes: a bare variable, a record field, truncation, LEN and SIZEOF.</summary>
  private const string _SOURCE = """
    TYPE Rec
      Tag AS ASCIIZ * 8
      Num AS INTEGER
    END TYPE
    DIM z AS ASCIIZ * 6
    DIM r AS Rec
    z = "HelloWorld"
    PRINT z; LEN(z); SIZEOF(z)
    z = "Hi"
    PRINT z; LEN(z)
    PRINT z + "!"
    r.Tag = "ABC"
    r.Num = 42
    PRINT r.Tag; LEN(r.Tag); SIZEOF(r.Tag); LEN(r); r.Num
    END
    """;

  [Test]
  public void Lowering_GivenAsciiz_ThenTheModuleLowers() {
    var module = IrLowering.TryLowerModule(Bind(_SOURCE), out var why);
    Assert.That(module, Is.Not.Null, $"declined: {why}");
  }

  [Test]
  public void Routed_GivenAsciiz_ThenItMatchesTheDirectEmitter()
    => Assert.That(Run(_SOURCE, routed: true), Is.EqualTo(Run(_SOURCE, routed: false)));

  [Test]
  public void Routed_GivenAsciiz_ThenTheBackEndOwnsTheBody()
    => Assert.That(RoutedNames(_SOURCE), Does.Contain("main"));

  /// <summary>
  /// The three answers that separate ASCIIZ from a fixed string, stated rather than only compared.
  /// An ASCIIZ * 6 holds five characters and a NUL, so a ten-character value truncates to five;
  /// LEN reports what is there and SIZEOF reports the capacity, and they differ.
  /// </summary>
  [Test]
  public void Asciiz_GivenAnOversizedValue_ThenItTruncatesAndTerminates() {
    var output = Run("""
      DIM z AS ASCIIZ * 6
      z = "HelloWorld"
      PRINT z
      PRINT LEN(z)
      PRINT SIZEOF(z)
      END
      """, routed: true).Replace("\r\n", "\n").Trim().Split('\n');
    Assert.That(output[0].Trim(), Is.EqualTo("Hello"), "five characters, then the NUL");
    Assert.That(output[1].Trim(), Is.EqualTo("5"), "LEN counts to the NUL");
    Assert.That(output[2].Trim(), Is.EqualTo("6"), "SIZEOF reports the capacity");
  }

  /// <summary>
  /// A SHORT value must not be padded. A fixed string would answer 6 here, and reading it back would
  /// carry four trailing spaces into every concatenation.
  /// </summary>
  [Test]
  public void Asciiz_GivenAShortValue_ThenNothingIsPadded() {
    var output = Run("""
      DIM z AS ASCIIZ * 6
      z = "Hi"
      PRINT LEN(z)
      PRINT z + "!"
      END
      """, routed: true).Replace("\r\n", "\n").Trim().Split('\n');
    Assert.That(output[0].Trim(), Is.EqualTo("2"));
    Assert.That(output[1].Trim(), Is.EqualTo("Hi!"));
  }
}
