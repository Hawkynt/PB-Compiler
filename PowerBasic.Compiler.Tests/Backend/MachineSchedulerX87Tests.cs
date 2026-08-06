using PowerBasic.Compiler.Backend;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The x87 stack is a resource no <see cref="MInstrEffect"/> can name, so the scheduler orders x87
/// instructions against each other by OPCODE instead.
///
/// This is a regression fixture for two measured miscompiles. An FSQRT with no declared effect was
/// moved past the FSTP that captured its answer, so SQR(16) printed 16; later a FADDP was moved out
/// from between the FLDs that set up its operands, so a DOUBLE accumulated round a loop printed the
/// addend instead of the sum. Both were first patched by claiming the instruction touched memory -
/// which worked, was untrue, and also pinned every unrelated integer load and store against every x87
/// operation. Naming the real resource is both truthful and narrower.
/// </summary>
[TestFixture]
public sealed class MachineSchedulerX87Tests {

  private static MInstr Op(MOpcode opcode, params MOperand[] operands)
    => new(opcode, operands, operands.Length == 0
      ? MInstrEffect.None
      : new MInstrEffect([], [], ReadsFlags: false, WritesFlags: false,
          ReadsMemory: opcode is MOpcode.Fld or MOpcode.Fild,
          WritesMemory: opcode is MOpcode.Fstp or MOpcode.Fistp));

  private static MOperand Slot(int index) => new MOperand.StackSlot(index, MRegSize.Tbyte);

  [Test]
  public void Schedule_GivenAnX87Sequence_ThenItsOrderIsPreserved() {
    var fn = new MFunction("f");
    var block = new MBlock("entry");
    fn.Blocks.Add(block);
    block.Instructions.AddRange([
      Op(MOpcode.Fld, Slot(0)),
      Op(MOpcode.Fld, Slot(1)),
      Op(MOpcode.Faddp),                       // no operands, no declared effect at all
      Op(MOpcode.Fstp, Slot(2)),
      Op(MOpcode.Fld, Slot(2)),
      Op(MOpcode.Fsqrt),
      Op(MOpcode.Fstp, Slot(3)),
      new MInstr(MOpcode.Ret, [], MInstrEffect.None),
    ]);
    var before = block.Instructions.Select(i => i.Opcode).ToList();

    MachineScheduler.Schedule(fn);

    Assert.That(block.Instructions.Select(i => i.Opcode), Is.EqualTo(before),
      "an operand-less x87 op must not float away from the loads and stores around it");
  }

  [Test]
  public void UsesX87_GivenTheStackOpcodes_ThenTheyAreAllRecognised() {
    foreach (var opcode in new[] {
               MOpcode.Fld, MOpcode.Fstp, MOpcode.Fild, MOpcode.Fistp,
               MOpcode.Faddp, MOpcode.Fsubp, MOpcode.Fmulp, MOpcode.Fdivp,
               MOpcode.Fcompp, MOpcode.FstswAx, MOpcode.Fsqrt,
               MOpcode.Fsin, MOpcode.Fcos, MOpcode.Fptan, MOpcode.Fpatan, MOpcode.Fyl2x,
               MOpcode.Fxch, MOpcode.FstpSt0,
               MOpcode.Fld1, MOpcode.Fldln2, MOpcode.Fldlg2, MOpcode.Fldl2e, MOpcode.Fldl2t })
      Assert.That(MOpcodes.UsesX87(opcode), Is.True, $"{opcode} uses the x87 stack");

    foreach (var opcode in new[] { MOpcode.Mov, MOpcode.Add, MOpcode.Call, MOpcode.Cmp, MOpcode.Sahf })
      Assert.That(MOpcodes.UsesX87(opcode), Is.False, $"{opcode} does not");
  }
}
