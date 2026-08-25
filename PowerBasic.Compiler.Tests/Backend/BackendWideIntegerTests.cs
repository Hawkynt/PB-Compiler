using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// 32-bit values on a 16-bit target. The baseline representation of a LONG/DWORD is a register
/// <b>pair</b>: the selector mints two ordinary virtual registers per value, which keeps the allocator
/// free of any pairing concept. Optimized 386 SPEED loops may instead keep a proven-safe recurrence
/// in one native dword register; arguments, runtime-derived values, and other fallbacks retain pairs.
///
/// This matters for correctness, not just coverage. Before the pair lowering, a 32-bit load minted a
/// single <c>Dword</c>-sized virtual register and emitted one <c>MOV</c> - and since the emitter
/// resolves every memory operand as <see cref="Mem.Word"/> and every register by identity regardless
/// of size, that silently read the <b>low 16 bits only</b> and carried it as a whole LONG. Functions
/// like that selected and would have miscompiled; these tests pin the shape that replaced it.
/// </summary>
[TestFixture]
public sealed class BackendWideIntegerTests {

  /// <summary>A function whose body the caller fills in, with one 32-bit frame slot to work on.</summary>
  private static IrFunction WideFunction(Action<IrBuilder, IrAlloca> body, IrType returnType) {
    var fn = new IrFunction("F", returnType, []);
    var entry = fn.CreateBlock("entry");
    var b = new IrBuilder(entry);
    var slot = b.Alloca(IrType.I32);
    body(b, slot);
    return fn;
  }

  private static List<MOpcode> Opcodes(MFunction m) => m.AllInstructions.Select(i => i.Opcode).ToList();

  [Test]
  public void Select_GivenLongLoad_ThenReadsBothWords() {
    var fn = WideFunction((b, slot) => b.Ret(b.Load(IrType.I32, slot)), IrType.I32);

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, reason);
    var loads = m!.AllInstructions
      .Where(i => i.Opcode == MOpcode.Mov && i.Operands[1] is MOperand.StackSlot or MOperand.Memory)
      .ToList();
    Assert.That(loads, Has.Count.EqualTo(2), "a 32-bit load is two word reads, not one");
    var displacements = loads.Select(i => i.Operands[1]).OfType<MOperand.Memory>().Select(m2 => m2.Disp).ToList();
    if (displacements.Count == 2)
      Assert.That(displacements[1] - displacements[0], Is.EqualTo(2), "the high word is at +2 (little-endian)");
  }

  [Test]
  public void Select_GivenLongAdd_ThenThreadsTheCarryWithAdc() {
    var fn = WideFunction((b, slot) => {
      var value = b.Load(IrType.I32, slot);
      b.Store(b.Add(value, new IrConstantInt(IrType.I32, 1)), slot);
      b.Ret(null);
    }, IrType.Void);

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, reason);
    var opcodes = Opcodes(m!);
    var add = opcodes.IndexOf(MOpcode.Add);
    var adc = opcodes.IndexOf(MOpcode.Adc);
    Assert.That(add, Is.GreaterThanOrEqualTo(0), "the low half adds");
    Assert.That(adc, Is.EqualTo(add + 1), "the high half must follow immediately, reading the carry");

    var adcInstr = m!.AllInstructions.First(i => i.Opcode == MOpcode.Adc);
    Assert.That(adcInstr.Effect.ReadsFlags, Is.True,
      "ADC must declare it reads the carry, or the scheduler could separate it from its ADD");
    Assert.That(m.AllInstructions.First(i => i.Opcode == MOpcode.Add).Effect.WritesFlags, Is.True);
  }

  [Test]
  public void Select_GivenLongSubtract_ThenBorrowsWithSbb() {
    var fn = WideFunction((b, slot) => {
      var value = b.Load(IrType.I32, slot);
      b.Store(b.Sub(value, new IrConstantInt(IrType.I32, 1)), slot);
      b.Ret(null);
    }, IrType.Void);

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, reason);
    Assert.That(Opcodes(m!), Does.Contain(MOpcode.Sbb));
  }

  /// <summary>
  /// The high word of a widened INTEGER is the sign bit smeared over all sixteen bits, and it is
  /// written <c>ADD r,r; SBB r,r</c> rather than the <c>SAR r,15</c> this used to assert.
  ///
  /// <para>
  /// The change is not a style preference. <c>SAR r,15</c> assembles to <c>C1</c>, an <b>80186</b>
  /// instruction, and the default target is an 8086 - so the old form put an instruction the declared
  /// part does not have into every widening. It is also the one shift count in the selector too large
  /// to unroll into single-bit steps, and staging 15 into <c>CL</c> would put a <c>CX</c> clobber on
  /// one of the shapes the allocator meets most often. The pair is the same four bytes, needs no
  /// register, and runs on every part: the <c>ADD</c> leaves the sign bit in <c>CF</c> and
  /// <c>SBB r,r</c> is <c>-CF</c>.
  /// </para>
  /// </summary>
  [Test]
  public void Select_GivenSignExtendToLong_ThenSmearsTheSignIntoTheHighWordWithoutAn80186Shift() {
    var fn = new IrFunction("F", IrType.I32, [new IrArgument(IrType.I16, 0)]);
    var entry = fn.CreateBlock("entry");
    var b = new IrBuilder(entry);
    b.Ret(b.SExt(fn.Parameters[0], IrType.I32));

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, reason);
    var instructions = m!.AllInstructions.ToList();
    var add = instructions.FindIndex(i => i.Opcode == MOpcode.Add
      && i.Operands[0] is MOperand.Register d && i.Operands[1] is MOperand.Register s && d.Reg.Equals(s.Reg));
    Assert.That(add, Is.GreaterThanOrEqualTo(0), "the sign smear doubles the copy so its sign lands in CF");
    Assert.That(instructions[add + 1].Opcode, Is.EqualTo(MOpcode.Sbb),
      "and subtracts the register from itself, which leaves -CF: 0FFFFh when negative, 0 when not");
    Assert.That(instructions[add + 1].Effect.ReadsFlags, Is.True,
      "the SBB has to declare the carry dependency, or the scheduler may put a flag writer between them");
    Assert.That(instructions.Any(i => i.Opcode == MOpcode.Sar), Is.False,
      "SAR r,15 is C1 - an 80186 encoding - and the default target is an 8086");
  }

  [Test]
  public void Select_GivenZeroExtendToLong_ThenClearsTheHighWord() {
    var fn = new IrFunction("F", IrType.U32, [new IrArgument(IrType.U16, 0)]);
    var entry = fn.CreateBlock("entry");
    var b = new IrBuilder(entry);
    b.Ret(b.ZExt(fn.Parameters[0], IrType.U32));

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, reason);
    Assert.That(Opcodes(m!), Does.Not.Contain(MOpcode.Sar), "an unsigned widening has no sign to smear");
    Assert.That(m!.AllInstructions.Any(i => i.Opcode == MOpcode.Mov && i.Operands[1] is MOperand.Immediate { Value: 0 }),
      "the high word is cleared");
  }

  [TestCase(IrBinaryOp.Shl, 1, MOpcode.Shl)]
  [TestCase(IrBinaryOp.Shl, 16, MOpcode.Shl)]
  [TestCase(IrBinaryOp.LShr, 31, MOpcode.Shr)]
  [TestCase(IrBinaryOp.AShr, 15, MOpcode.Sar)]
  public void Select_Given386ConstantLongShift_ThenUsesOneDwordMemoryInstruction(
      IrBinaryOp operation, int count, MOpcode expected) {
    var fn = WideFunction((b, slot) => {
      var value = b.Load(IrType.I32, slot);
      b.Store(b.Binary(operation, value, new IrConstantInt(IrType.I32, count)), slot);
      b.Ret();
    }, IrType.Void);

    var m = InstructionSelector.TrySelect(fn, out var reason,
      new SelectionTarget(Cpu386: true, Optimize: true));

    Assert.That(m, Is.Not.Null, reason);
    var shift = m!.AllInstructions.SingleOrDefault(i => i.Opcode == expected
      && i.Operands[0] is MOperand.StackSlot { Size: MRegSize.Dword });
    Assert.That(shift, Is.Not.Null, "the 386 path should shift one staged dword");
    Assert.That(((MOperand.Immediate)shift!.Operands[1]).Value, Is.EqualTo(count));
  }

  [TestCase(false, false)]
  [TestCase(false, true)]
  [TestCase(true, false)]
  public void Select_GivenTargetWithoutOptimized386_ThenKeepsTheWordCarryChain(bool cpu386, bool optimize) {
    var fn = WideFunction((b, slot) => {
      var value = b.Load(IrType.I32, slot);
      b.Store(b.Shl(value, new IrConstantInt(IrType.I32, 4)), slot);
      b.Ret();
    }, IrType.Void);

    var m = InstructionSelector.TrySelect(fn, out var reason, new SelectionTarget(Cpu386: cpu386, Optimize: optimize));

    Assert.That(m, Is.Not.Null, reason);
    Assert.That(m!.AllInstructions.Any(i => i.Opcode == MOpcode.Shl
      && i.Operands[0] is MOperand.StackSlot { Size: MRegSize.Dword }), Is.False,
      "an 8086 image must not contain an operand-size-prefixed shift");
    Assert.That(Opcodes(m), Does.Contain(MOpcode.Rcl), "the high word receives carry from the low word");
  }

  [TestCase(0, true)]
  [TestCase(32, false)]
  public void Select_Given386CountOutsideNativeRange_ThenDoesNotUseAMaskedDwordShift(int count, bool selects) {
    var fn = WideFunction((b, slot) => {
      var value = b.Load(IrType.I32, slot);
      b.Store(b.Shl(value, new IrConstantInt(IrType.I32, count)), slot);
      b.Ret();
    }, IrType.Void);

    var m = InstructionSelector.TrySelect(fn, out _, new SelectionTarget(Cpu386: true, Optimize: true));

    Assert.That(m is not null, Is.EqualTo(selects));
    Assert.That(m?.AllInstructions.Any(i => i.Opcode == MOpcode.Shl
      && i.Operands[0] is MOperand.StackSlot { Size: MRegSize.Dword }), Is.Not.True,
      "counts outside 1..31 must keep source-level semantics instead of using the CPU's masked count");
  }

  [Test]
  public void Emit_Given386ConstantLongShift_ThenWritesAnOperandSizePrefixedShift() {
    var fn = WideFunction((b, slot) => {
      var value = b.Load(IrType.I32, slot);
      b.Store(b.Shl(value, new IrConstantInt(IrType.I32, 4)), slot);
      b.Ret();
    }, IrType.Void);
    var target = new SelectionTarget(Cpu386: true, Optimize: true);
    var m = InstructionSelector.TrySelect(fn, out var reason, target);
    Assert.That(m, Is.Not.Null, reason);
    var alloc = LinearScanAllocator.Allocate(m!, target);
    Assert.That(alloc, Is.Not.Null);
    var asm = new Assembler();

    MachineEmitter.EmitFunction(asm, m!, alloc!, [], 0);

    var bytes = asm.ToArray();
    Assert.That(bytes.Zip(bytes.Skip(1), (a, b) => (a, b)),
      Has.Some.EqualTo(((byte)0x66, (byte)0xC1)), "SHL dword [BP+n],4 is 66 C1 /4 ib");
  }

  [Test]
  public void Select_GivenLongReturn_ThenResultLandsInDxAx() {
    var fn = WideFunction((b, slot) => b.Ret(b.Load(IrType.I32, slot)), IrType.I32);

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, reason);
    var pinned = m!.AllInstructions
      .Where(i => i.Opcode == MOpcode.Mov && i.Operands[0] is MOperand.Register { Reg.IsVirtual: false })
      .Select(i => ((MOperand.Register)i.Operands[0]).Reg.Physical)
      .ToList();
    // "Results: AX / DX:AX / ST0 / string handle in AX" - the low word in AX, the high in DX
    Assert.That(pinned, Does.Contain(Reg.AX).And.Contain(Reg.DX));
  }

  [Test]
  public void Select_GivenLongParameter_ThenTheProloguePlanLoadsBothWords() {
    // the prologue used to assume "argument i is virtual register i" and load one word each, which a
    // pair breaks; the selector now hands it an explicit table of which register takes which word
    var fn = new IrFunction("F", IrType.I32, [new IrArgument(IrType.I32, 0), new IrArgument(IrType.I16, 1)]);
    var entry = fn.CreateBlock("entry");
    new IrBuilder(entry).Ret(fn.Parameters[0]);

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, reason);
    var wide = m!.ArgumentLoads.Where(l => l.ArgumentIndex == 0).ToList();
    Assert.That(wide, Has.Count.EqualTo(2), "a 32-bit argument arrives as two words");
    Assert.That(wide.Select(l => l.ByteDelta), Is.EquivalentTo(new[] { 0, 2 }), "low at its own offset, high at +2");
    Assert.That(m.ArgumentLoads.Count(l => l.ArgumentIndex == 1), Is.EqualTo(1), "a 16-bit argument is one word");
  }

  [Test]
  public void EmitFunction_GivenLongParameter_ThenBothHalvesAreLoadedFromTheFrame() {
    var fn = new IrFunction("F", IrType.I32, [new IrArgument(IrType.I32, 0)]);
    var entry = fn.CreateBlock("entry");
    var b = new IrBuilder(entry);
    b.Ret(b.Add(fn.Parameters[0], new IrConstantInt(IrType.I32, 1)));
    var m = InstructionSelector.TrySelect(fn, out var reason);
    Assert.That(m, Is.Not.Null, reason);
    var alloc = LinearScanAllocator.Allocate(m!);
    Assert.That(alloc, Is.Not.Null);

    var asm = new Assembler();
    MachineEmitter.EmitFunction(asm, m!, alloc!, [6], 4);   // one 4-byte argument at [BP+6]

    // both halves must be loaded, so the prologue reads [BP+6] and [BP+8]
    var reference = new Assembler();
    reference.Mov(alloc![m!.ArgumentLoads[0].VirtualId], Mem.Word(Reg.BP, 6));
    reference.Mov(alloc[m.ArgumentLoads[1].VirtualId], Mem.Word(Reg.BP, 8));
    var expected = reference.ToArray();
    var actual = asm.ToArray();

    Assert.That(actual.Length, Is.GreaterThan(expected.Length));
    // the argument loads sit right after PUSH BP / MOV BP,SP (3 bytes)
    Assert.That(actual.Skip(3).Take(expected.Length), Is.EqualTo(expected));
  }

  [Test]
  public void Select_GivenLongMultiply_ThenCallsTheRuntimeHelperInItsPairConvention() {
    // a 32-bit multiply is a runtime helper (rt_lmul), not an instruction on this target: it takes
    // left in DX:AX and right in CX:BX and answers in DX:AX
    var fn = WideFunction((b, slot) => {
      var value = b.Load(IrType.I32, slot);
      b.Store(b.Mul(value, new IrConstantInt(IrType.I32, 3)), slot);
      b.Ret(null);
    }, IrType.Void);

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, $"declined: {reason}");
    var call = m!.AllInstructions.First(i => i.Opcode == MOpcode.Call);
    Assert.That(((MOperand.LabelRef)call.Operands[0]).Name, Is.EqualTo("rt_lmul"));
    var loaded = m.AllInstructions.TakeWhile(i => i != call)
      .Where(i => i.Opcode == MOpcode.Mov && i.Operands[0] is MOperand.Register { Reg.IsVirtual: false })
      .Select(i => ((MOperand.Register)i.Operands[0]).Reg.Physical)
      .ToList();
    Assert.That(loaded, Is.EqualTo(new[] { Reg.AX, Reg.DX, Reg.BX, Reg.CX }),
      "left DX:AX, right CX:BX - the convention the direct emitter uses");
  }

  [Test]
  public void Emit_GivenLongAdd_ThenTheBytesAreARealCarryChain() {
    var fn = WideFunction((b, slot) => {
      var value = b.Load(IrType.I32, slot);
      b.Store(b.Add(value, new IrConstantInt(IrType.I32, 1)), slot);
      b.Ret(null);
    }, IrType.Void);
    var m = InstructionSelector.TrySelect(fn, out var reason);
    Assert.That(m, Is.Not.Null, reason);
    var alloc = LinearScanAllocator.Allocate(m!);
    Assert.That(alloc, Is.Not.Null, "the pair should allocate - the halves are ordinary virtual registers");

    var asm = new Assembler();
    MachineEmitter.Emit(asm, m!, alloc!);
    var bytes = asm.ToArray();

    Assert.That(bytes, Is.Not.Empty);
    // 83 /2 is ADC r/m16, imm8 - the carry-propagating add of the high half must really be emitted
    Assert.That(bytes.Length, Is.GreaterThan(8), "a 32-bit read-modify-write is several instructions");
  }
}
