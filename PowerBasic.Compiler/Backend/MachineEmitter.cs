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
  private readonly Func<string, Label?>? _resolveCallee;
  private readonly Func<string, Mem?>? _resolveData;
  private readonly int[] _paramOffsets;

  private MachineEmitter(Assembler asm, MFunction function, IReadOnlyDictionary<int, Reg> allocation,
      Func<string, Label?>? resolveCallee = null, Func<string, Mem?>? resolveData = null,
      int[]? paramOffsets = null) {
    this._asm = asm;
    this._allocation = allocation;
    this._resolveCallee = resolveCallee;
    this._resolveData = resolveData;
    this._paramOffsets = paramOffsets ?? [];
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
  /// <param name="resolveCallee">
  /// Maps a called function's name to the <see cref="Label"/> the whole-program codegen bound for it.
  /// A call cannot go through <see cref="Assembler.Lbl"/>: procedure labels are minted with
  /// <see cref="Assembler.DefineLabel(string?)"/>, which is a different registry, so looking the name
  /// up there would create a fresh, never-bound label. The code generator owns the mapping and
  /// supplies it here; a name it does not know is a bug in the routing, not in this emitter.
  /// </param>
  /// <param name="onReturn">
  /// Emitted in place of the epilogue at every return site. The module body needs this: it does not
  /// RET to a caller, it falls into the runtime's exit - so the frame teardown and <c>RET n</c> would
  /// be both wrong and unreachable.
  /// </param>
  public static void EmitFunction(Assembler asm, MFunction function, IReadOnlyDictionary<int, Reg> allocation,
      int[] paramOffsets, int paramBytes, Func<string, Label?>? resolveCallee = null,
      Func<string, Mem?>? resolveData = null, Action<Assembler>? onReturn = null) {
    var emitter = new MachineEmitter(asm, function, allocation, resolveCallee, resolveData, paramOffsets);

    asm.Push(Asm.Reg.BP);
    asm.Mov(Asm.Reg.BP, Asm.Reg.SP);
    var frame = 0;
    foreach (var size in function.StackSlots)
      frame += (size + 1) & ~1;                      // word-aligned space for allocas / spills
    if (frame > 0) {
      asm.Sub(Asm.Reg.SP, (Imm)frame);
      // PB gives every local a zero start, and the frame is where the locals live - the direct path
      // spells this REP STOSW over the whole frame and so does this one. Skipping it is not a size
      // optimization here, it is a miscompile: a SUB with DIM a%(0 TO 49) that writes one element and
      // sums all fifty read forty-nine words of whatever the last call left on the stack. That is
      // exactly how it was found, and it read as plausible numbers rather than as a crash.
      //
      // It has to happen before the arguments are loaded, because it clobbers AX, CX, DI and ES - at
      // this point no allocated register holds anything yet. Spill slots get zeroed along with the
      // allocas; they are written before they are read, so it costs only the instruction.
      asm.Push(Asm.Reg.DS);
      asm.Pop(Asm.Reg.ES);
      asm.Mov(Asm.Reg.DI, Asm.Reg.SP);
      asm.Mov(Asm.Reg.CX, (Imm)(frame / 2));
      asm.Xor(Asm.Reg.AX, Asm.Reg.AX);
      asm.Rep();
      asm.Stosw();
    }

    // the caller pushed the arguments; load each into the register the allocator gave its vreg.
    // A 32-bit argument is two words, so the selector supplies an explicit table; a function selected
    // before that existed (or built by hand in a test) keeps the positional one-word-per-argument form.
    if (function.HasArgumentPlan)
      foreach (var (virtualId, argumentIndex, byteDelta) in function.ArgumentLoads) {
        if (allocation.TryGetValue(virtualId, out var reg))
          asm.Mov(reg, Asm.Mem.Word(Asm.Reg.BP, paramOffsets[argumentIndex] + byteDelta));
      }
    else
      for (var i = 0; i < paramOffsets.Length; ++i)
        asm.Mov(allocation[i], Asm.Mem.Word(Asm.Reg.BP, paramOffsets[i]));

    foreach (var block in function.Blocks) {
      asm.MarkLabel(emitter._labels[block.Label]);
      foreach (var instr in block.Instructions)
        if (instr.Opcode == MOpcode.Ret)
          if (onReturn is not null)
            onReturn(asm);                           // the module body leaves through the runtime's exit
          else
            emitter.EmitEpilogue(paramBytes);        // result already in AX; tear the frame down and RET n
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
      // the high half of a 32-bit add/subtract - it reads the carry the low half left
      case MOpcode.Adc: this.Emit2(ops[0], ops[1], asm.Adc, asm.Adc, asm.Adc, asm.Adc, asm.Adc); break;
      case MOpcode.Sbb: this.Emit2(ops[0], ops[1], asm.Sbb, asm.Sbb, asm.Sbb, asm.Sbb, asm.Sbb); break;
      case MOpcode.Cmp: this.Emit2(ops[0], ops[1], asm.Cmp, asm.Cmp, asm.Cmp, asm.Cmp, asm.Cmp); break;
      case MOpcode.Imul:
        if (this.ToSource(ops[1]) is Mem im)
          asm.Imul(this.Reg(ops[0]), im);
        else
          asm.Imul(this.Reg(ops[0]), this.Reg(ops[1]));
        break;
      case MOpcode.Lea: asm.Lea(this.Reg(ops[0]), this.Mem(ops[1])); break;
      case MOpcode.Cwd: asm.Cwd(); break;
      case MOpcode.Idiv:
        if (this.ToSource(ops[0]) is Mem divisor)
          asm.Idiv(divisor);
        else
          asm.Idiv(this.Reg(ops[0]));
        break;
      // the shifts take a memory destination too, which is what lets a spilled value be shifted in
      // place instead of blocking the whole function's allocation
      case MOpcode.Shl: this.Shift(ops, asm.Shl, asm.Shl); break;
      case MOpcode.Shr: this.Shift(ops, asm.Shr, asm.Shr); break;
      case MOpcode.Sar: this.Shift(ops, asm.Sar, asm.Sar); break;
      // the carry the neighbouring SHL/SHR left is rotated into the other half of a 32-bit shift
      case MOpcode.Rcl: asm.Rcl(this.Reg(ops[0]), (int)((MOperand.Immediate)ops[1]).Value); break;
      case MOpcode.Rcr: asm.Rcr(this.Reg(ops[0]), (int)((MOperand.Immediate)ops[1]).Value); break;
      case MOpcode.Jmp: asm.Jmp(this._labels[((MOperand.LabelRef)ops[0]).Name]); break;
      case MOpcode.JmpIndirect: asm.Jmp(this.Mem(ops[0])); break;
      case MOpcode.Jcc: asm.J(instr.Condition!.Value, this._labels[((MOperand.LabelRef)ops[0]).Name]); break;
      case MOpcode.Call: {
        // with a resolver (the whole-program routing) the callee MUST be one it bound - anything else
        // is a routing bug; without one, the name is an external/runtime symbol resolved by name
        var callee = ((MOperand.LabelRef)ops[0]).Name;
        asm.Call(this._resolveCallee is { } resolve
          ? resolve(callee) ?? throw new System.InvalidOperationException(
              $"no label for callee '{callee}' - the routing admitted a call it cannot bind")
          : asm.Lbl(callee));
        break;
      }
      case MOpcode.Push:
        switch (this.ToSource(ops[0])) {
          case Reg r: asm.Push(r); break;
          case Mem m: asm.Push(m); break;
          case Imm i: asm.Push(i); break;
        }
        break;
      case MOpcode.Ret: asm.Ret(); break;
      // x87: one memory operand, or none for the arithmetic that consumes the loaded pair
      case MOpcode.Fld: asm.Fld(this.Mem(ops[0])); break;
      case MOpcode.Fstp: asm.Fstp(this.Mem(ops[0])); break;
      case MOpcode.Fild: asm.Fild(this.Mem(ops[0])); break;
      case MOpcode.Fistp: asm.Fistp(this.Mem(ops[0])); break;
      case MOpcode.Faddp: asm.Faddp(); break;
      case MOpcode.Fsubp: asm.Fsubp(); break;
      case MOpcode.Fmulp: asm.Fmulp(); break;
      case MOpcode.Fdivp: asm.Fdivp(); break;
      default: throw new System.NotSupportedException($"machine opcode {instr.Opcode} has no emission yet");
    }
  }

  /// <summary>A shift by a constant count, against a register or a frame cell.</summary>
  private void Shift(IReadOnlyList<MOperand> ops, Action<Reg, int> onRegister, Action<Mem, int> onMemory) {
    var count = (int)((MOperand.Immediate)ops[1]).Value;
    if (ops[0] is MOperand.Register register)
      onRegister(this.Resolve(register.Reg), count);
    else
      onMemory(this.Mem(ops[0]), count);
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
    MOperand.Memory or MOperand.StackSlot or MOperand.DataCell or MOperand.ParamCell => this.Mem(operand),
    MOperand.DataOffset o => Imm.OffsetOf(this.DataLabel(o.Name), o.Disp),
    MOperand.BlockOffset b => Imm.OffsetOf(this._labels[b.Block]),
    _ => throw new System.NotSupportedException($"operand {operand} is not a source"),
  };

  private Reg Reg(MOperand operand) => this.Resolve(((MOperand.Register)operand).Reg);

  /// <summary>A module variable's cell, as the whole-program codegen lays it out (plus any word offset).</summary>
  private Mem DataCell(MOperand.DataCell cell) {
    var resolved = this.ResolveData(cell.Name);
    return Sized(Asm.Mem.At(resolved.Label!, resolved.Displacement + cell.Disp), cell.Size);
  }

  /// <summary>The label a named data object was laid out at - the address form, for <c>MOV SI, OFFSET .str0</c>.</summary>
  private Label DataLabel(string name) {
    var resolved = this.ResolveData(name);
    return resolved.Displacement == 0
      ? resolved.Label!
      : throw new System.NotSupportedException($"data object '{name}' is not at its label's own offset");
  }

  private Mem ResolveData(string name)
    => this._resolveData?.Invoke(name)
       ?? throw new System.InvalidOperationException(
         $"no data cell for global '{name}' - the routing admitted a reference it cannot address");

  private Reg Resolve(MReg reg) => reg.IsVirtual ? this._allocation[reg.VirtualId] : reg.Physical;

  private Mem Mem(MOperand operand) => operand switch {
    MOperand.StackSlot slot => Sized(Asm.Mem.At(Asm.Reg.BP, this._slotDisp[slot.Index]), slot.Size),
    MOperand.ParamCell p => Asm.Mem.Word(Asm.Reg.BP, this._paramOffsets[p.ArgumentIndex] + p.ByteDelta),
    MOperand.DataCell cell => this.DataCell(cell),
    MOperand.Memory m when m.Index is { } x => Sized(Asm.Mem.At(this.Resolve(m.Base!.Value), this.Resolve(x), m.Disp), m.Size),
    MOperand.Memory m when m.Base is { } b => Sized(Asm.Mem.At(this.Resolve(b), m.Disp), m.Size),
    MOperand.Memory m => Sized(Asm.Mem.At(m.Disp), m.Size),
    _ => throw new System.NotSupportedException($"operand {operand} is not a memory reference"),
  };

  /// <summary>
  /// Stamps the operand width onto a memory reference. Everything integer here is a word, but an x87
  /// load or store is a dword or a qword and the encoding differs - a SINGLE written through a word
  /// reference would be half a value.
  /// </summary>
  private static Mem Sized(Mem memory, MRegSize size) => size switch {
    MRegSize.Byte => memory.WithSize(OperandSize.Byte),
    MRegSize.Dword => memory.WithSize(OperandSize.Dword),
    MRegSize.Qword => memory.WithSize(OperandSize.Qword),
    _ => memory.WithSize(OperandSize.Word),
  };
}
