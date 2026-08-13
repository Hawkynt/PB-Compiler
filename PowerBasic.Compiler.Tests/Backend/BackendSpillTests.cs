using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Spilling on the x86-16 back end. The allocation failure that matters on this target is not "six
/// registers ran out" - it is a <c>CALL</c>, which destroys the whole caller-saved file, so a value
/// live across one may sit in none of them. Before spilling existed those functions were selected and
/// then dropped; the census called that out by reporting routed separately from selected.
///
/// x86 is a memory-operand machine, so a spilled value needs no reload code: it simply becomes its
/// frame cell. A parameter's cell is free - the caller already pushed it, and an IR argument is an SSA
/// value nothing writes - so a spilled parameter loses its prologue copy and its uses address
/// <c>[BP+offset]</c> directly.
/// </summary>
[TestFixture]
public sealed class BackendSpillTests {

  private const string _loopCarriedAcrossFilePrints = """
    OPEN "O.TXT" FOR OUTPUT AS #1
    total% = 0
    FOR index% = 1 TO 3
      IF index% > 1 THEN total% = total% + index%
      PRINT #1, "total"; index%; total%
    NEXT index%
    CLOSE #1
    """;

  private const string _wrappedByteLoop = """
    OPEN "O.TXT" FOR OUTPUT AS #1
    count% = 0
    FOR item? = 1 TO 255
      INCR count%
      IF count% > 300 THEN EXIT FOR
    NEXT item?
    PRINT #1, count%; item?
    CLOSE #1
    """;

  private const string _descendingUnsignedLoop = """
    OPEN "O.TXT" FOR OUTPUT AS #1
    count% = 0
    FOR item?? = 2 TO 0 STEP -1
      INCR count%
      IF count% > 5 THEN EXIT FOR
    NEXT item??
    PRINT #1, count%
    CLOSE #1
    """;

  private const string _branchingLoopWithFilePrints = """
    $OPTIMIZE SPEED
    OPEN "RESULT.TXT" FOR OUTPUT AS #1
    s% = 0
    FOR i% = 1 TO 15
      SELECT CASE i%
        CASE 1, 3, 5, 7
          s% = s% + i%
        CASE 8 TO 11
          s% = s% + 100
        CASE ELSE
          s% = s% - 1
      END SELECT
      PRINT #1, "i"; i%; s%
    NEXT i%
    PRINT #1, "sum"; s%
    CLOSE #1
    END
    """;

  // v% is loaded in the prologue and used AFTER the print - so it is live across a call that
  // destroys every allocatable register.
  //
  // Two call sites passing DIFFERENT constants, deliberately: with one constant call site,
  // interprocedural constant propagation replaces v% with the literal and the function no longer has
  // a parameter to spill - a correct optimization that would quietly turn this into a test of nothing.
  private const string _liveAcrossACall = """
    FUNCTION Twice%(BYVAL v%)
      PRINT "X"
      Twice% = v% + v%
    END FUNCTION

    PRINT Twice%(21)
    PRINT Twice%(22)
    """;

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static MFunction Select(string source, string function) {
    var module = IrLowering.TryLowerModule(Bind(source));
    Assert.That(module, Is.Not.Null, "outside the IR lowering's subset");
    IrPassManager.Standard().RunOnModule(module!);
    foreach (var f in module!.Functions)
      if (!f.IsDeclaration)
        IntegerRecovery.Run(f);
    IrPassManager.Standard().RunOnModule(module);
    var fn = module.Functions.First(f => f.Name.Equals(function, StringComparison.OrdinalIgnoreCase));
    var m = InstructionSelector.TrySelect(fn, out var reason);
    Assert.That(m, Is.Not.Null, $"{function} declined: {reason}");
    return m!;
  }

  [Test]
  public void Allocate_GivenAParameterLiveAcrossACall_ThenSpillsItToTheCallersOwnWord() {
    var m = Select(_liveAcrossACall, "Twice");
    Assert.That(m.ArgumentLoads, Is.Not.Empty, "the parameter starts out in a register");

    var allocation = LinearScanAllocator.Allocate(m);

    Assert.That(allocation, Is.Not.Null, "the function should route once the parameter can move to memory");
    Assert.That(m.AllInstructions.SelectMany(i => i.Operands).OfType<MOperand.ParamCell>().ToList(),
      Is.Not.Empty, "the uses address [BP+offset] directly");
    Assert.That(m.ArgumentLoads, Is.Empty, "a spilled parameter is not copied into a register at all");
  }

  [Test]
  public void Allocate_GivenNoPressure_ThenSpillsNothing() {
    var m = Select("""
      FUNCTION Twice%(BYVAL v%)
        Twice% = v% + v%
      END FUNCTION

      PRINT Twice%(21)
      """, "Twice");

    var allocation = LinearScanAllocator.Allocate(m);

    Assert.That(allocation, Is.Not.Null);
    Assert.That(m.ArgumentLoads, Is.Not.Empty, "nothing forced the parameter out of its register");
    Assert.That(m.AllInstructions.SelectMany(i => i.Operands).OfType<MOperand.ParamCell>(), Is.Empty);
    Assert.That(m.StackSlots, Is.Empty);
  }

  [Test]
  public void Allocate_GivenASpill_ThenTheInstructionStillHasAtMostOneMemoryOperand() {
    var m = Select(_liveAcrossACall, "Twice");

    LinearScanAllocator.Allocate(m);

    foreach (var instr in m.AllInstructions) {
      var memory = instr.Operands.Count(o =>
        o is MOperand.Memory or MOperand.StackSlot or MOperand.DataCell or MOperand.ParamCell);
      Assert.That(memory, Is.LessThanOrEqualTo(1), $"{instr.Opcode} would be a memory-to-memory instruction");
    }
  }

  [Test]
  public void Emit_GivenASpilledParameter_ThenTheImageAssemblesAndTheBackEndTookTheFunction() {
    var direct = new CodeGenerator(Bind(_liveAcrossACall)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(_liveAcrossACall)) { Optimize = true, UseExperimentalBackend = true };

    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routedImage, Is.Not.Empty);
    Assert.That(routed.BackendRoutedNames, Does.Contain("Twice"), "the back end did not take the function");
    // it emits its own code for the body - a spilled parameter read straight from [BP+6] rather than
    // reloaded around the call - and the whole image still assembles and links
    Assert.That(routedImage, Is.Not.EqualTo(directImage));
  }

  [Test]
  public void Allocate_GivenAStringResultAddressLiveAcrossRuntimeCalls_ThenRematerializesItAtEachUse() {
    var m = Select("""
      FUNCTION Greet$(name AS STRING)
        Greet$ = "HELLO, " + name + "!"
      END FUNCTION

      PRINT Greet$("WORLD")
      """, "Greet");

    var allocation = LinearScanAllocator.Allocate(m, out var reason);

    Assert.That(allocation, Is.Not.Null,
      $"allocation declined: {reason}\n{string.Join(Environment.NewLine, m.AllInstructions)}");
    Assert.That(m.ArgumentLoads, Is.Empty, "the long-lived pointer must not remain in a prologue virtual");
    Assert.That(m.AllInstructions.SelectMany(instruction => instruction.Operands)
      .OfType<MOperand.ParamCell>(), Is.Not.Empty, "each address use must reload the incoming pointer");
  }

  [Test]
  public void Allocate_GivenParameterCannotBecomeAMemoryOperand_ThenReloadsItAtEachUse() {
    var machine = Select("""
      SUB Callee(BYVAL fixed%, BYVAL varying%)
        PRINT "value"; fixed% + varying%
      END SUB

      Callee 100, 1
      Callee 100, 2
      """, "Callee");
    MachineScheduler.Schedule(machine);

    var allocation = LinearScanAllocator.Allocate(machine, out var reason);

    Assert.That(allocation, Is.Not.Null,
      $"allocation declined: {reason}\n{string.Join(Environment.NewLine, machine.AllInstructions)}");
  }

  [Test]
  public void Allocate_GivenALocalArrayAddressLiveAcrossRuntimeCalls_ThenRematerializesItsGepChain() {
    var m = Select("""
      $ERROR BOUNDS ON
      SUB Work()
        DIM values%(0 TO 20)
        index% = 5
        values%(index%) = index%
        PRINT #1, "idx"; values%(5); values%(10)
      END SUB

      OPEN "O.TXT" FOR OUTPUT AS #1
      Work
      CLOSE #1
      """, "Work");

    var slotsBefore = m.StackSlots.Count;

    var allocation = LinearScanAllocator.Allocate(m, out var reason);

    Assert.That(allocation, Is.Not.Null,
      $"allocation declined: {reason}\n{string.Join(Environment.NewLine, m.AllInstructions)}");
    Assert.That(m.StackSlots.Count, Is.GreaterThan(slotsBefore),
      "the remaining memory-to-memory value must be split through an explicit spill slot");
  }

  [Test]
  [CancelAfter(2_000)]
  public void Allocate_GivenMultipleDefinitionsStraddlingAClobber_ThenEliminatesTheOldInterval() {
    // The first definition has to be READ after the CALL for this to be the shape it claims. Without
    // the read it is a dead store, the value is not live where the clobber lands, and the allocator is
    // right to keep it in a register and split nothing - which is what it does now that it asks
    // liveness rather than the interval hull whether a clobber touches anything.
    var value = MReg.Virtual(0);
    var function = new MFunction("F") { VirtualRegisterCount = 1 };
    function.StackSlots.Add(2);
    var block = new MBlock("entry");
    block.Instructions.Add(new MInstr(MOpcode.Mov,
      [new MOperand.Register(value), new MOperand.Immediate(1)],
      new MInstrEffect([0], [], false, false, false, false)));
    block.Instructions.Add(new MInstr(MOpcode.Call, [new MOperand.LabelRef("rt")], MInstrEffect.None,
      clobbers: [Reg.AX, Reg.BX, Reg.CX, Reg.DX, Reg.SI, Reg.DI]));
    block.Instructions.Add(new MInstr(MOpcode.Mov,
      [new MOperand.StackSlot(0, MRegSize.Word), new MOperand.Register(value)],
      new MInstrEffect([], [1], false, false, false, true)));
    block.Instructions.Add(new MInstr(MOpcode.Mov,
      [new MOperand.Register(value), new MOperand.Immediate(2)],
      new MInstrEffect([0], [], false, false, false, false)));
    block.Instructions.Add(new MInstr(MOpcode.Mov,
      [new MOperand.StackSlot(0, MRegSize.Word), new MOperand.Register(value)],
      new MInstrEffect([], [1], false, false, false, true)));
    block.Instructions.Add(new MInstr(MOpcode.Ret, [], MInstrEffect.None));
    function.Blocks.Add(block);

    var allocation = LinearScanAllocator.Allocate(function, out var reason);

    Assert.That(allocation, Is.Not.Null, $"allocation declined: {reason}");
    Assert.That(function.AllInstructions.Select(LivenessAnalysis.RegistersOf)
      .SelectMany(registers => registers.Reads.Concat(registers.Writes)), Does.Not.Contain(0),
      "each definition and use must receive a fresh short-lived virtual id");
    Assert.That(function.StackSlots, Has.Count.EqualTo(2), "all definitions must share one spill cell");
  }

  [Test]
  [CancelAfter(2_000)]
  public void Allocate_GivenReadModifyWriteDefinitionNeedsSplitting_ThenReloadsBeforeUpdating() {
    var value = MReg.Virtual(0);
    var function = new MFunction("F") { VirtualRegisterCount = 1 };
    function.StackSlots.AddRange([2, 2, 2]);
    var block = new MBlock("entry");
    block.Instructions.Add(new MInstr(MOpcode.Mov,
      [new MOperand.Register(value), new MOperand.StackSlot(0, MRegSize.Word)],
      new MInstrEffect([0], [], false, false, true, false)));
    block.Instructions.Add(new MInstr(MOpcode.Add,
      [new MOperand.Register(value), new MOperand.StackSlot(1, MRegSize.Word)],
      new MInstrEffect([0], [0], false, true, true, false)));
    block.Instructions.Add(new MInstr(MOpcode.Call, [new MOperand.LabelRef("rt")], MInstrEffect.None,
      clobbers: [Reg.AX, Reg.BX, Reg.CX, Reg.DX, Reg.SI, Reg.DI]));
    block.Instructions.Add(new MInstr(MOpcode.Mov,
      [new MOperand.StackSlot(2, MRegSize.Word), new MOperand.Register(value)],
      new MInstrEffect([], [1], false, false, false, true)));
    block.Instructions.Add(new MInstr(MOpcode.Ret, [], MInstrEffect.None));
    function.Blocks.Add(block);

    var allocation = LinearScanAllocator.Allocate(function, out var reason);

    Assert.That(allocation, Is.Not.Null, $"allocation declined: {reason}");
    Assert.That(function.AllInstructions.Select(LivenessAnalysis.RegistersOf)
      .SelectMany(registers => registers.Reads.Concat(registers.Writes)), Does.Not.Contain(0));
    Assert.That(function.StackSlots, Has.Count.EqualTo(4), "the whole update chain must share one spill cell");
  }

  [Test]
  public void Allocate_GivenWordAndByteViewsOfOneValue_ThenSpillReferencesKeepTheirOwnWidths() {
    var word = MReg.Virtual(0, MRegSize.Word);
    var lowByte = MReg.Virtual(0, MRegSize.Byte);
    var result = MReg.Virtual(1, MRegSize.Byte);
    var function = new MFunction("F") { VirtualRegisterCount = 2 };
    var block = new MBlock("entry");
    block.Instructions.Add(new MInstr(MOpcode.Mov,
      [new MOperand.Register(word), new MOperand.Immediate(0)],
      new MInstrEffect([0], [], false, false, false, false)));
    block.Instructions.Add(new MInstr(MOpcode.Mov,
      [new MOperand.Register(lowByte), new MOperand.Immediate(1)],
      new MInstrEffect([0], [], false, false, false, false)));
    block.Instructions.Add(new MInstr(MOpcode.Call, [new MOperand.LabelRef("rt")], MInstrEffect.None,
      clobbers: [Reg.AX, Reg.BX, Reg.CX, Reg.DX, Reg.SI, Reg.DI]));
    block.Instructions.Add(new MInstr(MOpcode.Mov,
      [new MOperand.Register(result), new MOperand.Register(lowByte)],
      new MInstrEffect([0], [1], false, false, false, false)));
    block.Instructions.Add(new MInstr(MOpcode.Ret, [], MInstrEffect.None));
    function.Blocks.Add(block);

    var allocation = LinearScanAllocator.Allocate(function, out var reason);

    Assert.That(allocation, Is.Not.Null, $"allocation declined: {reason}");
    var cells = function.AllInstructions.SelectMany(instruction => instruction.Operands)
      .OfType<MOperand.StackSlot>().ToList();
    Assert.That(cells, Has.Count.EqualTo(3));
    Assert.That(cells.Select(cell => cell.Size), Is.EqualTo(new[] {
      MRegSize.Word,
      MRegSize.Byte,
      MRegSize.Byte,
    }));
  }

  [Test]
  public void Allocate_GivenABranchingLoopWithFilePrints_ThenRegisterMemoryMovesMatchWidths() {
    var machine = Select(_branchingLoopWithFilePrints, "main");
    MachineScheduler.Schedule(machine);
    var slotsBefore = machine.StackSlots.Count;

    var allocation = LinearScanAllocator.Allocate(machine, out var reason);

    Assert.That(allocation, Is.Not.Null, $"allocation declined: {reason}");
    static MRegSize? Size(MOperand operand) => operand switch {
      MOperand.Register register => register.Reg.Size,
      MOperand.Memory memory => memory.Size,
      MOperand.StackSlot stack => stack.Size,
      MOperand.DataCell data => data.Size,
      MOperand.ParamCell parameter => parameter.Size,
      _ => null,
    };
    var mismatches = machine.AllInstructions
      .Where(instruction => instruction.Opcode == MOpcode.Mov && instruction.Operands.Count == 2)
      .Where(instruction => Size(instruction.Operands[0]) is { } left
                            && Size(instruction.Operands[1]) is { } right && left != right)
      .Select(instruction => instruction.ToString())
      .ToList();
    Assert.That(mismatches, Is.Empty,
      $"{slotsBefore} slots before allocation; {machine.StackSlots.Count} after\n" +
      string.Join(Environment.NewLine, machine.AllInstructions));
  }

  [Test]
  public void Run_GivenARematerializedLocalArrayAddress_ThenBothBackendsObserveTheSameValues() {
    const string source = """
      SUB Work()
        DIM values%(0 TO 20)
        values%(5) = 5
        PRINT "idx"; values%(5); values%(10)
      END SUB

      Work
      """;
    var machine = Select(source, "Work");
    MachineScheduler.Schedule(machine);
    var allocation = LinearScanAllocator.Allocate(machine);
    Assert.That(allocation, Is.Not.Null);
    var trace = string.Join(Environment.NewLine, machine.AllInstructions)
      + Environment.NewLine + string.Join(", ", allocation!.OrderBy(pair => pair.Key)
        .Select(pair => $"v{pair.Key}={pair.Value}"));
    var direct = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(routed.BackendRoutedNames, Does.Contain("Work"), "the back end did not take the array function");
    Assert.That(routedCpu.ExitCode, Is.EqualTo(directCpu.ExitCode));
    Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output), trace);
  }

  [Test]
  public void Allocate_GivenLoopCarriedPhiAcrossRuntimeCalls_ThenSplitsAllDefinitions() {
    var machine = Select(_loopCarriedAcrossFilePrints, "main");
    MachineScheduler.Schedule(machine);

    var allocation = LinearScanAllocator.Allocate(machine, out var reason);

    Assert.That(allocation, Is.Not.Null,
      $"allocation declined: {reason}\n{string.Join(Environment.NewLine, machine.AllInstructions)}");
  }

  [Test]
  public void Run_GivenSplitLoopCarriedPhi_ThenBothBackendsWriteTheSameFile() {
    var direct = new CodeGenerator(Bind(_loopCarriedAcrossFilePrints)) {
      Optimize = true,
      UseExperimentalBackend = false,
    };
    var routed = new CodeGenerator(Bind(_loopCarriedAcrossFilePrints)) {
      Optimize = true,
      UseExperimentalBackend = true,
    };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(routed.BackendRoutedNames, Does.Contain("main"), "the back end did not take the loop body");
    Assert.That(routedCpu.ExitCode, Is.EqualTo(directCpu.ExitCode));
    Assert.That(routedCpu.FileContent("O.TXT"), Is.EqualTo(directCpu.FileContent("O.TXT")));
  }

  [Test]
  public void Run_GivenSplitWrappedByteLoop_ThenBothBackendsWriteTheSameValues() {
    var machine = Select(_wrappedByteLoop, "main");
    MachineScheduler.Schedule(machine);
    var before = $"before allocation ({machine.StackSlots.Count} slots):\n" +
      string.Join(Environment.NewLine, machine.AllInstructions);
    var allocation = LinearScanAllocator.Allocate(machine);
    Assert.That(allocation, Is.Not.Null);
    var trace = before + $"\nafter allocation ({machine.StackSlots.Count} slots):\n" +
      string.Join(Environment.NewLine, machine.AllInstructions)
      + Environment.NewLine + string.Join(", ", allocation!.OrderBy(pair => pair.Key)
        .Select(pair => $"v{pair.Key}={pair.Value}"));
    var direct = new CodeGenerator(Bind(_wrappedByteLoop)) {
      Optimize = true,
      UseExperimentalBackend = false,
    };
    var routed = new CodeGenerator(Bind(_wrappedByteLoop)) {
      Optimize = true,
      UseExperimentalBackend = true,
    };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(routed.BackendRoutedNames, Does.Contain("main"));
    Assert.That(routedCpu.FileContent("O.TXT"), Is.EqualTo(directCpu.FileContent("O.TXT")), trace);
  }

  [Test]
  public void Run_GivenDescendingUnsignedLoop_ThenBothBackendsWriteTheSameValue() {
    var machine = Select(_descendingUnsignedLoop, "main");
    MachineScheduler.Schedule(machine);
    var allocation = LinearScanAllocator.Allocate(machine);
    Assert.That(allocation, Is.Not.Null);
    var trace = string.Join(Environment.NewLine,
        machine.AllInstructions.Select(instruction => $"{instruction} [{instruction.Condition}]"))
      + Environment.NewLine + string.Join(", ", allocation!.OrderBy(pair => pair.Key)
        .Select(pair => $"v{pair.Key}={pair.Value}"));
    var direct = new CodeGenerator(Bind(_descendingUnsignedLoop)) {
      Optimize = true,
      UseExperimentalBackend = false,
    };
    var routed = new CodeGenerator(Bind(_descendingUnsignedLoop)) {
      Optimize = true,
      UseExperimentalBackend = true,
    };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(routed.BackendRoutedNames, Does.Contain("main"));
    Assert.That(routedCpu.FileContent("O.TXT"), Is.EqualTo(directCpu.FileContent("O.TXT")), trace);
  }
}
