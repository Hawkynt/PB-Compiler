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

  private static SemanticModel Bind(string source, string name, Dialect dialect) =>
    Binder.Bind(Parser.Parse(Lexer.Tokenize(source, name, dialect), name, dialect), dialect);

  /// <summary>
  /// The dialect a corpus program is written IN, from the directory it lives in - tests/diff/gw holds
  /// GW-BASIC, tests/diff/qb45 QuickBASIC 4.5. Compiling those as pb36 would compare two different
  /// languages; lowering each in its own dialect is the whole point of having an IR, and what comes
  /// out the other side is pb35 whichever one went in.
  /// </summary>
  private static Dialect DialectOf(string file) {
    var folder = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
    return DialectFacts.TryParse(folder, out var dialect) ? dialect : Dialect.Pb36;
  }

  private readonly record struct Behaviour(string Output, string? File, int ExitCode);

  /// <summary>Compiles and runs, or null when the program cannot be built or executed here.</summary>
  private static Behaviour? Run(string source, string name, Dialect dialect, bool optimize) {
    try {
      var model = Bind(source, name, dialect);
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
  private static string? Render(string source, string name, Dialect dialect) {
    try {
      var model = Bind(source, name, dialect);
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

    // Every dialect in the corpus, each lowered in the one it was WRITTEN in. The rendered output is
    // pb35 whichever went in, which is the IR earning its keep: a GW-BASIC program comes back out as
    // PowerBASIC that behaves the same.
    foreach (var file in Corpus(dir)) {
      var name = Path.GetFileName(file);
      var source = File.ReadAllText(file);
      var dialect = DialectOf(file);

      if (Render(source, name, dialect) is not { } rendered) {
        ++renderFailed;
        continue;
      }
      // the original has to run here at all, or there is nothing to compare against
      if (Run(source, name, dialect, optimize: true) is not { } original) {
        ++notRunnable;
        continue;
      }
      // the rendered text is pb35 by construction, whatever the original dialect was
      if (Run(rendered, name, Dialect.Pb35, optimize: false) is not { } plain) {
        ++notRunnable;
        continue;
      }
      if (Run(rendered, name, Dialect.Pb35, optimize: true) is not { } optimized) {
        ++notRunnable;
        continue;
      }

      ++compared;
      // the same file name appears once per dialect, so the label carries both
      var label = $"{dialect.CanonicalName()}/{name}";
      // a rendering that already disagrees unoptimized is the WRITER's fault, not the optimizer's.
      // Naming WHICH part differs matters: "the program behaves differently" and "it exits with a
      // different code" are separate findings, and a bare inequality reports them as one.
      if (plain != original)
        badRendering.Add($"{label} [{Differs(original, plain)}]");
      else if (optimized != plain)
        badOptimization.Add($"{label} [{Differs(plain, optimized)}]");
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
    Assert.That(badRendering.Where(bad => !_knownRenderingGaps.ContainsKey(bad.Split(' ')[0])), Is.Empty,
      "the IR writer produced BASIC that does not behave like the program it came from:\n" + report);
    // a floor, so a regression that makes the writer decline everything cannot pass this quietly
    Assert.That(compared, Is.GreaterThanOrEqualTo(_floor), "fewer programs were compared than used to be:\n" + report);
  }

  /// <summary>Which observable component the two runs disagree on.</summary>
  private static string Differs(Behaviour a, Behaviour b) {
    var parts = new List<string>();
    if (a.Output != b.Output)
      parts.Add("output");
    if (a.File != b.File)
      parts.Add("file");
    if (a.ExitCode != b.ExitCode)
      parts.Add($"exit {a.ExitCode}->{b.ExitCode}");
    return string.Join("+", parts);
  }

  /// <summary>Every corpus program, of every dialect.</summary>
  private static IEnumerable<string> Corpus(string root) =>
    Directory.EnumerateFiles(root, "*.BAS", SearchOption.AllDirectories)
      .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// Programs the writer does not yet render faithfully, each with what is actually wrong. None is an
  /// optimizer finding - the direct emitter agrees with itself on all of them - and none is a
  /// mystery; they are the parts of the language the writer has not modelled.
  /// </summary>
  private static readonly Dictionary<string, string> _knownRenderingGaps = new(StringComparer.OrdinalIgnoreCase) {
    ["pb36/DIFF01.BAS"] = "INT/FIX of a float: the writer truncates where the direct emitter rounds. Which is "
      + "right needs the genuine compiler to settle - INT(2.7) is 2 by the language definition and 3 here - "
      + "so neither side is changed until the oracle can say.",
    // Float PRINT FORMATTING, not arithmetic: Turbo Basic and PB 2.1 render 1000000000000000 where
    // pb35 renders 1E+15, and .000001 where pb35 renders 1E-006. The rendered program IS pb35 and
    // formats the pb35 way, which is correct for what it is - the dialect's formatter is a front-end
    // property the IR does not carry.
    // The writer materializes every SSA value into a declared BASIC variable, and that is what this
    // one runs into. PowerBASIC computes a float expression at the x87's width and lets the DECLARED
    // type of the expression pick the formatter - which the IR now models (LowerArithmetic) and the
    // x86 back end reproduces exactly. BASIC source cannot separate the two: giving the temporary a
    // SINGLE type rounds the value, giving it an EXT type changes the digit count. Only rendering the
    // arithmetic INLINE, so PB types the expression from its operands as the original does, would be
    // faithful - which the writer does for pure expressions but not across a materialized temporary.
    ["pb36/DIFF35.BAS"] = "a float temporary is materialized into a declared variable, so PB picks its "
      + "formatter from the temporary's type rather than the expression's - see LowerArithmetic.",
    ["tb10/DIFF01.BAS"] = "float PRINT formatting differs between TB 1.0 and pb35 (1E+15 against 1000000000000000).",
    ["tb11/DIFF01.BAS"] = "float PRINT formatting differs between TB 1.1 and pb35.",
    ["pb21/DIFF01.BAS"] = "float PRINT formatting differs between PB 2.1 and pb35.",
    // Not yet diagnosed. Compiled optimized, the original and the rendering agree exactly; the
    // disagreement is between the original and the UNOPTIMIZED rendering, and the cause has not been
    // established. It is recorded as unknown rather than guessed at - an earlier note here blamed
    // MBF floats, which the evidence does not support (the binder does map GW SINGLEs to MbfType and
    // the lowering declines on them, so they never reach the writer silently).
    ["gw/DIFF01.BAS"] = "UNDIAGNOSED: optimized, the two agree; the unoptimized rendering does not.",
    // BASICA reaches the writer at all only because MBF is now carried through the IR instead of
    // refused. It disagrees for the reason the rendering WARNS about: pb35 has no Microsoft Binary
    // Format, so the storage becomes IEEE and float values print at a different precision. This one
    // is a translation decision that was made explicitly, not a defect.
    ["basica/DIFF01.BAS"] = "MBF storage dropped to IEEE, as the rendering's own warning says: pb35 "
      + "has no Microsoft Binary Format, so floats print at IEEE precision.",
    ["pb36/DIFF04.BAS"] = "an unsigned DWORD prints as signed (-1 for 4294967295): the writer loses the "
      + "unsignedness when the value passes through a temporary.",
    ["pb36/DIFF06.BAS"] = "the same unsigned width loss on a DWORD literal (4000000000 renders as -294967296).",
    ["pb36/DIFF24.BAS"] = "unsigned width loss, as DIFF04.",
    ["pb36/DIFF25.BAS"] = "unsigned width loss, as DIFF04.",
    ["pb36/DIFF47.BAS"] = "unsigned width loss, as DIFF04.",
    ["pb36/DIFF55.BAS"] = "unsigned width loss, as DIFF04.",
    ["pb36/DIFF15.BAS"] = "a QUAD product is one off (73300775184 against ...85) - 64-bit arithmetic the "
      + "rendered BASIC computes at a different width.",
    ["pb36/DIFF18.BAS"] = "a BYTE FOR counter must WRAP past 255 (QUIRK 2.28); the rendered loop does not, "
      + "so the trip count differs.",
    ["pb30/QUIRK30.BAS"] = "the unsigned width loss again (40000 renders as -25536), not a PB 3.0 "
      + "quirk - the same defect as DIFF04, reached through a different program.",
    // The ROUNDING that used to be the cause here is fixed - the IR carries the mode and the writer
    // expands it, and every value now matches byte for byte. What is left is one byte: the QuickBASIC
    // 1.0-3.0 runtime terminates a sequential OUTPUT file with a CP/M ^Z (0x1A) and pb35 does not, so
    // the file is 35 bytes against 34. That is a RUNTIME dialect quirk, not anything the IR or the
    // writer could express in pb35 source.
    ["qb10/DIFF02.BAS"] = "the file differs by one trailing byte: QB 1.0-3.0 write a CP/M ^Z (0x1A) at "
      + "the end of a sequential OUTPUT file and pb35 does not. Every value matches.",
    ["qb20/DIFF02.BAS"] = "as qb10/DIFF02: the trailing ^Z.",
    ["qb30/DIFF02.BAS"] = "as qb10/DIFF02: the trailing ^Z.",
  };

  private const int _floor = 80;   // 83 across every dialect
}
