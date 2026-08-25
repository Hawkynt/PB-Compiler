using System.Text;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// How much of the real corpus the in-house x86-16 back end can actually compile, and - for
/// everything it cannot - what stopped it.
///
/// The back end is the retargetable path's fidelity proof (docs/X86-BACKEND.md): every function it
/// selects is register-allocated and scheduled from SSA rather than emitted AX-serially by the
/// direct codegen. Widening it is only worth doing in the order the corpus actually demands, so this
/// fixture is the measurement that ranks the next increment - it prints a histogram of decline
/// reasons over the DOS battery and pins the count that currently routes, so a regression in
/// coverage fails rather than passing quietly.
///
/// <para>
/// <b>The headline is what the PRODUCTION routing did, not what the selector would have done.</b>
/// This fixture used to report 262/262 functions selected and 161/161 module bodies owned, and that
/// pair was quoted as evidence that coverage was complete. It was not: it measured the SELECTOR over
/// every function the lowering produced, while <see cref="CodeGenerator.BackendProcs"/> refuses a
/// procedure on its SHAPE - a BYREF or string or QUAD or BYTE parameter, a non-default calling
/// convention, error handling in the body - before the selector is asked at all. A procedure the
/// filter skips appeared in neither the numerator nor the denominator, so the ratio measured "of the
/// functions we attempted, how many succeeded", which is nearly a tautology. Today that costs
/// nothing, because a skipped procedure falls back to the direct emitter; after <c>CodeGen/</c> is
/// retired there is no fallback and each one is a compile failure. So the routed figure is now taken
/// from <see cref="CodeGenerator.BackendDeclines"/>, which is the routing's own record of its own
/// decision, and the selector figure is kept BESIDE it rather than in front of it.
/// </para>
///
/// <para>
/// The three outcomes are kept apart on purpose, because collapsing them is how a coverage number
/// starts lying - and there is a fourth: the back end can THROW, producing no executable at all.
/// A throw is neither a success nor a clean decline, so it gets its own list and its own assertion.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendCoverageTests {

  private static readonly string _repoRoot =
    Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  private sealed record Census(int Functions, int Selected, int Allocated, List<string> MainBodies,
    Dictionary<string, int> Declines, List<string> SelectionCases, List<string> ProgramsLowered,
    int ProgramsTotal, int ProgramsRejectedByFrontEnd, Dictionary<string, int> LoweringDeclines,
    Dictionary<string, int> ProcedureDeclines, Dictionary<string, int> AllocationDeclines,
    List<string> AllocationCases,
    // the production half: what CodeGenerator.BackendProcs/BackendMain really took, and why not
    int Bodies, int Routed, int RoutedNoOptimize, List<string> MainBodiesNotRouted,
    Dictionary<string, int> RoutingDeclines, Dictionary<string, HashSet<string>> RoutingDeclinePrograms,
    List<string> RoutingDeclineCases, List<string> NotRoutedNames, List<string> ProcedureBodiesNotLowered,
    List<string> ThrewPrograms, int ExternalDeclarations);

  /// <summary>
  /// Runs the back end over every battery program, twice over: once through the selector alone (what
  /// the selector's reach is, over every function the lowering produced) and once through the WHOLE
  /// production code generator with routing enabled (what the back end actually took, and why not).
  ///
  /// <para>
  /// The second half is not a mirror of <c>CodeGenerator.BackendProcs</c> - it IS
  /// <c>CodeGenerator.BackendProcs</c>, read back through <see cref="CodeGenerator.BackendDeclines"/>.
  /// A census that re-derives the routing rule measures the rule it re-derived; this one cannot drift
  /// from production because it has nothing of its own to drift.
  /// </para>
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
    int functions = 0, selected = 0, allocated = 0, total = 0, rejected = 0;
    var mainBodies = new List<string>();
    var lowered = new List<string>();
    // the production half
    int bodies = 0, routed = 0, routedNoOptimize = 0, externalDeclarations = 0;
    var mainBodiesNotRouted = new List<string>();
    var routingDeclines = new Dictionary<string, int>(StringComparer.Ordinal);
    var routingDeclinePrograms = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    var routingDeclineCases = new List<string>();
    var notRoutedNames = new List<string>();
    var proceduresNotLowered = new List<string>();
    var threw = new List<string>();
    var dir = Path.Combine(_repoRoot, "tests");
    if (!Directory.Exists(dir))
      return new(0, 0, 0, mainBodies, declines, selectionCases, lowered, 0, 0, loweringDeclines,
        procedureDeclines, allocationDeclines, allocationCases, 0, 0, 0, mainBodiesNotRouted,
        routingDeclines, routingDeclinePrograms, routingDeclineCases, notRoutedNames,
        proceduresNotLowered, threw, 0);

    // the whole corpus: the golden battery plus tests/diff, the 100+ differential programs
    foreach (var file in Directory.EnumerateFiles(dir, "*.BAS", SearchOption.AllDirectories)
               .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
      ++total;
      var name = Path.GetFileName(file);
      SemanticModel model;
      try {
        var text = File.ReadAllText(file);
        model = Binder.Bind(Parser.Parse(Lexer.Tokenize(text, name, Dialect.Pb36), name, Dialect.Pb36), Dialect.Pb36);
        if (model.Errors.Count > 0) {
          ++rejected;                           // a program the front end rejects is not the back end's business
          continue;
        }
      } catch (Exception) {
        ++rejected;
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

      // ---- the production half: what the whole code generator really routed, and why not ----
      //
      // The denominator is the SOURCE, not the IR: every procedure that has a body plus this
      // program's module body. It differs from the IR function count by exactly the procedures whose
      // body the lowering refused - IrLowering leaves those a declaration, so they disappear from
      // the IR entirely and a census over IR functions counts them in neither half. A procedure that
      // stops existing must not raise a coverage ratio.
      bodies += model.ProcedureList.Count(p => !p.IsExternal && p.Body is not null) + 1;
      externalDeclarations += model.ProcedureList.Count(p => p.IsExternal || p.Body is null);
      foreach (var (procName, procWhy) in module.ProcedureLoweringDeclines)
        proceduresNotLowered.Add($"{Path.GetRelativePath(dir, file).Replace('\\', '/')}::{procName}: {procWhy}");

      List<string> routedNames;
      List<(string Name, string Reason)> routingDeclineList;
      try {
        var generator = new CodeGenerator(model) { Optimize = true, UseExperimentalBackend = true };
        generator.EmitExecutable();
        routedNames = generator.BackendRoutedNames.ToList();
        routingDeclineList = generator.BackendDeclines.ToList();
      } catch (Exception e) {
        // The fourth outcome. A throw is no executable at all, so it is neither a routed function
        // nor a clean decline, and counting it as either would flatter one column or the other.
        threw.Add($"{Path.GetRelativePath(dir, file).Replace('\\', '/')}: {e.GetType().Name}: {e.Message}");
        continue;
      }
      routed += routedNames.Count;
      if (!routedNames.Contains("main", StringComparer.OrdinalIgnoreCase))
        mainBodiesNotRouted.Add(name);
      foreach (var (declinedName, declinedBecause) in routingDeclineList) {
        if (routedNames.Contains(declinedName, StringComparer.OrdinalIgnoreCase))
          continue;                                 // the fixpoint records a removal it later re-takes
        var key = Summarize(declinedBecause);
        routingDeclines[key] = routingDeclines.GetValueOrDefault(key) + 1;
        (routingDeclinePrograms.TryGetValue(key, out var programs)
          ? programs
          : routingDeclinePrograms[key] = new(StringComparer.Ordinal)).Add(name);
        routingDeclineCases.Add($"{Path.GetRelativePath(dir, file).Replace('\\', '/')}::{declinedName}: {declinedBecause}");
        notRoutedNames.Add(declinedName);
      }

      // ...and the same question with the optimizer OFF, which is the harder and more honest one.
      // The inliner absorbs a filtered callee into its caller, so an optimized main routes where an
      // unoptimized one is stranded by the very call the filter refused - the flip has to survive
      // BOTH, and the gap between the two figures is exactly how much of the routed number is on
      // loan from the inliner.
      try {
        var unoptimized = new CodeGenerator(model) { Optimize = false, UseExperimentalBackend = true };
        unoptimized.EmitExecutable();
        routedNoOptimize += unoptimized.BackendRoutedNames.Count();
      } catch (Exception e) {
        threw.Add($"{Path.GetRelativePath(dir, file).Replace('\\', '/')} (--no-optimize): {e.GetType().Name}: {e.Message}");
      }

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
      rejected, loweringDeclines, procedureDeclines, allocationDeclines, allocationCases,
      bodies, routed, routedNoOptimize, mainBodiesNotRouted, routingDeclines, routingDeclinePrograms,
      routingDeclineCases, notRoutedNames, proceduresNotLowered, threw, externalDeclarations);
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
      // the denominator above is every .BAS on disk, which is not the same as every program the back
      // end could ever be asked for: one the FRONT end rejects never reaches a lowering to decline.
      // Reported separately so the gap that is actually the back end's to close is legible.
      .AppendLine($"                     ({census.ProgramsRejectedByFrontEnd} of the rest are rejected by the front end and never reach the IR)")
      // THE HEADLINE. What the production code generator routed, over what it would have to route
      // once the direct emitter is gone: every procedure that has a body, plus one module body per
      // program. Everything below this line is diagnosis of the gap.
      .AppendLine($"functions ROUTED   : {census.Routed}/{census.Bodies} (production, --optimize; "
                  + $"{census.RoutedNoOptimize}/{census.Bodies} with --no-optimize)")
      .AppendLine($"module bodies OWNED: {census.ProgramsLowered.Count - census.MainBodiesNotRouted.Count}/{census.ProgramsLowered.Count} (production)")
      .AppendLine($"                     ({census.ExternalDeclarations} EXTERNAL declarations have no body and are nobody's coverage)")
      .AppendLine($"                     ({census.ThrewPrograms.Count} programs threw out of the back end - neither routed nor declined)")
      .AppendLine("routing declines - what the PRODUCTION routing refused, by its own reason:")
      .AppendLine("  (filter:    never offered to the selector - a shape the routed ABI cannot express)")
      .AppendLine("  (lowering:  the procedure body never reached the IR)")
      .AppendLine("  (selection: offered and refused by the instruction selector)")
      .AppendLine("  (allocation: selected, but no register assignment exists)")
      .AppendLine("  (routing:   stranded by a callee or an unaddressable symbol)");
    foreach (var (reason, count) in census.RoutingDeclines.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
      report.AppendLine($"  {count,5} in {census.RoutingDeclinePrograms[reason].Count,3} programs  {reason}");
    foreach (var routingCase in census.RoutingDeclineCases)
      report.AppendLine($"         {routingCase}");
    foreach (var thrown in census.ThrewPrograms)
      report.AppendLine($"  THREW  {thrown}");
    foreach (var notLowered in census.ProcedureBodiesNotLowered)
      report.AppendLine($"  BODY DID NOT LOWER  {notLowered}");

    report
      .AppendLine("--- the SELECTOR's own reach, over every function the lowering produced ---")
      .AppendLine("    (this is the pair that used to be the headline; it says nothing about the")
      .AppendLine("     procedures the filter never offers, which is why it can read 262/262)")
      .AppendLine($"functions selected : {census.Selected}/{census.Functions}")
      .AppendLine($"functions allocated: {census.Allocated}/{census.Functions} (selected AND allocated)")
      .AppendLine($"module bodies      : {census.MainBodies.Count}/{census.ProgramsLowered.Count} whole programs the selector can own")
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

    // ---- the honest headline, and the assertions that keep it honest ----
    //
    // 245 of 263, not 262 of 262. The difference is not a regression and nothing got worse: it is
    // what the number always was once the procedures the filter skips are counted as the declines
    // they are. Ranked by how many procedures each class costs, over the corpus:
    //
    //   12  BYREF parameter (7 INTEGER, 3 SINGLE, 2 LONG)   filtered - never offered
    //    2  STRING return type                              filtered
    //    2  a callee with no link symbol                     routing
    //    1  STRING parameter                                filtered
    //    1  a procedure body the lowering refused            lowering - invisible before this census
    //
    // A non-SPEED BASIC/PASCAL caller can call a direct callee through their shared stack ABI, so
    // BYREF procedures no longer strand their module bodies. External declarations and procedure
    // bodies that did not lower still have no linkable local body and keep their callers direct.
    //
    // Classes the corpus does NOT exercise are real all the same, and BackendRoutingGateTests holds
    // one program each: QUAD and BYTE parameters and returns, UDT/FIX/EXT parameters, a
    // CDECL/STDCALL/FASTCALL/WATCALL convention, and error handling inside a procedure body.
    //
    // A floor, so a widening may only raise it. Lowering it means the back end took less than it did.
    Assert.That(census.Routed, Is.GreaterThanOrEqualTo(245),
      $"the x86-16 back end now ROUTES fewer corpus functions than it used to ({census.Routed}/{census.Bodies}):\n" + report);
    Assert.That(census.RoutedNoOptimize, Is.GreaterThanOrEqualTo(242),
      "the x86-16 back end routes fewer corpus functions with --no-optimize than it used to:\n" + report);

    // Pinned by name for the reason every other set here is: a count cannot tell "a program stopped
    // routing" from "a program was added and never did".
    Assert.That(census.MainBodiesNotRouted, Is.EquivalentTo(_mainBodiesNotRouted),
      "the set of module bodies the PRODUCTION routing does not take has changed:\nactual: " +
      string.Join(", ", census.MainBodiesNotRouted) + "\n" + report);

    // A procedure whose body the lowering refuses is left a declaration, so it vanishes from the IR
    // and every selector census stops counting it. Pinned so that vanishing can never again read as
    // one fewer function to cover.
    Assert.That(census.ProcedureBodiesNotLowered, Is.EquivalentTo(_procedureBodiesNotLowered),
      "the set of procedure bodies the IR lowering refuses has changed:\nactual: " +
      string.Join(", ", census.ProcedureBodiesNotLowered) + "\n" + report);

    // The fourth outcome. A program that throws out of the back end produces no executable, so it is
    // neither routed nor declined; if one appears it must be visible rather than averaged away.
    Assert.That(census.ThrewPrograms, Is.Empty,
      "a corpus program throws out of the x86-16 back end - that is neither coverage nor a decline:\n" + report);

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
    // 216 -> 215 on purpose: refusing to fold INEXACT float constants (IrConstFold.FoldFloat, so the
    // x87's eighty bits are not pre-rounded to sixty-four) leaves one FPToSI f32 -> i64 the selector
    // does not handle, where the fold used to remove it. Correctness bought back six battery
    // programs for one function's coverage, which is the trade worth making in that direction.
    // Then 220 -> 224 when shared-array GEPs began at the direct emitter's data label and STATIC
    // globals gained procedure-qualified identities; SHAREDG and SUBFN lose their four procedure
    // declines without inventing a second data layout.
    // Then 224 -> 225 when IrSwitch gained word and dword compare-chain selection, removing the last
    // named-procedure decline from the corpus.
    // Then 225 -> 227 when the complete MK/CV binary-record family and the DX:AX result convention
    // routed DIFF08 and DIFF58 through their whole module bodies.
    // Then 227 -> 228 when raw segmented-pointer comparison routed DIFF10's UDT equality.
    // Then 228 -> 230 when segmented memcpy/memset routed DIFF23's whole-record assignment and
    // DIFF74's static ERASE.
    // Then PB 3.2 data pointers brought DIFF09 and DIFF12 onto the path whole - three more functions,
    // two more module bodies - because a pointer is an ADDRESS in the IR and never a number: the
    // forms whose segment is known (VARPTR32 of storage, a pointer copied from another) lower, and
    // one made out of a DWORD declines rather than being given a segment nobody named.
    // Then one more when PRINT USING and LPRINT lowered: rt_usefmt was already the direct emitter's
    // own formatter, so what was missing was the ABI row and the compile-time read of the format.
    // Then 230 -> 231 when a QUAD read out of storage gained a frame cell of its own: FILD/FISTP
    // copies the eight bytes the moment the load names them, and the 64-bit printer takes them from
    // there instead of insisting on a literal.
    // Then 231 -> 234 as the last three declines went: the documented inline-asm string-manager
    // routines are bound names rather than unknown ones, and FIX/BCD storage lowers - a FIX cell
    // being a scaled int64 the runtime's own scaling routines read and write.
    // Then when dynamic array storage routed: an IR pointer gained an ADDRESS SPACE, so a block in the
    // far array heap is a different kind of pointer rather than the same kind pointing somewhere the
    // back end could not name, and the allocation family took its size in bytes.
    // Then 257 -> 258 when EXIT FAR lowered, which is one program's whole gain: DIFF14 reached the IR
    // at all and its SUB selected. Its module body did not - the decline behind EXIT FAR was another
    // one, and this is what "the count moved by one" looks like when that happens.
    // Then 260 -> 261 with the memory-model array classes: DIFF17 was the LAST program the lowering
    // declined, so this is the row where the lowering-decline histogram above becomes empty.
    // Then 261 -> 262, which empties the selection histogram too: an inline-asm statement now DECLARES
    // the registers it defines and reads (the assembler reads them out of the text), so a countdown
    // held in CX across a BASIC statement is a promise the allocator can keep instead of a shape that
    // had to decline. LOWLEVEL.BAS was the last function on this list.
    Assert.That(census.Selected, Is.GreaterThanOrEqualTo(262),
      "the x86-16 back end now compiles fewer corpus functions than it used to:\n" + report);
    Assert.That(census.ProcedureDeclines, Is.Empty,
      "a lowered named procedure no longer reaches the x86-16 back end:\n" + report);

    // How many programs reach the IR at all - the figure the runtime-trap and error-handling work
    // moves, since a program that declines at the lowering never reaches the selector to be counted
    // above. 119 -> 122 with $ERROR OVERFLOW ON and dynamic-array bounds checking, 122 -> 129 with
    // ON ERROR / RESUME and the ERR / ERL cells their handlers read, 129 -> 132 with $ERROR NUMERIC
    // ON and ERRCLEAR.
    // By NAME, like the module bodies below and for the same reason: a count cannot tell a program
    // that stopped lowering from a program that was added and never did. Both move the number by one
    // and only one of them is a regression.
    Assert.That(census.ProgramsLowered, Is.EquivalentTo(_loweredToIr),
      // spelled out rather than left to NUnit's elision: the set is the thing being re-pinned, and
      // "..." after ten entries cannot be pasted back into the list below
      "the set of corpus programs reaching the IR has changed:\nlowered: " +
      string.Join(", ", census.ProgramsLowered) + "\n" + report);

    // selection is not routing: the whole-program codegen also schedules and allocates, and a value
    // live across a CALL has no register unless the spiller can move it to the frame.
    // 256 -> 257, and the gap closes: the last function that selected without routing did so because
    // SCHEDULING made the pressure. The scheduler now refuses a reordering that would keep more values
    // alive at once than the register file holds (MachineScheduler.CostsRegisters).
    // 261 -> 262, and the gap between selection and routing stays closed: a register an inline-asm
    // statement holds for a later one is reserved over exactly the stretch between them, so the shape
    // that needed it allocates rather than declining.
    Assert.That(census.Allocated, Is.GreaterThanOrEqualTo(262),
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
  /// <summary>
  /// Every corpus module body the PRODUCTION routing does not take, with the reason. Both are
  /// consequences rather than causes: one is stranded by external declarations with no local body,
  /// and the other by a procedure body the lowering refused. Fix the cause and the body follows.
  /// </summary>
  private static readonly string[] _mainBodiesNotRouted = [
    "LINKDEMO.BAS",   // calls AddInts/Bump/Greet, which are EXTERNAL and have no body here
    "CODEGEN.BAS",    // calls SwapIsInline, whose body the lowering refused
  ];

  /// <summary>
  /// Every procedure that HAS a body and whose body the IR lowering refused. Such a procedure is
  /// left a declaration, so it leaves the IR entirely: it is in no selection histogram, no
  /// allocation histogram, and neither half of a ratio taken over IR functions.
  /// </summary>
  private static readonly string[] _procedureBodiesNotLowered = [
    "optimize/CODEGEN.BAS::SwapIsInline: unsupported lvalue",
  ];

  private static readonly string[] _loweredToIr = [
    "ARRAY.BAS",
    "ARITH.BAS",
    "CTRL.BAS",
    "DATAREAD.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "DIFF03.BAS",
    "DIFF04.BAS",
    "DIFF05.BAS",
    "DIFF06.BAS",
    "DIFF07.BAS",   // ASCIIZ * n
    "DIFF08.BAS",
    "DIFF09.BAS",   // data pointers: VARPTR32, @p, @p[i], @q.Field
    "DIFF10.BAS",
    "DIFF11.BAS",   // code pointers: CODEPTR32 of a label, GOTO / GOSUB DWORD
    "DIFF12.BAS",   // BYVAL pointer override against a BYREF parameter
    "DIFF13.BAS",   // INSTR ANY / VERIFY / EXTRACT$ / TALLY, REPLACE, BIT, variadic CHR$
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
    "DIFF114.BAS",   // DIM ... AT segment (an ABSOLUTE array over the text screen)
    // EXIT FAR: the unwind point and the jump through it, as intrinsics the back end expands inline.
    // The program lowers and its SUB selects; its MODULE BODY does not, so DIFF14 is absent from the
    // owned bodies below - the report's selection declines say why (UIToFP u32 -> f80, from USING$ of
    // a DWORD), and its SUB takes a BYREF parameter, which the whole-program routing excludes anyway.
    "DIFF14.BAS",
    "DIFF15.BAS",
    "DIFF16.BAS",   // FIX (@) and BCD (@@): a scaled int64 cell and an f80 one
    "DIFF17.BAS",   // DIM HUGE / DIM VIRTUAL: the DOS and EMS allocators, and FRE(-11)
    "DIFF18.BAS",
    "DIFF19.BAS",   // $ERROR STACK ON
    "DIFF20.BAS",   // FIELD, LSET/RSET and bare GET/PUT (main still declines on inline asm)
    "DIFF21.BAS",   // CHAIN with a COMMON handoff
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
    "DIFF34.BAS",
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
    "DIFF57.BAS",   // WRITE # and SETEOF
    "DIFF58.BAS",
    "DIFF59.BAS",
    "DIFF60.BAS",
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
    "DIFF74.BAS",
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
    "DIFF86.BAS",   // ARRAY SORT / ARRAY SCAN / TAGARRAY over numeric arrays
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
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "QUIRK30.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "DIFF03.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "DIFF03.BAS",
    "FILEIO1.BAS",
    "HELLO.BAS",
    "INPUTS.BAS",
    "INTREG.BAS",   // REG / CALL INTERRUPT
    "LINKDEMO.BAS",
    "LOWLEVEL.BAS",   // VARPTR, and a countdown an inline-asm block holds in CX across BASIC code
    "MATHUNIT.BAS",
    "ONERR.BAS",
    "ONERRNXT.BAS",
    "CODEGEN.BAS",
    "RANGES.BAS",
    "PRTUSING.BAS",   // PRINT USING
    "RANDFILE.BAS",
    "SHAREDG.BAS",
    "STRBOUND.BAS",
    "STRHEAP.BAS",
    "STRINGS.BAS",
    "SUBFN.BAS",
  ];

  private static readonly string[] _ownedMainBodies = [
    "ARITH.BAS",
    "ARRAY.BAS",     // dynamic array storage: the far-heap address space and the byte-count allocator ABI
    "CTRL.BAS",
    "DATAREAD.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "DIFF03.BAS",
    "DIFF04.BAS",
    "DIFF05.BAS",
    "DIFF06.BAS",
    "DIFF07.BAS",   // ASCIIZ * n
    "DIFF08.BAS",
    "DIFF09.BAS",   // data pointers: VARPTR32, @p, @p[i], @q.Field
    "DIFF10.BAS",
    "DIFF11.BAS",   // code pointers: the GOSUB dispatch is listed after the continuations it reaches
    "DIFF12.BAS",   // BYVAL pointer override against a BYREF parameter
    "DIFF13.BAS",   // INSTR ANY / VERIFY / EXTRACT$ / TALLY, REPLACE, BIT, variadic CHR$
    "DIFF100.BAS",
    "DIFF101.BAS",
    "DIFF102.BAS",
    "DIFF103.BAS",
    "DIFF104.BAS",
    "DIFF105.BAS",  // $ERROR OVERFLOW: unsigned wrap + narrowing-store check
    "DIFF106.BAS",
    "DIFF107.BAS",
    "DIFF108.BAS",
    "DIFF109.BAS",
    "DIFF110.BAS",
    "DIFF111.BAS",
    "DIFF112.BAS",
    "DIFF113.BAS",
    "DIFF114.BAS",   // DIM ... AT segment (an ABSOLUTE array over the text screen)
    "DIFF15.BAS",
    "DIFF16.BAS",   // FIX (@) and BCD (@@): a scaled int64 cell and an f80 one
    "DIFF17.BAS",   // DIM HUGE / DIM VIRTUAL: segment stepping and the EMS page window
    "DIFF18.BAS",
    "DIFF19.BAS",
    "DIFF20.BAS",   // the documented inline-asm string-manager ABI, called by name
    "DIFF21.BAS",   // CHAIN with a COMMON handoff
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
    "DIFF32.BAS",   // unsigned 32-bit divide + the Error-11 guard
    "DIFF33.BAS",
    "DIFF34.BAS",   // whole-record copy between array elements
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
    "DIFF55.BAS",   // INT/FIX round trip through a qword
    "DIFF56.BAS",   // a 32-bit accumulation over an array, unrolled: pressure the scheduler must not add
    "DIFF57.BAS",
    "DIFF58.BAS",
    "DIFF59.BAS",
    "DIFF60.BAS",
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
    "DIFF74.BAS",
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
    "DIFF86.BAS",   // a QUAD read out of an array reaches the 64-bit printer through its own cell
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
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "QUIRK30.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "DIFF01.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "DIFF03.BAS",
    "DIFF01.BAS",
    "DIFF02.BAS",
    "DIFF03.BAS",
    "FILEIO1.BAS",
    "HELLO.BAS",
    "INTREG.BAS",   // REG / CALL INTERRUPT
    "INPUTS.BAS",
    "LINKDEMO.BAS",
    "MATHUNIT.BAS",
    "ONERR.BAS",
    "ONERRNXT.BAS",
    "RANGES.BAS",
    "CODEGEN.BAS",
    "PRTUSING.BAS",   // PRINT USING
    "RANDFILE.BAS",
    "SHAREDG.BAS",
    "STRBOUND.BAS",
    "STRHEAP.BAS",
    "STRINGS.BAS",
    "SUBFN.BAS",
    // UIToFP (FILD reads signed, so an unsigned source stages one size larger with the top zeroed)
    // and a compare-as-a-value whose left operand is an immediate, mirrored the way the branch
    // path already mirrors it. With these the corpus is COMPLETE on selection and allocation.
    "DIFF14.BAS",
    // The last module body of all, and the one that took a promise rather than a widening: its
    // countdown lives in CX across `n = n + 1`, so the asm statements had to be able to say which
    // registers they define and read before the allocator could be trusted with the frame.
    "LOWLEVEL.BAS",
  ];
}
