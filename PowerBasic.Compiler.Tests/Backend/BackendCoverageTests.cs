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

  private sealed record Census(int Functions, int Selected, int Allocated, Dictionary<string, int> Declines,
    int ProgramsLowered, int ProgramsTotal, Dictionary<string, int> LoweringDeclines,
    Dictionary<string, int> ProcedureDeclines);

  /// <summary>
  /// Runs the back end's own pipeline over every battery program and tallies what selects.
  /// This mirrors <c>CodeGenerator.BackendProcs</c> exactly - lower, optimize, recover the integer
  /// form of PB's float-shaped integer arithmetic, optimize again - so the numbers describe the
  /// production routing rather than a laboratory setup.
  /// </summary>
  private static Census Measure() {
    var declines = new Dictionary<string, int>(StringComparer.Ordinal);
    var loweringDeclines = new Dictionary<string, int>(StringComparer.Ordinal);
    // the same tally restricted to named procedures: routing a module body (main) additionally needs
    // the whole startup/exit sequence, so what blocks a PROCEDURE is the cheaper next increment
    var procedureDeclines = new Dictionary<string, int>(StringComparer.Ordinal);
    int functions = 0, selected = 0, allocated = 0, lowered = 0, total = 0;
    var dir = Path.Combine(_repoRoot, "tests");
    if (!Directory.Exists(dir))
      return new(0, 0, 0, declines, 0, 0, loweringDeclines, procedureDeclines);

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
      string? why;
      try {
        module = IrLowering.TryLowerModule(model, out why);
      } catch (Exception e) {
        (module, why) = (null, e.Message);
      }
      if (module is null) {
        // the IR path stops one level earlier than the selector for most of the corpus, so the
        // lowering's own reasons rank what would widen it
        var reason = Summarize(why ?? "unknown");
        loweringDeclines[reason] = loweringDeclines.GetValueOrDefault(reason) + 1;
        continue;
      }
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
        if (InstructionSelector.TrySelect(fn, out var reason) is { } machine) {
          ++selected;
          // selection is not routing: the whole-program codegen also schedules and allocates, and a
          // value live across a CALL has no register while there is no spilling - so this is the
          // number of functions the back end would really take
          MachineScheduler.Schedule(machine);
          if (LinearScanAllocator.Allocate(machine) is not null)
            ++allocated;
        }
        else {
          declines[reason ?? "unknown"] = declines.GetValueOrDefault(reason ?? "unknown") + 1;
          if (!fn.Name.Equals("main", StringComparison.OrdinalIgnoreCase))
            procedureDeclines[reason ?? "unknown"] = procedureDeclines.GetValueOrDefault(reason ?? "unknown") + 1;
        }
      }
    }

    return new(functions, selected, allocated, declines, lowered, total, loweringDeclines, procedureDeclines);
  }

  /// <summary>Collapses a decline message to its cause, so names/labels do not fragment the histogram.</summary>
  private static string Summarize(string reason) {
    var cut = reason.IndexOf(" '", StringComparison.Ordinal);
    var head = cut > 0 ? reason[..cut] : reason;
    return head.Length > 70 ? head[..70] : head;
  }

  /// <summary>
  /// A float value must never merely "select". The scalar path sizes a value from its bit width, so a
  /// SINGLE load would mint one Dword virtual register and emit a single WORD-sized MOV - half the
  /// value, silently. The same silent truncation was found for 32-bit integers once; the guard exists
  /// so it cannot be found a second time.
  /// </summary>
  [Test]
  public void Selector_GivenAFloatValue_ThenDeclinesRatherThanHalfLoadIt() {
    var fn = new IrFunction("F", IrType.Void, []);
    var entry = fn.CreateBlock("entry");
    var slot = entry.Append(new IrAlloca(IrType.F32));
    var value = entry.Append(new IrLoad(IrType.F32, slot));
    entry.Append(new IrStore(value, slot));
    entry.Append(new IrRet(null));

    InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(reason, Does.Contain("floating point"));
  }

  [Test]
  public void Selector_GivenTheBattery_ThenReportsWhatBlocksCoverage() {
    var census = Measure();
    Assume.That(census.ProgramsTotal, Is.GreaterThan(0), "no tests/*.BAS corpus present");

    var report = new StringBuilder()
      .AppendLine($"programs           : {census.ProgramsLowered}/{census.ProgramsTotal} lowered to IR")
      .AppendLine($"functions selected : {census.Selected}/{census.Functions}")
      .AppendLine($"functions routed   : {census.Allocated}/{census.Functions} (selected AND allocated)")
      .AppendLine("lowering declines - what keeps a program off the IR path entirely:");
    foreach (var (reason, count) in census.LoweringDeclines.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(12))
      report.AppendLine($"  {count,5}  {reason}");
    report.AppendLine("selection declines - what keeps a lowered function off the x86-16 back end:");
    foreach (var (reason, count) in census.Declines.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
      report.AppendLine($"  {count,5}  {reason}");
    report.AppendLine($"of those, {census.ProcedureDeclines.Values.Sum()} are named procedures (main excluded):");
    foreach (var (reason, count) in census.ProcedureDeclines.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
      report.AppendLine($"  {count,5}  {reason}");
    TestContext.Out.Write(report.ToString());

    // A floor, not an exact count: widening the selector may only raise it, and a change that lowers
    // it has taken coverage away from the retargetable path - the thing this back end exists to grow.
    //
    // The history is worth keeping, because one step DOWN was a fix: 8 -> 9 when calls to defined
    // procedures became selectable, 9 -> 15 when metastatements stopped blocking the lowering, then
    // 15 -> 13 when 32-bit values became register PAIRS. Those two lost functions were never really
    // compiled correctly - a LONG used to mint one Dword-sized register and emit a single word-sized
    // MOV, silently carrying the low 16 bits as the whole value - so they declined honestly instead.
    // A coverage number is only worth defending when every function under it is actually right.
    // Then 13 -> 15 as the 32-bit forms landed for real: constant shifts, parameters, call arguments
    // and results, and a module variable addressed through the codegen's own data cell.
    // 15 -> 38 with the runtime-label bridge: a call to rt_print_str/i16/i32/nl now selects in the DOS
    // runtime's own register convention instead of declining
    // then 38 -> 66 as the bridge grew string constants (rt_strmem) and files (rt_fopen/rt_fclose plus
    // the rt_fselect routing PRINT # needs) - the battery writes its results to a file, so file I/O
    // was what stood in front of almost every module body
    // then 66 -> 69 with string concatenation (rt_strcat) and the 32-bit multiply helper (rt_lmul)
    Assert.That(census.Selected, Is.GreaterThanOrEqualTo(69),
      "the x86-16 back end now compiles fewer corpus functions than it used to:\n" + report);
  }
}
