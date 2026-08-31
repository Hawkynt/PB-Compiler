using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  /// <summary>
  /// Feature requirements layered on top of the generic operand-width classifier for scalar extension
  /// instructions absent from the historical TextAssembler table. Independent CPUID features stay
  /// independent: POPCNT is not implied by SSE4.2 even though they appeared in the same CPU era.
  /// </summary>
  private static RuntimeCpuFeatures RequiredSupplementalFeature(InlineInstruction instruction) =>
    IsPopcnt(instruction.Mnemonic) ? RuntimeCpuFeatures.Popcnt : RuntimeCpuFeatures.None;

  private static bool IsPopcnt(string mnemonic) => mnemonic == "POPCNT";

  /// <summary>Emits native scalar CPU-extension instructions absent from the legacy text assembler.</summary>
  private bool TryEmitNativeCpuExtensionInstruction(InlineInstruction instruction, InlineAsmResolver resolver, out string? error) {
    error = null;
    if (!IsPopcnt(instruction.Mnemonic))
      return false;

    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out error))
      return true;
    if (operands.Count != 2 || operands[0] is not TextAssembler.ParsedAsmRegister destination
        || !destination.Register.IsWord() && !destination.Register.IsDword()) {
      error = "POPCNT expects a 16- or 32-bit GP register destination and a matching register/memory source";
      return true;
    }

    var width = destination.Register.Size();
    switch (operands[1]) {
      case TextAssembler.ParsedAsmRegister source when source.Register.Size() == width
          && (source.Register.IsWord() || source.Register.IsDword()):
        this._asm.Popcnt(destination.Register, source.Register);
        return true;
      case TextAssembler.ParsedAsmMemory source when source.Memory.Size is OperandSize.None || source.Memory.Size == width:
        this._asm.Popcnt(destination.Register, source.Memory);
        return true;
      default:
        error = $"POPCNT source must match the {width} destination width";
        return true;
    }
  }

  /// <summary>
  /// Exact POPCNT lowering for targets without the POPCNT feature. The source is snapshotted before
  /// any scratch-stack activity, making destructive aliases exact. 32-bit operands reuse the existing
  /// GP32 virtual high-word bank; no second architectural register model is introduced.
  /// </summary>
  private bool TryEmitVirtualPopcntInstruction(InlineInstruction instruction, InlineAsmResolver resolver,
      RuntimeTarget target, out string? error) {
    error = null;
    if (!IsPopcnt(instruction.Mnemonic))
      return false;

    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out error))
      return true;
    if (operands.Count != 2 || operands[0] is not TextAssembler.ParsedAsmRegister destination
        || !destination.Register.IsWord() && !destination.Register.IsDword()) {
      error = "POPCNT emulation expects a 16- or 32-bit GP register destination";
      return true;
    }

    var width = destination.Register.Size();
    if (!TryNormalizePopcntSource(operands[1], width, out var source)) {
      error = $"POPCNT source must be a register or memory operand matching the {width} destination";
      return true;
    }

    if (width == OperandSize.Dword
        && (destination.Register == Reg.ESP || source is TextAssembler.ParsedAsmRegister { Register: Reg.ESP })) {
      error = "32-bit POPCNT emulation cannot virtualize ESP because the compiler stack is live";
      return true;
    }

    var state = this.EnsureVirtualIsaState();
    if (width == OperandSize.Dword)
      this.StageDword(state, source, GpSourceScratch, target);
    else
      this.StagePopcntWord(state, source);

    this.SavePopcntFlags(state);
    this.EmitPopcntCount(state, width == OperandSize.Dword ? 2 : 1);
    this.RestorePopcntFlags(state);

    if (width == OperandSize.Dword) {
      this._asm.Mov(this.GpScratch(state, GpDestScratch + 2), 0);
      this.WriteDwordPlace(state, DwordPlace.Of(destination.Register), GpDestScratch, target);
    } else {
      this._asm.Mov(destination.Register, this.GpScratch(state, GpDestScratch));
    }
    return true;
  }

  private static bool TryNormalizePopcntSource(TextAssembler.ParsedAsmOperand operand, OperandSize width,
      out TextAssembler.ParsedAsmOperand normalized) {
    switch (operand) {
      case TextAssembler.ParsedAsmRegister register
          when register.Register.Size() == width && (register.Register.IsWord() || register.Register.IsDword()):
        normalized = register;
        return true;
      case TextAssembler.ParsedAsmMemory memory when memory.Memory.Size is OperandSize.None || memory.Memory.Size == width:
        normalized = new TextAssembler.ParsedAsmMemory(memory.Memory.WithSize(width));
        return true;
      default:
        normalized = null!;
        return false;
    }
  }

  private void StagePopcntWord(VirtualIsaState state, TextAssembler.ParsedAsmOperand source) {
    var scratch = this.GpScratch(state, GpSourceScratch);
    switch (source) {
      case TextAssembler.ParsedAsmRegister register:
        // In particular, read SP before introducing any compiler-owned pushes.
        this._asm.Mov(scratch, register.Register);
        return;
      case TextAssembler.ParsedAsmMemory memory:
        this._asm.Push(Reg.AX);
        this._asm.Mov(Reg.AX, memory.Memory.WithSize(OperandSize.Word));
        this._asm.Mov(scratch, Reg.AX);
        this._asm.Pop(Reg.AX);
        return;
      default:
        throw new InvalidOperationException("POPCNT word source was not normalized");
    }
  }

  private void SavePopcntFlags(VirtualIsaState state) {
    this._asm.Push(Reg.AX);
    this._asm.Pushf();
    this._asm.Pop(Reg.AX);
    this._asm.Mov(this.GpScratch(state, GpOrigFlagsScratch), Reg.AX);
    this._asm.Pop(Reg.AX);
  }

  private void EmitPopcntCount(VirtualIsaState state, int words) {
    this._asm.Push(Reg.AX);
    this._asm.Push(Reg.BX);
    this._asm.Push(Reg.CX);
    this._asm.Xor(Reg.BX, Reg.BX);

    for (var word = 0; word < words; ++word) {
      this._asm.Mov(Reg.AX, this.GpScratch(state, GpSourceScratch + word * 2));
      this._asm.Mov(Reg.CX, 16);
      var loop = this._asm.DefineLabel();
      this._asm.MarkLabel(loop);
      this._asm.Shr(Reg.AX, 1);
      this._asm.Adc(Reg.BX, 0);
      this._asm.Loop(loop);
    }

    this._asm.Mov(this.GpScratch(state, GpDestScratch), Reg.BX);
    this._asm.Pop(Reg.CX);
    this._asm.Pop(Reg.BX);
    this._asm.Pop(Reg.AX);
  }

  private void RestorePopcntFlags(VirtualIsaState state) {
    // Intel defines all six arithmetic status flags: CF/PF/AF/SF/OF clear, ZF = (SRC == 0).
    // Preserve every non-status bit from the incoming FLAGS image.
    var nonZero = this._asm.DefineLabel();
    this._asm.Push(Reg.AX);
    this._asm.Mov(Reg.AX, this.GpScratch(state, GpOrigFlagsScratch));
    this._asm.And(Reg.AX, 0xF72A); // clear CF/PF/AF/ZF/SF/OF (mask complement of 0x08D5)
    this._asm.Cmp(this.GpScratch(state, GpDestScratch), 0);
    this._asm.J(Condition.NotEqual, nonZero);
    this._asm.Or(Reg.AX, 0x0040);
    this._asm.MarkLabel(nonZero);
    this._asm.Push(Reg.AX);
    this._asm.Popf();
    this._asm.Pop(Reg.AX);
  }
}
