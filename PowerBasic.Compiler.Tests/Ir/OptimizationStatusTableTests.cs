using System.Text;
using System.Text.RegularExpressions;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// The optimization table in the top-level README is a view of the pages under
/// <c>docs/optimizations/</c>, not a second place to record status.
///
/// It had drifted badly: 125 of its rows disagreed with the page they linked to, almost all of them
/// showing an optimization as planned that had already shipped, and one page had no row at all.
/// Nothing catches that by reading either file alone - the README looks internally consistent and so
/// does every page. This fixture reads both and fails when they disagree, so the answer to "what is
/// implemented" cannot depend on which document the reader happened to open.
/// </summary>
[TestFixture]
public sealed class OptimizationStatusTableTests {

  private static readonly string _root = Path.GetFullPath(Path.Combine(
    TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  private static readonly Regex _pageStatus =
    new(@"^\|\s*\*\*Status\*\*\s*\|\s*(\S+)", RegexOptions.Multiline);

  private static readonly Regex _tableRow =
    new(@"^\|\s*(\S+)\s*\|\s*\[([A-Z]\d{4})\]\(docs/optimizations/", RegexOptions.Multiline);

  private static Dictionary<string, string> PageStatuses() {
    var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var path in Directory.EnumerateFiles(Path.Combine(_root, "docs", "optimizations"), "*.md")) {
      var id = Path.GetFileName(path).Split('-')[0];
      if (!Regex.IsMatch(id, @"^[A-Z]\d{4}$"))
        continue;                                   // the index page itself
      var match = _pageStatus.Match(File.ReadAllText(path));
      Assert.That(match.Success, Is.True, $"{id} has no Status row");
      statuses[id] = match.Groups[1].Value;
    }
    return statuses;
  }

  [Test]
  public void Readme_GivenEveryOptimizationPage_ThenTheTableAgreesWithIt() {
    var pages = PageStatuses();
    var readme = File.ReadAllText(Path.Combine(_root, "README.md"));
    var rows = _tableRow.Matches(readme)
      .ToDictionary(m => m.Groups[2].Value, m => m.Groups[1].Value, StringComparer.Ordinal);

    var disagreeing = new StringBuilder();
    foreach (var (id, icon) in rows.OrderBy(entry => entry.Key, StringComparer.Ordinal))
      if (pages.TryGetValue(id, out var page) && page != icon)
        disagreeing.AppendLine($"  {id}: README says {icon}, {id}'s page says {page}");

    Assert.Multiple(() => {
      Assert.That(disagreeing.ToString(), Is.Empty,
        "the README table is a view of the pages; update the page, then the table");
      Assert.That(pages.Keys.Except(rows.Keys, StringComparer.Ordinal), Is.Empty,
        "every optimization page needs a row in the README table");
      Assert.That(rows.Keys.Except(pages.Keys, StringComparer.Ordinal), Is.Empty,
        "every README row needs the page it links to");
    });
  }

  [Test]
  public void Readme_GivenTheStatusTally_ThenItCountsThePages() {
    var pages = PageStatuses();
    var readme = File.ReadAllText(Path.Combine(_root, "README.md"));
    var tally = Regex.Match(readme,
      @"^\|\s*\*\*all\*\*\s*\|\s*\*\*(\d+)\*\*\s*\|\s*\*\*(\d+)\*\*\s*\|\s*\*\*(\d+)\*\*\s*\|\s*\*\*(\d+)\*\*",
      RegexOptions.Multiline);
    Assert.That(tally.Success, Is.True, "the README lost its status tally");

    Assert.Multiple(() => {
      Assert.That(int.Parse(tally.Groups[1].Value), Is.EqualTo(pages.Values.Count(s => s == "✅")), "implemented");
      Assert.That(int.Parse(tally.Groups[2].Value), Is.EqualTo(pages.Values.Count(s => s == "\U0001F7E1")), "partial");
      Assert.That(int.Parse(tally.Groups[3].Value), Is.EqualTo(pages.Values.Count(s => s == "⬜")), "planned");
      Assert.That(int.Parse(tally.Groups[4].Value), Is.EqualTo(pages.Count), "total");
    });
  }
}
