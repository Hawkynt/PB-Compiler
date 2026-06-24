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
