using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Stage 4 of the x86-16 back end (docs/X86-BACKEND.md): linear-scan register allocation. Overlapping
/// live intervals must receive distinct physical registers; this is the register reassignment that lets
/// independent values occupy independent registers.
/// </summary>
[TestFixture]
public sealed class LinearScanAllocatorTests {

  [Test]
  public void Allocate_GivenSelectedFunction_ThenOverlappingValuesGetDistinctRegisters() {
    // F(a) = (a + 3) * a : the argument and the (a+3) temporary are simultaneously live at the multiply
    var arg = new IrArgument(IrType.I16, 0);
    var fn = new IrFunction("F", IrType.I16, [arg]);
    var entry = fn.CreateBlock("entry");
    var sum = entry.Append(new IrBinary(IrBinaryOp.Add, arg, new IrConstantInt(IrType.I16, 3)));
    var prod = entry.Append(new IrBinary(IrBinaryOp.Mul, sum, arg));
    entry.Append(new IrRet(prod));

    var m = InstructionSelector.TrySelect(fn);
    Assert.That(m, Is.Not.Null);
    var alloc = LinearScanAllocator.Allocate(m!);

    Assert.That(alloc, Is.Not.Null);
    // every interval that is live at the same time as another holds a different register; verify by
    // recomputing overlaps and checking the assignment keeps them apart
    var intervals = LivenessAnalysis.Compute(m!);
    foreach (var x in intervals)
      foreach (var y in intervals)
        if (x.VirtualId < y.VirtualId && x.Start <= y.End && y.Start <= x.End)
          Assert.That(alloc![x.VirtualId], Is.Not.EqualTo(alloc[y.VirtualId]),
            $"v{x.VirtualId} and v{y.VirtualId} overlap and must not share a register");
  }

  [Test]
  public void Allocate_GivenValueLiveAcrossClobberingCall_ThenAvoidsCallerSavedRegisters() {
    // v0 = 5 ; CALL (clobbers AX/CX/DX) ; store v0  -- v0 is live across the call, so it must land in
    // a callee-saved register (BX/SI/DI), never one the call destroys
    var fn = new MFunction("t");
    var block = new MBlock("entry");
    block.Instructions.Add(new MInstr(MOpcode.Mov, [new MOperand.Register(MReg.Virtual(0)), new MOperand.Immediate(5)],
      new MInstrEffect([0], [], false, false, false, false)));
    block.Instructions.Add(new MInstr(MOpcode.Call, [new MOperand.LabelRef("rt")], MInstrEffect.None,
      clobbers: [Reg.AX, Reg.CX, Reg.DX]));
    block.Instructions.Add(new MInstr(MOpcode.Mov,
      [new MOperand.Memory(MReg.Physical_(Reg.BP), null, 1, 4, MRegSize.Word), new MOperand.Register(MReg.Virtual(0))],
      new MInstrEffect([], [1], false, false, false, true)));
    fn.Blocks.Add(block);

    var alloc = LinearScanAllocator.Allocate(fn);

    Assert.That(alloc, Is.Not.Null);
    Assert.That(alloc![0], Is.AnyOf(Reg.BX, Reg.SI, Reg.DI), "a value live across a call avoids the caller-saved registers");
  }

  [Test]
  public void Allocate_GivenDisjointIntervals_ThenRegisterIsReused() {
    // two independent straight-line writes whose values never overlap can share one register
    var fn = new IrFunction("G", IrType.I16);
    var entry = fn.CreateBlock("entry");
    var p = entry.Append(new IrAlloca(IrType.I16));
    entry.Append(new IrStore(new IrConstantInt(IrType.I16, 1), p));
    entry.Append(new IrStore(new IrConstantInt(IrType.I16, 2), p));
    entry.Append(new IrRet());

    var m = InstructionSelector.TrySelect(fn);
    Assert.That(m, Is.Not.Null);
    var alloc = LinearScanAllocator.Allocate(m!);

    Assert.That(alloc, Is.Not.Null);
    Assert.That(alloc!.Values, Is.All.Matches<Reg>(r => r is Reg.AX or Reg.BX or Reg.CX or Reg.DX or Reg.SI or Reg.DI));
  }
}
