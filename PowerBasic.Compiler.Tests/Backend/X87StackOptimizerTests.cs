using PowerBasic.Compiler.Backend;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>O0348/O0349 — conservative x87 stackification/value retention after selection.</summary>
[TestFixture]
public sealed class X87StackOptimizerTests {

  private static MInstr Load(MOperand operand) => new(MOpcode.Fld, [operand],
    new MInstrEffect([], [], false, false, ReadsMemory: true, WritesMemory: false));

  private static MInstr Store(MOperand operand) => new(MOpcode.Fstp, [operand],
    new MInstrEffect([], [], false, false, ReadsMemory: false, WritesMemory: true));

  private static MInstr Op(MOpcode opcode) => new(opcode, [], MInstrEffect.None);

  private static MOperand.DataCell Data(string name) => new(name, 0, MRegSize.Qword);
  private static MOperand.StackSlot Temp(int index) => new(index, MRegSize.Tbyte);

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
  public void Retention_GivenTemporaryHasAnotherReader_ThenItMustStillBeMaterialized() {
    var temporary = Temp(0);
    var (function, block) = OneBlock(
      Load(Data("a")), Store(temporary),
      Load(temporary), Store(Temp(1)),
      Load(temporary), Store(Temp(2)));

    Assert.That(X87StackOptimizer.Run(function), Is.Zero);
    Assert.That(block.Instructions, Has.Count.EqualTo(6));
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

  [Test]
  public void Scheduling_GivenRightSubtreeWouldOverflowWithResidentLeft_ThenTreeIsNotStackified() {
    var left = Temp(0);
    var right = Temp(1);
    var result = Temp(2);
    var instructions = new List<MInstr> { Load(Data("a")), Store(left) };
    for (var i = 0; i < 8; ++i)
      instructions.Add(Load(Data("r" + i)));
    for (var i = 0; i < 7; ++i)
      instructions.Add(Op(MOpcode.Faddp));
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
}
