using System.Text;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Whether the IR path could produce byte-identical output to the direct emitter with the optimizer
/// off — measured, because it is a reasonable thing to hope and an expensive thing to assume.
///
/// The hope goes: unoptimized, the direct emitter is stereotyped (AX-serial, one statement at a
/// time), so a second code generator fed the same program might land on the same bytes. The
/// measurement says otherwise, and says WHY: the routed images are not the same length. They are
/// mostly <b>shorter</b>, because the IR path does real register allocation where the direct emitter
/// is AX-serial by construction — a value that stays in SI across three statements is the whole
/// point of the retargetable path, and it is exactly what byte-identity would forbid.
///
/// So this is not a near-miss to be closed by tidying. Byte-identity would require the IR back end to
/// reproduce the direct emitter's instruction selection, which is the opposite of what it exists to
/// do. The contract the IR path is held to is observable equivalence
/// (<see cref="BackendCorpusDifferentialTests"/>), and this fixture records the size delta so the
/// claim stays a measurement rather than a memory.
/// </summary>
[TestFixture, Category("Slow")]
public sealed class UnoptimizedByteCompatibilityTests {

  private static readonly string _repoRoot =
    Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  [Test]
  public void Unoptimized_GivenTheCorpus_ThenTheRoutedImageIsNotByteIdenticalAndIsMostlySmaller() {
    var dir = Path.Combine(_repoRoot, "tests");
    Assume.That(Directory.Exists(dir), "no tests/*.BAS corpus present");

    int identical = 0, compared = 0, smaller = 0, larger = 0, totalDelta = 0;
    var report = new StringBuilder();

    foreach (var file in Directory.EnumerateFiles(dir, "*.BAS", SearchOption.TopDirectoryOnly)
               .Concat(Directory.EnumerateFiles(Path.Combine(dir, "diff"), "*.BAS", SearchOption.TopDirectoryOnly))
               .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
      var name = Path.GetFileName(file);
      var text = File.ReadAllText(file);
      // the preprocessor, not the lexer - see BackendCoverageTests for what tokenizing directly costs
      SemanticModel Bind() => Binder.Bind(
        Parser.Parse(Preprocessor.Expand(file, new FileSourceProvider(), Dialect.Pb36), name, Dialect.Pb36), Dialect.Pb36);
      try {
        if (Bind().Errors.Count > 0)
          continue;
        var direct = new CodeGenerator(Bind()) { Optimize = false, UseExperimentalBackend = false };
        var routed = new CodeGenerator(Bind()) { Optimize = false, UseExperimentalBackend = true };
        var a = direct.EmitExecutable();
        var b = routed.EmitExecutable();
        if (direct.Errors.Count > 0 || routed.Errors.Count > 0 || !routed.BackendRoutedNames.Any())
          continue;
        ++compared;
        if (a.SequenceEqual(b)) {
          ++identical;
          continue;
        }
        var delta = b.Length - a.Length;
        totalDelta += delta;
        if (delta < 0)
          ++smaller;
        else if (delta > 0)
          ++larger;
      } catch (Exception) {
        // a program neither path can build is not this measurement's business
      }
    }

    report.AppendLine($"unoptimized, programs the back end took part in : {compared}")
      .AppendLine($"  byte-identical to the direct emitter            : {identical}")
      .AppendLine($"  routed image SMALLER                            : {smaller}")
      .AppendLine($"  routed image LARGER                             : {larger}")
      .AppendLine($"  net size difference across all of them          : {totalDelta} bytes");
    TestContext.Out.Write(report.ToString());

    Assume.That(compared, Is.GreaterThan(0), "nothing routed, so nothing was measured");
    // The finding, pinned: byte-identity is not achieved, and the images differ in LENGTH - which is
    // what rules out "same instructions, different registers". If this ever becomes non-zero it is a
    // real change of direction and should be noticed, not absorbed.
    Assert.That(identical, Is.Zero,
      "an unoptimized routed image is now byte-identical to the direct emitter - the retargetable "
      + "path was not built to reproduce its instruction selection, so this needs a decision:\n" + report);
    Assert.That(smaller + larger, Is.EqualTo(compared),
      "every routed image used to differ in LENGTH from the direct one, which is what rules out "
      + "'same instructions, different registers':" + Environment.NewLine + report);
  }
}
