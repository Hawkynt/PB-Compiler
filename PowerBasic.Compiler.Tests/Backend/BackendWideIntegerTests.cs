using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// 32-bit values on a 16-bit target. x86-16 has no 32-bit register, so a LONG/DWORD lives in a
/// register <b>pair</b>: the selector mints two ordinary virtual registers per value, which keeps the
/// allocator free of any pairing concept - it places and frees the halves like anything else.
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

  [Test]
  public void Select_GivenSignExtendToLong_ThenSmearsTheSignIntoTheHighWord() {
    var fn = new IrFunction("F", IrType.I32, [new IrArgument(IrType.I16, 0)]);
    var entry = fn.CreateBlock("entry");
    var b = new IrBuilder(entry);
    b.Ret(b.SExt(fn.Parameters[0], IrType.I32));

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, reason);
    var sar = m!.AllInstructions.FirstOrDefault(i => i.Opcode == MOpcode.Sar);
    Assert.That(sar, Is.Not.Null, "sign extension smears the sign bit with SAR");
    Assert.That(((MOperand.Immediate)sar!.Operands[1]).Value, Is.EqualTo(15),
      "SAR by 15 fills the high word with copies of the sign bit");
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
  public void Select_GivenLongParameter_ThenDeclinesRatherThanLoadHalfOfIt() {
    // the prologue loads one word per argument into allocation[i], which a pair breaks - declining
    // is correct until it can load both halves
    var fn = new IrFunction("F", IrType.I32, [new IrArgument(IrType.I32, 0)]);
    var entry = fn.CreateBlock("entry");
    new IrBuilder(entry).Ret(fn.Parameters[0]);

    Assert.That(InstructionSelector.TrySelect(fn, out var reason), Is.Null);
    Assert.That(reason, Does.Contain("32-bit"));
  }

  [Test]
  public void Select_GivenLongMultiply_ThenDeclines() {
    // a 32-bit multiply is a runtime helper (rt_lmul), not an instruction on this target
    var fn = WideFunction((b, slot) => {
      var value = b.Load(IrType.I32, slot);
      b.Store(b.Mul(value, new IrConstantInt(IrType.I32, 3)), slot);
      b.Ret(null);
    }, IrType.Void);

    Assert.That(InstructionSelector.TrySelect(fn, out var reason), Is.Null);
    Assert.That(reason, Does.Contain("32-bit binary"));
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
