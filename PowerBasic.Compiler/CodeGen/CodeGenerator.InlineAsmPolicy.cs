using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  /// <summary>
  /// Applies explicit $ISA/$FPU/$FLOAT policy before the normal target legality path. ERROR only
  /// rejects an instruction when the selected hardware cannot execute it. AUTO normally delegates to
  /// the existing target-aware fallbacks; an explicit x87 AUTO ($FLOAT EMULATE) is the historical
  /// hybrid mode and therefore requests software emulation only when no native x87 is guaranteed.
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

    // An ERROR policy means "there must be hardware; don't synthesize a fallback". It is invisible
    // on a target that already has the requested ISA, exactly like a successful static requirement.
    if (mode == IsaFallbackMode.Error) {
      if (nativelySupported)
        return false;
      error = $"{instruction.Mnemonic} requires {target.DescribeMissing(required)}; ISA policy forbids emulation";
      return true;
    }

    if (mode == IsaFallbackMode.Native) {
      this._textAssembler ??= new(this._asm);
      if (!this._textAssembler.TryParse(line, resolver, out error))
        error = $"native emission failed: {error}";
      return true;
    }

    if (mode == IsaFallbackMode.Auto) {
      if (!x87)
        return false;

      // Distinguish PB's explicit $FLOAT EMULATE / $ISA X87 AUTO from the absence of any x87 rule.
      // The latter preserves existing compiler behaviour until the default floating library itself is
      // moved onto the software-x87 substrate.
      var explicitX87Auto = policy.TryGet(instruction.Mnemonic, out _)
        || policy.TryGet("X87", out _)
        || policy.TryGet("FPU", out _);
      if (!explicitX87Auto || nativelySupported)
        return false;
      // Explicit hybrid mode on a no-x87 target falls through to software emulation.
    } else if (mode == IsaFallbackMode.Emulate && nativelySupported && !x87) {
      // EMULATE is intentionally forceable even on capable hardware for regression testing, so do
      // not return native here. This branch exists only to document the distinction from ERROR.
    }

    // EMULATE (or explicit x87 AUTO on a target without x87). Baseline 8086 instructions have
    // nothing to emulate unless an exact rule selected them; avoid turning DEFAULT EMULATE into an
    // interpreter for the whole integer instruction set.
    if (!x87 && required == RuntimeCpuFeatures.None)
      return false;

    if (this.TryEmitVirtualInstruction(instruction, resolver, target, out error))
      return true;

    // Reuse existing exact scalar lowerings (MOVSD, CMOVcc, BSWAP, 8->16 MOVZX/MOVSX). Mask only
    // the requested feature so forced-emulation tests on capable hardware still exercise the fallback.
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
