using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Stage 5 of the x86-16 back end (docs/X86-BACKEND.md): emission. The selected machine IR, once
/// register-allocated, emits real machine code through the existing <see cref="Assembler"/> with every
/// virtual operand rewritten to its physical register. These tests drive the whole pipeline
/// (select -> allocate -> emit) and check the produced bytes.
/// </summary>
[TestFixture]
public sealed class MachineEmitterTests {

  [Test]
  public void Emit_GivenAllocatedFunction_ThenProducesTheResolvedInstructionStream() {
    // F(a) = a + 3 : selects to  MOV v1,v0 ; ADD v1,3 ; MOV AX,v1 ; RET
    var arg = new IrArgument(IrType.I16, 0);
    var fn = new IrFunction("F", IrType.I16, [arg]);
    var entry = fn.CreateBlock("entry");
    var sum = entry.Append(new IrBinary(IrBinaryOp.Add, arg, new IrConstantInt(IrType.I16, 3)));
    entry.Append(new IrRet(sum));

    var m = InstructionSelector.TrySelect(fn);
    Assert.That(m, Is.Not.Null);
    var alloc = LinearScanAllocator.Allocate(m!);
    Assert.That(alloc, Is.Not.Null);
    Assert.That(alloc, Does.ContainKey(0).And.ContainKey(1));

    var asm = new Assembler();
    MachineEmitter.Emit(asm, m!, alloc!);
    var bytes = asm.ToArray();

    // a reference stream with the SAME physical registers the allocator chose
    var reference = new Assembler();
    reference.Mov(alloc![1], alloc[0]);   // MOV v1, v0
    reference.Add(alloc[1], (Imm)3);       // ADD v1, 3
    reference.Mov(Reg.AX, alloc[1]);       // MOV AX, v1 (return value)
    reference.Ret();

    Assert.That(bytes, Is.EqualTo(reference.ToArray()));
    Assert.That(bytes, Is.Not.Empty);
  }

  [Test]
  public void Emit_GivenAllocaStoreLoad_ThenResolvesStackSlotToFrameCell() {
    // p = alloca i16 ; store 7,p ; x = load p ; ret x  -> the slot becomes [BP-2]
    var fn = new IrFunction("G", IrType.I16);
    var entry = fn.CreateBlock("entry");
    var p = entry.Append(new IrAlloca(IrType.I16));
    entry.Append(new IrStore(new IrConstantInt(IrType.I16, 7), p));
    var x = entry.Append(new IrLoad(IrType.I16, p));
    entry.Append(new IrRet(x));

    var m = InstructionSelector.TrySelect(fn);
    Assert.That(m, Is.Not.Null);
    var alloc = LinearScanAllocator.Allocate(m!);
    Assert.That(alloc, Is.Not.Null);

    var asm = new Assembler();
    MachineEmitter.Emit(asm, m!, alloc!);
    // the alloca lowers to LEA <reg>, [BP-2] taking the address of the first frame slot; that
    // resolved frame reference must appear in the emitted stream
    var reference = new Assembler();
    reference.Lea(alloc![0], Mem.Word(Reg.BP, -2));
    Assert.That(IndexOf(asm.ToArray(), reference.ToArray()), Is.GreaterThanOrEqualTo(0), "the alloca slot resolves to [BP-2]");
  }

  [Test]
  public void Emit_GivenBranchingFunction_ThenEmitsCompareAndConditionalJump() {
    // Sign(a): if a < 0 goto neg else pos ; pos: ret 1 ; neg: ret -1
    var arg = new IrArgument(IrType.I16, 0);
    var fn = new IrFunction("Sign", IrType.I16, [arg]);
    var entry = fn.CreateBlock("entry");
    var pos = fn.CreateBlock("pos");
    var neg = fn.CreateBlock("neg");
    var cmp = entry.Append(new IrCmp(IrCmpPred.Slt, arg, new IrConstantInt(IrType.I16, 0)));
    entry.Append(new IrCondBr(cmp, neg, pos));
    pos.Append(new IrRet(new IrConstantInt(IrType.I16, 1)));
    neg.Append(new IrRet(new IrConstantInt(IrType.I16, -1)));

    var m = InstructionSelector.TrySelect(fn);
    Assert.That(m, Is.Not.Null);
    var alloc = LinearScanAllocator.Allocate(m!);
    Assert.That(alloc, Is.Not.Null);

    var asm = new Assembler();
    MachineEmitter.Emit(asm, m!, alloc!);
    var bytes = asm.ToArray();   // resolves the label fixups - throws if a branch target is unbound

    Assert.That(bytes, Is.Not.Empty);
    // a JL (signed less-than) conditional jump - near form 0F 8C - is present
    Assert.That(IndexOf(bytes, [0x0F, 0x8C]), Is.GreaterThanOrEqualTo(0), "a JL conditional jump is present");
  }

  [Test]
  public void EmitFunction_GivenLeafFunction_ThenWrapsBodyInTheStandardStackAbi() {
    // F(a, b) = a + b : the full function with prologue, argument loads and a RET that cleans 4 bytes
    var a = new IrArgument(IrType.I16, 0);
    var b = new IrArgument(IrType.I16, 1);
    var fn = new IrFunction("F", IrType.I16, [a, b]);
    var entry = fn.CreateBlock("entry");
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.Add, a, b))));

    var m = InstructionSelector.TrySelect(fn);
    Assert.That(m, Is.Not.Null);
    var alloc = LinearScanAllocator.Allocate(m!);
    Assert.That(alloc, Is.Not.Null);

    var asm = new Assembler();
    // the two BYVAL word parameters sit at [BP+4] and [BP+6]; the function cleans 4 bytes on return
    MachineEmitter.EmitFunction(asm, m!, alloc!, [4, 6], 4);
    var bytes = asm.ToArray();

    Assert.That(bytes[0], Is.EqualTo((byte)0x55), "PUSH BP opens the frame");
    Assert.That(IndexOf(bytes, [0xC2, 0x04, 0x00]), Is.GreaterThanOrEqualTo(0), "RET 4 cleans the two word arguments");
  }

  [Test]
  public void Emit_GivenPushAndCall_ThenEmitsArgumentPushAndCallOpcode() {
    // PUSH 7 ; CALL rt  -- a runtime call with one stacked argument
    var fn = new MFunction("t");
    var block = new MBlock("entry");
    block.Instructions.Add(new MInstr(MOpcode.Push, [new MOperand.Immediate(7)],
      new MInstrEffect([], [], false, false, false, true)));
    block.Instructions.Add(new MInstr(MOpcode.Call, [new MOperand.LabelRef("rt")], MInstrEffect.None,
      clobbers: [Reg.AX, Reg.CX, Reg.DX]));
    fn.Blocks.Add(block);

    var asm = new Assembler();
    MachineEmitter.Emit(asm, fn, new Dictionary<int, Reg>());
    asm.MarkLabel(asm.Lbl("rt"));   // define the call target so the fixup resolves
    var bytes = asm.ToArray();

    Assert.That(bytes, Does.Contain((byte)0xE8), "a near CALL opcode is emitted");
    Assert.That(bytes[0], Is.AnyOf((byte)0x68, (byte)0x6A), "the argument PUSH leads (PUSH imm16 / imm8)");
  }

  private static int IndexOf(byte[] haystack, byte[] needle) {
    for (var i = 0; i + needle.Length <= haystack.Length; ++i) {
      var hit = true;
      for (var k = 0; k < needle.Length; ++k)
        if (haystack[i + k] != needle[k]) { hit = false; break; }
      if (hit)
        return i;
    }
    return -1;
  }
}
