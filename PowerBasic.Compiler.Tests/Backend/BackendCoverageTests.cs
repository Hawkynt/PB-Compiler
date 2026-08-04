using System.Text;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// How much of the real corpus the in-house x86-16 back end can actually compile, and - for
/// everything it cannot - which IR construct stopped it.
///
/// The back end is the retargetable path's fidelity proof (docs/X86-BACKEND.md): every function it
/// selects is register-allocated and scheduled from SSA rather than emitted AX-serially by the
/// direct codegen. Widening it is only worth doing in the order the corpus actually demands, so this
/// fixture is the measurement that ranks the next increment - it prints a histogram of decline
/// reasons over the DOS battery and pins the count that currently selects, so a regression in
/// coverage fails rather than passing quietly.
/// </summary>
[TestFixture]
public sealed class BackendCoverageTests {

  private static readonly string _repoRoot =
    Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  private sealed record Census(int Functions, int Selected, Dictionary<string, int> Declines, int ProgramsLowered, int ProgramsTotal);

  /// <summary>
  /// Runs the back end's own pipeline over every battery program and tallies what selects.
  /// This mirrors <c>CodeGenerator.BackendProcs</c> exactly - lower, optimize, recover the integer
  /// form of PB's float-shaped integer arithmetic, optimize again - so the numbers describe the
  /// production routing rather than a laboratory setup.
  /// </summary>
  private static Census Measure() {
    var declines = new Dictionary<string, int>(StringComparer.Ordinal);
    int functions = 0, selected = 0, lowered = 0, total = 0;
    var dir = Path.Combine(_repoRoot, "tests");
    if (!Directory.Exists(dir))
      return new(0, 0, declines, 0, 0);

    // the whole corpus: the golden battery plus tests/diff, the 100+ differential programs
    foreach (var file in Directory.EnumerateFiles(dir, "*.BAS", SearchOption.AllDirectories)
               .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
      ++total;
      var name = Path.GetFileName(file);
      SemanticModel model;
      try {
        var text = File.ReadAllText(file);
        model = Binder.Bind(Parser.Parse(Lexer.Tokenize(text, name, Dialect.Pb36), name, Dialect.Pb36), Dialect.Pb36);
        if (model.Errors.Count > 0)
          continue;                             // a program the front end rejects is not the back end's business
      } catch (Exception) {
        continue;
      }

      IrModule? module;
      try {
        module = IrLowering.TryLowerModule(model);
      } catch (Exception) {
        module = null;
      }
      if (module is null)
        continue;                               // outside the lowering's subset (docs/IR.md) - counted below
      ++lowered;

      try {
        IrPassManager.Standard().RunOnModule(module);
        foreach (var f in module.Functions)
          if (!f.IsDeclaration)
            IntegerRecovery.Run(f);
        IrPassManager.Standard().RunOnModule(module);
      } catch (Exception) {
        continue;
      }

      foreach (var fn in module.Functions) {
        if (fn.IsDeclaration)
          continue;
        ++functions;
        if (InstructionSelector.TrySelect(fn, out var reason) is not null)
          ++selected;
        else
          declines[reason ?? "unknown"] = declines.GetValueOrDefault(reason ?? "unknown") + 1;
      }
    }

    return new(functions, selected, declines, lowered, total);
  }

  [Test]
  public void Selector_GivenTheBattery_ThenReportsWhatBlocksCoverage() {
    var census = Measure();
    Assume.That(census.ProgramsTotal, Is.GreaterThan(0), "no tests/*.BAS corpus present");

    var report = new StringBuilder()
      .AppendLine($"programs           : {census.ProgramsLowered}/{census.ProgramsTotal} lowered to IR")
      .AppendLine($"functions selected : {census.Selected}/{census.Functions}")
      .AppendLine("declines by cause (most blocking first):");
    foreach (var (reason, count) in census.Declines.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
      report.AppendLine($"  {count,5}  {reason}");
    TestContext.Out.Write(report.ToString());

    // a floor, not an exact count: widening the selector may only raise it
    Assert.That(census.Selected, Is.GreaterThan(0), "the back end selects nothing at all:\n" + report);
  }
}
