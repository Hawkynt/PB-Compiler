using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>Integer switch selection for ON ... GOTO and the IR's GOSUB return dispatch.</summary>
[TestFixture]
public sealed class BackendSwitchTests {

  private const string _onGotoProgram = """
    SUB Dispatch(BYVAL selector%) NOINLINE
      ON selector% GOTO one, two, three
      PRINT "default"
      EXIT SUB
      one: PRINT "one" : EXIT SUB
      two: PRINT "two" : EXIT SUB
      three: PRINT "three" : EXIT SUB
    END SUB

    SUB DispatchLong(BYVAL selector&) NOINLINE
      ON selector& GOTO longOne, longTwo
      PRINT "longDefault"
      EXIT SUB
      longOne: PRINT "longOne" : EXIT SUB
      longTwo: PRINT "longTwo" : EXIT SUB
    END SUB

    Dispatch -1
    Dispatch 0
    Dispatch 1
    Dispatch 2
    Dispatch 3
    Dispatch 4
    DispatchLong -1
    DispatchLong 0
    DispatchLong 1
    DispatchLong 2
    DispatchLong 3
    DispatchLong 65537
    """;

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  [Test]
  public void Select_GivenWordSwitchWithPhiTargets_ThenEmitsEveryCaseAndItsEdgeCopies() {
    var selector = new IrArgument(IrType.I16, 0, "selector");
    var fn = new IrFunction("Dispatch", IrType.I16, [selector]);
    var entry = fn.CreateBlock("entry");
    var @default = fn.CreateBlock("default");
    var one = fn.CreateBlock("one");
    var two = fn.CreateBlock("two");
    var defaultValue = @default.AppendPhi(new IrPhi(IrType.I16));
    defaultValue.AddIncoming(new IrConstantInt(IrType.I16, 0), entry);
    new IrBuilder(@default).Ret(defaultValue);
    var oneValue = one.AppendPhi(new IrPhi(IrType.I16));
    oneValue.AddIncoming(new IrConstantInt(IrType.I16, 11), entry);
    new IrBuilder(one).Ret(oneValue);
    var twoValue = two.AppendPhi(new IrPhi(IrType.I16));
    twoValue.AddIncoming(new IrConstantInt(IrType.I16, 22), entry);
    new IrBuilder(two).Ret(twoValue);
    var sw = new IrSwitch(selector, @default);
    sw.AddCase(1, one);
    sw.AddCase(2, two);
    entry.Append(sw);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    var machine = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(machine, Is.Not.Null, $"switch declined: {reason}");
    MachineScheduler.Schedule(machine!);
    var allocation = LinearScanAllocator.Allocate(machine!, out var allocationReason);
    Assert.That(allocation, Is.Not.Null, $"allocation declined: {allocationReason}");
    var dispatch = machine!.Blocks.First(block => block.Label == "entry");
    var branchAt = dispatch.Instructions.FindIndex(instruction => instruction.Opcode == MOpcode.Jcc);
    Assert.That(dispatch.Instructions.Take(branchAt)
      .Count(instruction => instruction.Opcode == MOpcode.Mov), Is.EqualTo(3),
      "every successor phi value must be copied before the first branch");
    Assert.That(dispatch.Instructions.Skip(branchAt).Select(instruction => instruction.Opcode),
      Is.EqualTo(new[] { MOpcode.Jcc, MOpcode.Jmp }));
    Assert.That(machine.Blocks.SelectMany(block => block.Successors),
      Is.SupersetOf(new[] { "one", "two", "default" }));
    Assert.That(machine.Blocks.Where(block => block.Instructions.Any(i => i.Opcode == MOpcode.Cmp))
      .All(HasOnePinnedDecision), Is.True, "the scheduler requires one trailing branch decision per block");

    static bool HasOnePinnedDecision(MBlock block) => block.Instructions.Count(i => i.Opcode == MOpcode.Cmp) == 1
      && block.Instructions.TakeLast(2).Select(i => i.Opcode).SequenceEqual([MOpcode.Jcc, MOpcode.Jmp]);
  }

  [Test]
  public void Select_GivenDwordSwitch_ThenComparesHighGroupsAndLowCaseValues() {
    var selector = new IrArgument(IrType.I32, 0, "selector");
    var fn = new IrFunction("Dispatch32", IrType.Void, [selector]);
    var entry = fn.CreateBlock("entry");
    var @default = fn.CreateBlock("default");
    var minusOne = fn.CreateBlock("minusOne");
    var one = fn.CreateBlock("one");
    var highOne = fn.CreateBlock("highOne");
    new IrBuilder(@default).Ret();
    new IrBuilder(minusOne).Ret();
    new IrBuilder(one).Ret();
    new IrBuilder(highOne).Ret();
    var sw = new IrSwitch(selector, @default);
    sw.AddCase(-1, minusOne);
    sw.AddCase(1, one);
    sw.AddCase(0x0001_0002, highOne);
    entry.Append(sw);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    var machine = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(machine, Is.Not.Null, $"wide switch declined: {reason}");
    MachineScheduler.Schedule(machine!);
    var allocation = LinearScanAllocator.Allocate(machine!, out var allocationReason);
    Assert.That(allocation, Is.Not.Null, $"allocation declined: {allocationReason}");
    var decisions = machine!.Blocks.Where(block => block.Instructions.Any(i => i.Opcode == MOpcode.Cmp)).ToList();
    Assert.That(decisions.Sum(block => block.Instructions.Count(i => i.Opcode == MOpcode.Cmp)), Is.EqualTo(6),
      "three high-word groups and their three low-word cases each need one compare");
    Assert.That(decisions.All(block => block.Instructions.Count(i => i.Opcode == MOpcode.Cmp) == 1
      && block.Instructions.TakeLast(2).Select(i => i.Opcode).SequenceEqual([MOpcode.Jcc, MOpcode.Jmp])),
      Is.True, "every decision must end its own scheduler-safe machine block");
    Assert.That(machine.Blocks.SelectMany(block => block.Successors),
      Is.SupersetOf(new[] { "minusOne", "one", "highOne", "default" }));
  }

  [Test]
  public void Select_GivenUnsignedCaseSpelling_ThenMatchesTheSameSignedWordPattern() {
    var selector = new IrArgument(IrType.I16, 0, "selector");
    var fn = new IrFunction("DispatchPattern", IrType.Void, [selector]);
    var entry = fn.CreateBlock("entry");
    var @default = fn.CreateBlock("default");
    var matched = fn.CreateBlock("matched");
    new IrBuilder(@default).Ret();
    new IrBuilder(matched).Ret();
    var sw = new IrSwitch(selector, @default);
    sw.AddCase(ushort.MaxValue, matched);
    entry.Append(sw);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    var machine = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(machine, Is.Not.Null, $"bit-pattern switch declined: {reason}");
    MachineScheduler.Schedule(machine!);
    Assert.That(LinearScanAllocator.Allocate(machine!, out var allocationReason), Is.Not.Null,
      $"allocation declined: {allocationReason}");
    Assert.That(machine!.AllInstructions.SelectMany(instruction => instruction.Operands)
      .OfType<MOperand.Immediate>().Select(immediate => unchecked((ushort)immediate.Value)),
      Does.Contain(ushort.MaxValue));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenOnGotoBoundaries_ThenTheRoutedEmitterMatchesFallthroughAndEveryArm(bool optimize) {
    var direct = new CodeGenerator(Bind(_onGotoProgram)) {
      Optimize = optimize,
      UseExperimentalBackend = false,
    };
    var routed = new CodeGenerator(Bind(_onGotoProgram)) {
      Optimize = optimize,
      UseExperimentalBackend = true,
    };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("Dispatch"),
      "the ON GOTO procedure must not pass through the direct-emitter fallback");
    Assert.That(routed.BackendRoutedNames, Does.Contain("DispatchLong"),
      "the LONG-source ON GOTO procedure must not pass through the direct-emitter fallback");
    Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
    Assert.That(directCpu.Output.Trim().Replace("\r\n", "|"),
      Is.EqualTo("default|default|one|two|three|default|" +
        "longDefault|longDefault|longOne|longTwo|longDefault|longOne"));
  }
}
