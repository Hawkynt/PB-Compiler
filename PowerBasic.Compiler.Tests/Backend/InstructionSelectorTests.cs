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
    // LEA slot-addr ; MOV [p],7 ; MOV x,[p] ; MOV AX,x ; RET - the LEA is still emitted, and is now
    // dead: a single-slot alloca is addressed AS its slot, so nothing reads the register
    Assert.That(ops, Is.EqualTo(new[] { MOpcode.Lea, MOpcode.Mov, MOpcode.Mov, MOpcode.Mov, MOpcode.Ret }));
    Assert.That(m.StackSlots, Has.Count.EqualTo(1));

    var lea = m.AllInstructions.First(i => i.Opcode == MOpcode.Lea);
    Assert.That(lea.Operands[1], Is.InstanceOf<MOperand.StackSlot>());
    var store = m.Blocks[0].Instructions[1];
    Assert.That(store.Operands[0], Is.InstanceOf<MOperand.StackSlot>(),
      "a scalar local is its slot, not [base] - which is what keeps its address out of a register");
    Assert.That(store.Effect.WritesMemory, Is.True);
  }

  [Test]
  public void TrySelect_GivenUnconditionalBranch_ThenEmitsJumpWithSuccessor() {
    var fn = new IrFunction("H", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var tail = fn.CreateBlock("tail");
    entry.Append(new IrBr(tail));
    tail.Append(new IrRet());

    var m = InstructionSelector.TrySelect(fn);

    Assert.That(m, Is.Not.Null);
    Assert.That(m!.Blocks[0].Instructions.Select(i => i.Opcode), Does.Contain(MOpcode.Jmp));
    Assert.That(m.Blocks[0].Successors, Is.EqualTo(new[] { "tail" }));
  }

  [Test]
  public void TrySelect_GivenConditionalBranch_ThenEmitsCompareAndConditionalJump() {
    // entry: if a < 0 goto neg else pos ; pos: ret 1 ; neg: ret -1  (no phi - each path returns directly)
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
    Assert.That(m!.Blocks, Has.Count.EqualTo(3));
    var entryOps = m.Blocks[0].Instructions.Select(i => i.Opcode).ToArray();
    // the compare folds into the branch: CMP ; Jcc(Less)->neg ; JMP->pos
    Assert.That(entryOps, Is.EqualTo(new[] { MOpcode.Cmp, MOpcode.Jcc, MOpcode.Jmp }));
    var jcc = m.Blocks[0].Instructions[1];
    Assert.That(jcc.Condition, Is.EqualTo(PowerBasic.Compiler.Asm.Condition.Less));
    Assert.That(m.Blocks[0].Successors, Is.EqualTo(new[] { "neg", "pos" }));
  }

  [Test]
  public void TrySelect_GivenPhiNode_ThenLowersToEdgeCopy() {
    // entry -> join ; join: x = phi [arg from entry] ; ret x  -- the phi becomes a copy in entry
    var arg = new IrArgument(IrType.I16, 0);
    var fn = new IrFunction("P", IrType.I16, [arg]);
    var entry = fn.CreateBlock("entry");
    var join = fn.CreateBlock("join");
    entry.Append(new IrBr(join));
    var phi = new IrPhi(IrType.I16);
    join.AppendPhi(phi);
    phi.AddIncoming(arg, entry);
    join.Append(new IrRet(phi));

    var m = InstructionSelector.TrySelect(fn);

    Assert.That(m, Is.Not.Null);
    // the entry block gains a MOV (the phi's incoming copy) before its JMP terminator
    var entryOps = m!.Blocks[0].Instructions.Select(i => i.Opcode).ToArray();
    Assert.That(entryOps, Is.EqualTo(new[] { MOpcode.Mov, MOpcode.Jmp }));
  }

  [Test]
  public void TrySelect_GivenDiamondMergePhi_ThenCopiesOnBothEdges() {
    // entry: if arg<0 goto neg else pos ; pos->join (phi=1) ; neg->join (phi=2) ; join: ret phi
    var arg = new IrArgument(IrType.I16, 0);
    var fn = new IrFunction("D", IrType.I16, [arg]);
    var entry = fn.CreateBlock("entry");
    var pos = fn.CreateBlock("pos");
    var neg = fn.CreateBlock("neg");
    var join = fn.CreateBlock("join");
    var cmp = entry.Append(new IrCmp(IrCmpPred.Slt, arg, new IrConstantInt(IrType.I16, 0)));
    entry.Append(new IrCondBr(cmp, neg, pos));
    pos.Append(new IrBr(join));
    neg.Append(new IrBr(join));
    var phi = new IrPhi(IrType.I16);
    join.AppendPhi(phi);
    phi.AddIncoming(new IrConstantInt(IrType.I16, 1), pos);
    phi.AddIncoming(new IrConstantInt(IrType.I16, 2), neg);
    join.Append(new IrRet(phi));

    var m = InstructionSelector.TrySelect(fn);

    Assert.That(m, Is.Not.Null);
    // both the pos and neg blocks get a MOV (their incoming phi copy) before their JMP to join
    foreach (var label in new[] { "pos", "neg" }) {
      var b = m!.Blocks.First(x => x.Label == label);
      Assert.That(b.Instructions.Select(i => i.Opcode), Is.EqualTo(new[] { MOpcode.Mov, MOpcode.Jmp }), $"{label} copies its phi value before the jump");
    }
  }

  /// <summary>
  /// A runtime divisor selects. It used to decline, on the grounds that a value which might be zero
  /// needs the Error-11 guard and the selector had no way to raise - but the guard belongs to the
  /// LANGUAGE, not to this stage, and it is emitted by the lowering as an ordinary comparison and
  /// raise. What arrives here is already guarded, so declining it only cost coverage.
  /// </summary>
  [Test]
  public void TrySelect_GivenDivisionByARuntimeValue_ThenSelects() {
    var fn = new IrFunction("D", IrType.I16, [new IrArgument(IrType.I16, 0), new IrArgument(IrType.I16, 1)]);
    var entry = fn.CreateBlock("entry");
    var div = entry.Append(new IrBinary(IrBinaryOp.SDiv, fn.Parameters[0], fn.Parameters[1]));
    entry.Append(new IrRet(div));

    Assert.That(InstructionSelector.TrySelect(fn), Is.Not.Null);
  }

  private static IrFunction TimesConstant(short multiplier) {
    var fn = new IrFunction("M", IrType.I16, [new IrArgument(IrType.I16, 0)]);
    var entry = fn.CreateBlock("entry");
    var product = entry.Append(new IrBinary(IrBinaryOp.Mul, fn.Parameters[0], new IrConstantInt(IrType.I16, multiplier)));
    entry.Append(new IrRet(product));
    return fn;
  }

  private static PowerBasic.Compiler.CodeGen.TargetCost Speed(PowerBasic.Compiler.CodeGen.CpuTier tier)
    => new(tier, PowerBasic.Compiler.CodeGen.CostObjective.Speed);

  /// <summary>
  /// A 16-bit multiply is the accumulator form the 8086 actually has - <c>MOV AX,lhs; IMUL r16</c> -
  /// and not the two-operand <c>IMUL r16,r/m16</c>, which is an 80386 encoding this back end used to
  /// emit whatever <c>$CPU</c> said. The factor is a register because there is no immediate form, and
  /// which one is the allocator's business.
  /// </summary>
  [Test]
  public void TrySelect_GivenAWordMultiply_ThenTheAccumulatorFormAgainstAxAndDx() {
    var m = InstructionSelector.TrySelect(TimesConstant(3));

    Assert.That(m, Is.Not.Null);
    var multiply = m!.AllInstructions.Single(i => i.Opcode == MOpcode.Imul);
    Assert.That(multiply.Operands, Has.Count.EqualTo(1), "the accumulator IMUL names only its factor");
    Assert.That(multiply.Operands[0], Is.InstanceOf<MOperand.Register>());
    Assert.That(multiply.Clobbers, Does.Contain(Reg.AX).And.Contain(Reg.DX));
    var intoAx = m.AllInstructions.Last(i => i.Opcode == MOpcode.Mov
      && i.Operands[0] is MOperand.Register { Reg: { IsVirtual: false, Physical: Reg.AX } });
    Assert.That(intoAx, Is.Not.Null, "the multiplicand is staged in AX");
  }

  /// <summary>
  /// O0078: with a cost model in hand - which the code generator supplies only under
  /// <c>$OPTIMIZE SPEED</c> - a constant multiplier of up to three set bits becomes shifts and adds.
  /// Without one the compact multiply stays, because the chain is bigger and buys only cycles.
  /// </summary>
  [Test]
  public void TrySelect_GivenAThreeBitMultiplierAndACostModel_ThenShiftsAndAddsInsteadOfImul() {
    var decomposed = InstructionSelector.TrySelect(TimesConstant(11),
      new SelectionTarget(Cost: Speed(PowerBasic.Compiler.CodeGen.CpuTier.I8086)));
    var compact = InstructionSelector.TrySelect(TimesConstant(11));

    Assert.That(decomposed, Is.Not.Null);
    Assert.That(decomposed!.AllInstructions.Any(i => i.Opcode == MOpcode.Imul), Is.False, "11 = 8+2+1 needs no multiply");
    Assert.That(decomposed.AllInstructions.Count(i => i.Opcode == MOpcode.Shl), Is.EqualTo(3), "one 1-bit shift per step");
    Assert.That(compact!.AllInstructions.Any(i => i.Opcode == MOpcode.Imul), Is.True, "no cost model, no trade");
  }

  /// <summary>
  /// The cost model decides the four-bit chain per target: ~eight instructions beat the 8086's
  /// microcoded multiply and lose to the 386's, so the same multiplier decomposes on one and not the
  /// other. Five set bits pay on neither.
  /// </summary>
  [TestCase(PowerBasic.Compiler.CodeGen.CpuTier.I8086, (short)23, false)]
  [TestCase(PowerBasic.Compiler.CodeGen.CpuTier.I80386, (short)23, true)]
  [TestCase(PowerBasic.Compiler.CodeGen.CpuTier.I8086, (short)341, true)]
  public void TrySelect_GivenAWideMultiplierAndACostModel_ThenTheTierDecides(
      PowerBasic.Compiler.CodeGen.CpuTier tier, short multiplier, bool keepsImul) {
    var m = InstructionSelector.TrySelect(TimesConstant(multiplier), new SelectionTarget(Cost: Speed(tier)));

    Assert.That(m, Is.Not.Null);
    Assert.That(m!.AllInstructions.Any(i => i.Opcode == MOpcode.Imul), Is.EqualTo(keepsImul));
  }
}
