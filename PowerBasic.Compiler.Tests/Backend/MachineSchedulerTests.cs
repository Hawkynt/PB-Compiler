using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Stage 6 of the x86-16 back end (docs/X86-BACKEND.md): scheduling the allocated machine IR. With
/// independent values in independent registers, the dependency-driven list scheduler interleaves their
/// chains and clusters loads; dependent chains keep their order. This is the deeper reordering the
/// AX-centric byte-level scheduler could not reach.
/// </summary>
[TestFixture]
public sealed class MachineSchedulerTests {

  private static MInstr Load(Reg dest, int disp) => new(MOpcode.Mov,
    [new MOperand.Register(MReg.Physical_(dest)), new MOperand.Memory(MReg.Physical_(Reg.BP), null, 1, disp, MRegSize.Word)],
    new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false, ReadsMemory: true, WritesMemory: false));

  private static MInstr Store(int disp, Reg src) => new(MOpcode.Mov,
    [new MOperand.Memory(MReg.Physical_(Reg.BP), null, 1, disp, MRegSize.Word), new MOperand.Register(MReg.Physical_(src))],
    new MInstrEffect(WrittenRegs: [], ReadRegs: [1], ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: true));

  private static MInstr AddRR(Reg dest, Reg src) => new(MOpcode.Add,
    [new MOperand.Register(MReg.Physical_(dest)), new MOperand.Register(MReg.Physical_(src))],
    new MInstrEffect(WrittenRegs: [0], ReadRegs: [0, 1], ReadsFlags: false, WritesFlags: true, ReadsMemory: false, WritesMemory: false));

  private static MFunction OneBlock(params MInstr[] instrs) {
    var fn = new MFunction("t");
    var block = new MBlock("entry");
    block.Instructions.AddRange(instrs);
    fn.Blocks.Add(block);
    return fn;
  }

  [Test]
  public void Schedule_GivenIndependentLoadAluLoad_ThenLoadsGroupBeforeTheAlu() {
    // load AX,[2] ; ADD BX,CX ; load DX,[4]  - the two loads are independent of the ALU and of each other
    var fn = OneBlock(Load(Reg.AX, 2), AddRR(Reg.BX, Reg.CX), Load(Reg.DX, 4));
    MachineScheduler.Schedule(fn);

    var ops = fn.Blocks[0].Instructions;
    // memory-first issue order hoists both loads ahead of the ALU
    Assert.That(ops[0].Effect.ReadsMemory, Is.True, "a load leads");
    Assert.That(ops[2].Opcode, Is.EqualTo(MOpcode.Add), "the independent ALU op trails the grouped loads");
  }

  [Test]
  public void Schedule_GivenDependentChain_ThenOrderPreserved() {
    // load AX,[2] ; ADD AX,CX ; store [4],AX  - a RAW chain through AX must keep its order
    var load = Load(Reg.AX, 2);
    var add = AddRR(Reg.AX, Reg.CX);
    var store = Store(4, Reg.AX);
    var fn = OneBlock(load, add, store);
    MachineScheduler.Schedule(fn);

    var ops = fn.Blocks[0].Instructions;
    Assert.That(ops[0], Is.SameAs(load));
    Assert.That(ops[1], Is.SameAs(add));
    Assert.That(ops[2], Is.SameAs(store));
  }
}
