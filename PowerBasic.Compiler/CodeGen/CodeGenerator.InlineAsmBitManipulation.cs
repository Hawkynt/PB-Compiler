using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  /// <summary>Feature requirement for scalar bit-manipulation instructions handled outside TextAssembler.</summary>
  private static RuntimeCpuFeatures RequiredBitManipulationFeature(InlineInstruction instruction) => instruction.Mnemonic switch {
    "POPCNT" => RuntimeCpuFeatures.Popcnt,
    _ => RuntimeCpuFeatures.None,
  };

  /// <summary>Native scalar bit-manipulation emission for instructions newer than the historical assembler table.</summary>
  private bool TryEmitNativeBitManipulationInstruction(InlineInstruction instruction, InlineAsmResolver resolver, out string? error) {
    error = null;
    if (instruction.Mnemonic != "POPCNT")
      return false;

    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out error))
      return true;
    return this.EmitNativePopcnt(operands, out error);
  }

  private bool EmitNativePopcnt(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 2 || operands[0] is not TextAssembler.ParsedAsmRegister destination
        || (!destination.Register.IsWord() && !destination.Register.IsDword())) {
      error = "POPCNT expects r16/r32, r/m16/r/m32";
      return true;
    }

    var dword = destination.Register.IsDword();
    switch (operands[1]) {
      case TextAssembler.ParsedAsmRegister source
          when dword ? source.Register.IsDword() : source.Register.IsWord():
        this._asm.Popcnt(destination.Register, source.Register);
        return true;
      case TextAssembler.ParsedAsmMemory source
          when source.Memory.Size == (dword ? OperandSize.Dword : OperandSize.Word):
        this._asm.Popcnt(destination.Register, source.Memory);
        return true;
      default:
        error = $"POPCNT source width must match the {(dword ? "32" : "16")}-bit destination";
        return true;
    }
  }

  /// <summary>
  /// Exact POPCNT emulation. It stages the complete source before touching any temporary state, counts
  /// bits into runtime scratch, reconstructs POPCNT's architectural flags, restores compiler scratch
  /// registers, and only then writes the destination. That ordering also preserves source/destination
  /// overlap and permits SP as a 16-bit destination.
  /// </summary>
  private bool TryEmitVirtualBitManipulationInstruction(InlineInstruction instruction, InlineAsmResolver resolver,
      RuntimeTarget target, out string? error) {
    error = null;
    if (instruction.Mnemonic != "POPCNT")
      return false;

    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out error))
      return true;
    if (operands.Count != 2 || operands[0] is not TextAssembler.ParsedAsmRegister destination
        || (!destination.Register.IsWord() && !destination.Register.IsDword())) {
      error = "POPCNT expects r16/r32, r/m16/r/m32";
      return true;
    }

    var dword = destination.Register.IsDword();
    if (!this.IsPopcntSource(operands[1], dword)) {
      error = $"POPCNT source width must match the {(dword ? "32" : "16")}-bit destination";
      return true;
    }
    if (dword && destination.Register == Reg.ESP && !target.Has32BitGeneralPurpose) {
      error = "POPCNT ESP cannot be emulated on a pre-386 target because SP is the compiler's live stack";
      return true;
    }
    if (dword && operands[1] is TextAssembler.ParsedAsmRegister { Register: Reg.ESP }
        && !target.Has32BitGeneralPurpose) {
      error = "POPCNT cannot read virtual ESP on a pre-386 target";
      return true;
    }

    var state = this.EnsureVirtualIsaState();
    if (dword)
      this.StagePopcntDwordSource(state, operands[1], target);
    else
      this.StagePopcntWordSource(state, operands[1]);

    var low = this.GpScratch(state, GpSourceScratch);
    var high = this.GpScratch(state, GpSourceScratch + 2);
    var count = this.GpScratch(state, GpDestScratch);
    var countHigh = this.GpScratch(state, GpDestScratch + 2);
    var flags = this.GpScratch(state, GpOrigFlagsScratch);

    this._asm.Push(Reg.AX);
    this._asm.Pushf();
    this._asm.Pop(Reg.AX);
    this._asm.Mov(flags, Reg.AX);
    // POPCNT clears OF/SF/AF/CF/PF. ZF is set iff the SOURCE was zero.
    this._asm.And(flags, 0xF72A);
    this._asm.Mov(Reg.AX, low);
    if (dword)
      this._asm.Or(Reg.AX, high);
    else
      this._asm.Test(Reg.AX, Reg.AX);
    var nonzero = this._asm.DefineLabel();
    this._asm.J(Condition.NotEqual, nonzero);
    this._asm.Or(flags, 0x0040);
    this._asm.MarkLabel(nonzero);

    this._asm.Mov(count, 0);
    this._asm.Mov(countHigh, 0);
    EmitPopcntWord(this._asm, low, count);
    if (dword)
      EmitPopcntWord(this._asm, high, count);
    this._asm.Pop(Reg.AX);

    this._asm.Push(flags);
    this._asm.Popf();
    if (!dword) {
      this._asm.Mov(destination.Register, count);
      return true;
    }

    if (destination.Register == Reg.ESP) {
      // Only reachable on 386+: restore all helper stack state and flags first, then make the
      // architectural ESP write the final action exactly as a hardware POPCNT would.
      this._asm.Mov(Reg.ESP, Mem.Dword(state.Scratch, GpDestScratch).Cs());
      return true;
    }

    this.WriteDwordPlace(state, DwordPlace.Of(destination.Register), GpDestScratch, target);
    return true;
  }

  private bool IsPopcntSource(TextAssembler.ParsedAsmOperand source, bool dword) => source switch {
    TextAssembler.ParsedAsmRegister r => dword ? r.Register.IsDword() : r.Register.IsWord(),
    TextAssembler.ParsedAsmMemory m => m.Memory.Size == (dword ? OperandSize.Dword : OperandSize.Word),
    _ => false,
  };

  private void StagePopcntWordSource(VirtualIsaState state, TextAssembler.ParsedAsmOperand source) {
    var scratch = this.GpScratch(state, GpSourceScratch);
    switch (source) {
      case TextAssembler.ParsedAsmRegister register:
        this._asm.Mov(scratch, register.Register);
        return;
      case TextAssembler.ParsedAsmMemory memory:
        this._asm.Push(Reg.AX);
        this._asm.Mov(Reg.AX, memory.Memory.WithSize(OperandSize.Word));
        this._asm.Mov(scratch, Reg.AX);
        this._asm.Pop(Reg.AX);
        return;
      default:
        throw new InvalidOperationException("not a POPCNT word source");
    }
  }

  private void StagePopcntDwordSource(VirtualIsaState state, TextAssembler.ParsedAsmOperand source, RuntimeTarget target) {
    if (source is TextAssembler.ParsedAsmRegister { Register: Reg.ESP }) {
      // Forced software lowering on a 386+ can still read real ESP. The pre-386 case was rejected
      // above because the virtual GP32 model intentionally has no ESP shadow.
      this._asm.Mov(Mem.Dword(state.Scratch, GpSourceScratch).Cs(), Reg.ESP);
      return;
    }
    this.StageDword(state, source, GpSourceScratch, target);
  }

  private static void EmitPopcntWord(Assembler assembler, Mem source, Mem count) {
    // Fixed-width straight-line lowering: no data-dependent branch and no loop counter/register
    // pressure. Each SHR supplies exactly one bit through CF to the following ADC.
    for (var bit = 0; bit < 16; ++bit) {
      assembler.Shr(source, 1);
      assembler.Adc(count, 0);
    }
  }
}
