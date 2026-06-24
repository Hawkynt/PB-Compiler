using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Stage 3 of the x86-16 back end (docs/X86-BACKEND.md): live-interval analysis. Each virtual
/// register's interval runs from first definition to last use; registers nested in a memory operand
/// count as reads so an address register stays live across the access.
/// </summary>
[TestFixture]
public sealed class LivenessAnalysisTests {

  [Test]
  public void Compute_GivenSelectedFunction_ThenEachValueLivesFromDefToLastUse() {
    // F(a) = (a + 3) * a : a is live across both uses, the sum is a short-lived temporary
    var arg = new IrArgument(IrType.I16, 0);
    var fn = new IrFunction("F", IrType.I16, [arg]);
    var entry = fn.CreateBlock("entry");
    var sum = entry.Append(new IrBinary(IrBinaryOp.Add, arg, new IrConstantInt(IrType.I16, 3)));
    var prod = entry.Append(new IrBinary(IrBinaryOp.Mul, sum, arg));
    entry.Append(new IrRet(prod));

    var m = InstructionSelector.TrySelect(fn);
    Assert.That(m, Is.Not.Null);
    var intervals = LivenessAnalysis.Compute(m!);

    // every virtual register has a well-formed interval (start <= end)
    Assert.That(intervals, Is.Not.Empty);
    Assert.That(intervals.All(i => i.Start <= i.End), Is.True);

    // the argument's vreg (defined as live-in, used by both binaries) outlives the first sum temporary
    var argInterval = intervals.OrderBy(i => i.VirtualId).First();   // v0 = the argument
    var argSpan = argInterval.End - argInterval.Start;
    Assert.That(argSpan, Is.GreaterThan(0), "the argument is used more than once, so it spans multiple instructions");
  }

  [Test]
  public void Compute_GivenLoopCarriedValue_ThenIntervalSpansTheBackEdge() {
    // entry: v0 = 5 ; jmp loop      loop: v2 = v0 ; v1 = 7 ; jmp loop (self-loop)
    // v0 is used at the top of the loop and the loop branches back, so it is live across the whole loop -
    // its interval must extend past its last LINEAR use (the v2=v0 read) to cover the later v1 definition,
    // which a single straight scan would miss (and then wrongly let v1 reuse v0's register).
    MInstr MovImm(int d, long val) => new(MOpcode.Mov, [new MOperand.Register(MReg.Virtual(d)), new MOperand.Immediate(val)],
      new MInstrEffect([0], [], false, false, false, false));
    MInstr MovRR(int d, int s) => new(MOpcode.Mov, [new MOperand.Register(MReg.Virtual(d)), new MOperand.Register(MReg.Virtual(s))],
      new MInstrEffect([0], [1], false, false, false, false));
    MInstr Jmp(string label) => new(MOpcode.Jmp, [new MOperand.LabelRef(label)], MInstrEffect.None);

    var fn = new MFunction("t");
    var entry = new MBlock("entry");
    entry.Instructions.Add(MovImm(0, 5));   // idx 0: def v0
    entry.Instructions.Add(Jmp("loop"));    // idx 1
    entry.Successors.Add("loop");
    var loop = new MBlock("loop");
    loop.Instructions.Add(MovRR(2, 0));     // idx 2: v2 = v0   (last LINEAR use of v0)
    loop.Instructions.Add(MovImm(1, 7));    // idx 3: def v1
    loop.Instructions.Add(Jmp("loop"));     // idx 4: back-edge
    loop.Successors.Add("loop");
    fn.Blocks.Add(entry);
    fn.Blocks.Add(loop);

    var intervals = LivenessAnalysis.Compute(fn);
    var v0 = intervals.First(i => i.VirtualId == 0);

    // straight-scan would end v0 at index 2; loop-aware liveness keeps it live through the back-edge
    Assert.That(v0.End, Is.GreaterThanOrEqualTo(3), "the loop-carried value stays live past its last linear use");
  }

  [Test]
  public void RegistersOf_GivenMemoryOperand_ThenBaseRegisterIsRead() {
    // MOV v1, [v0]  -> v0 (the address) is read, v1 is written
    var load = new MInstr(MOpcode.Mov,
      [new MOperand.Register(MReg.Virtual(1)), new MOperand.Memory(MReg.Virtual(0), null, 1, 0, MRegSize.Word)],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false, ReadsMemory: true, WritesMemory: false));

    var (reads, writes) = LivenessAnalysis.RegistersOf(load);

    Assert.That(reads, Does.Contain(0), "the address register is a read");
    Assert.That(writes, Does.Contain(1), "the destination is a write");
  }
}
