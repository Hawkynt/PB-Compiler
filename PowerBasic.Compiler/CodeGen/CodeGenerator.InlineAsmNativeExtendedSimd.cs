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
        this.EmitNativeExtendedSimdRegReg(mnemonic, destination.Register, source.Register);
        return true;
      case TextAssembler.ParsedAsmMemory source:
        this.EmitNativeExtendedSimdRegMem(mnemonic, destination.Register, source.Memory);
        return true;
      default:
        error = $"{mnemonic} expects register or memory source";
        return true;
    }
  }

  private void EmitNativeExtendedSimdRegReg(string mnemonic, Reg d, Reg s) {
    switch (mnemonic) {
      case "PABSB": this._asm.Pabsb(d, s); break; case "PABSW": this._asm.Pabsw(d, s); break; case "PABSD": this._asm.Pabsd(d, s); break;
      case "PSHUFB": this._asm.Pshufb(d, s); break;
      case "PHADDW": this._asm.Phaddw(d, s); break; case "PHADDD": this._asm.Phaddd(d, s); break; case "PHADDSW": this._asm.Phaddsw(d, s); break;
      case "PHSUBW": this._asm.Phsubw(d, s); break; case "PHSUBD": this._asm.Phsubd(d, s); break; case "PHSUBSW": this._asm.Phsubsw(d, s); break;
      case "PMADDUBSW": this._asm.Pmaddubsw(d, s); break; case "PMULHRSW": this._asm.Pmulhrsw(d, s); break;
      case "PSIGNB": this._asm.Psignb(d, s); break; case "PSIGNW": this._asm.Psignw(d, s); break; case "PSIGND": this._asm.Psignd(d, s); break;
      case "PMULLD": this._asm.Pmulld(d, s); break;
      case "PMINSB": this._asm.Pminsb(d, s); break; case "PMAXSB": this._asm.Pmaxsb(d, s); break;
      case "PMINUW": this._asm.Pminuw(d, s); break; case "PMAXUW": this._asm.Pmaxuw(d, s); break;
      case "PMINUD": this._asm.Pminud(d, s); break; case "PMAXUD": this._asm.Pmaxud(d, s); break;
      case "PCMPEQQ": this._asm.Pcmpeqq(d, s); break; case "PACKUSDW": this._asm.Packusdw(d, s); break; case "PHMINPOSUW": this._asm.Phminposuw(d, s); break;
      case "PCMPGTQ": this._asm.Pcmpgtq(d, s); break;
    }
  }

  private void EmitNativeExtendedSimdRegMem(string mnemonic, Reg d, Mem s) {
    switch (mnemonic) {
      case "PABSB": this._asm.Pabsb(d, s); break; case "PABSW": this._asm.Pabsw(d, s); break; case "PABSD": this._asm.Pabsd(d, s); break;
      case "PSHUFB": this._asm.Pshufb(d, s); break;
      case "PHADDW": this._asm.Phaddw(d, s); break; case "PHADDD": this._asm.Phaddd(d, s); break; case "PHADDSW": this._asm.Phaddsw(d, s); break;
      case "PHSUBW": this._asm.Phsubw(d, s); break; case "PHSUBD": this._asm.Phsubd(d, s); break; case "PHSUBSW": this._asm.Phsubsw(d, s); break;
      case "PMADDUBSW": this._asm.Pmaddubsw(d, s); break; case "PMULHRSW": this._asm.Pmulhrsw(d, s); break;
      case "PSIGNB": this._asm.Psignb(d, s); break; case "PSIGNW": this._asm.Psignw(d, s); break; case "PSIGND": this._asm.Psignd(d, s); break;
      case "PMULLD": this._asm.Pmulld(d, s); break;
      case "PMINSB": this._asm.Pminsb(d, s); break; case "PMAXSB": this._asm.Pmaxsb(d, s); break;
      case "PMINUW": this._asm.Pminuw(d, s); break; case "PMAXUW": this._asm.Pmaxuw(d, s); break;
      case "PMINUD": this._asm.Pminud(d, s); break; case "PMAXUD": this._asm.Pmaxud(d, s); break;
      case "PCMPEQQ": this._asm.Pcmpeqq(d, s); break; case "PACKUSDW": this._asm.Packusdw(d, s); break; case "PHMINPOSUW": this._asm.Phminposuw(d, s); break;
      case "PCMPGTQ": this._asm.Pcmpgtq(d, s); break;
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
        this.EmitNativeExtendedSimdImmediateReg(mnemonic, destination.Register, source.Register, control);
        return true;
      case TextAssembler.ParsedAsmMemory source:
        this.EmitNativeExtendedSimdImmediateMem(mnemonic, destination.Register, source.Memory, control);
        return true;
      default:
        error = $"{mnemonic} expects register or memory second operand";
        return true;
    }
  }

  private void EmitNativeExtendedSimdImmediateReg(string mnemonic, Reg d, Reg s, byte imm) {
    switch (mnemonic) {
      case "PALIGNR": this._asm.Palignr(d, s, imm); break;
      case "PBLENDW": this._asm.Pblendw(d, s, imm); break;
      case "PCMPESTRI": this._asm.Pcmpestri(d, s, imm); break; case "PCMPESTRM": this._asm.Pcmpestrm(d, s, imm); break;
      case "PCMPISTRI": this._asm.Pcmpistri(d, s, imm); break; case "PCMPISTRM": this._asm.Pcmpistrm(d, s, imm); break;
    }
  }

  private void EmitNativeExtendedSimdImmediateMem(string mnemonic, Reg d, Mem s, byte imm) {
    switch (mnemonic) {
      case "PALIGNR": this._asm.Palignr(d, s, imm); break;
      case "PBLENDW": this._asm.Pblendw(d, s, imm); break;
      case "PCMPESTRI": this._asm.Pcmpestri(d, s, imm); break; case "PCMPESTRM": this._asm.Pcmpestrm(d, s, imm); break;
      case "PCMPISTRI": this._asm.Pcmpistri(d, s, imm); break; case "PCMPISTRM": this._asm.Pcmpistrm(d, s, imm); break;
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
