using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>O0348/O0349 — conservative x87 stack scheduling/value retention after selection.</summary>
[TestFixture]
public sealed class X87StackOptimizerTests {

  private static MInstr Load(MOperand operand) => new(MOpcode.Fld, [operand],
    new MInstrEffect([], [], false, false, ReadsMemory: true, WritesMemory: false));

  private static MInstr Store(MOperand operand) => new(MOpcode.Fstp, [operand],
    new MInstrEffect([], [], false, false, ReadsMemory: false, WritesMemory: true));

  private static MInstr Op(MOpcode opcode) => new(opcode, [], MInstrEffect.None);

  private static MInstr Jump(string target) => new(MOpcode.Jmp, [new MOperand.LabelRef(target)], MInstrEffect.None);

  private static MInstr ConditionalJump(string target) => new(MOpcode.Jcc, [new MOperand.LabelRef(target)],
    MInstrEffect.None, Condition.NotZero);

  private static MOperand.DataCell Data(string name) => new(name, 0, MRegSize.Qword);
  private static MOperand.StackSlot Temp(int index) => new(index, MRegSize.Tbyte);

  private static List<MInstr> DeepSum(string prefix, int depth) {
    var instructions = new List<MInstr>();
    for (var i = 0; i < depth; ++i)
      instructions.Add(Load(Data(prefix + i)));
    for (var i = 1; i < depth; ++i)
      instructions.Add(Op(MOpcode.Faddp));
    return instructions;
  }

  private static (MFunction Function, MBlock Block) OneBlock(params MInstr[] instructions) {
    var function = new MFunction("f");
    var block = new MBlock("entry");
    block.Instructions.AddRange(instructions);
    function.Blocks.Add(block);
    return (function, block);
  }

  [Test]
  public void Retention_GivenPrivateTbyteStoreImmediatelyReloaded_ThenRoundTripDisappears() {
    var temporary = Temp(0);
    var result = Temp(1);
    var (function, block) = OneBlock(
      Load(Data("a")), Store(temporary), Load(temporary), Store(result));

    Assert.That(X87StackOptimizer.Run(function), Is.EqualTo(1));

    Assert.Multiple(() => {
      Assert.That(block.Instructions.Select(instruction => instruction.Opcode),
        Is.EqualTo(new[] { MOpcode.Fld, MOpcode.Fstp }));
      Assert.That(block.Instructions[^1].Operands[0], Is.EqualTo(result));
    });
  }

  [Test]
  public void Retention_GivenTemporaryHasSeveralReaders_ThenValueStaysResidentAndReadersDuplicateIt() {
    var temporary = Temp(0);
    var first = Temp(1);
    var second = Temp(2);
    var (function, block) = OneBlock(
      Load(Data("a")), Store(temporary),
      Load(temporary), Store(first),
      Load(temporary), Store(second));

    Assert.That(X87StackOptimizer.Run(function), Is.EqualTo(1));

    var duplicates = block.Instructions
      .Where(instruction => instruction.Opcode == MOpcode.InlineAsm)
      .Select(instruction => ((MOperand.InlineAsmText)instruction.Operands[0]).Text)
      .ToList();
    Assert.Multiple(() => {
      Assert.That(duplicates, Is.EqualTo(new[] { "FLD ST(0)", "FLD ST(0)" }));
      Assert.That(block.Instructions.Any(instruction => IsStoreOf(instruction, temporary)), Is.False);
      Assert.That(block.Instructions.Any(instruction => IsLoadOf(instruction, temporary)), Is.False);
      Assert.That(block.Instructions[^1].Opcode, Is.EqualTo(MOpcode.FstpSt0),
        "the retained source is discarded when the region ends");
    });
  }

  [Test]
  public void Retention_GivenResidentBelowTransientValue_ThenReloadDuplicatesTheCorrectStackDepth() {
    var temporary = Temp(0);
    var result = Temp(1);
    var again = Temp(2);
    var (function, block) = OneBlock(
      Load(Data("a")), Store(temporary),
      Load(Data("b")), Load(temporary), Op(MOpcode.Faddp), Store(result),
      Load(temporary), Store(again));

    Assert.That(X87StackOptimizer.Run(function), Is.EqualTo(1));

    var duplicates = block.Instructions
      .Where(instruction => instruction.Opcode == MOpcode.InlineAsm)
      .Select(instruction => ((MOperand.InlineAsmText)instruction.Operands[0]).Text)
      .ToList();
    Assert.That(duplicates, Is.EqualTo(new[] { "FLD ST(1)", "FLD ST(0)" }));
  }

  [Test]
  public void Retention_GivenLoopInvariantTemporary_ThenResidencyCrossesBackEdgeAndFlushesOnExit() {
    var value = Temp(0);
    var observed = Temp(1);
    var function = new MFunction("f");
    var preheader = new MBlock("preheader");
    var loop = new MBlock("loop");
    var exit = new MBlock("exit");
    preheader.Instructions.AddRange([Load(Data("a")), Store(value), Jump("loop")]);
    preheader.Successors.Add("loop");
    loop.Instructions.AddRange([Load(value), Store(observed), ConditionalJump("exit")]);
    loop.Successors.AddRange(["loop", "exit"]);
    exit.Instructions.Add(Op(MOpcode.Ret));
    function.Blocks.AddRange([preheader, loop, exit]);

    Assert.That(X87StackOptimizer.Run(function), Is.EqualTo(1));

    Assert.Multiple(() => {
      Assert.That(preheader.Instructions.Any(instruction => IsStoreOf(instruction, value)), Is.False);
      Assert.That(loop.Instructions.Any(instruction => IsLoadOf(instruction, value)), Is.False);
      Assert.That(loop.Instructions.Any(instruction => instruction is { Opcode: MOpcode.InlineAsm,
        Operands: [MOperand.InlineAsmText { Text: "FLD ST(0)" }] }), Is.True);
      Assert.That(exit.Instructions.Select(instruction => instruction.Opcode),
        Is.EqualTo(new[] { MOpcode.FstpSt0, MOpcode.Ret }));
    });
  }

  [Test]
  public void Retention_GivenCallInsideRequiredRegion_ThenTemporaryRemainsMaterialized() {
    var temporary = Temp(0);
    var (function, block) = OneBlock(
      Load(Data("a")), Store(temporary),
      new MInstr(MOpcode.Call, [new MOperand.LabelRef("rt")], MInstrEffect.None),
      Load(temporary), Store(Temp(1)),
      Load(temporary), Store(Temp(2)));

    Assert.That(X87StackOptimizer.Run(function), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(block.Instructions.Any(instruction => IsStoreOf(instruction, temporary)), Is.True);
      Assert.That(block.Instructions.Count(instruction => IsLoadOf(instruction, temporary)), Is.EqualTo(2));
    });
  }

  [Test]
  public void Retention_GivenNarrowRoundingCell_ThenStoreLoadPairIsPreserved() {
    var narrow = new MOperand.StackSlot(0, MRegSize.Dword);
    var (function, block) = OneBlock(
      Load(Data("a")), Store(narrow), Load(narrow), Store(Temp(1)));

    Assert.That(X87StackOptimizer.Run(function), Is.Zero);
    Assert.That(block.Instructions, Has.Count.EqualTo(4));
  }

  [Test]
  public void Scheduling_GivenTwoPrivateArithmeticSubtrees_ThenBothResultsStayOnX87Stack() {
    var left = Temp(0);
    var right = Temp(1);
    var result = Temp(2);
    var (function, block) = OneBlock(
      Load(Data("a")), Load(Data("b")), Op(MOpcode.Faddp), Store(left),
      Load(Data("c")), Load(Data("d")), Op(MOpcode.Faddp), Store(right),
      Load(left), Load(right), Op(MOpcode.Fmulp), Store(result));

    Assert.That(X87StackOptimizer.Run(function), Is.GreaterThanOrEqualTo(1));

    Assert.Multiple(() => {
      Assert.That(block.Instructions.Select(instruction => instruction.Opcode), Is.EqualTo(new[] {
        MOpcode.Fld, MOpcode.Fld, MOpcode.Faddp,
        MOpcode.Fld, MOpcode.Fld, MOpcode.Faddp,
        MOpcode.Fmulp, MOpcode.Fstp,
      }));
      Assert.That(block.Instructions.Count(instruction => instruction.Opcode == MOpcode.Fstp), Is.EqualTo(1),
        "only the externally visible tree result is materialized");
    });
  }

  [TestCase(MOpcode.Faddp)]
  [TestCase(MOpcode.Fmulp)]
  public void Scheduling_GivenRightSubtreeWouldOverflowWithResidentLeftAndRootIsCommutative_ThenRightRunsFirst(
      MOpcode rootOpcode) {
    var left = Temp(0);
    var right = Temp(1);
    var result = Temp(2);
    var instructions = new List<MInstr> { Load(Data("a")), Store(left) };
    instructions.AddRange(DeepSum("r", 8));
    instructions.Add(Store(right));
    instructions.Add(Load(left));
    instructions.Add(Load(right));
    instructions.Add(Op(rootOpcode));
    instructions.Add(Store(result));
    var (function, block) = OneBlock([.. instructions]);

    Assert.That(X87StackOptimizer.Run(function), Is.GreaterThanOrEqualTo(1));

    Assert.Multiple(() => {
      Assert.That(block.Instructions[0].Operands[0], Is.EqualTo(Data("r0")),
        "the higher-pressure right subtree must execute first");
      Assert.That(block.Instructions.FindIndex(instruction => instruction.Operands.Contains(Data("a"))),
        Is.GreaterThan(0));
      Assert.That(block.Instructions.Any(instruction => instruction.Opcode == MOpcode.Fstp
        && instruction.Operands[0].Equals(left)), Is.False);
      Assert.That(block.Instructions.Any(instruction => instruction.Opcode == MOpcode.Fld
        && instruction.Operands[0].Equals(left)), Is.False);
      Assert.That(block.Instructions.Any(instruction => instruction.Opcode == MOpcode.Fstp
        && instruction.Operands[0].Equals(right)), Is.False);
      Assert.That(block.Instructions.Any(instruction => instruction.Opcode == MOpcode.Fld
        && instruction.Operands[0].Equals(right)), Is.False);
      Assert.That(block.Instructions.Any(instruction => instruction.Opcode == MOpcode.Fxch), Is.False);
    });
  }

  [TestCase(MOpcode.Fsubp)]
  [TestCase(MOpcode.Fdivp)]
  public void Scheduling_GivenRightSubtreeRunsFirstAndRootIsNonCommutative_ThenFxchRestoresOperandOrder(
      MOpcode rootOpcode) {
    var left = Temp(0);
    var right = Temp(1);
    var result = Temp(2);
    var instructions = new List<MInstr> { Load(Data("a")), Store(left) };
    instructions.AddRange(DeepSum("r", 8));
    instructions.Add(Store(right));
    instructions.Add(Load(left));
    instructions.Add(Load(right));
    instructions.Add(Op(rootOpcode));
    instructions.Add(Store(result));
    var (function, block) = OneBlock([.. instructions]);

    Assert.That(X87StackOptimizer.Run(function), Is.GreaterThanOrEqualTo(1));

    var root = block.Instructions.FindIndex(instruction => instruction.Opcode == rootOpcode);
    Assert.Multiple(() => {
      Assert.That(block.Instructions[0].Operands[0], Is.EqualTo(Data("r0")));
      Assert.That(root, Is.GreaterThan(0));
      Assert.That(block.Instructions[root - 1].Opcode, Is.EqualTo(MOpcode.Fxch),
        "right-first FSUBP/FDIVP require ST(1)=left and ST(0)=right");
      Assert.That(block.Instructions.Count(instruction => instruction.Opcode == MOpcode.Fxch), Is.EqualTo(1));
      Assert.That(block.Instructions.Any(instruction => instruction.Opcode == MOpcode.Fstp
        && (instruction.Operands[0].Equals(left) || instruction.Operands[0].Equals(right))), Is.False);
    });
  }

  [Test]
  public void Scheduling_GivenBothSubtreeOrdersWouldOverflow_ThenParentSpillsArePreserved() {
    var left = Temp(0);
    var right = Temp(1);
    var result = Temp(2);
    var instructions = DeepSum("l", 8);
    instructions.Add(Store(left));
    instructions.AddRange(DeepSum("r", 8));
    instructions.Add(Store(right));
    instructions.Add(Load(left));
    instructions.Add(Load(right));
    instructions.Add(Op(MOpcode.Faddp));
    instructions.Add(Store(result));
    var (function, block) = OneBlock([.. instructions]);

    X87StackOptimizer.Run(function);

    Assert.Multiple(() => {
      Assert.That(block.Instructions.Any(instruction => instruction.Opcode == MOpcode.Fstp
        && instruction.Operands[0].Equals(left)), Is.True);
      Assert.That(block.Instructions.Any(instruction => instruction.Opcode == MOpcode.Fld
        && instruction.Operands[0].Equals(left)), Is.True);
      Assert.That(block.Instructions.Any(instruction => instruction.Opcode == MOpcode.Fstp
        && instruction.Operands[0].Equals(right)), Is.True);
      Assert.That(block.Instructions.Any(instruction => instruction.Opcode == MOpcode.Fld
        && instruction.Operands[0].Equals(right)), Is.True);
    });
  }

  [Test]
  public void Scheduling_GivenRequiredNarrowingInsideRightSubtree_ThenRetentionDoesNotCrossIt() {
    var left = Temp(0);
    var right = Temp(1);
    var rounded = new MOperand.StackSlot(3, MRegSize.Dword);
    var (function, block) = OneBlock(
      Load(Data("a")), Store(left),
      Load(Data("b")), Store(rounded), Load(rounded), Store(right),
      Load(left), Load(right), Op(MOpcode.Faddp), Store(Temp(2)));

    X87StackOptimizer.Run(function);

    Assert.That(block.Instructions.Any(instruction => instruction.Opcode == MOpcode.Fstp
      && instruction.Operands[0].Equals(left)), Is.True,
      "a SINGLE/DOUBLE rounding boundary is not crossed by retention");
  }

  [Test]
  public void Scheduler_GivenGeneratedRegisterDuplicate_ThenItStaysOrderedWithOtherX87Instructions() {
    var temporary = Temp(0);
    var (function, block) = OneBlock(
      Load(Data("a")), Store(temporary),
      Load(temporary), Store(Temp(1)),
      Load(temporary), Store(Temp(2)));
    Assert.That(X87StackOptimizer.Run(function), Is.EqualTo(1));

    MachineScheduler.Schedule(function);

    var x87 = block.Instructions
      .Where(instruction => instruction.Opcode is MOpcode.Fld or MOpcode.Fstp or MOpcode.FstpSt0
        or MOpcode.InlineAsm)
      .Select(instruction => instruction.Opcode == MOpcode.InlineAsm
        ? ((MOperand.InlineAsmText)instruction.Operands[0]).Text
        : instruction.Opcode.ToString())
      .ToList();
    Assert.That(x87, Is.EqualTo(new[] { "Fld", "FLD ST(0)", "Fstp", "FLD ST(0)", "Fstp", "FstpSt0" }));
  }

  [Test]
  public void Scheduler_GivenUnmarkedFunction_ThenX87StackificationDoesNotRun() {
    var temporary = Temp(0);
    var (function, block) = OneBlock(
      Load(Data("a")), Store(temporary), Load(temporary), Store(Temp(1)));

    MachineScheduler.Schedule(function);

    Assert.That(block.Instructions, Has.Count.EqualTo(4),
      "an unoptimized machine function is not marked by the production peephole entry");
  }

  [Test]
  public void Scheduler_GivenOptimizerMarkedFunction_ThenX87StackificationRunsBeforeScheduling() {
    var temporary = Temp(0);
    var (function, block) = OneBlock(
      Load(Data("a")), Store(temporary), Load(temporary), Store(Temp(1)));
    Peephole.Run(function); // public production entry marks the function as optimizer-owned

    MachineScheduler.Schedule(function);

    Assert.That(block.Instructions, Has.Count.EqualTo(2));
  }

  private static bool IsStoreOf(MInstr instruction, MOperand.StackSlot slot)
    => instruction is { Opcode: MOpcode.Fstp, Operands: [MOperand.StackSlot candidate] }
      && candidate.Equals(slot);

  private static bool IsLoadOf(MInstr instruction, MOperand.StackSlot slot)
    => instruction is { Opcode: MOpcode.Fld, Operands: [MOperand.StackSlot candidate] }
      && candidate.Equals(slot);
}
