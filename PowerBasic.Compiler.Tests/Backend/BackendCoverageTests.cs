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

  private sealed record Census(int Functions, int Selected, int Allocated, List<string> MainBodies,
    Dictionary<string, int> Declines, List<string> SelectionCases, List<string> ProgramsLowered,
    int ProgramsTotal, Dictionary<string, int> LoweringDeclines,
    Dictionary<string, int> ProcedureDeclines, Dictionary<string, int> AllocationDeclines,
    List<string> AllocationCases);

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
    var selectionCases = new List<string>();
    // why a function that DID select still does not route - the row that used to read only as a count
    var allocationDeclines = new Dictionary<string, int>(StringComparer.Ordinal);
    var allocationCases = new List<string>();
    int functions = 0, selected = 0, allocated = 0, total = 0;
    var mainBodies = new List<string>();
    var lowered = new List<string>();
    var dir = Path.Combine(_repoRoot, "tests");
    if (!Directory.Exists(dir))
      return new(0, 0, 0, mainBodies, declines, selectionCases, lowered, 0, loweringDeclines,
        procedureDeclines, allocationDeclines, allocationCases);

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
      lowered.Add(name);

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
          // allocation includes memory spills, rematerialization and explicit live-range splitting;
          // this is therefore the number of functions the back end would really take
          MachineScheduler.Schedule(machine);
          if (LinearScanAllocator.Allocate(machine, out var noRegisters) is not null) {
            ++allocated;
            // a module body that selects AND allocates is a whole program the back end can own
            if (fn.Name.Equals("main", StringComparison.OrdinalIgnoreCase))
              mainBodies.Add(name);
          } else {
            var allocationReason = noRegisters ?? "unknown";
            allocationDeclines[allocationReason] = allocationDeclines.GetValueOrDefault(allocationReason) + 1;
            allocationCases.Add($"{Path.GetRelativePath(dir, file).Replace('\\', '/')}::{fn.Name}: {allocationReason}");
          }
        }
        else {
          declines[reason ?? "unknown"] = declines.GetValueOrDefault(reason ?? "unknown") + 1;
          selectionCases.Add($"{Path.GetRelativePath(dir, file).Replace('\\', '/')}::{fn.Name}: {reason ?? "unknown"}");
          if (!fn.Name.Equals("main", StringComparison.OrdinalIgnoreCase))
            procedureDeclines[reason ?? "unknown"] = procedureDeclines.GetValueOrDefault(reason ?? "unknown") + 1;
        }
      }
    }

    return new(functions, selected, allocated, mainBodies, declines, selectionCases, lowered, total,
      loweringDeclines, procedureDeclines, allocationDeclines, allocationCases);
  }

  /// <summary>Collapses a decline message to its cause, so names/labels do not fragment the histogram.</summary>
  private static string Summarize(string reason) {
    var cut = reason.IndexOf(" '", StringComparison.Ordinal);
    var head = cut > 0 ? reason[..cut] : reason;
    return head.Length > 70 ? head[..70] : head;
  }

  /// <summary>
  /// A float value must never go through the scalar path. That path sizes a value from its bit width,
  /// so a SINGLE load would mint one Dword virtual register and emit a single WORD-sized MOV - half
  /// the value, silently. The same truncation was found for 32-bit integers once; a float now takes
  /// the x87 path (a frame cell bracketed by FLD/FSTP) and never a register.
  /// </summary>
  [Test]
  public void Selector_GivenAFloatValue_ThenUsesTheX87PathAndNoRegister() {
    var fn = new IrFunction("F", IrType.Void, []);
    var entry = fn.CreateBlock("entry");
    var slot = entry.Append(new IrAlloca(IrType.F32));
    var value = entry.Append(new IrLoad(IrType.F32, slot));
    entry.Append(new IrStore(value, slot));
    entry.Append(new IrRet(null));

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, $"declined: {reason}");
    var opcodes = m!.AllInstructions.Select(i => i.Opcode).ToList();
    Assert.That(opcodes, Does.Contain(MOpcode.Fld));
    Assert.That(opcodes, Does.Contain(MOpcode.Fstp));
    Assert.That(m.AllInstructions.SelectMany(i => i.Operands).OfType<MOperand.Register>()
      .Where(r => r.Reg.Size == MRegSize.Dword), Is.Empty, "no float ever lands in a register");
  }

  [Test]
  public void Selector_GivenTheBattery_ThenReportsWhatBlocksCoverage() {
    var census = Measure();
    Assume.That(census.ProgramsTotal, Is.GreaterThan(0), "no tests/*.BAS corpus present");

    var report = new StringBuilder()
      .AppendLine($"programs           : {census.ProgramsLowered.Count}/{census.ProgramsTotal} lowered to IR")
      .AppendLine($"functions selected : {census.Selected}/{census.Functions}")
      .AppendLine($"functions routed   : {census.Allocated}/{census.Functions} (selected AND allocated)")
      .AppendLine($"module bodies      : {census.MainBodies.Count}/{census.ProgramsLowered.Count} whole programs the back end can own")
      .AppendLine("lowering declines - what keeps a program off the IR path entirely:");
    foreach (var (reason, count) in census.LoweringDeclines.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(12))
      report.AppendLine($"  {count,5}  {reason}");
    report.AppendLine("selection declines - what keeps a lowered function off the x86-16 back end:");
    foreach (var (reason, count) in census.Declines.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
      report.AppendLine($"  {count,5}  {reason}");
    foreach (var selectionCase in census.SelectionCases)
      report.AppendLine($"         {selectionCase}");
    report.AppendLine($"of those, {census.ProcedureDeclines.Values.Sum()} are named procedures (main excluded):");
    foreach (var (reason, count) in census.ProcedureDeclines.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
      report.AppendLine($"  {count,5}  {reason}");
    report.AppendLine($"allocation declines - selected but not routed ({census.Selected - census.Allocated}):");
    foreach (var (reason, count) in census.AllocationDeclines.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
      report.AppendLine($"  {count,5}  {reason}");
    foreach (var allocationCase in census.AllocationCases)
      report.AppendLine($"         {allocationCase}");
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
    // then 69 -> 81 with x87: floats live in frame cells bracketed by FLD/FSTP
    // then 81 -> 88 (of a larger denominator, since 14 more programs now lower at all): CINT and the
    // rounding float-to-integer conversion it is spelled from
    // then 185 -> 192 when constant QUAD prints gained their exact qword-to-x87 ABI bridge
    // then 192 -> 200 when ordered x87 comparisons could materialize BASIC's -1/0 truth value;
    // DIFF28 reaches the next honest blocker (32-bit signed division), so eight rather than nine
    // complete functions move even though all nine float-comparison declines disappear;
    // then 200 -> 205 when signed 32-bit divide/remainder reused the DOS runtime's pair-register
    // helpers. Six decline entries disappear, but DIFF32 exposes its next blocker (i64 truncation)
    // then 205 -> 209 when SINGLE/DOUBLE BYVAL parameters and ST(0) results gained their declared-width
    // stack ABI. Four honest float-procedure declines disappear and three more module bodies route
    Assert.That(census.Selected, Is.GreaterThanOrEqualTo(209),
      "the x86-16 back end now compiles fewer corpus functions than it used to:\n" + report);

    // How many programs reach the IR at all - the figure the runtime-trap and error-handling work
    // moves, since a program that declines at the lowering never reaches the selector to be counted
    // above. 119 -> 122 with $ERROR OVERFLOW ON and dynamic-array bounds checking, 122 -> 129 with
    // ON ERROR / RESUME and the ERR / ERL cells their handlers read, 129 -> 132 with $ERROR NUMERIC
    // ON and ERRCLEAR.
    // By NAME, like the module bodies below and for the same reason: a count cannot tell a program
    // that stopped lowering from a program that was added and never did. Both move the number by one
    // and only one of them is a regression.
    Assert.That(census.ProgramsLowered, Is.EquivalentTo(_loweredToIr),
      "the set of corpus programs reaching the IR has changed:\n" + report);

    // selection is not routing: the whole-program codegen also schedules and allocates, and a value
    // live across a CALL has no register unless the spiller can move it to the frame
    Assert.That(census.Allocated, Is.GreaterThanOrEqualTo(209),
      "fewer selected functions survive register allocation than they used to:\n" + report);

    // The figure that matters for whole-program ownership: module bodies the back end compiles end
    // to end. It was zero until main became routable at all.
    //
    // Pinned by NAME rather than by count, for the reason the writer census is: a count over a
    // corpus cannot tell "the back end got worse" from "the corpus got harder". This one went 65 to
    // 64 at 802ca90, which added an optimization-battery scenario to tests/optimize/CODEGEN.BAS -
    // the file that accumulates every scenario there is, and so the first to stop lowering whenever
    // one uses something the IR does not model yet. No back-end code changed. A set names the
    // program that stopped; a new program the back end cannot own leaves it alone.
    Assert.That(census.MainBodies, Is.EquivalentTo(_ownedMainBodies),
      "the set of whole module bodies the back end can compile has changed:\nactual: " +
      string.Join(", ", census.MainBodies) + "\n" + report);
  }

  /// <summary>
  /// Every corpus program whose module body the x86-16 back end selects, schedules and allocates end
  /// to end. Zero until main became routable at all; 65 before an optimization-battery scenario using
  /// MIN%/MAX% stopped CODEGEN.BAS from lowering, which is why this is a set rather than a floor.
  ///
  /// Add a name when a program becomes ownable. Removing one is a regression and needs a reason.
  /// </summary>
  /// <summary>
  /// Every corpus program the IR lowering takes whole. Pinned by name for the reason the module
  /// bodies below are: a count cannot tell a program that STOPPED lowering from one that was added
  /// and never did, and both move it by one.
  /// </summary>
  private static readonly string[] _loweredToIr = [
    "ARITH.BAS",
    "CODEGEN.BAS",
    "CTRL.BAS",
    "DATAREAD.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "DIFF02.BAS",
    "DIFF02.BAS",
    "DIFF02.BAS",
    "DIFF02.BAS",
    "DIFF02.BAS",
    "DIFF02.BAS",
    "DIFF02.BAS",
    "DIFF02.BAS",
    "DIFF02.BAS",
    "DIFF02.BAS",
    "DIFF03.BAS",
    "DIFF03.BAS",
    "DIFF03.BAS",
    "DIFF04.BAS",
    "DIFF05.BAS",
    "DIFF06.BAS",
    "DIFF10.BAS",
    "DIFF100.BAS",
    "DIFF101.BAS",
    "DIFF102.BAS",
    "DIFF103.BAS",
    "DIFF104.BAS",
    "DIFF105.BAS",
    "DIFF106.BAS",
    "DIFF107.BAS",
    "DIFF108.BAS",
    "DIFF109.BAS",
    "DIFF110.BAS",
    "DIFF111.BAS",
    "DIFF112.BAS",
    "DIFF113.BAS",
    "DIFF15.BAS",
    "DIFF18.BAS",
    "DIFF22.BAS",
    "DIFF23.BAS",
    "DIFF24.BAS",
    "DIFF25.BAS",
    "DIFF26.BAS",
    "DIFF27.BAS",
    "DIFF28.BAS",
    "DIFF29.BAS",
    "DIFF30.BAS",
    "DIFF31.BAS",
    "DIFF32.BAS",
    "DIFF33.BAS",
    "DIFF35.BAS",
    "DIFF36.BAS",
    "DIFF37.BAS",
    "DIFF38.BAS",
    "DIFF39.BAS",
    "DIFF40.BAS",
    "DIFF41.BAS",
    "DIFF42.BAS",
    "DIFF43.BAS",
    "DIFF44.BAS",
    "DIFF45.BAS",
    "DIFF46.BAS",
    "DIFF47.BAS",
    "DIFF48.BAS",
    "DIFF49.BAS",
    "DIFF50.BAS",
    "DIFF51.BAS",
    "DIFF52.BAS",
    "DIFF53.BAS",
    "DIFF54.BAS",
    "DIFF55.BAS",
    "DIFF56.BAS",
    "DIFF58.BAS",
    "DIFF59.BAS",
    "DIFF61.BAS",
    "DIFF62.BAS",
    "DIFF63.BAS",
    "DIFF64.BAS",
    "DIFF65.BAS",
    "DIFF66.BAS",
    "DIFF67.BAS",
    "DIFF68.BAS",
    "DIFF69.BAS",
    "DIFF70.BAS",
    "DIFF71.BAS",
    "DIFF72.BAS",
    "DIFF73.BAS",
    "DIFF75.BAS",
    "DIFF76.BAS",
    "DIFF77.BAS",
    "DIFF78.BAS",
    "DIFF79.BAS",
    "DIFF80.BAS",
    "DIFF81.BAS",
    "DIFF82.BAS",
    "DIFF83.BAS",
    "DIFF84.BAS",
    "DIFF85.BAS",
    "DIFF87.BAS",
    "DIFF88.BAS",
    "DIFF89.BAS",
    "DIFF90.BAS",
    "DIFF91.BAS",
    "DIFF92.BAS",
    "DIFF93.BAS",
    "DIFF94.BAS",
    "DIFF95.BAS",
    "DIFF96.BAS",
    "DIFF97.BAS",
    "DIFF99.BAS",
    "HELLO.BAS",
    "INPUTS.BAS",
    "MATHUNIT.BAS",
    "ONERR.BAS",
    "ONERRNXT.BAS",
    "QUIRK30.BAS",
    "RANGES.BAS",
    "SHAREDG.BAS",
    "STRHEAP.BAS",
    "STRINGS.BAS",
    "SUBFN.BAS",
  ];

  private static readonly string[] _ownedMainBodies = [
    "ARITH.BAS",
    "CTRL.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "DIFF02.BAS",
    "DIFF02.BAS",
    "DIFF02.BAS",
    "DIFF02.BAS",
    "DIFF02.BAS",
    "DIFF02.BAS",
    "DIFF02.BAS",
    "DIFF02.BAS",
    "DIFF02.BAS",
    "DIFF03.BAS",
    "DIFF03.BAS",
    "DIFF03.BAS",
    "DIFF04.BAS",
    "DIFF100.BAS",
    "DIFF101.BAS",
    "DIFF102.BAS",
    "DIFF103.BAS",
    "DIFF104.BAS",
    "DIFF106.BAS",
    "DIFF107.BAS",
    "DIFF108.BAS",
    "DIFF109.BAS",
    "DIFF110.BAS",
    "DIFF111.BAS",
    "DIFF112.BAS",
    "DIFF113.BAS",
    "DIFF15.BAS",
    "DIFF18.BAS",
    "DIFF22.BAS",
    "DIFF24.BAS",
    "DIFF25.BAS",
    "DIFF26.BAS",
    "DIFF27.BAS",
    "DIFF28.BAS",
    "DIFF29.BAS",
    "DIFF30.BAS",
    "DIFF31.BAS",
    "DIFF33.BAS",
    "DIFF35.BAS",
    "DIFF36.BAS",
    "DIFF38.BAS",
    "DIFF39.BAS",
    "DIFF41.BAS",
    "DIFF42.BAS",
    "DIFF43.BAS",
    "DIFF44.BAS",
    "DIFF45.BAS",
    "DIFF46.BAS",
    "DIFF47.BAS",
    "DIFF48.BAS",
    "DIFF49.BAS",
    "DIFF50.BAS",
    "DIFF51.BAS",
    "DIFF52.BAS",
    "DIFF53.BAS",
    "DIFF55.BAS",
    "DIFF59.BAS",
    "DIFF62.BAS",
    "DIFF63.BAS",
    "DIFF64.BAS",
    "DIFF65.BAS",
    "DIFF66.BAS",
    "DIFF67.BAS",
    "DIFF68.BAS",
    "DIFF69.BAS",
    "DIFF70.BAS",
    "DIFF71.BAS",
    "DIFF72.BAS",
    "DIFF73.BAS",
    "DIFF75.BAS",
    "DIFF76.BAS",
    "DIFF77.BAS",
    "DIFF78.BAS",
    "DIFF79.BAS",
    "DIFF80.BAS",
    "DIFF81.BAS",
    "DIFF82.BAS",
    "DIFF83.BAS",
    "DIFF84.BAS",
    "DIFF85.BAS",
    "DIFF87.BAS",
    "DIFF88.BAS",
    "DIFF89.BAS",
    "DIFF90.BAS",
    "DIFF91.BAS",
    "DIFF92.BAS",
    "DIFF93.BAS",
    "DIFF94.BAS",
    "DIFF95.BAS",
    "DIFF96.BAS",
    "DIFF97.BAS",
    "DIFF99.BAS",
    "HELLO.BAS",
    "MATHUNIT.BAS",
    "ONERR.BAS",
    "ONERRNXT.BAS",
    "QUIRK30.BAS",
    "RANGES.BAS",
    "SUBFN.BAS",
  ];
}
