using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Backend;

/// <summary>
/// Stage 5 of the x86-16 back end (docs/X86-BACKEND.md): emission. Given a selected
/// <see cref="MFunction"/> and the linear-scan allocation (stage 4), it rewrites every virtual
/// register operand to its physical register, resolves each stack slot to a <c>[BP+disp]</c> frame
/// cell, and emits the instruction through the existing <see cref="Assembler"/> - so encoding, length
/// and fixups are handled there (no byte patching, the reason the asm-IL layer avoids the byte-level
/// renaming walls). This emits the instruction body; the calling-convention prologue/epilogue and the
/// wiring into the whole-program codegen are the integration step that follows.
/// </summary>
public sealed class MachineEmitter {

  private readonly Assembler _asm;
  private readonly IReadOnlyDictionary<int, Reg> _allocation;
  private readonly int[] _slotDisp;
  private readonly Dictionary<string, Label> _labels = [];

  private MachineEmitter(Assembler asm, MFunction function, IReadOnlyDictionary<int, Reg> allocation) {
    this._asm = asm;
    this._allocation = allocation;
    // lay the stack slots out below BP: slot k lives at [BP - offset], word-aligned
    this._slotDisp = new int[function.StackSlots.Count];
    var running = 0;
    for (var k = 0; k < function.StackSlots.Count; ++k) {
      running += (function.StackSlots[k] + 1) & ~1;   // round each slot up to an even size
      this._slotDisp[k] = -running;
    }
    // one assembler label per block, so branches can target them
    foreach (var block in function.Blocks)
      this._labels[block.Label] = asm.DefineLabel(block.Label);
  }

  /// <summary>Emits the body of <paramref name="function"/> into <paramref name="asm"/> using the given register allocation.</summary>
  public static void Emit(Assembler asm, MFunction function, IReadOnlyDictionary<int, Reg> allocation) {
    var emitter = new MachineEmitter(asm, function, allocation);
    foreach (var block in function.Blocks) {
      asm.MarkLabel(emitter._labels[block.Label]);
      foreach (var instr in block.Instructions)
        emitter.EmitInstruction(instr);
    }
  }

  /// <summary>
  /// Emits a complete function with the standard PowerBASIC stack ABI: a <c>PUSH BP; MOV BP,SP</c>
  /// prologue (matching the caller's frame view, so <paramref name="paramOffsets"/> - the existing
  /// codegen's <c>[BP+disp]</c> for each parameter - are valid), the incoming arguments loaded into
  /// their allocated registers, the body, and an epilogue that returns the result in <c>AX</c> and
  /// cleans <paramref name="paramBytes"/> of arguments (<c>RET n</c>). The body's IrRet already moved
  /// the result into AX, so each return site falls into the shared epilogue sequence.
  /// </summary>
  public static void EmitFunction(Assembler asm, MFunction function, IReadOnlyDictionary<int, Reg> allocation,
      int[] paramOffsets, int paramBytes) {
    var emitter = new MachineEmitter(asm, function, allocation);

    asm.Push(Asm.Reg.BP);
    asm.Mov(Asm.Reg.BP, Asm.Reg.SP);
    var frame = 0;
    foreach (var size in function.StackSlots)
      frame += (size + 1) & ~1;                      // word-aligned space for allocas / spills
    if (frame > 0)
      asm.Sub(Asm.Reg.SP, (Imm)frame);

    // the caller pushed the arguments; load each into the register the allocator gave its vreg
    for (var i = 0; i < paramOffsets.Length; ++i)
      asm.Mov(allocation[i], Asm.Mem.Word(Asm.Reg.BP, paramOffsets[i]));

    foreach (var block in function.Blocks) {
      asm.MarkLabel(emitter._labels[block.Label]);
      foreach (var instr in block.Instructions)
        if (instr.Opcode == MOpcode.Ret)
          emitter.EmitEpilogue(paramBytes);          // result already in AX; tear the frame down and RET n
        else
          emitter.EmitInstruction(instr);
    }
  }

  private void EmitEpilogue(int paramBytes) {
    this._asm.Mov(Asm.Reg.SP, Asm.Reg.BP);
    this._asm.Pop(Asm.Reg.BP);
    if (paramBytes > 0)
      this._asm.Ret((ushort)paramBytes);
    else
      this._asm.Ret();
  }

  private void EmitInstruction(MInstr instr) {
    var asm = this._asm;
    var ops = instr.Operands;
    switch (instr.Opcode) {
      case MOpcode.Mov: this.Emit2(ops[0], ops[1], asm.Mov, asm.Mov, asm.Mov, asm.Mov, asm.Mov); break;
      case MOpcode.Add: this.Emit2(ops[0], ops[1], asm.Add, asm.Add, asm.Add, asm.Add, asm.Add); break;
      case MOpcode.Sub: this.Emit2(ops[0], ops[1], asm.Sub, asm.Sub, asm.Sub, asm.Sub, asm.Sub); break;
      case MOpcode.And: this.Emit2(ops[0], ops[1], asm.And, asm.And, asm.And, asm.And, asm.And); break;
      case MOpcode.Or: this.Emit2(ops[0], ops[1], asm.Or, asm.Or, asm.Or, asm.Or, asm.Or); break;
      case MOpcode.Xor: this.Emit2(ops[0], ops[1], asm.Xor, asm.Xor, asm.Xor, asm.Xor, asm.Xor); break;
      case MOpcode.Cmp: this.Emit2(ops[0], ops[1], asm.Cmp, asm.Cmp, asm.Cmp, asm.Cmp, asm.Cmp); break;
      case MOpcode.Imul:
        if (this.ToSource(ops[1]) is Mem im)
          asm.Imul(this.Reg(ops[0]), im);
        else
          asm.Imul(this.Reg(ops[0]), this.Reg(ops[1]));
        break;
      case MOpcode.Lea: asm.Lea(this.Reg(ops[0]), this.Mem(ops[1])); break;
      case MOpcode.Shl: asm.Shl(this.Reg(ops[0]), (int)((MOperand.Immediate)ops[1]).Value); break;
      case MOpcode.Shr: asm.Shr(this.Reg(ops[0]), (int)((MOperand.Immediate)ops[1]).Value); break;
      case MOpcode.Sar: asm.Sar(this.Reg(ops[0]), (int)((MOperand.Immediate)ops[1]).Value); break;
      case MOpcode.Jmp: asm.Jmp(this._labels[((MOperand.LabelRef)ops[0]).Name]); break;
      case MOpcode.Jcc: asm.J(instr.Condition!.Value, this._labels[((MOperand.LabelRef)ops[0]).Name]); break;
      case MOpcode.Ret: asm.Ret(); break;
      default: throw new System.NotSupportedException($"machine opcode {instr.Opcode} has no emission yet");
    }
  }

  /// <summary>Dispatches a two-operand instruction to the right Assembler overload by the operand shapes.</summary>
  private void Emit2(MOperand dest, MOperand src,
      System.Action<Reg, Reg> rr, System.Action<Reg, Mem> rm, System.Action<Mem, Reg> mr,
      System.Action<Reg, Imm> ri, System.Action<Mem, Imm> mi) {
    if (dest is MOperand.Register dr) {
      var d = this.Resolve(dr.Reg);
      switch (this.ToSource(src)) {
        case Reg s: rr(d, s); break;
        case Mem m: rm(d, m); break;
        case Imm i: ri(d, i); break;
      }
    } else {
      var m = this.Mem(dest);
      switch (this.ToSource(src)) {
        case Reg s: mr(m, s); break;
        case Imm i: mi(m, i); break;
        default: throw new System.NotSupportedException("memory-to-memory machine instruction");
      }
    }
  }

  // an operand as the Reg / Mem / Imm the Assembler consumes (registers resolved virtual -> physical)
  private object ToSource(MOperand operand) => operand switch {
    MOperand.Register r => (object)this.Resolve(r.Reg),
    MOperand.Immediate i => (Imm)(int)i.Value,
    MOperand.Memory or MOperand.StackSlot => this.Mem(operand),
    _ => throw new System.NotSupportedException($"operand {operand} is not a source"),
  };

  private Reg Reg(MOperand operand) => this.Resolve(((MOperand.Register)operand).Reg);

  private Reg Resolve(MReg reg) => reg.IsVirtual ? this._allocation[reg.VirtualId] : reg.Physical;

  private Mem Mem(MOperand operand) => operand switch {
    MOperand.StackSlot slot => Asm.Mem.Word(Asm.Reg.BP, this._slotDisp[slot.Index]),
    MOperand.Memory m when m.Index is { } x => Asm.Mem.Word(this.Resolve(m.Base!.Value), this.Resolve(x), m.Disp),
    MOperand.Memory m when m.Base is { } b => Asm.Mem.Word(this.Resolve(b), m.Disp),
    MOperand.Memory m => Asm.Mem.Word(m.Disp),
    _ => throw new System.NotSupportedException($"operand {operand} is not a memory reference"),
  };
}
