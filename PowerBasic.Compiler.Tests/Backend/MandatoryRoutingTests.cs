using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Every corpus program compiles with routing mandatory: there is no fallback emitter, so a body the
/// back end does not take is a compile error carrying the routing's own reason, and this collects
/// those errors over the whole differential corpus.
///
/// <para>
/// This was the direct-emitter retirement question - would the corpus still compile without
/// <c>CodeGen/</c>? - asked while a fallback still hid every decline behind a program that compiled.
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
  public void Compile_GivenTheCorpusWithRoutingMandatory_ThenTheRoutingRefusesNoBody(bool optimize) {
    var refused = new List<string>();
    var programs = 0;
    foreach (var path in CorpusPrograms()) {
      ++programs;
      foreach (var message in MandatoryRoutingErrors(File.ReadAllText(path), optimize))
        refused.Add($"{Path.GetFileName(path)}: {message}");
    }

    Assume.That(programs, Is.GreaterThan(0), "no corpus programs found");
    Assert.That(refused, Is.Empty,
      $"{refused.Count} of {programs} corpus programs were refused by the mandatory routing:"
        + Environment.NewLine + string.Join(Environment.NewLine, refused.Take(25)));
  }
}
