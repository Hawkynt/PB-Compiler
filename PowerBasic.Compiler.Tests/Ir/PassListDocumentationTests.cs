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

  private static readonly string _middleEnd = Path.GetFullPath(Path.Combine(
    TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..",
    "PowerBasic.Compiler", "Ir", "Passes", "IrMiddleEndPipeline.cs"));

  [Test]
  public void PipelinePolicy_GivenTheProductionMiddleEnd_ThenEachEntryPointIsDefinedOnce() {
    var text = File.ReadAllText(_middleEnd);

    Assert.Multiple(() => {
      Assert.That(Regex.Matches(text, @"public static IrPassManager Standard\(").Count,
        Is.EqualTo(1), "Standard policy must have one owner");
      Assert.That(Regex.Matches(text, @"public static IrPassManager Legalize\(").Count,
        Is.EqualTo(1), "Legalize policy must have one owner");
    });
  }
}
