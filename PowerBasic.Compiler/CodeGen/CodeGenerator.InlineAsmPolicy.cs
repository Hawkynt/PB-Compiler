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
  private bool TryEmitPolicyInlineAsm(string line, InlineAsmResolver resolver, RuntimeTarget target, out string? error) {
    error = null;
    var instruction = InlineInstruction.Parse(line);
    if (instruction.Mnemonic.Length == 0)
      return false;

    var x87 = IsX87InlineMnemonic(instruction.Mnemonic);
    var required = x87 ? RuntimeCpuFeatures.X87 : RequiredFeature(instruction);
    var policy = this.RuntimeIsaPolicyForRuntime();
    var mode = x87 ? policy.ResolveX87(instruction.Mnemonic) : policy.Resolve(instruction.Mnemonic, required);
    var nativelySupported = required == RuntimeCpuFeatures.None || target.Has(required);

    if (mode == IsaFallbackMode.Error) {
      if (nativelySupported)
        return false;
      error = $"{instruction.Mnemonic} requires {target.DescribeMissing(required)}; ISA policy forbids emulation";
      return true;
    }

    if (mode == IsaFallbackMode.Native) {
      if (this.TryEmitNativeExtendedSimdInstruction(instruction, resolver, out error))
        return true;
      this._textAssembler ??= new(this._asm);
      if (!this._textAssembler.TryParse(line, resolver, out error))
        error = $"native emission failed: {error}";
      return true;
    }

    // Native capability always wins for AUTO. Extended SSSE3/SSE4 instructions use the dedicated
    // 0F38/0F3A encoders because the historical TextAssembler table predates those maps.
    if (mode == IsaFallbackMode.Auto && nativelySupported) {
      if (this.TryEmitNativeExtendedSimdInstruction(instruction, resolver, out error))
        return true;
      return false;
    }

    // A baseline instruction with no feature requirement has no alternate architecture to emulate.
    if (!x87 && required == RuntimeCpuFeatures.None)
      return false;

    if (this.TryEmitVirtualGp32ExtendedInstruction(instruction, resolver, target, out error))
      return true;
    if (this.TryEmitVirtualGp32ArithmeticInstruction(instruction, resolver, target, out error))
      return true;
    if (this.TryEmitVirtualGp32StringInstruction(instruction, target, out error))
      return true;
    if (this.TryEmitVirtualGp32Instruction(instruction, resolver, target, out error))
      return true;

    // PACKSSDW has a subtle signed saturation boundary; keep its exact lowering ahead of the generic
    // packed-SIMD scalarizer so the generic pack implementation can never shadow it.
    if (this.TryEmitVirtualVectorFixup(instruction, resolver, target, out error))
      return true;
    if (this.TryEmitVirtualShuffleInstruction(instruction, resolver, out error))
      return true;
    if (this.TryEmitVirtualSsse3ArithmeticInstruction(instruction, resolver, out error))
      return true;
    if (this.TryEmitVirtualHorizontalInstruction(instruction, resolver, out error))
      return true;
    if (this.TryEmitVirtualSupplementalInstruction(instruction, resolver, out error))
      return true;
    if (this.TryEmitVirtualExtendedInstruction(instruction, resolver, out error))
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
