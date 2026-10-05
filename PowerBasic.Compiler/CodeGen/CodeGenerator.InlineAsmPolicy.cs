using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  /// <summary>
  /// Applies explicit $ISA/$FPU/$FLOAT policy before native inline-asm emission. AUTO means native
  /// when the target supports the instruction and the best semantics-preserving lowering otherwise;
  /// ERROR disables that fallback, NATIVE deliberately raises the hardware requirement, and EMULATE
  /// deliberately exercises the software path even on capable hardware.
  /// </summary>
  /// <summary>
  /// Whether a general-purpose instruction works on a LONG variable: <c>DEC n&amp;</c> names no 32-bit
  /// register and says no <c>DWORD PTR</c>, so the text alone looks like 8086 code, yet the variable
  /// makes it a 32-bit operation. Only the resolver knows the operand's size.
  /// </summary>
  private bool NamesDwordVariable(InlineInstruction instruction, IAsmSymbolResolver resolver) {
    if (instruction.Mnemonic is not ("MOV" or "XCHG" or "ADD" or "ADC" or "SUB" or "SBB" or "AND" or "OR" or "XOR"
        or "CMP" or "TEST" or "INC" or "DEC" or "NOT" or "NEG" or "PUSH" or "POP"))
      return false;
    this._textAssembler ??= new(this._asm);
    return this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out _)
      && operands.Any(operand => operand is TextAssembler.ParsedAsmMemory { Memory.Size: OperandSize.Dword });
  }

  private bool TryEmitPolicyInlineAsm(string line, IAsmSymbolResolver resolver, RuntimeTarget target, out string? error) {
    error = null;
    var instruction = InlineInstruction.Parse(line);
    if (instruction.Mnemonic.Length == 0)
      return false;

    if (this.TryReject8086ImmediatePush(instruction, resolver, target, out error))
      return true;

    if (this.TryRejectLegacyCpuForm(instruction, resolver, target, out error))
      return true;

    if (this.TryEmit8086CompatibleShift(instruction, resolver, target, out error))
      return true;

    var x87 = IsX87InlineMnemonic(instruction.Mnemonic);
    var required = x87 ? RuntimeCpuFeatures.X87 : RequiredFeature(instruction);
    if (!x87)
      required |= RequiredBitManipulationFeature(instruction) | RequiredSupplementalFeature(instruction)
        | RequiredCryptoFeature(instruction) | RequiredBmiFeature(instruction);
    if (required == RuntimeCpuFeatures.None && !x87 && this.NamesDwordVariable(instruction, resolver))
      required = RuntimeCpuFeatures.GeneralPurpose32;
    var policy = this.RuntimeIsaPolicyForRuntime();
    var mode = x87 ? policy.ResolveX87(instruction.Mnemonic) : policy.Resolve(instruction.Mnemonic, required);
    var nativelySupported = required == RuntimeCpuFeatures.None || target.Has(required);

    if (mode == IsaFallbackMode.Error) {
      if (!nativelySupported) {
        error = $"{instruction.Mnemonic} requires {target.DescribeMissing(required)}; ISA policy forbids emulation";
        return true;
      }

      // ERROR forbids fallback; it does not bypass dedicated extension encoders. The historical
      // TextAssembler table predates POPCNT/AES/PCLMUL/BMI and the 0F38/0F3A SIMD maps, so supported
      // extensions must still route through their native backends rather than fall through as unknown.
      if (this.TryEmitNativeBitManipulationInstruction(instruction, resolver, out error))
        return true;
      if (this.TryEmitNativeCpuExtensionInstruction(instruction, resolver, out error))
        return true;
      if (this.TryEmitNativeCryptoInstruction(instruction, resolver, out error))
        return true;
      if (this.TryEmitNativeBmiInstruction(instruction, resolver, out error))
        return true;
      if (this.TryEmitNativeExtendedSimdInstruction(instruction, resolver, out error))
        return true;
      return false;
    }

    if (mode == IsaFallbackMode.Native) {
      if (this.TryEmitNativeBitManipulationInstruction(instruction, resolver, out error))
        return true;
      if (this.TryEmitNativeCpuExtensionInstruction(instruction, resolver, out error))
        return true;
      if (this.TryEmitNativeCryptoInstruction(instruction, resolver, out error))
        return true;
      if (this.TryEmitNativeBmiInstruction(instruction, resolver, out error))
        return true;
      if (this.TryEmitNativeExtendedSimdInstruction(instruction, resolver, out error))
        return true;
      this._textAssembler ??= new(this._asm);
      if (!this._textAssembler.TryParse(line, resolver, out error))
        error = $"native emission failed: {error}";
      return true;
    }

    // Once target-policy diagnostics and an explicit NATIVE request have been honoured, SPEED may
    // erase a provable architectural identity. Typed parsing inside the predicate prevents malformed
    // assembly from being accepted merely because its tokens resemble a no-op.
    if (this.Optimize && this.OptimizeSpeed && this.IsZeroOverheadInlineAsmIdentity(instruction, resolver))
      return true;

    // Native capability always wins for AUTO. Extensions absent from the historical TextAssembler
    // table use dedicated encoders so target policy and native byte generation share one resolution.
    if (mode == IsaFallbackMode.Auto && nativelySupported) {
      if (this.TryEmitNativeBitManipulationInstruction(instruction, resolver, out error))
        return true;
      if (this.TryEmitNativeCpuExtensionInstruction(instruction, resolver, out error))
        return true;
      if (this.TryEmitNativeCryptoInstruction(instruction, resolver, out error))
        return true;
      if (this.TryEmitNativeBmiInstruction(instruction, resolver, out error))
        return true;
      if (this.TryEmitNativeExtendedSimdInstruction(instruction, resolver, out error))
        return true;
      return false;
    }

    // A baseline instruction with no feature requirement has no alternate architecture to emulate.
    if (!x87 && required == RuntimeCpuFeatures.None)
      return false;

    if (this.TryEmitVirtualBitManipulationInstruction(instruction, resolver, target, out error))
      return true;
    if (this.TryEmitVirtualPackedStringInstruction(instruction, resolver, target, out error))
      return true;
    if (this.TryEmitVirtualCrc32Instruction(instruction, resolver, target, out error))
      return true;
    if (this.TryEmitVirtualPopcntInstruction(instruction, resolver, target, out error))
      return true;
    if (this.TryEmitVirtualCryptoInstruction(instruction, resolver, target, out error))
      return true;
    if (this.TryEmitVirtualBmiInstruction(instruction, resolver, target, out error))
      return true;
    if (this.TryEmitVirtualGp32ExtendedInstruction(instruction, resolver, target, out error))
      return true;
    if (this.TryEmitVirtualGp32ArithmeticInstruction(instruction, resolver, target, out error))
      return true;
    if (this.TryEmitVirtualGp32StringInstruction(instruction, target, out error))
      return true;
    if (this.TryEmitVirtualGp32Instruction(instruction, resolver, target, out error))
      return true;

    // PACKSSDW has a subtle signed saturation boundary; keep its exact lowering ahead of every generic
    // packed-SIMD scalarizer so no broader emulator can accidentally shadow its boundary semantics.
    if (this.TryEmitVirtualVectorFixup(instruction, resolver, target, out error))
      return true;
    if (this.TryEmitVirtualExtendedVectorInstruction(instruction, resolver, target, out error))
      return true;
    // The horizontal and SSSE3-arithmetic lowerings above cover PHADD/PHSUB/PMADDUBSW/PMULHRSW,
    // which the extended-vector emulator does not implement; every mnemonic both know is left to it.
    if (this.TryEmitVirtualHorizontalInstruction(instruction, resolver, out error))
      return true;
    if (this.TryEmitVirtualSsse3ArithmeticInstruction(instruction, resolver, out error))
      return true;
    if (this.TryEmitVirtualInstruction(instruction, resolver, target, out error))
      return true;

    if (x87 && this.TryEmitSoftwareX87Instruction(instruction, resolver, target, out error))
      return true;

    // Exact scalar lowerings cover forms that do not need persistent virtual architectural state.
    // Mask the requested feature so forced-emulation on capable hardware reaches the same fallback.
    var forcedFeatures = target.Features & ~required;
    var forcedTarget = new RuntimeTarget(target.CpuLevel, forcedFeatures);
    if (this.TryEmitTargetedInlineAsm(line, resolver, forcedTarget, out var exactError)) {
      error ??= exactError;
      return true;
    }

    error ??= x87
      ? "x87 software emulation backend is not available for this instruction"
      : $"no semantics-preserving emulator is registered for {instruction.Mnemonic}";
    return true;
  }

  /// <summary>The 8086 has PUSH r/m16, but immediate and label-offset forms require the 80186.</summary>
  private bool TryReject8086ImmediatePush(InlineInstruction instruction, IAsmSymbolResolver resolver,
      RuntimeTarget target, out string? error) {
    error = null;
    if (target.CpuLevel >= 186 || instruction.Mnemonic != "PUSH")
      return false;

    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out error))
      return true;
    if (operands.Count != 1 || operands[0] is not (TextAssembler.ParsedAsmImmediate or TextAssembler.ParsedAsmLabel))
      return false;

    error = "PUSH with an immediate requires 80186 or later; target is 8086";
    return true;
  }

  /// <summary>
  /// PUSHA/POPA and immediate IMUL start at 80186; two-operand register/memory IMUL starts at 80386.
  /// These forms are encoded directly by TextAssembler, so target legality must be checked first.
  /// </summary>
  private bool TryRejectLegacyCpuForm(InlineInstruction instruction, IAsmSymbolResolver resolver,
      RuntimeTarget target, out string? error) {
    error = null;
    if (target.CpuLevel < 186 && instruction.Mnemonic is ("PUSHA" or "POPA")
        && instruction.Operands.Length == 0) {
      error = $"{instruction.Mnemonic} requires 80186 or later; target is 8086";
      return true;
    }
    if (instruction.Mnemonic != "IMUL" || target.CpuLevel >= 386)
      return false;

    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out error))
      return true;
    if (operands is [TextAssembler.ParsedAsmRegister { Register: var register }, ..] && register.IsDword())
      return false; // the GP32 virtualization below lowers these forms to 8086 instructions

    var requiredCpu = operands switch {
      [TextAssembler.ParsedAsmRegister, TextAssembler.ParsedAsmImmediate] => 186,
      [TextAssembler.ParsedAsmRegister, TextAssembler.ParsedAsmRegister or TextAssembler.ParsedAsmMemory] => 386,
      [TextAssembler.ParsedAsmRegister, TextAssembler.ParsedAsmRegister or TextAssembler.ParsedAsmMemory,
        TextAssembler.ParsedAsmImmediate] => 186,
      _ => 0,
    };
    if (requiredCpu == 0 || target.CpuLevel >= requiredCpu)
      return false;

    var cpuName = requiredCpu == 186 ? "80186" : "80386";
    error = $"IMUL with {operands.Count} operands requires {cpuName} or later; target is {target.CpuLevel}";
    return true;
  }

  /// <summary>
  /// The 8086/8088 have D0/D1 count-one and D2/D3 CL-count shifts/rotates. C0/C1 with an arbitrary
  /// immediate count arrived with the 80186. CL forms stay native; multi-bit immediates are expanded
  /// to repeated count-one operations so an 8086 target never receives a later-generation opcode.
  /// </summary>
  private bool TryEmit8086CompatibleShift(InlineInstruction instruction, IAsmSymbolResolver resolver,
      RuntimeTarget target, out string? error) {
    error = null;
    if (target.CpuLevel >= 186 || !IsLegacyShiftOrRotate(instruction.Mnemonic))
      return false;

    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out error))
      return true;

    // CL is a genuine 8086 form and count=1 is D0/D1. Dword operands belong to GP32 virtualization.
    if (operands.Count != 2 || operands[1] is not TextAssembler.ParsedAsmImmediate immediate || immediate.Value == 1)
      return false;
    if (immediate.Value is < 1 or > 31) {
      error = "shift/rotate count must be 1..31";
      return true;
    }

    switch (operands[0]) {
      case TextAssembler.ParsedAsmRegister { Register: var register } when register.IsByte() || register.IsWord():
        for (var i = 0; i < immediate.Value; ++i)
          this.Emit8086ShiftOne(instruction.Mnemonic, register);
        return true;

      case TextAssembler.ParsedAsmMemory { Memory: var memory }
          when memory.Size is OperandSize.Byte or OperandSize.Word:
        for (var i = 0; i < immediate.Value; ++i)
          this.Emit8086ShiftOne(instruction.Mnemonic, memory);
        return true;

      default:
        return false;
    }
  }

  private static bool IsLegacyShiftOrRotate(string mnemonic) => mnemonic is
    "SHL" or "SAL" or "SHR" or "SAR" or "ROL" or "ROR" or "RCL" or "RCR";

  private void Emit8086ShiftOne(string mnemonic, Reg destination) {
    switch (mnemonic) {
      case "SHL" or "SAL": this._asm.Shl(destination, 1); break;
      case "SHR": this._asm.Shr(destination, 1); break;
      case "SAR": this._asm.Sar(destination, 1); break;
      case "ROL": this._asm.Rol(destination, 1); break;
      case "ROR": this._asm.Ror(destination, 1); break;
      case "RCL": this._asm.Rcl(destination, 1); break;
      case "RCR": this._asm.Rcr(destination, 1); break;
      default: throw new InvalidOperationException($"not a legacy shift/rotate mnemonic: {mnemonic}");
    }
  }

  private void Emit8086ShiftOne(string mnemonic, Mem destination) {
    switch (mnemonic) {
      case "SHL" or "SAL": this._asm.Shl(destination, 1); break;
      case "SHR": this._asm.Shr(destination, 1); break;
      case "SAR": this._asm.Sar(destination, 1); break;
      case "ROL": this._asm.Rol(destination, 1); break;
      case "ROR": this._asm.Ror(destination, 1); break;
      case "RCL": this._asm.Rcl(destination, 1); break;
      case "RCR": this._asm.Rcr(destination, 1); break;
      default: throw new InvalidOperationException($"not a legacy shift/rotate mnemonic: {mnemonic}");
    }
  }

  private static bool IsX87InlineMnemonic(string mnemonic) => mnemonic is
    "FLD" or "FST" or "FSTP" or "FILD" or "FIST" or "FISTP" or
    "FADD" or "FMUL" or "FSUB" or "FSUBR" or "FDIV" or "FDIVR" or
    "FADDP" or "FMULP" or "FSUBP" or "FSUBRP" or "FDIVP" or "FDIVRP" or
    "FIADD" or "FIMUL" or "FISUB" or "FISUBR" or "FIDIV" or "FIDIVR" or "FICOM" or "FICOMP" or
    "FCOM" or "FCOMP" or "FCOMPP" or "FUCOM" or "FUCOMP" or "FUCOMPP" or "FXCH" or "FFREE" or
    "FTST" or "FCHS" or "FABS" or "FSQRT" or "FRNDINT" or "FSCALE" or "FPREM" or "FPREM1" or
    "FPTAN" or "FPATAN" or "F2XM1" or "FYL2X" or "FYL2XP1" or "FSIN" or "FCOS" or "FSINCOS" or
    "FLDZ" or "FLD1" or "FLDPI" or "FLDL2E" or "FLDL2T" or "FLDLG2" or "FLDLN2" or
    "FINIT" or "FNINIT" or "FCLEX" or "FNCLEX" or "FINCSTP" or "FDECSTP" or "FWAIT" or "WAIT" or
    "FSTSW" or "FNSTSW" or "FSTCW" or "FNSTCW" or "FLDCW";
}
