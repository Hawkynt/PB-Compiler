using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Stage 2 of the x86-16 back end (docs/X86-BACKEND.md): selecting the typed-SSA IR into the
/// machine IR. This first increment covers the straight-line integer core and declines anything
/// else; the tests inspect the selected <see cref="MFunction"/> directly (execution is a later stage).
/// </summary>
[TestFixture]
public sealed class InstructionSelectorTests {

  [Test]
  public void TrySelect_GivenIntegerBinary_ThenTwoAddressFormThenReturnInAx() {
    // function F(a) = a + 3
    var arg = new IrArgument(IrType.I16, 0);
    var fn = new IrFunction("F", IrType.I16, [arg]);
    var entry = fn.CreateBlock("entry");
    var sum = entry.Append(new IrBinary(IrBinaryOp.Add, arg, new IrConstantInt(IrType.I16, 3)));
    entry.Append(new IrRet(sum));

    var m = InstructionSelector.TrySelect(fn);

    Assert.That(m, Is.Not.Null);
    var ops = m!.AllInstructions.Select(i => i.Opcode).ToArray();
    // dest = a ; dest += 3 ; AX = dest ; ret
    Assert.That(ops, Is.EqualTo(new[] { MOpcode.Mov, MOpcode.Add, MOpcode.Mov, MOpcode.Ret }));

    // the ADD is two-address: it writes operand 0 and reads operand 0, with an immediate rhs
    var add = m.AllInstructions.First(i => i.Opcode == MOpcode.Add);
    Assert.That(add.Effect.WrittenRegs, Is.EqualTo(new[] { 0 }));
    Assert.That(add.Effect.ReadRegs, Is.EqualTo(new[] { 0 }));
    Assert.That(add.Effect.WritesFlags, Is.True);
    Assert.That(add.Operands[1], Is.InstanceOf<MOperand.Immediate>());

    // the return moves into a physical AX
    var retMov = m.Blocks[0].Instructions[^2];
    var dst = ((MOperand.Register)retMov.Operands[0]).Reg;
    Assert.That(dst.IsVirtual, Is.False);
    Assert.That(dst.Physical, Is.EqualTo(Reg.AX));
  }

  [Test]
  public void TrySelect_GivenAllocaStoreLoad_ThenSlotAndMemoryOperands() {
    // p = alloca i16 ; store 7, p ; x = load p ; ret x
    var fn = new IrFunction("G", IrType.I16);
    var entry = fn.CreateBlock("entry");
    var p = entry.Append(new IrAlloca(IrType.I16));
    entry.Append(new IrStore(new IrConstantInt(IrType.I16, 7), p));
    var x = entry.Append(new IrLoad(IrType.I16, p));
    entry.Append(new IrRet(x));

    var m = InstructionSelector.TrySelect(fn);

    Assert.That(m, Is.Not.Null);
    var ops = m!.AllInstructions.Select(i => i.Opcode).ToArray();
    // LEA slot-addr ; MOV [p],7 ; MOV x,[p] ; MOV AX,x ; RET
    Assert.That(ops, Is.EqualTo(new[] { MOpcode.Lea, MOpcode.Mov, MOpcode.Mov, MOpcode.Mov, MOpcode.Ret }));
    Assert.That(m.StackSlots, Has.Count.EqualTo(1));

    var lea = m.AllInstructions.First(i => i.Opcode == MOpcode.Lea);
    Assert.That(lea.Operands[1], Is.InstanceOf<MOperand.StackSlot>());
    var store = m.Blocks[0].Instructions[1];
    Assert.That(store.Operands[0], Is.InstanceOf<MOperand.Memory>());
    Assert.That(store.Effect.WritesMemory, Is.True);
  }

  [Test]
  public void TrySelect_GivenBranchTerminator_ThenDeclines() {
    // a block that does not end in a return is outside this increment -> null (fall back to direct codegen)
    var fn = new IrFunction("H", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var tail = fn.CreateBlock("tail");
    entry.Append(new IrBr(tail));
    tail.Append(new IrRet());

    Assert.That(InstructionSelector.TrySelect(fn), Is.Null);
  }

  [Test]
  public void TrySelect_GivenFloatOrDivision_ThenDeclines() {
    var fn = new IrFunction("D", IrType.I16, [new IrArgument(IrType.I16, 0)]);
    var entry = fn.CreateBlock("entry");
    var div = entry.Append(new IrBinary(IrBinaryOp.SDiv, fn.Parameters[0], new IrConstantInt(IrType.I16, 2)));
    entry.Append(new IrRet(div));

    Assert.That(InstructionSelector.TrySelect(fn), Is.Null, "SDiv is not in this increment");
  }
}
