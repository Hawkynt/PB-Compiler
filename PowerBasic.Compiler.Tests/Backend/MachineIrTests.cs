using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The machine-IR data model (docs/X86-BACKEND.md) the x86-16 back end selects into: virtual
/// registers that allocation later binds to physical <see cref="Reg"/>s, instructions carrying a
/// def/use effect so one model drives liveness, allocation and scheduling.
/// </summary>
[TestFixture]
public sealed class MachineIrTests {

  [Test]
  public void VirtualRegister_IsVirtualUntilBoundToPhysical() {
    var v = MReg.Virtual(3);
    Assert.That(v.IsVirtual, Is.True);
    Assert.That(v.VirtualId, Is.EqualTo(3));
    Assert.That(v.Size, Is.EqualTo(MRegSize.Word));

    var ax = MReg.Physical_(Reg.AX);
    Assert.That(ax.IsVirtual, Is.False);
    Assert.That(ax.Physical, Is.EqualTo(Reg.AX));
  }

  [Test]
  public void MInstr_CarriesDefUseEffectByOperandIndex() {
    // ADD v0, v1  -> writes operand 0, reads operands 0 and 1, writes flags
    var add = new MInstr(
      MOpcode.Add,
      [new MOperand.Register(MReg.Virtual(0)), new MOperand.Register(MReg.Virtual(1))],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [0, 1], ReadsFlags: false, WritesFlags: true, ReadsMemory: false, WritesMemory: false));

    Assert.That(add.Effect.WrittenRegs, Is.EqualTo(new[] { 0 }));
    Assert.That(add.Effect.ReadRegs, Is.EqualTo(new[] { 0, 1 }));
    Assert.That(add.Effect.WritesFlags, Is.True);
    Assert.That(add.IsTerminator, Is.False);
  }

  [Test]
  public void MFunction_EnumeratesInstructionsAcrossBlocksInOrder() {
    var fn = new MFunction("main");
    var entry = new MBlock("entry");
    entry.Instructions.Add(new MInstr(MOpcode.Mov, [new MOperand.Register(MReg.Virtual(0)), new MOperand.Immediate(7)], MInstrEffect.None));
    var tail = new MBlock("tail");
    tail.Instructions.Add(new MInstr(MOpcode.Ret, [], MInstrEffect.None));
    entry.Successors.Add("tail");
    fn.Blocks.Add(entry);
    fn.Blocks.Add(tail);

    var ops = fn.AllInstructions.Select(i => i.Opcode).ToArray();
    Assert.That(ops, Is.EqualTo(new[] { MOpcode.Mov, MOpcode.Ret }));
    Assert.That(fn.Blocks[0].Successors, Is.EqualTo(new[] { "tail" }));
    Assert.That(fn.Blocks[^1].Instructions[^1].IsTerminator, Is.True);
  }
}
