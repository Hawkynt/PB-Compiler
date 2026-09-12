using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The direct-emitter retirement question, asked directly: would the corpus still compile if
/// <c>CodeGen/</c> were not there?
///
/// <para>
/// Every other gate answers it only indirectly, and a fallback is why. With one present a decline is
/// invisible - the program still compiles, the differential still agrees, the coverage census still
/// counts the body - because for that body BOTH sides ran the same emitter. <c>RequireBackend</c>
/// removes the fallback: a body the back end does not take becomes a compile error carrying the
/// routing's own reason.
/// </para>
/// <para>
/// A bodiless EXTERNAL declaration is exempt and that is not a loophole: it is a link import with no
/// code to emit on either path, so it is nobody's coverage.
/// </para>
/// </summary>
[TestFixture]
public sealed class MandatoryRoutingTests {

  private static readonly string _repoRoot =
    Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  private static IEnumerable<string> CorpusPrograms() {
    var diff = Path.Combine(_repoRoot, "tests", "diff");
    if (!Directory.Exists(diff))
      yield break;
    foreach (var path in Directory.EnumerateFiles(diff, "*.BAS", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
      yield return path;
  }

  private static IReadOnlyList<string> MandatoryRoutingErrors(string source, bool optimize) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    if (model.Errors.Count > 0)
      return [];   // rejected by the front end; it never reaches the routing and is nobody's coverage
    var generator = new CodeGenerator(model) {
      Optimize = optimize,
      UseExperimentalBackend = true,
      RequireBackend = true,
    };
    generator.EmitExecutable();
    return [.. generator.Errors.Select(e => e.Message).Where(m => m.Contains("routing is mandatory", StringComparison.Ordinal))];
  }

  /// <summary>
  /// Both optimizer modes, because they route differently: the optimizer changes calling conventions
  /// and absorbs calls, so a body that routes with it on can decline with it off.
  /// </summary>
  [TestCase(false)]
  [TestCase(true)]
  public void Compile_GivenTheCorpusWithRoutingMandatory_ThenNothingFallsBackToTheDirectEmitter(bool optimize) {
    var refused = new List<string>();
    var programs = 0;
    foreach (var path in CorpusPrograms()) {
      ++programs;
      foreach (var message in MandatoryRoutingErrors(File.ReadAllText(path), optimize))
        refused.Add($"{Path.GetFileName(path)}: {message}");
    }

    Assume.That(programs, Is.GreaterThan(0), "no corpus programs found");
    Assert.That(refused, Is.Empty,
      $"{refused.Count} of {programs} corpus programs would not compile without the direct emitter:"
        + Environment.NewLine + string.Join(Environment.NewLine, refused.Take(25)));
  }

  /// <summary>
  /// The premise, which the test above cannot establish on its own: a corpus that happens to contain
  /// nothing the routing refuses would pass it whether or not <c>RequireBackend</c> does anything at
  /// all. These two constructs DO decline, so they must fail - and must fail with the routing's own
  /// reason rather than some unrelated diagnostic.
  /// </summary>
  [TestCase("""
    SUB S(a$())
      a$(2) = a$(1) + "!"
    END SUB
    DIM v$(1 TO 2)
    v$(1) = "ab"
    S v$()
    PRINT v$(2)
    """, "element")]
  [TestCase("""
    FUNCTION F(BYVAL a%) AS BCD
      F = a% / 2
    END FUNCTION
    PRINT F(3)
    """, "return type outside the routed ABI")]
  public void Compile_GivenAConstructTheRoutingRefuses_ThenMandatoryRoutingFailsTheCompile(string source, string expected) {
    var refused = MandatoryRoutingErrors(source, optimize: false);

    Assert.That(refused, Is.Not.Empty, "RequireBackend accepted a construct the routing declines");
    Assert.That(string.Join(" | ", refused), Does.Contain(expected));
  }

  /// <summary>
  /// And the other half of the premise: the SAME construct compiles clean without
  /// <c>RequireBackend</c>, because the direct emitter picks it up. Without this, a mandatory-routing
  /// failure could not be distinguished from the program being broken.
  /// </summary>
  [Test]
  public void Compile_GivenAConstructTheRoutingRefuses_ThenTheOrdinaryBuildStillSucceeds() {
    const string source = """
      SUB S(a$())
        a$(2) = a$(1) + "!"
      END SUB
      DIM v$(1 TO 2)
      v$(1) = "ab"
      S v$()
      PRINT v$(2)
      """;
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var generator = new CodeGenerator(model) { Optimize = false, UseExperimentalBackend = true };

    var image = generator.EmitExecutable();

    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(image, Is.Not.Empty);
  }
}
