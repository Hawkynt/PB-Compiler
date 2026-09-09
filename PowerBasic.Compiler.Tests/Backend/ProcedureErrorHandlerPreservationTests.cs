using PowerBasic.Compiler.Backend;

namespace PowerBasic.Compiler.Tests.Backend;

[TestFixture]
public sealed class ProcedureErrorHandlerPreservationTests {

  [Test]
  public void Run_GivenTwoReturns_ThenSavesOnceAndRestoresBeforeEveryReturn() {
    var function = new MFunction("handler") { VirtualRegisterCount = 2 };
    function.StackSlots.Add(4);
    var entry = new MBlock("entry");
    var alternate = new MBlock("alternate");
    entry.Instructions.Add(Ret());
    alternate.Instructions.Add(Ret());
    function.Blocks.Add(entry);
    function.Blocks.Add(alternate);

    var inserted = ProcedureErrorHandlerPreservation.Run(function);

    Assert.Multiple(() => {
      Assert.That(inserted, Is.EqualTo(18));
      Assert.That(function.StackSlots, Is.EqualTo(new[] { 4, 6 }));
      Assert.That(function.VirtualRegisterCount, Is.EqualTo(11));
      Assert.That(entry.Instructions.Count, Is.EqualTo(13));
      Assert.That(alternate.Instructions.Count, Is.EqualTo(7));
    });

    AssertTransfer(entry.Instructions, 0, fromHandlerCells: true, firstVirtualId: 2);
    AssertTransfer(entry.Instructions, 6, fromHandlerCells: false, firstVirtualId: 5);
    Assert.That(entry.Instructions[^1].Opcode, Is.EqualTo(MOpcode.Ret));

    AssertTransfer(alternate.Instructions, 0, fromHandlerCells: false, firstVirtualId: 8);
    Assert.That(alternate.Instructions[^1].Opcode, Is.EqualTo(MOpcode.Ret));
  }

  [Test]
  public void Run_GivenNoBlocks_ThenLeavesTheFunctionAlone() {
    var function = new MFunction("empty") { VirtualRegisterCount = 3 };

    Assert.Multiple(() => {
      Assert.That(ProcedureErrorHandlerPreservation.Run(function), Is.Zero);
      Assert.That(function.StackSlots, Is.Empty);
      Assert.That(function.VirtualRegisterCount, Is.EqualTo(3));
    });
  }

  private static MInstr Ret() => new(MOpcode.Ret, [], MInstrEffect.None);

  private static void AssertTransfer(
      IReadOnlyList<MInstr> instructions, int start, bool fromHandlerCells, int firstVirtualId) {
    string[] cells = ["rt_onerr", "rt_onerr_bp", "rt_onerr_sp"];
    for (var index = 0; index < cells.Length; ++index) {
      var load = instructions[start + index * 2];
      var store = instructions[start + index * 2 + 1];
      var scratch = new MOperand.Register(MReg.Virtual(firstVirtualId + index, MRegSize.Word));
      var handler = new MOperand.DataCell(cells[index], 0, MRegSize.Word);
      var saved = new MOperand.StackSlot(1, MRegSize.Word, index * 2);

      Assert.Multiple(() => {
        Assert.That(load.Opcode, Is.EqualTo(MOpcode.Mov));
        Assert.That(store.Opcode, Is.EqualTo(MOpcode.Mov));
        Assert.That(load.Operands,
          Is.EqualTo(fromHandlerCells ? new MOperand[] { scratch, handler } : [scratch, saved]));
        Assert.That(store.Operands,
          Is.EqualTo(fromHandlerCells ? new MOperand[] { saved, scratch } : [handler, scratch]));
      });
    }
  }
}
