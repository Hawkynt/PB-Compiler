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

  private static MInstr LoadVirtual(MReg dest, int disp) => new(MOpcode.Mov,
    [new MOperand.Register(dest), new MOperand.Memory(MReg.Physical_(Reg.BP), null, 1, disp, MRegSize.Word)],
    new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false, ReadsMemory: true, WritesMemory: false));

  private static MInstr AccumulateVirtual(MReg accumulator, MReg addend) => new(MOpcode.Add,
    [new MOperand.Register(accumulator), new MOperand.Register(addend)],
    new MInstrEffect(WrittenRegs: [0], ReadRegs: [0, 1], ReadsFlags: false, WritesFlags: true, ReadsMemory: false, WritesMemory: false));

  /// <summary>
  /// A serial accumulation fed by one load per step: written this way it needs two registers at a
  /// time, however long it is. Every load is ready at the top of the block though, and the list
  /// scheduler prefers memory work, so nothing but a pressure gate stops it issuing all of them there
  /// - which on a six-register machine is a function that selects and then cannot be allocated.
  /// </summary>
  [Test]
  public void Schedule_GivenASerialChainFedByMoreLoadsThanRegisters_ThenTheBlockKeepsItsOrder() {
    var accumulator = MReg.Virtual(0);
    var instructions = new List<MInstr> { LoadVirtual(accumulator, 0) };
    for (var step = 1; step <= 8; ++step) {
      instructions.Add(LoadVirtual(MReg.Virtual(step), 2 * step));
      instructions.Add(AccumulateVirtual(accumulator, MReg.Virtual(step)));
    }
    var fn = OneBlock([.. instructions]);

    MachineScheduler.Schedule(fn);

    Assert.That(fn.Blocks[0].Instructions, Is.EqualTo(instructions),
      "hoisting the loads would keep nine values alive where the written order keeps two");
  }

  /// <summary>
  /// The same shape short enough to fit: the gate is the register file, not "never reorder", so a
  /// chain whose loads all fit in registers still gets them clustered ahead of the arithmetic.
  /// </summary>
  [Test]
  public void Schedule_GivenASerialChainWhoseLoadsFitTheRegisterFile_ThenTheyStillGroupFirst() {
    var accumulator = MReg.Virtual(0);
    var instructions = new List<MInstr> { LoadVirtual(accumulator, 0) };
    for (var step = 1; step <= 3; ++step) {
      instructions.Add(LoadVirtual(MReg.Virtual(step), 2 * step));
      instructions.Add(AccumulateVirtual(accumulator, MReg.Virtual(step)));
    }
    var fn = OneBlock([.. instructions]);

    MachineScheduler.Schedule(fn);

    Assert.That(fn.Blocks[0].Instructions.Take(4).Select(i => i.Opcode),
      Is.All.EqualTo(MOpcode.Mov), "four values at once is within the file, so the loads may lead");
  }

  [Test]
  public void Schedule_GivenPhysicalRuntimeArgumentSetup_ThenKeepsItAfterIndependentVirtualWork() {
    var address = MReg.Virtual(0);
    var lea = new MInstr(MOpcode.Lea,
      [new MOperand.Register(address), new MOperand.StackSlot(0, MRegSize.Word)],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: false, WritesMemory: false));
    var arrayStore = new MInstr(MOpcode.Mov,
      [new MOperand.Memory(address, null, 1, 0, MRegSize.Word), new MOperand.Immediate(5)],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: false, WritesMemory: true));
    var setSi = new MInstr(MOpcode.Mov,
      [new MOperand.Register(MReg.Physical_(Reg.SI)), new MOperand.DataOffset(".str0", 0)],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: false, WritesMemory: false), clobbers: [Reg.SI]);
    var setCx = new MInstr(MOpcode.Mov,
      [new MOperand.Register(MReg.Physical_(Reg.CX)), new MOperand.Immediate(3)],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: false, WritesMemory: false), clobbers: [Reg.CX]);
    var call = new MInstr(MOpcode.Call, [new MOperand.LabelRef("rt_print_str")],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: true, WritesMemory: true), clobbers: [Reg.AX, Reg.BX, Reg.CX, Reg.DX, Reg.SI, Reg.DI]);
    var fn = OneBlock(lea, arrayStore, setSi, setCx, call);

    MachineScheduler.Schedule(fn);

    Assert.That(fn.Blocks[0].Instructions, Is.EqualTo(new[] { lea, arrayStore, setSi, setCx, call }),
      "virtual work must not enter a physical-register ABI setup window before allocation");
  }
}
