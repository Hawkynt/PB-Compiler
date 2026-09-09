using System.Text.RegularExpressions;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// The sentence in <see cref="IrPassManager"/> that lists which passes <c>Standard</c> leaves off has
/// now been duplicated three separate times, and never by anyone editing it twice.
///
/// Two branches each rewrote it, neither touching the other's lines, and git unioned them: the list
/// ended up printed twice with different omissions, followed by two contradictory sentences about what
/// the caller runs. No conflict was ever raised, both sides looked correct in review, and the result
/// reads as one paragraph that contradicts itself halfway through. Removing it by hand fixed it twice
/// and it came back both times, because nothing was watching. This is what watches.
/// </summary>
[TestFixture]
public sealed class PassListDocumentationTests {

  private static readonly string _passManager = Path.GetFullPath(Path.Combine(
    TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..",
    "PowerBasic.Compiler", "Ir", "Passes", "IrPassManager.cs"));

  [Test]
  public void PassList_GivenTheStandardSummary_ThenItIsWrittenExactlyOnce() {
    var text = File.ReadAllText(_passManager);

    Assert.Multiple(() => {
      Assert.That(Regex.Matches(text, @"Everything else in <see cref=""Standard""/> is optimization").Count,
        Is.EqualTo(1), "the pass-list sentence was duplicated - two branches rewrote it and the merge kept both");
      Assert.That(Regex.Matches(text, @"string/global module passes").Count,
        Is.EqualTo(1), "the pass list itself appears more than once");
    });
  }
}
