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
    var skipped = new List<string>();
    var reboundNames = new List<string>();

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
        if (model.Errors.Count > 0) {
          skipped.Add($"{dialect.CanonicalName()}/{name}: does not bind - {model.Errors[0].Message}");
          continue;
        }
        var module = IrLowering.TryLowerModule(model, out var why);
        if (module is null) {
          skipped.Add($"{dialect.CanonicalName()}/{name}: not lowered - {why}");
          continue;
        }
        IrPassManager.Standard().RunOnModule(module);
        rendered = IrBasicWriter.Write(module);
      } catch (Exception e) {
        // a module the writer declines is measured above, but WHICH module stopped counting is not,
        // and a count that only ever moves by one is exactly the kind of drop nobody can diagnose
        skipped.Add($"{dialect.CanonicalName()}/{name}: {e.GetType().Name} - {e.Message}");
        continue;
      }

      try {
        var back = Binder.Bind(Parser.Parse(Lexer.Tokenize(rendered, "RT.BAS", Dialect.Pb35), "RT.BAS", Dialect.Pb35), Dialect.Pb35);
        if (back.Errors.Count > 0)
          rejected.Add($"{dialect.CanonicalName()}/{name}: {back.Errors[0].Message}");
        else
          reboundNames.Add($"{dialect.CanonicalName()}/{name}");
      } catch (Exception e) {
        rejected.Add($"{dialect.CanonicalName()}/{name}: {e.Message}");
      }
    }

    TestContext.Out.WriteLine($"modules rendered and re-bound under pb35: {reboundNames.Count}, "
      + $"rejected: {rejected.Count}, never reached the writer: {skipped.Count}");
    foreach (var r in rejected.Take(20))
      TestContext.Out.WriteLine("  REJECTED " + r);
    foreach (var s in skipped)
      TestContext.Out.WriteLine("  skipped  " + s);

    Assert.That(rejected, Is.Empty,
      "the IR writer produced text the pb35 front end rejects:" + Environment.NewLine
      + string.Join(Environment.NewLine, rejected));
    // Pinned by NAME rather than by count, because a count over a corpus cannot tell "the compiler
    // got worse" from "the corpus got harder". Both happened here: the floor of 95 was real when it
    // was set, and adding one optimization-battery scenario - a MIN%/MAX% fold, in a file that
    // accumulates every scenario there is - took it to 94 without a line of the compiler changing.
    // A count says only that the number moved. A set says which program stopped, and adding a
    // program the writer cannot yet render leaves it alone instead of quietly lowering the bar.
    Assert.That(reboundNames, Is.EquivalentTo(_reboundUnderPb35),
      "the set of modules that render and re-bind has changed - the skip list above says why each one did not reach the writer");
  }

  /// <summary>
  /// Every corpus program the IR writer renders into text the pb35 front end accepts. 79 at the
  /// first measurement, 80 with SHARED globals, 94 once each program was lowered in the dialect it
  /// was written in, 95 with the TYPE-variable render fixed - and then back to 94 when an
  /// optimization-battery scenario using MIN%/MAX% was added, which is why this is a set now.
  ///
  /// Add a name when a program starts re-binding. Removing one is a regression and needs a reason.
  /// </summary>
  private static readonly string[] _reboundUnderPb35 = [
"basica/DIFF01.BAS",
    "gw/DIFF01.BAS",
    "pb21/DIFF01.BAS",
    "pb21/DIFF02.BAS",
    "pb30/QUIRK30.BAS",
    "pb36/ARITH.BAS",
    "pb36/CTRL.BAS",
    "pb36/DIFF01.BAS",
    "pb36/DIFF03.BAS",
    "pb36/DIFF04.BAS",
    "pb36/DIFF06.BAS",
    "pb36/DIFF100.BAS",
    "pb36/DIFF101.BAS",
    "pb36/DIFF102.BAS",
    "pb36/DIFF103.BAS",
    "pb36/DIFF104.BAS",
    "pb36/DIFF106.BAS",
    "pb36/DIFF107.BAS",
    "pb36/DIFF108.BAS",
    "pb36/DIFF109.BAS",
    "pb36/DIFF110.BAS",
    "pb36/DIFF112.BAS",
    "pb36/DIFF15.BAS",
    "pb36/DIFF18.BAS",
    "pb36/DIFF22.BAS",
    "pb36/DIFF24.BAS",
    "pb36/DIFF25.BAS",
    "pb36/DIFF26.BAS",
    "pb36/DIFF31.BAS",
    "pb36/DIFF33.BAS",
    "pb36/DIFF35.BAS",
    "pb36/DIFF36.BAS",
    "pb36/DIFF38.BAS",
    "pb36/DIFF39.BAS",
    "pb36/DIFF42.BAS",
    "pb36/DIFF43.BAS",
    "pb36/DIFF44.BAS",
    "pb36/DIFF47.BAS",
    "pb36/DIFF48.BAS",
    "pb36/DIFF49.BAS",
    "pb36/DIFF50.BAS",
    "pb36/DIFF51.BAS",
    "pb36/DIFF52.BAS",
    "pb36/DIFF53.BAS",
    "pb36/DIFF55.BAS",
    "pb36/DIFF59.BAS",
    "pb36/DIFF62.BAS",
    "pb36/DIFF63.BAS",
    "pb36/DIFF64.BAS",
    "pb36/DIFF65.BAS",
    "pb36/DIFF66.BAS",
    "pb36/DIFF67.BAS",
    "pb36/DIFF68.BAS",
    "pb36/DIFF69.BAS",
    "pb36/DIFF70.BAS",
    "pb36/DIFF71.BAS",
    "pb36/DIFF72.BAS",
    "pb36/DIFF73.BAS",
    "pb36/DIFF75.BAS",
    "pb36/DIFF76.BAS",
    "pb36/DIFF77.BAS",
    "pb36/DIFF78.BAS",
    "pb36/DIFF79.BAS",
    "pb36/DIFF80.BAS",
    "pb36/DIFF81.BAS",
    "pb36/DIFF82.BAS",
    "pb36/DIFF83.BAS",
    "pb36/DIFF85.BAS",
    "pb36/DIFF87.BAS",
    "pb36/DIFF88.BAS",
    "pb36/DIFF89.BAS",
    "pb36/DIFF90.BAS",
    "pb36/DIFF91.BAS",
    "pb36/DIFF92.BAS",
    "pb36/DIFF93.BAS",
    "pb36/DIFF94.BAS",
    "pb36/DIFF95.BAS",
    "pb36/DIFF96.BAS",
    "pb36/DIFF97.BAS",
    "pb36/DIFF99.BAS",
    "pb36/HELLO.BAS",
    "pb36/RANGES.BAS",
    "pb36/SHAREDG.BAS",
    "pb36/STRHEAP.BAS",
    "pds70/DIFF02.BAS",
    "pds71/DIFF02.BAS",
    "qb10/DIFF02.BAS",
    "qb20/DIFF02.BAS",
    "qb30/DIFF02.BAS",
    "qb40/DIFF02.BAS",
    "qb45/DIFF02.BAS",
    "qbasic/DIFF01.BAS",
    "tb10/DIFF01.BAS",
    "tb11/DIFF01.BAS",
  ];

  private const int _floor = 173;   // 13 at the first measurement; +scalar slots, +PRINT, +file I/O, +arrays,
                                   // +globals, +string and math intrinsics, +exact truncation
}
