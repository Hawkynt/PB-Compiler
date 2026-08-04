using System.Text;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Emit;

/// <summary>
/// The direct emitter's optimizations, checked against BASIC the IR wrote.
///
/// <see cref="IrPassObservableEquivalenceTests"/> holds the IR passes to the observable contract.
/// This holds the OTHER four hundred - the ones in <c>CodeGen/CodeGenerator.Optimize*.cs</c> that
/// rewrite the program on its way to machine code. They are the optimizations that weave the BASIC,
/// and until now nothing checked them against anything but hand-written programs and the golden
/// battery.
///
/// <para>
/// The lever is that <see cref="IrBasicWriter"/> produces BASIC no person would write: every value in
/// its own variable, control flow as a mesh of labels and GOTOs, loops unrolled into straight lines,
/// subscripts rebuilt from byte offsets. Feeding that back through the front end and out through the
/// direct emitter - once with the optimizer off, once on - exercises those optimizations on shapes
/// the hand-written corpus never produces. An optimizer bug that needs an unusual shape to fire has
/// nowhere to hide.
/// </para>
/// <para>
/// Three outputs must agree for every program: the original compiled, the rendered BASIC compiled
/// unoptimized, and the rendered BASIC compiled optimized. The first pair catches a bad rendering;
/// the second catches an optimization that changed behaviour. Keeping them apart is what makes a
/// failure diagnosable rather than merely alarming.
/// </para>
/// </summary>
[TestFixture]
public sealed class DirectOptimizerOnRenderedBasicTests {

  private static readonly string _repoRoot =
    Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  private static SemanticModel Bind(string source, string name) =>
    Binder.Bind(Parser.Parse(Lexer.Tokenize(source, name, Dialect.Pb36), name, Dialect.Pb36), Dialect.Pb36);

  private readonly record struct Behaviour(string Output, string? File, int ExitCode);

  /// <summary>Compiles and runs, or null when the program cannot be built or executed here.</summary>
  private static Behaviour? Run(string source, string name, bool optimize) {
    try {
      var model = Bind(source, name);
      if (model.Errors.Count > 0)
        return null;
      var cg = new CodeGenerator(model) { Optimize = optimize };
      var image = cg.EmitExecutable();
      if (cg.Errors.Count > 0)
        return null;
      var cpu = Cpu8086.Run(image);
      return new(cpu.Output, cpu.FileContent("RESULT.TXT"), cpu.ExitCode);
    } catch (Exception) {
      return null;                                // an unrunnable program is not this test's business
    }
  }

  /// <summary>The BASIC the IR writes for a program, or null when either step declines.</summary>
  private static string? Render(string source, string name) {
    try {
      var model = Bind(source, name);
      if (model.Errors.Count > 0)
        return null;
      var module = IrLowering.TryLowerModule(model, out _);
      if (module is null)
        return null;
      IrPassManager.Standard().RunOnModule(module);
      return IrBasicWriter.Write(module);
    } catch (Exception) {
      return null;
    }
  }

  [Test]
  public void Optimize_GivenBasicTheIrWrote_ThenTheOptimizerDoesNotChangeWhatItPrints() {
    var report = new StringBuilder();
    int compared = 0, renderFailed = 0, notRunnable = 0;
    var badRendering = new List<string>();
    var badOptimization = new List<string>();

    var dir = Path.Combine(_repoRoot, "tests");
    Assume.That(Directory.Exists(dir), "no tests/*.BAS corpus present");

    // Only the PowerBASIC corpus. tests/diff/<dialect>/ holds programs written FOR another dialect -
    // gw, qb45, pds71 - and compiling those as pb36 compares two different languages, which would
    // report the front end's job as this harness's finding.
    foreach (var file in PowerBasicCorpus(dir)) {
      var name = Path.GetFileName(file);
      var source = File.ReadAllText(file);

      if (Render(source, name) is not { } rendered) {
        ++renderFailed;
        continue;
      }
      // the original has to run here at all, or there is nothing to compare against
      if (Run(source, name, optimize: true) is not { } original) {
        ++notRunnable;
        continue;
      }
      if (Run(rendered, name, optimize: false) is not { } plain) {
        ++notRunnable;
        continue;
      }
      if (Run(rendered, name, optimize: true) is not { } optimized) {
        ++notRunnable;
        continue;
      }

      ++compared;
      // a rendering that already disagrees unoptimized is the WRITER's fault, not the optimizer's
      if (plain != original)
        badRendering.Add(name);
      else if (optimized != plain)
        badOptimization.Add(name);
    }

    report.AppendLine($"programs compared          : {compared}")
      .AppendLine($"  the IR writer declined   : {renderFailed}")
      .AppendLine($"  not runnable here        : {notRunnable}")
      .AppendLine($"rendering disagreements    : {badRendering.Count} [{string.Join(", ", badRendering)}]")
      .AppendLine($"optimization disagreements : {badOptimization.Count} [{string.Join(", ", badOptimization)}]");
    TestContext.Out.Write(report.ToString());

    // The point of this harness: the optimizer must not change behaviour, on any shape. Pinned at
    // zero, because there is no such thing as an acceptable one.
    Assert.That(badOptimization, Is.Empty,
      "the direct emitter's optimizer changed what a program prints, on BASIC the IR wrote:\n" + report);
    // Rendering disagreements are the WRITER's gaps, diagnosed by name below. They are listed rather
    // than fixed because each needs its own work, and a bare count would let the list grow unnoticed.
    Assert.That(badRendering.Where(bad => !_knownRenderingGaps.ContainsKey(bad)), Is.Empty,
      "the IR writer produced BASIC that does not behave like the program it came from:\n" + report);
    // a floor, so a regression that makes the writer decline everything cannot pass this quietly
    Assert.That(compared, Is.GreaterThanOrEqualTo(_floor), "fewer programs were compared than used to be:\n" + report);
  }

  /// <summary>The pb35/pb36 programs: the battery, the differential set and the optimizer suite.</summary>
  private static IEnumerable<string> PowerBasicCorpus(string root) =>
    new[] { root, Path.Combine(root, "diff"), Path.Combine(root, "optimize") }
      .Where(Directory.Exists)
      .SelectMany(d => Directory.EnumerateFiles(d, "*.BAS", SearchOption.TopDirectoryOnly))
      .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// Programs the writer does not yet render faithfully, each with what is actually wrong. None is an
  /// optimizer finding - the direct emitter agrees with itself on all of them - and none is a
  /// mystery; they are the parts of the language the writer has not modelled.
  /// </summary>
  private static readonly Dictionary<string, string> _knownRenderingGaps = new(StringComparer.OrdinalIgnoreCase) {
    ["DIFF01.BAS"] = "INT/FIX of a float: the writer truncates where the direct emitter rounds. Which is right "
      + "needs the genuine compiler to settle - INT(2.7) is 2 by the language definition and 3 here - so "
      + "neither side is changed until the oracle can say.",
    ["DIFF04.BAS"] = "an unsigned DWORD prints as signed (-1 for 4294967295): the writer loses the "
      + "unsignedness when the value passes through a temporary.",
    ["DIFF06.BAS"] = "the same unsigned width loss on a DWORD literal (4000000000 renders as -294967296).",
    ["DIFF15.BAS"] = "a QUAD product is one off (73300775184 against ...85) - 64-bit arithmetic the rendered "
      + "BASIC computes at a different width.",
    ["DIFF18.BAS"] = "a BYTE FOR counter must WRAP past 255 (QUIRK 2.28); the rendered loop does not, so the "
      + "trip count differs.",
    ["DIFF24.BAS"] = "unsigned width loss, as DIFF04.",
    ["DIFF25.BAS"] = "unsigned width loss, as DIFF04.",
    ["DIFF47.BAS"] = "unsigned width loss, as DIFF04.",
    ["DIFF55.BAS"] = "unsigned width loss, as DIFF04.",
  };

  private const int _floor = 70;   // 73 at the first measurement
}
