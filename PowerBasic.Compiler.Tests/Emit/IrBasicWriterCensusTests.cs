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

  private const int _floor = 173;   // 13 at the first measurement; +scalar slots, +PRINT, +file I/O, +arrays,
                                   // +globals, +string and math intrinsics, +exact truncation
}
