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
/// Register residency in the routed path (docs/X86-BACKEND.md, docs/PB36.md O5): a loop's counter and
/// its accumulator stay in a register for the whole loop under <c>$OPTIMIZE SPEED</c>, in <c>SI</c>
/// and <c>DI</c>, and the out-of-SSA copies around them are gone.
///
/// Three separate claims, each of which was false in its own way before:
///
/// <list type="number">
/// <item>the value has a register AT ALL - it used to be spilled whenever a block laid out between the
///   loop head and its latch contained a call, which for a FOR loop is the block holding the
///   <c>PRINT</c> that follows it;</item>
/// <item>the register is <c>SI</c> or <c>DI</c>, the two no fixed-register sequence claims;</item>
/// <item>the loop body does not copy it in and out on every iteration - <c>ADD SI, 1</c>, not
///   <c>MOV t, SI / ADD t, 1 / MOV SI, t</c>.</item>
/// </list>
///
/// The fourth claim is the one the RULES of this back end care about most and it is measured over the
/// whole corpus rather than asserted: a preference must never cost a function its allocation.
/// </summary>
[TestFixture]
public sealed class BackendResidencyTests {

  private static readonly string _repoRoot =
    Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  private static readonly SelectionTarget _speed = new(Optimize: true, OptimizeSpeed: true);
  private static readonly SelectionTarget _speed386 = new(CpuLevel: 386, Optimize: true, OptimizeSpeed: true);

  /// <summary>The module body of <paramref name="source"/>, selected and scheduled but not yet allocated.</summary>
  private static MFunction Select(string source, SelectionTarget target) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    foreach (var f in module!.Functions)
      if (!f.IsDeclaration)
        IntegerRecovery.Run(f);
    IrPassManager.Standard(target.OptimizeSpeed).RunOnModule(module);
    foreach (var f in module.Functions)
      if (!f.IsDeclaration)
        IntegerRecovery.Run(f);
    IrPassManager.Standard(target.OptimizeSpeed).RunOnModule(module);
    var main = module.FindFunction("main");
    Assert.That(main, Is.Not.Null);
    var machine = InstructionSelector.TrySelect(main!, out var reason, target);
    Assert.That(machine, Is.Not.Null, $"selection declined: {reason}");
    MachineScheduler.Schedule(machine!);
    return machine!;
  }

  // an accumulator and a counter, both live all the way round, and no call inside the loop
  private const string _ACCUMULATING_LOOP =
    "$OPTIMIZE SPEED\ns% = 0\ni% = 1\nDO\n  s% = s% + i%\n  i% = i% + 1\nLOOP UNTIL i% > 10\nPRINT s%\nEND";

  private const string _LONG_ACCUMULATING_LOOP = """
    $CPU 80386
    $OPTIMIZE SPEED
    s& = 0
    i& = 1
    DO
      s& = s& + i&
      i& = i& + 1
    LOOP UNTIL i& > 10
    PRINT s&
    END
    """;

  [Test]
  public void Allocate_GivenLoopCarriedValues_WhenSpeed_ThenTheyLiveInSiAndDi() {
    var machine = Select(_ACCUMULATING_LOOP, _speed);
    var carried = LivenessAnalysis.LoopCarried(machine);
    Assert.That(carried, Is.Not.Empty, "the accumulator and the counter both span the back edge");

    var allocation = LinearScanAllocator.Allocate(machine, _speed, out var reason);

    Assert.That(allocation, Is.Not.Null, $"allocation declined: {reason}");
    // LoopCarried is re-measured after coalescing merged the phi copies away, so ask the final form
    var resident = LivenessAnalysis.LoopCarried(machine).Select(v => allocation![v]).ToList();
    Assert.That(resident, Is.SubsetOf(new[] { Reg.SI, Reg.DI }),
      "every value live round the loop takes one of the two registers nothing is pinned to");
  }

  [Test]
  public void Allocate_GivenLoopCarriedValues_WhenNotSpeed_ThenTheOrdinaryPoolOrderStands() {
    var machine = Select(_ACCUMULATING_LOOP, SelectionTarget.Baseline);

    var allocation = LinearScanAllocator.Allocate(machine, SelectionTarget.Baseline, out var reason);

    Assert.That(allocation, Is.Not.Null, $"allocation declined: {reason}");
    Assert.That(allocation!.Values, Has.No.Member(Reg.SI).And.No.Member(Reg.DI),
      "without the speed objective the scarce addressing registers stay free");
  }

  [Test]
  public void Allocate_GivenLongLoopCarriedValues_When386Speed_ThenTheyLiveInEsiAndEdi() {
    var machine = Select(_LONG_ACCUMULATING_LOOP, _speed386);

    var allocation = LinearScanAllocator.Allocate(machine, _speed386, out var reason);

    Assert.That(allocation, Is.Not.Null, $"allocation declined: {reason}");
    var carried = LivenessAnalysis.LoopCarried(machine);
    var dwordResidents = machine.AllInstructions
      .SelectMany(instruction => instruction.Operands)
      .OfType<MOperand.Register>()
      .Select(operand => operand.Reg)
      .Where(register => register.IsVirtual && register.Size == MRegSize.Dword
        && carried.Contains(register.VirtualId))
      .Select(register => allocation![register.VirtualId])
      .Distinct()
      .ToList();
    Assert.That(dwordResidents, Is.EquivalentTo(new[] { Reg.ESI, Reg.EDI }),
      string.Join("\n", machine.AllInstructions));
  }

  [Test]
  public void Execute_GivenRotatedPreTestedLoop_WhenRouted_ThenZeroAndPositiveTripsMatchDirect() {
    const string source = """
      $OPTIMIZE SPEED
      DECLARE SUB Walk(BYVAL i%)
      Walk 4
      Walk 1
      END
      SUB Walk(BYVAL i%) NOINLINE
        DO WHILE i% < 4
          PRINT i%
          i% = i% + 1
        LOOP
      END SUB
      """;

    static (byte[] Image, IReadOnlyList<string> Routes) Compile(string source, bool routed) {
      var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36),
        Dialect.Pb36);
      Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
      var generator = new CodeGenerator(model) { UseExperimentalBackend = routed };
      var image = generator.EmitExecutable();
      Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
      return (image, generator.BackendRoutedNames.ToList());
    }

    var direct = Compile(source, routed: false);
    var routed = Compile(source, routed: true);
    var directOutput = Cpu8086.Run(direct.Image).Output;
    var routedOutput = Cpu8086.Run(routed.Image).Output;
    Assert.Multiple(() => {
      Assert.That(routed.Routes, Does.Contain("Walk"), "the rotated procedure must not pass through fallback");
      Assert.That(routedOutput, Is.EqualTo(directOutput));
      Assert.That(routedOutput.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim()), Is.EqualTo(new[] { "1", "2", "3" }));
    });
  }

  [TestCase(false, true, true, TestName = "Select_Given8086Speed_ThenLongsRemainWordPairs")]
  [TestCase(true, false, true, TestName = "Select_Given386OptimizeOff_ThenLongsRemainWordPairs")]
  [TestCase(true, true, false, TestName = "Select_Given386WithoutSpeed_ThenLongsRemainWordPairs")]
  public void Select_GivenTargetOutside386Speed_ThenNoNativeDwordRegisters(
      bool target386, bool optimize, bool optimizeSpeed) {
    var target = new SelectionTarget(CpuLevel: target386 ? 386 : 86, Optimize: optimize, OptimizeSpeed: optimizeSpeed);

    var machine = Select(_LONG_ACCUMULATING_LOOP, target);

    Assert.That(machine.AllInstructions
      .SelectMany(instruction => instruction.Operands)
      .OfType<MOperand.Register>()
      .Any(operand => operand.Reg.IsVirtual && operand.Reg.Size == MRegSize.Dword), Is.False);
  }

  [Test]
  public void Select_GivenLongLoopStartingFromRuntimePair_When386Speed_ThenWordPairFallbackStillSelects() {
    const string source = """
      $CPU 80386
      $OPTIMIZE SPEED
      INPUT s&
      i% = 0
      DO
        s& = s& + 1
        i% = i% + 1
      LOOP UNTIL i% > 3
      PRINT s&
      END
      """;

    var machine = Select(source, _speed386);

    Assert.That(machine.AllInstructions
      .SelectMany(instruction => instruction.Operands)
      .OfType<MOperand.Register>()
      .Any(operand => operand.Reg.IsVirtual && operand.Reg.Size == MRegSize.Dword), Is.False,
      "a pair-valued runtime result must keep the loop on the baseline representation");
  }

  [Test]
  public void Allocate_GivenLongForWithNumericPrint_When386Speed_ThenDwordResidencySurvivesCalls() {
    const string source = """
      $CPU 80386
      $OPTIMIZE SPEED
      s& = 0
      FOR i& = 1 TO 100
        s& = s& + i&
        PRINT s&
      NEXT i&
      PRINT s&
      END
      """;
    var machine = Select(source, _speed386);

    var allocation = LinearScanAllocator.Allocate(machine, _speed386, out var reason);

    Assert.That(allocation, Is.Not.Null, $"allocation declined: {reason}");
    var native = machine.AllInstructions
      .SelectMany(instruction => instruction.Operands)
      .OfType<MOperand.Register>()
      .Select(operand => operand.Reg)
      .Where(register => register.IsVirtual && register.Size == MRegSize.Dword)
      .Select(register => allocation![register.VirtualId])
      .Distinct()
      .ToList();
    Assert.That(native, Is.SupersetOf(new[] { Reg.ESI, Reg.EDI }),
      string.Join("\n", machine.AllInstructions));
  }

  [Test]
  public void Allocate_GivenAnIncrementRoundALoop_WhenSpeed_ThenItIsTwoAddress() {
    var machine = Select(_ACCUMULATING_LOOP, _speed);

    var allocation = LinearScanAllocator.Allocate(machine, _speed, out var reason);

    Assert.That(allocation, Is.Not.Null, $"allocation declined: {reason}");
    var loopBody = machine.Blocks.Single(b => b.Instructions.Any(i => i.Opcode == MOpcode.Add));
    Assert.That(loopBody.Instructions.Where(i => i.Opcode == MOpcode.Mov), Is.Empty,
      "coalescing leaves the loop body arithmetic only - no copy in and out of the resident register");
  }

  /// <summary>
  /// The shape that made the first coalescer miscompile: <c>a</c> and <c>b</c> are provably EQUAL at
  /// the second instruction, and merging them would still destroy the value the third one reads. A
  /// copy's two registers may only be merged when no definition of either lands where the other is
  /// still live.
  /// </summary>
  [Test]
  public void Allocate_GivenACopyFollowedByARedefinitionOfItsSource_ThenTheCopyIsNotCoalesced() {
    var a = MReg.Virtual(0);
    var b = MReg.Virtual(1);
    var c = MReg.Virtual(2);
    var function = new MFunction("F") { VirtualRegisterCount = 3 };
    function.StackSlots.Add(2);
    var block = new MBlock("entry");
    MInstr Move(MOperand destination, MOperand source)
      => new(MOpcode.Mov, [destination, source], new MInstrEffect([0], [1], false, false, false, false));
    block.Instructions.Add(new MInstr(MOpcode.Mov, [new MOperand.Register(b), new MOperand.Immediate(1)],
      new MInstrEffect([0], [], false, false, false, false)));
    block.Instructions.Add(new MInstr(MOpcode.Mov, [new MOperand.Register(c), new MOperand.Immediate(2)],
      new MInstrEffect([0], [], false, false, false, false)));
    block.Instructions.Add(Move(new MOperand.Register(a), new MOperand.Register(b)));   // a := b
    block.Instructions.Add(Move(new MOperand.Register(b), new MOperand.Register(c)));   // b := c, a still wanted
    block.Instructions.Add(new MInstr(MOpcode.Mov,
      [new MOperand.StackSlot(0, MRegSize.Word), new MOperand.Register(a)],
      new MInstrEffect([], [1], false, false, false, true)));
    block.Instructions.Add(new MInstr(MOpcode.Mov,
      [new MOperand.StackSlot(0, MRegSize.Word), new MOperand.Register(b)],
      new MInstrEffect([], [1], false, false, false, true)));
    block.Instructions.Add(new MInstr(MOpcode.Ret, [], MInstrEffect.None));
    function.Blocks.Add(block);

    var allocation = LinearScanAllocator.Allocate(function, _speed, out var reason);

    Assert.That(allocation, Is.Not.Null, $"allocation declined: {reason}");
    Assert.That(allocation![a.VirtualId], Is.Not.EqualTo(allocation[b.VirtualId]),
      "a and b hold different values while both are live, so they may not share a register");
  }

  /// <summary>
  /// The rule the speed policy is held to: it may change WHICH register a value gets and never WHETHER
  /// it gets one. Coalescing unions two live ranges (so the merged value must dodge the clobbers of
  /// both) and the preference asks for two of the three registers that can address memory - either
  /// could cost an allocation, which is why the allocator keeps the plain policy as a fallback. This
  /// measures that over every function of the corpus rather than trusting the argument.
  /// </summary>
  [Test]
  public void Allocate_GivenTheWholeCorpus_WhenSpeed_ThenNothingThatAllocatedStopsAllocating() {
    var directory = Path.Combine(_repoRoot, "tests");
    if (!Directory.Exists(directory))
      Assert.Ignore("corpus not present");

    var regressions = new List<string>();
    var measured = 0;
    foreach (var file in Directory.EnumerateFiles(directory, "*.BAS", SearchOption.AllDirectories)
               .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
      var name = Path.GetFileName(file);
      IrModule? module;
      try {
        // the preprocessor, not the lexer - see BackendCoverageTests for what tokenizing directly costs
        var model = Binder.Bind(Parser.Parse(Preprocessor.Expand(file, new FileSourceProvider(), Dialect.Pb36), name, Dialect.Pb36), Dialect.Pb36);
        if (model.Errors.Count > 0)
          continue;
        module = IrLowering.TryLowerModule(model);
        if (module is null)
          continue;
        IrPassManager.Standard().RunOnModule(module);
        foreach (var f in module.Functions)
          if (!f.IsDeclaration)
            IntegerRecovery.Run(f);
        IrPassManager.Standard().RunOnModule(module);
      } catch (Exception) {
        continue;                                  // the census owns the decline histogram; this owns allocation
      }

      foreach (var fn in module.Functions) {
        if (fn.IsDeclaration || InstructionSelector.TrySelect(fn, _speed) is not { } machine)
          continue;
        MachineScheduler.Schedule(machine);
        var plainForm = machine.Clone();
        var speedForm = machine.Clone();
        ++measured;
        var plain = LinearScanAllocator.Allocate(plainForm, SelectionTarget.Baseline);
        var speed = LinearScanAllocator.Allocate(speedForm, _speed);
        if (plain is not null && speed is null)
          regressions.Add($"{name}::{fn.Name}");
      }
    }

    Assert.That(measured, Is.GreaterThan(100), "the corpus should have produced a real sample");
    Assert.That(regressions, Is.Empty, "the speed policy must never turn an allocatable function into a declining one");
  }
}
