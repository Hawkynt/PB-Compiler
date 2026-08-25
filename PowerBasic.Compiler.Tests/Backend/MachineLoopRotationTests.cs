using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>The SPEED-only machine rotation that keeps one entry guard and moves later tests to the latch.</summary>
[TestFixture]
public sealed class MachineLoopRotationTests {

  private static readonly MReg _counter = MReg.Virtual(0);

  private static MInstr Compare() => new(MOpcode.Cmp,
    [new MOperand.Register(_counter), new MOperand.Immediate(1000)],
    new MInstrEffect([], [0], false, true, false, false));

  private static MInstr JumpIf(string target) => new(MOpcode.Jcc, [new MOperand.LabelRef(target)],
    new MInstrEffect([], [], true, false, false, false), Condition.Less);

  private static MInstr Jump(string target) => new(MOpcode.Jmp, [new MOperand.LabelRef(target)], MInstrEffect.None);

  private static MFunction PreTestedLoop() {
    var function = new MFunction("main") { VirtualRegisterCount = 1 };
    var entry = new MBlock("entry");
    var header = new MBlock("header");
    var body = new MBlock("body");
    var exit = new MBlock("exit");
    entry.Instructions.Add(Jump("header"));
    entry.Successors.Add("header");
    header.Instructions.AddRange([Compare(), JumpIf("body"), Jump("exit")]);
    header.Successors.AddRange(["body", "exit"]);
    body.Instructions.Add(new MInstr(MOpcode.Add,
      [new MOperand.Register(_counter), new MOperand.Immediate(1)],
      new MInstrEffect([0], [0], false, true, false, false)));
    body.Instructions.Add(Jump("header"));
    body.Successors.Add("header");
    exit.Instructions.Add(new MInstr(MOpcode.Ret, [], MInstrEffect.None));
    function.Blocks.AddRange([entry, header, body, exit]);
    return function;
  }

  [Test]
  public void Run_GivenNullFunction_ThenThrows()
    => Assert.Throws<ArgumentNullException>(() => MachineLoopRotation.Run(null!));

  [Test]
  public void Run_GivenCanonicalPreTestedLoop_ThenLatchRepeatsTheGuardAndBranchesToTheBody() {
    var function = PreTestedLoop();

    Assert.That(MachineLoopRotation.Run(function), Is.EqualTo(1));

    var body = function.Blocks.Single(block => block.Label == "body");
    Assert.Multiple(() => {
      Assert.That(body.Instructions.Select(instruction => instruction.Opcode).TakeLast(3),
        Is.EqualTo(new[] { MOpcode.Cmp, MOpcode.Jcc, MOpcode.Jmp }));
      Assert.That(body.Successors, Is.EqualTo(new[] { "body", "exit" }));
      Assert.That(((MOperand.LabelRef)body.Instructions[^2].Operands[0]).Name, Is.EqualTo("body"));
      Assert.That(((MOperand.LabelRef)body.Instructions[^1].Operands[0]).Name, Is.EqualTo("exit"));
    });
  }

  [Test]
  public void Run_GivenAHeaderThatComputesItsCondition_ThenItStaysUnrotated() {
    var function = PreTestedLoop();
    var header = function.Blocks.Single(block => block.Label == "header");
    header.Instructions.Insert(0, new MInstr(MOpcode.Mov,
      [new MOperand.Register(MReg.Virtual(1)), new MOperand.Register(_counter)],
      new MInstrEffect([0], [1], false, false, false, false)));

    Assert.That(MachineLoopRotation.Run(function), Is.Zero);
    Assert.That(function.Blocks.Single(block => block.Label == "body").Successors, Is.EqualTo(new[] { "header" }));
  }

  [Test]
  public void Run_GivenOpaqueInlineAssembly_ThenItStaysUnrotated() {
    var function = PreTestedLoop();
    var body = function.Blocks.Single(block => block.Label == "body");
    body.Instructions.Insert(0, new MInstr(MOpcode.InlineAsm, [], MInstrEffect.None));

    Assert.That(MachineLoopRotation.Run(function), Is.Zero);
    Assert.That(body.Successors, Is.EqualTo(new[] { "header" }));
  }

  [Test]
  public void Run_GivenTwoBackEdges_ThenItStaysUnrotated() {
    var function = PreTestedLoop();
    var secondLatch = new MBlock("second.latch");
    secondLatch.Instructions.Add(Jump("header"));
    secondLatch.Successors.Add("header");
    function.Blocks.Add(secondLatch);

    Assert.That(MachineLoopRotation.Run(function), Is.Zero);
  }
}
