using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  /// <summary>
  /// Emits the 0F38/0F3A SSSE3/SSE4.x surface that is intentionally kept out of the historical
  /// TextAssembler dispatch table. Operand parsing is still shared with TextAssembler, so symbols,
  /// segment overrides and PB memory syntax have one implementation.
  /// </summary>
  private bool TryEmitNativeExtendedSimdInstruction(InlineInstruction instruction, InlineAsmResolver resolver, out string? error) {
    error = null;
    var mnemonic = instruction.Mnemonic;
    if (!IsSsse3(mnemonic) && !IsSse41(mnemonic) && !IsSse42(mnemonic))
      return false;

    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out error))
      return true;

    if (mnemonic == "CRC32")
      return this.EmitNativeCrc32(operands, out error);
    if (mnemonic is "PALIGNR" or "PBLENDW" or "PCMPESTRI" or "PCMPESTRM" or "PCMPISTRI" or "PCMPISTRM")
      return this.EmitNativeExtendedSimdImmediate(mnemonic, operands, out error);
    return this.EmitNativeExtendedSimdBinary(mnemonic, operands, out error);
  }

  private bool EmitNativeExtendedSimdBinary(string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 2 || operands[0] is not TextAssembler.ParsedAsmRegister destination) {
      error = $"{mnemonic} expects register, register/memory";
      return true;
    }

    var allowMmx = IsSsse3(mnemonic);
    if (!(destination.Register.IsXmm() || allowMmx && destination.Register.IsMmx())) {
      error = $"{mnemonic} expects {(allowMmx ? "MMX/XMM" : "XMM")} destination";
      return true;
    }

    switch (operands[1]) {
      case TextAssembler.ParsedAsmRegister source when source.Register.IsXmm() || allowMmx && source.Register.IsMmx():
        if (source.Register.IsMmx() != destination.Register.IsMmx()) { error = "SIMD operand register classes differ"; return true; }
        if (!this.EmitNativeExtendedSimdRegReg(mnemonic, destination.Register, source.Register))
          error = $"native extended-SIMD emitter has no {mnemonic} register lowering";
        return true;
      case TextAssembler.ParsedAsmMemory source:
        if (!this.EmitNativeExtendedSimdRegMem(mnemonic, destination.Register, source.Memory))
          error = $"native extended-SIMD emitter has no {mnemonic} memory lowering";
        return true;
      default:
        error = $"{mnemonic} expects register or memory source";
        return true;
    }
  }

  private bool EmitNativeExtendedSimdRegReg(string mnemonic, Reg d, Reg s) {
    switch (mnemonic) {
      case "PABSB": this._asm.Pabsb(d, s); return true;
      case "PABSW": this._asm.Pabsw(d, s); return true;
      case "PABSD": this._asm.Pabsd(d, s); return true;
      case "PSHUFB": this._asm.Pshufb(d, s); return true;
      case "PHADDW": this._asm.Phaddw(d, s); return true;
      case "PHADDD": this._asm.Phaddd(d, s); return true;
      case "PHADDSW": this._asm.Phaddsw(d, s); return true;
      case "PHSUBW": this._asm.Phsubw(d, s); return true;
      case "PHSUBD": this._asm.Phsubd(d, s); return true;
      case "PHSUBSW": this._asm.Phsubsw(d, s); return true;
      case "PMADDUBSW": this._asm.Pmaddubsw(d, s); return true;
      case "PMULHRSW": this._asm.Pmulhrsw(d, s); return true;
      case "PSIGNB": this._asm.Psignb(d, s); return true;
      case "PSIGNW": this._asm.Psignw(d, s); return true;
      case "PSIGND": this._asm.Psignd(d, s); return true;
      case "PMULLD": this._asm.Pmulld(d, s); return true;
      case "PMINSB": this._asm.Pminsb(d, s); return true;
      case "PMAXSB": this._asm.Pmaxsb(d, s); return true;
      case "PMINUW": this._asm.Pminuw(d, s); return true;
      case "PMAXUW": this._asm.Pmaxuw(d, s); return true;
      case "PMINUD": this._asm.Pminud(d, s); return true;
      case "PMAXUD": this._asm.Pmaxud(d, s); return true;
      case "PCMPEQQ": this._asm.Pcmpeqq(d, s); return true;
      case "PACKUSDW": this._asm.Packusdw(d, s); return true;
      case "PHMINPOSUW": this._asm.Phminposuw(d, s); return true;
      case "PCMPGTQ": this._asm.Pcmpgtq(d, s); return true;
      default: return false;
    }
  }

  private bool EmitNativeExtendedSimdRegMem(string mnemonic, Reg d, Mem s) {
    switch (mnemonic) {
      case "PABSB": this._asm.Pabsb(d, s); return true;
      case "PABSW": this._asm.Pabsw(d, s); return true;
      case "PABSD": this._asm.Pabsd(d, s); return true;
      case "PSHUFB": this._asm.Pshufb(d, s); return true;
      case "PHADDW": this._asm.Phaddw(d, s); return true;
      case "PHADDD": this._asm.Phaddd(d, s); return true;
      case "PHADDSW": this._asm.Phaddsw(d, s); return true;
      case "PHSUBW": this._asm.Phsubw(d, s); return true;
      case "PHSUBD": this._asm.Phsubd(d, s); return true;
      case "PHSUBSW": this._asm.Phsubsw(d, s); return true;
      case "PMADDUBSW": this._asm.Pmaddubsw(d, s); return true;
      case "PMULHRSW": this._asm.Pmulhrsw(d, s); return true;
      case "PSIGNB": this._asm.Psignb(d, s); return true;
      case "PSIGNW": this._asm.Psignw(d, s); return true;
      case "PSIGND": this._asm.Psignd(d, s); return true;
      case "PMULLD": this._asm.Pmulld(d, s); return true;
      case "PMINSB": this._asm.Pminsb(d, s); return true;
      case "PMAXSB": this._asm.Pmaxsb(d, s); return true;
      case "PMINUW": this._asm.Pminuw(d, s); return true;
      case "PMAXUW": this._asm.Pmaxuw(d, s); return true;
      case "PMINUD": this._asm.Pminud(d, s); return true;
      case "PMAXUD": this._asm.Pmaxud(d, s); return true;
      case "PCMPEQQ": this._asm.Pcmpeqq(d, s); return true;
      case "PACKUSDW": this._asm.Packusdw(d, s); return true;
      case "PHMINPOSUW": this._asm.Phminposuw(d, s); return true;
      case "PCMPGTQ": this._asm.Pcmpgtq(d, s); return true;
      default: return false;
    }
  }

  private bool EmitNativeExtendedSimdImmediate(string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 3 || operands[0] is not TextAssembler.ParsedAsmRegister destination
        || operands[2] is not TextAssembler.ParsedAsmImmediate immediate) {
      error = $"{mnemonic} expects register, register/memory, imm8";
      return true;
    }
    var allowMmx = mnemonic == "PALIGNR";
    if (!(destination.Register.IsXmm() || allowMmx && destination.Register.IsMmx())) {
      error = $"{mnemonic} expects {(allowMmx ? "MMX/XMM" : "XMM")} first operand";
      return true;
    }
    var control = unchecked((byte)immediate.Value);
    switch (operands[1]) {
      case TextAssembler.ParsedAsmRegister source when source.Register.IsXmm() || allowMmx && source.Register.IsMmx():
        if (source.Register.IsMmx() != destination.Register.IsMmx()) { error = "SIMD operand register classes differ"; return true; }
        if (!this.EmitNativeExtendedSimdImmediateReg(mnemonic, destination.Register, source.Register, control))
          error = $"native extended-SIMD emitter has no {mnemonic} immediate register lowering";
        return true;
      case TextAssembler.ParsedAsmMemory source:
        if (!this.EmitNativeExtendedSimdImmediateMem(mnemonic, destination.Register, source.Memory, control))
          error = $"native extended-SIMD emitter has no {mnemonic} immediate memory lowering";
        return true;
      default:
        error = $"{mnemonic} expects register or memory second operand";
        return true;
    }
  }

  private bool EmitNativeExtendedSimdImmediateReg(string mnemonic, Reg d, Reg s, byte imm) {
    switch (mnemonic) {
      case "PALIGNR": this._asm.Palignr(d, s, imm); return true;
      case "PBLENDW": this._asm.Pblendw(d, s, imm); return true;
      case "PCMPESTRI": this._asm.Pcmpestri(d, s, imm); return true;
      case "PCMPESTRM": this._asm.Pcmpestrm(d, s, imm); return true;
      case "PCMPISTRI": this._asm.Pcmpistri(d, s, imm); return true;
      case "PCMPISTRM": this._asm.Pcmpistrm(d, s, imm); return true;
      default: return false;
    }
  }

  private bool EmitNativeExtendedSimdImmediateMem(string mnemonic, Reg d, Mem s, byte imm) {
    switch (mnemonic) {
      case "PALIGNR": this._asm.Palignr(d, s, imm); return true;
      case "PBLENDW": this._asm.Pblendw(d, s, imm); return true;
      case "PCMPESTRI": this._asm.Pcmpestri(d, s, imm); return true;
      case "PCMPESTRM": this._asm.Pcmpestrm(d, s, imm); return true;
      case "PCMPISTRI": this._asm.Pcmpistri(d, s, imm); return true;
      case "PCMPISTRM": this._asm.Pcmpistrm(d, s, imm); return true;
      default: return false;
    }
  }

  private bool EmitNativeCrc32(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    error = null;
    if (operands.Count != 2 || operands[0] is not TextAssembler.ParsedAsmRegister destination || !destination.Register.IsDword()) {
      error = "CRC32 expects a 32-bit GP destination";
      return true;
    }
    switch (operands[1]) {
      case TextAssembler.ParsedAsmRegister source when source.Register.IsByte(): this._asm.Crc32Byte(destination.Register, source.Register); return true;
      case TextAssembler.ParsedAsmRegister source when source.Register.IsWord() || source.Register.IsDword(): this._asm.Crc32(destination.Register, source.Register); return true;
      case TextAssembler.ParsedAsmMemory source when source.Memory.Size == OperandSize.Byte: this._asm.Crc32Byte(destination.Register, source.Memory); return true;
      case TextAssembler.ParsedAsmMemory source when source.Memory.Size is OperandSize.Word or OperandSize.Dword: this._asm.Crc32(destination.Register, source.Memory); return true;
      default: error = "CRC32 source must be byte/word/dword register or explicitly sized memory"; return true;
    }
  }
}
