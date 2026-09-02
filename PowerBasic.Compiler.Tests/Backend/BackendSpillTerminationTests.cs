using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// That the x86-16 allocator's spill loop STOPS - and stops because each round gets measurably closer
/// to an allocation, not because a work budget cuts it off.
///
/// Two of the spiller's moves used to be able to undo each other. <c>MOV [address], constant</c> has
/// two recomputable operands and one instruction slot next to the store, so rematerializing either one
/// displaced the other, which then looked like it needed rematerializing in its turn: one round and one
/// fresh virtual register per swap, for ever. The budget that bounded it (<c>BudgetFor</c>) declined the
/// whole function instead of hanging, which is survivable while the back end has a fallback and is a
/// compile failure once it has not.
///
/// The fixtures below therefore lift the budget out of the way - a loop that has stopped converging has
/// to show up as an absurd round count or a hang, never as a tidy decline - and pin the corpus's worst
/// round count with the optimizer both on and off.
/// </summary>
[TestFixture, Category("Slow")]
public sealed class BackendSpillTerminationTests {

  private static readonly string _repoRoot =
    Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  /// <summary>Far above any budget the allocator would impose, so the loop has to stop on its own.</summary>
  private const int _LIFTED_BUDGET = 20_000;

  private static readonly IReadOnlyList<Reg> _wholeRegisterFile =
    [Reg.AX, Reg.BX, Reg.CX, Reg.DX, Reg.SI, Reg.DI];

  /// <summary>
  /// The shape that never settled: a frame address and a constant, both live across a <c>CALL</c> that
  /// destroys the register file, both consumed by one store.
  ///
  /// Neither can move to memory - an x86-16 memory BASE has to be a register, and the constant's only
  /// use already carries a memory operand, so spilling it would ask for a memory-to-memory <c>MOV</c> -
  /// so rematerialization is the only move either one has, and they compete for the same spot.
  ///
  /// <para>
  /// A THIRD value crosses the call as well, and it is what makes the fixture measure the defect rather
  /// than describe it. Rematerializing is offered first and, while the two operands are swapping, it
  /// always answers "I moved something" - so the sweep is retried, fails again on this third value, and
  /// the split that would actually fix it is never reached. Without it the pair swaps at most twice
  /// before the sweep happens to succeed, and the loop that cannot terminate terminates.
  /// </para>
  /// <para>
  /// It is a plain load-then-store, which is the one value shape that can be neither rematerialized
  /// (a frame cell is not a constant) nor spilled (both of its instructions already carry a memory
  /// operand, and neither may carry two). Splitting through a spill cell is the only move it has.
  /// </para>
  /// </summary>
  private static MFunction StoreOfAConstantThroughAFrameAddress() {
    var address = MReg.Virtual(0);
    var constant = MReg.Virtual(1);
    var carried = MReg.Virtual(2);
    var function = new MFunction("F") { VirtualRegisterCount = 3 };
    function.StackSlots.AddRange([2, 2, 2]);
    var block = new MBlock("entry");
    block.Instructions.Add(new MInstr(MOpcode.Lea,
      [new MOperand.Register(address), new MOperand.StackSlot(0, MRegSize.Word)],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: false, WritesMemory: false)));
    block.Instructions.Add(new MInstr(MOpcode.Mov,
      [new MOperand.Register(constant), new MOperand.Immediate(1234)],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: false, WritesMemory: false)));
    block.Instructions.Add(new MInstr(MOpcode.Mov,
      [new MOperand.Register(carried), new MOperand.StackSlot(1, MRegSize.Word)],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: true, WritesMemory: false)));
    block.Instructions.Add(new MInstr(MOpcode.Call, [new MOperand.LabelRef("rt_print_nl")],
      MInstrEffect.None, clobbers: _wholeRegisterFile));
    block.Instructions.Add(new MInstr(MOpcode.Mov,
      [new MOperand.Memory(address, null, 1, 0, MRegSize.Word), new MOperand.Register(constant)],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [1], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: false, WritesMemory: true)));
    block.Instructions.Add(new MInstr(MOpcode.Mov,
      [new MOperand.StackSlot(2, MRegSize.Word), new MOperand.Register(carried)],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [1], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: false, WritesMemory: true)));
    block.Instructions.Add(new MInstr(MOpcode.Ret, [], MInstrEffect.None));
    function.Blocks.Add(block);
    return function;
  }

  /// <summary>
  /// With the budget lifted this runs until the fixture's own cancellation on an unfixed tree; with the
  /// budget in place it is the decline the budget was added to produce. The assertion is on the ROUND
  /// COUNT rather than on the allocation alone, because a budget-free loop that happened to converge
  /// late would also report an allocation in the end.
  /// </summary>
  [Test]
  [CancelAfter(30_000)]
  public void Allocate_GivenTwoRecomputableOperandsOfOneStore_ThenTheSpillerSettlesInsteadOfSwapping() {
    var function = StoreOfAConstantThroughAFrameAddress();

    var allocation = LinearScanAllocator.Allocate(function, SelectionTarget.Baseline, out var reason,
      out var rounds, _LIFTED_BUDGET);

    Assert.That(allocation, Is.Not.Null,
      $"declined after {rounds} rounds: {reason}\n{string.Join(Environment.NewLine, function.AllInstructions)}");
    Assert.That(rounds, Is.LessThanOrEqualTo(6),
      "each operand is recomputed once, and the value that needs the split then gets to it:\n"
      + string.Join(Environment.NewLine, function.AllInstructions));
    // and the point of the whole exercise: the address is recomputed once, not once per round
    Assert.That(function.AllInstructions.Count(instruction => instruction.Opcode is MOpcode.Lea),
      Is.EqualTo(1));
  }

  /// <summary>
  /// The same shape reached through the real pipeline rather than built by hand, and unoptimized -
  /// which is the state the ping-pong needs, since a constant stored through a frame address is exactly
  /// what the optimizer would have folded away first. A second call site with a different argument
  /// keeps interprocedural constant propagation from proving the parameter (it does not run here, but
  /// the fixture must not depend on that).
  /// </summary>
  [Test]
  [CancelAfter(60_000)]
  public void Allocate_GivenUnoptimizedIrWithFrameStores_ThenEveryFunctionSettlesQuickly() {
    const string source = """
      SUB Fill(BYVAL seed%)
        DIM cell%(0 TO 3)
        cell%(0) = 11
        cell%(1) = 22
        cell%(2) = seed%
        PRINT "cells"; cell%(0); cell%(1); cell%(2)
      END SUB

      Fill 7
      Fill 9
      """;
    var worst = 0;
    var declined = new List<string>();

    foreach (var (name, machine) in Compile(source, optimize: false)) {
      var allocation = LinearScanAllocator.Allocate(machine, SelectionTarget.Baseline, out var reason,
        out var rounds, _LIFTED_BUDGET);
      worst = Math.Max(worst, rounds);
      if (allocation is null)
        declined.Add($"{name}: {reason} (after {rounds} rounds)");
    }

    Assert.That(declined, Is.Empty);
    Assert.That(worst, Is.LessThanOrEqualTo(64), "the spill loop no longer converges on unoptimized IR");
  }

  /// <summary>
  /// The corpus measurement: with the budget lifted, how many rounds does the WORST function in the
  /// whole battery need - with the optimizer on, and with it off (the pipeline a <c>--no-optimize</c>
  /// routed build runs, and the one the non-terminating shapes need). It is the assertion that turns a
  /// future regression into a number instead of a hang, and it is what says the budget is unreachable.
  /// </summary>
  [Test]
  [CancelAfter(600_000)]
  [TestCase(true, TestName = "Allocate_GivenTheCorpus_WhenOptimized_ThenTheSpillLoopSettlesFarBelowTheBudget")]
  [TestCase(false, TestName = "Allocate_GivenTheCorpus_WhenUnoptimized_ThenTheSpillLoopSettlesFarBelowTheBudget")]
  public void Allocate_GivenTheCorpus_ThenTheSpillLoopSettlesFarBelowTheBudget(bool optimize) {
    var directory = Path.Combine(_repoRoot, "tests");
    Assume.That(Directory.Exists(directory), "no tests/*.BAS corpus present");
    var worst = (Rounds: 0, Where: "nothing");
    var overBudget = new List<string>();
    var declined = new List<string>();
    var functions = 0;
    var allocated = 0;

    foreach (var file in Directory.EnumerateFiles(directory, "*.BAS", SearchOption.AllDirectories)
               .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
      foreach (var (name, machine) in Compile(File.ReadAllText(file), optimize, Path.GetFileName(file))) {
        ++functions;
        var budget = Math.Min(machine.VirtualRegisterCount + 64, 512);
        var allocation = LinearScanAllocator.Allocate(machine, SelectionTarget.Baseline, out var reason,
          out var rounds, _LIFTED_BUDGET);
        if (allocation is not null)
          ++allocated;
        else
          declined.Add($"{Path.GetFileName(file)}::{name}: {reason}");
        if (rounds > worst.Rounds)
          worst = (rounds, $"{Path.GetFileName(file)}::{name}");
        if (rounds > budget)
          overBudget.Add($"{Path.GetFileName(file)}::{name}: {rounds} rounds against a budget of {budget}");
      }

    TestContext.Out.WriteLine($"optimize={optimize}: {allocated}/{functions} allocated, "
      + $"worst {worst.Rounds} spiller rounds ({worst.Where})");
    foreach (var entry in declined)
      TestContext.Out.WriteLine($"  declined {entry}");
    Assume.That(functions, Is.GreaterThan(0), "no corpus function reached the allocator");
    Assert.That(overBudget, Is.Empty, "the work budget is reachable again, so it is holding the loop up");
    // A ceiling with room in it, not the measured number: the point is that the loop settles in
    // something proportional to the function, never that it settles in exactly N.
    Assert.That(worst.Rounds, Is.LessThanOrEqualTo(256),
      $"the worst corpus function now needs {worst.Rounds} spiller rounds ({worst.Where})");
  }

  /// <summary>
  /// The back end's own routing pipeline, in both optimization modes - the same shape
  /// <c>CodeGenerator.BackendProcs</c> runs, so what the fixtures measure is what production does.
  /// </summary>
  private static IEnumerable<(string Name, MFunction Machine)> Compile(string source, bool optimize,
      string file = "T.BAS") {
    SemanticModel model;
    IrModule? module;
    try {
      model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, file, Dialect.Pb36), file, Dialect.Pb36), Dialect.Pb36);
      if (model.Errors.Count > 0)
        yield break;
      module = IrLowering.TryLowerModule(model, out _);
    } catch (Exception) {
      yield break;
    }
    if (module is null)
      yield break;

    var pipeline = optimize ? () => IrPassManager.Standard() : (Func<IrPassManager>)IrPassManager.Legalize;
    foreach (var function in module.Functions)
      if (!function.IsDeclaration)
        IntegerRecovery.Run(function);
    pipeline().RunOnModule(module);
    foreach (var function in module.Functions)
      if (!function.IsDeclaration)
        IntegerRecovery.Run(function);
    pipeline().RunOnModule(module);

    foreach (var function in module.Functions) {
      if (function.IsDeclaration || InstructionSelector.TrySelect(function, out _) is not { } machine)
        continue;
      MachineScheduler.Schedule(machine);
      yield return (function.Name, machine);
    }
  }
}
