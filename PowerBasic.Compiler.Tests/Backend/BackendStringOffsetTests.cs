using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The string forms that take a POSITION, through the x86-16 back end: <c>ASC(s$, i)</c>, the
/// <c>ASC(s$, i) = code</c> statement, and the <c>CV*</c> family's optional starting offset.
///
/// All three are lowered as a composition rather than as a new runtime entry - the substring first,
/// then the conversion - because that is the sequence the direct emitter writes when it is not
/// optimizing, and it reuses entries whose register conventions are already pinned. Comparing against
/// the direct emitter rather than against literals is the point: the composition has to agree with
/// the reference, not merely look reasonable.
///
/// The assignment form is the one worth watching. A read of a string variable hands out an owned
/// COPY, so poking the value the expression lowering returned would edit a temporary and leave the
/// variable untouched - a bug that shows as "the string never changed" and not as a crash.
/// </summary>
[TestFixture]
public sealed class BackendStringOffsetTests {

  private static string Run(string body, bool routed) {
    var source = body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = true, UseExperimentalBackend = routed };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  private static void Agrees(string body) =>
    Assert.That(Run(body, routed: true), Is.EqualTo(Run(body, routed: false)));

  /// <summary>Every position, including 0 and one past the end, where the clamp is what differs.</summary>
  [Test]
  public void Asc_GivenAPosition_ThenTheRoutedPathAgreesWithTheDirectOne() => Agrees("""
    DIM s AS STRING
    s = "ABCDE"
    FOR i% = 0 TO 7
      PRINT ASC(s, i%);
    NEXT i%
    PRINT
    """);

  [Test]
  public void AscAssign_GivenAPosition_ThenTheVariableItselfChanges() => Agrees("""
    DIM s AS STRING
    s = "hello"
    ASC(s, 1) = 72
    ASC(s, 5) = 79
    PRINT s
    """);

  /// <summary>A position past the end is ignored rather than growing or faulting.</summary>
  [Test]
  public void AscAssign_GivenAnOutOfRangePosition_ThenNothingChanges() => Agrees("""
    DIM s AS STRING
    s = "abc"
    ASC(s, 0) = 88
    ASC(s, 9) = 88
    PRINT s; LEN(s)
    """);

  // No test for a bare ASC(s$) = code: the parser requires the position, so the AST's optional
  // Index is not reachable from source through this statement.

  [Test]
  public void Cvi_GivenAnOffset_ThenTheRoutedPathAgreesWithTheDirectOne() => Agrees("""
    DIM s AS STRING
    s = MKI$(258) + MKI$(-2)
    PRINT CVI(s); CVI(s, 3)
    """);

  [Test]
  public void Cvl_GivenAnOffset_ThenTheRoutedPathAgreesWithTheDirectOne() => Agrees("""
    DIM s AS STRING
    s = MKL$(70000) + MKL$(-70000)
    PRINT CVL(s); CVL(s, 5)
    """);

  [Test]
  public void Cvs_GivenAnOffset_ThenTheRoutedPathAgreesWithTheDirectOne() => Agrees("""
    DIM s AS STRING
    s = MKS$(1.5) + MKS$(-2.25)
    PRINT CVS(s); CVS(s, 5)
    """);
}
