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
///
/// <para>
/// <b>This fixture used to carry its own premise, and no longer can.</b> Two tests sat below: one
/// compiled a construct the routing refused and required the compile to fail with the routing's own
/// reason, the other required the SAME construct to build clean without <c>RequireBackend</c>, so a
/// mandatory-routing failure could be told apart from a broken program. Their subject kept expiring
/// as classes closed - procedure-local error handling, then FASTCALL, then an array parameter, then
/// a BCD result, then <c>ERASE</c> of an ABSOLUTE array - and with that last one routing, there is
/// no construct left to point them at. They said so themselves and asked to be deleted rather than
/// re-pointed, which is what happened: a routing that refuses nothing cannot demonstrate refusing.
/// The census in <c>BackendCoverageTests</c> is what now shows the measurement is live.
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
}
