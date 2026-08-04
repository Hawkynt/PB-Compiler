using System.Text;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Emit;

/// <summary>
/// How much of the real corpus <see cref="IrBasicWriter"/> can render, and - for everything it
/// cannot - which IR construct stopped it.
///
/// The writer is meant to replace <see cref="BasicWriter"/> eventually, and "eventually" needs a
/// number attached. This is that number, plus the ranked list of what to build next, measured the
/// same way the back end's own coverage is: run the production pipeline, try every function, tally
/// the refusals.
/// </summary>
[TestFixture]
public sealed class IrBasicWriterCensusTests {

  private static readonly string _repoRoot =
    Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  [Test]
  public void Write_GivenTheCorpus_ThenReportsWhatItCannotRenderYet() {
    var declines = new Dictionary<string, int>(StringComparer.Ordinal);
    int rendered = 0, functions = 0;
    var dir = Path.Combine(_repoRoot, "tests");
    Assume.That(Directory.Exists(dir), "no tests/*.BAS corpus present");

    foreach (var file in Directory.EnumerateFiles(dir, "*.BAS", SearchOption.AllDirectories)
               .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
      var name = Path.GetFileName(file);
      IrModule? module;
      try {
        var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(File.ReadAllText(file), name, Dialect.Pb36), name, Dialect.Pb36), Dialect.Pb36);
        if (model.Errors.Count > 0)
          continue;
        module = IrLowering.TryLowerModule(model, out _);
        if (module is null)
          continue;
        IrPassManager.Standard().RunOnModule(module);
      } catch (Exception) {
        continue;
      }

      foreach (var fn in module.Functions) {
        if (fn.IsDeclaration)
          continue;
        ++functions;
        try {
          IrBasicWriter.Write(fn);
          ++rendered;
        } catch (IrBasicWriterException e) {
          declines[e.What] = declines.GetValueOrDefault(e.What) + 1;
        } catch (Exception e) {
          var key = e.GetType().Name;
          declines[key] = declines.GetValueOrDefault(key) + 1;
        }
      }
    }

    var report = new StringBuilder().AppendLine($"IR functions rendered back to BASIC: {rendered}/{functions}");
    foreach (var (reason, count) in declines.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
      report.AppendLine($"  {count,5}  {reason}");
    TestContext.Out.Write(report.ToString());

    // A floor: the writer may only get better at this. Replacing BasicWriter means driving the
    // denominator to the numerator, and every refusal above names one step of that.
    Assert.That(rendered, Is.GreaterThanOrEqualTo(_floor), "fewer IR functions render than used to:\n" + report);
  }

  /// <summary>
  /// The contract <see cref="BasicWriter"/> is held to, applied to the IR writer: the rendered text
  /// must be a program the <b>pb35 front end accepts</b>, not merely plausible source. That is the
  /// actual bar for replacing it - the back-emitter's job is down-translation to a dialect, and
  /// output that does not re-bind is not a translation of anything.
  ///
  /// Rendering and re-binding are counted separately on purpose. A module the writer refuses is a
  /// known gap; one it renders into text pb35 rejects is a BUG, and pinning them at one number would
  /// let the second hide behind the first.
  /// </summary>
  [Test]
  public void Write_GivenTheCorpus_ThenWhatItRendersRebindsUnderPb35() {
    var dir = Path.Combine(_repoRoot, "tests");
    Assume.That(Directory.Exists(dir), "no tests/*.BAS corpus present");
    var rejected = new List<string>();
    var rebound = 0;

    // every dialect, each program lowered in the one it was WRITTEN in - a GW-BASIC or QuickBASIC
    // program goes in and pb35 comes out, which is the point of rendering from the IR rather than
    // from the tree the front end happened to parse
    foreach (var file in Directory.EnumerateFiles(dir, "*.BAS", SearchOption.AllDirectories)
               .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
      var name = Path.GetFileName(file);
      var folder = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
      var dialect = DialectFacts.TryParse(folder, out var parsed) ? parsed : Dialect.Pb36;
      string rendered;
      try {
        var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(File.ReadAllText(file), name, dialect), name, dialect), dialect);
        if (model.Errors.Count > 0)
          continue;
        var module = IrLowering.TryLowerModule(model, out _);
        if (module is null)
          continue;
        IrPassManager.Standard().RunOnModule(module);
        rendered = IrBasicWriter.Write(module);
      } catch (Exception) {
        continue;                                  // a module the writer declines is measured above
      }

      try {
        var back = Binder.Bind(Parser.Parse(Lexer.Tokenize(rendered, "RT.BAS", Dialect.Pb35), "RT.BAS", Dialect.Pb35), Dialect.Pb35);
        if (back.Errors.Count > 0)
          rejected.Add($"{dialect.CanonicalName()}/{name}: {back.Errors[0].Message}");
        else
          ++rebound;
      } catch (Exception e) {
        rejected.Add($"{dialect.CanonicalName()}/{name}: {e.Message}");
      }
    }

    TestContext.Out.WriteLine($"modules rendered and re-bound under pb35: {rebound}, rejected: {rejected.Count}");
    foreach (var r in rejected.Take(20))
      TestContext.Out.WriteLine("  " + r);

    Assert.That(rejected, Is.Empty,
      "the IR writer produced text the pb35 front end rejects:" + Environment.NewLine
      + string.Join(Environment.NewLine, rejected));
    Assert.That(rebound, Is.GreaterThanOrEqualTo(_reboundFloor), "fewer modules re-bind than used to");
  }

  private const int _reboundFloor = 95;   // 79 at first measurement; 80 with SHARED globals, 94 once every
                                        // dialect was lowered in its own rather than all as pb36

  private const int _floor = 173;   // 13 at the first measurement; +scalar slots, +PRINT, +file I/O, +arrays,
                                   // +globals, +string and math intrinsics, +exact truncation
}
