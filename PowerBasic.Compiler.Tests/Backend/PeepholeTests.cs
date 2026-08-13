using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The encoding idioms <see cref="Peephole"/> folds over the selected machine IR: an ALU operand read
/// straight from memory, a cell read-modified-written in place, and a bit test that never materializes
/// the masked value.
///
/// Each rewrite is guarded by a census of the whole function, so the tests come in pairs: the shape
/// that folds, and the same shape with one more mention of the intermediate - which must not, because
/// somebody else can then see it.
/// </summary>
[TestFixture]
public sealed class PeepholeTests {

  private static MOperand.Register V(int id) => new(MReg.Virtual(id));

  private static MOperand.StackSlot Cell(int slot) => new(slot, MRegSize.Word);

  private static MInstr Load(int dest, MOperand from) => new(MOpcode.Mov, [V(dest), from],
    new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
      ReadsMemory: true, WritesMemory: false));

  private static MInstr Store(MOperand to, int source) => new(MOpcode.Mov, [to, V(source)],
    new MInstrEffect(WrittenRegs: [], ReadRegs: [1], ReadsFlags: false, WritesFlags: false,
      ReadsMemory: false, WritesMemory: true));

  private static MInstr Alu(MOpcode opcode, int dest, MOperand source) => new(opcode, [V(dest), source],
    new MInstrEffect(WrittenRegs: opcode == MOpcode.Cmp ? [] : [0],
      ReadRegs: source is MOperand.Register ? [0, 1] : [0],
      ReadsFlags: false, WritesFlags: true, ReadsMemory: false, WritesMemory: false));

  private static MInstr Branch() => new(MOpcode.Jcc, [new MOperand.LabelRef("x")],
    new MInstrEffect([], [], ReadsFlags: true, WritesFlags: false, ReadsMemory: false, WritesMemory: false),
    Condition.NotEqual);

  private static MInstr Call() => new(MOpcode.Call, [new MOperand.LabelRef("rt_x")],
    new MInstrEffect([], [], ReadsFlags: false, WritesFlags: true, ReadsMemory: true, WritesMemory: true),
    condition: null, clobbers: [Reg.AX, Reg.CX, Reg.DX]);

  private static MFunction OneBlock(params MInstr[] instrs) {
    var fn = new MFunction("t") { VirtualRegisterCount = 16 };
    var block = new MBlock("entry");
    block.Instructions.AddRange(instrs);
    fn.Blocks.Add(block);
    return fn;
  }

  private static List<MInstr> Body(MFunction fn) => fn.Blocks[0].Instructions;

  [Test]
  public void Fold_GivenLoadReadOnlyByAnAluOp_WhenRun_ThenTheAluTakesTheMemoryOperand() {
    // MOV v1,[slot0] ; ADD v0,v1  ->  ADD v0,[slot0]
    var fn = OneBlock(Load(1, Cell(0)), Alu(MOpcode.Add, 0, V(1)));

    Assert.That(Peephole.Run(fn), Is.EqualTo(1));
    Assert.That(Body(fn), Has.Count.EqualTo(1));
    Assert.That(Body(fn)[0].Opcode, Is.EqualTo(MOpcode.Add));
    Assert.That(Body(fn)[0].Operands[1], Is.EqualTo(Cell(0)), "the cell is the ALU op's own operand");
    Assert.That(Body(fn)[0].Effect.ReadsMemory, Is.True, "and the descriptor says so, or the scheduler would reorder it");
  }

  [Test]
  public void Fold_GivenLoadWithASecondReader_WhenRun_ThenTheLoadStays() {
    // the value is wanted twice: folding would trade one instruction for two memory accesses
    var fn = OneBlock(Load(1, Cell(0)), Alu(MOpcode.Add, 0, V(1)), Alu(MOpcode.Sub, 2, V(1)));

    Assert.That(Peephole.Run(fn), Is.Zero);
    Assert.That(Body(fn), Has.Count.EqualTo(3));
  }

  [Test]
  public void Fold_GivenAStoreBetweenTheLoadAndItsReader_WhenRun_ThenTheLoadStays() {
    // the intervening store may be to the very cell being folded - there is no aliasing analysis here
    var fn = OneBlock(Load(1, Cell(0)), Store(Cell(3), 4), Alu(MOpcode.Add, 0, V(1)));

    Assert.That(Peephole.Run(fn), Is.Zero);
    Assert.That(Body(fn)[0].Opcode, Is.EqualTo(MOpcode.Mov));
  }

  [Test]
  public void Fold_GivenACallBetweenTheLoadAndItsReader_WhenRun_ThenTheLoadStays() {
    var fn = OneBlock(Load(1, Cell(0)), Call(), Alu(MOpcode.Add, 0, V(1)));

    Assert.That(Peephole.Run(fn), Is.Zero);
  }

  [Test]
  public void Fold_GivenARegisterFormedAddressAndAGapToItsReader_WhenRun_ThenTheLoadStays() {
    // a value used as a memory base may only live in BX/SI/DI and cannot spill, so its live range is
    // never lengthened; the fold is offered only to the instruction immediately following the load
    var indirect = new MOperand.Memory(MReg.Virtual(9), null, 1, 0, MRegSize.Word);
    var gapped = OneBlock(Load(1, indirect), Alu(MOpcode.Xor, 5, new MOperand.Immediate(1)), Alu(MOpcode.Add, 0, V(1)));
    var adjacent = OneBlock(Load(1, indirect), Alu(MOpcode.Add, 0, V(1)));

    Assert.That(Peephole.Run(gapped), Is.Zero, "a gap would lengthen the address register's range");
    Assert.That(Peephole.Run(adjacent), Is.EqualTo(1), "the very next instruction lengthens nothing");
  }

  [Test]
  public void Fold_GivenLoadIncrementStoreOfTheSameCell_WhenRun_ThenIncrementsInPlace() {
    // MOV v1,[slot0] ; ADD v1,1 ; MOV [slot0],v1  ->  INC [slot0]
    var fn = OneBlock(Load(1, Cell(0)), Alu(MOpcode.Add, 1, new MOperand.Immediate(1)), Store(Cell(0), 1), Call());

    Assert.That(Peephole.Run(fn), Is.EqualTo(1));
    Assert.That(Body(fn)[0].Opcode, Is.EqualTo(MOpcode.Inc));
    Assert.That(Body(fn)[0].Operands[0], Is.EqualTo(Cell(0)));
  }

  [Test]
  public void Fold_GivenLoadIncrementStoreWhoseFlagsAreRead_WhenRun_ThenKeepsTheAddThatWritesCarry() {
    // INC leaves the carry alone where ADD writes it, so it is only taken where the flags are dead
    var fn = OneBlock(Load(1, Cell(0)), Alu(MOpcode.Add, 1, new MOperand.Immediate(1)), Store(Cell(0), 1), Branch());

    Assert.That(Peephole.Run(fn), Is.EqualTo(1));
    Assert.That(Body(fn)[0].Opcode, Is.EqualTo(MOpcode.Add), "still in place, but through ADD");
    Assert.That(Body(fn)[0].Operands[0], Is.EqualTo(Cell(0)));
  }

  [Test]
  public void Fold_GivenLoadAddConstantStore_WhenRun_ThenAddsToTheCellDirectly() {
    var fn = OneBlock(Load(1, Cell(0)), Alu(MOpcode.Add, 1, new MOperand.Immediate(5)), Store(Cell(0), 1), Call());

    Assert.That(Peephole.Run(fn), Is.EqualTo(1));
    Assert.That(Body(fn), Has.Count.EqualTo(2));
    Assert.That(Body(fn)[0].Opcode, Is.EqualTo(MOpcode.Add));
    Assert.That(Body(fn)[0].Operands, Is.EqualTo(new MOperand[] { Cell(0), new MOperand.Immediate(5) }));
  }

  [Test]
  public void Fold_GivenLoadModifyStoreToADifferentCell_WhenRun_ThenNothingIsFolded() {
    var fn = OneBlock(Load(1, Cell(0)), Alu(MOpcode.Add, 1, new MOperand.Immediate(1)), Store(Cell(2), 1), Call());

    Assert.That(Peephole.Run(fn), Is.Zero, "a read-modify-write is one cell, not two");
  }

  [Test]
  public void Fold_GivenMaskThenCompareAgainstZero_WhenRun_ThenOneBitTest() {
    // MOV v1,v0 ; AND v1,4 ; CMP v1,0  ->  TEST v0,4
    var fn = OneBlock(new MInstr(MOpcode.Mov, [V(1), V(0)],
        new MInstrEffect(WrittenRegs: [0], ReadRegs: [1], ReadsFlags: false, WritesFlags: false,
          ReadsMemory: false, WritesMemory: false)),
      Alu(MOpcode.And, 1, new MOperand.Immediate(4)),
      Alu(MOpcode.Cmp, 1, new MOperand.Immediate(0)),
      Branch());

    Assert.That(Peephole.Run(fn), Is.EqualTo(1));
    Assert.That(Body(fn), Has.Count.EqualTo(2));
    Assert.That(Body(fn)[0].Opcode, Is.EqualTo(MOpcode.Test));
    Assert.That(Body(fn)[0].Operands, Is.EqualTo(new MOperand[] { V(0), new MOperand.Immediate(4) }));
  }

  [Test]
  public void Fold_GivenAMaskedValueSomethingElseReads_WhenRun_ThenTheMaskIsKept() {
    var fn = OneBlock(new MInstr(MOpcode.Mov, [V(1), V(0)],
        new MInstrEffect(WrittenRegs: [0], ReadRegs: [1], ReadsFlags: false, WritesFlags: false,
          ReadsMemory: false, WritesMemory: false)),
      Alu(MOpcode.And, 1, new MOperand.Immediate(4)),
      Alu(MOpcode.Cmp, 1, new MOperand.Immediate(0)),
      Branch(),
      Store(Cell(7), 1));

    Assert.That(Peephole.Run(fn), Is.Zero, "the masked value is stored, so it has to be materialized");
  }

  [Test]
  public void Fold_GivenAFlagReadBetweenTheMaskAndTheTest_WhenRun_ThenNothingIsFolded() {
    // those flags are the AND's; the rewrite does not produce them until the TEST, which is later
    var fn = OneBlock(new MInstr(MOpcode.Mov, [V(1), V(0)],
        new MInstrEffect(WrittenRegs: [0], ReadRegs: [1], ReadsFlags: false, WritesFlags: false,
          ReadsMemory: false, WritesMemory: false)),
      Alu(MOpcode.And, 1, new MOperand.Immediate(4)),
      new MInstr(MOpcode.Adc, [V(3), V(3)],
        new MInstrEffect(WrittenRegs: [0], ReadRegs: [0, 1], ReadsFlags: true, WritesFlags: true,
          ReadsMemory: false, WritesMemory: false)),
      Alu(MOpcode.Cmp, 1, new MOperand.Immediate(0)),
      Branch());

    Assert.That(Peephole.Run(fn), Is.Zero);
  }
}
