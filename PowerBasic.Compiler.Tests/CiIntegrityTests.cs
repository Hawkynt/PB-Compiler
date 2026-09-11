using System.Text.RegularExpressions;

namespace PowerBasic.Compiler.Tests;

/// <summary>
/// Coverage is easy to lose by accident and hard to notice afterwards.
///
/// A branch that cannot get green has three cheap ways out: widen the category filter so the failing
/// tests stop being selected, mark them <c>[Ignore]</c>, or delete the workflow that runs them. All
/// three leave a green tick. One branch in this repository's backlog removes sixty-four files,
/// including two workflows and about forty test files, and its pull request reads as a normal
/// feature. These assertions make each of those routes fail loudly instead.
/// </summary>
[TestFixture]
public sealed class CiIntegrityTests {

  private static readonly string _root = Path.GetFullPath(Path.Combine(
    TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  private static string Workflow(string name) =>
    File.ReadAllText(Path.Combine(_root, ".github", "workflows", name));

  /// <summary>
  /// The gate runs everything except the wall-clock tier. Any other filter means some part of the
  /// suite stopped being run, which is indistinguishable from it passing.
  /// </summary>
  [Test]
  public void Ci_GivenTheCoreTestStep_ThenItStillRunsEverythingButPerformance() {
    var ci = Workflow("ci.yml");
    var core = Regex.Match(ci, @"name:\s*Core tests.*?--filter\s*""([^""]+)""", RegexOptions.Singleline);

    Assert.Multiple(() => {
      Assert.That(core.Success, Is.True, "the Core tests step lost its filter, or the step is gone");
      Assert.That(core.Groups[1].Value, Is.EqualTo("TestCategory!=Performance"),
        "widening this filter silently stops running part of the suite");
      Assert.That(ci, Does.Contain("dotnet test"), "ci.yml no longer runs the tests at all");
    });
  }

  /// <summary>
  /// A skipped test reports as passing to everything that reads the run. There are none today, and a
  /// new one should be a deliberate decision rather than something that arrives inside a feature.
  /// </summary>
  [Test]
  public void Suite_GivenEveryFixture_ThenNothingIsSilencedWithIgnoreOrExplicit() {
    var silenced = Directory
      .EnumerateFiles(Path.Combine(_root, "PowerBasic.Compiler.Tests"), "*.cs", SearchOption.AllDirectories)
      .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                  && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
      .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"\[(Ignore|Explicit)\(")
        .Select(_ => Path.GetFileName(path)))
      .Distinct()
      .ToList();

    Assert.That(silenced, Is.Empty,
      "use Assert.Ignore with a reason for a genuinely unavailable dependency; do not silence a fixture");
  }

  /// <summary>The workflows the branch ruleset requires have to exist to be able to report.</summary>
  [Test]
  public void Workflows_GivenTheRequiredChecks_ThenTheirWorkflowsArePresent() {
    var workflows = Path.Combine(_root, ".github", "workflows");
    Assert.Multiple(() => {
      Assert.That(File.Exists(Path.Combine(workflows, "ci.yml")), Is.True, "ci.yml provides ubuntu-latest and windows-latest");
      Assert.That(File.Exists(Path.Combine(workflows, "smoke.yml")), Is.True, "smoke.yml is the fast tier");
      Assert.That(Workflow("ci.yml"), Does.Contain("syntax oracle"), "the PB35 / PDS syntax oracle is a required check");
    });
  }
}
