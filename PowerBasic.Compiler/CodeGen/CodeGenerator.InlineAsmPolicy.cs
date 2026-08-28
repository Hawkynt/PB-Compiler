using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  /// <summary>
  /// Applies an explicit $ISA/$FPU policy before the normal target legality path. AUTO deliberately
  /// returns false so the existing exact compatibility lowerings remain the default behaviour.
  /// </summary>
  private bool TryEmitPolicyInlineAsm(string line, InlineAsmResolver resolver, RuntimeTarget target, out string? error) {
    error = null;
    var instruction = InlineInstruction.Parse(line);
    if (instruction.Mnemonic.Length == 0)
      return false;

    var x87 = IsX87InlineMnemonic(instruction.Mnemonic);
    var required = x87 ? RuntimeCpuFeatures.None : RequiredFeature(instruction);
    var policy = this.RuntimeIsaPolicyForRuntime();
    var mode = x87 ? policy.ResolveX87(instruction.Mnemonic) : policy.Resolve(instruction.Mnemonic, required);
    if (mode == IsaFallbackMode.Auto)
      return false;

    if (mode == IsaFallbackMode.Error) {
      error = $"{instruction.Mnemonic} is disabled by ISA policy";
      return true;
    }

    if (mode == IsaFallbackMode.Native) {
      this._textAssembler ??= new(this._asm);
      if (!this._textAssembler.TryParse(line, resolver, out error))
        error = $"native emission failed: {error}";
      return true;
    }

    // EMULATE. Baseline 8086 instructions have nothing to emulate unless an exact mnemonic rule
    // selected them; keeping them native avoids turning `$ISA DEFAULT EMULATE` into an interpreter.
    if (!x87 && required == RuntimeCpuFeatures.None)
      return false;

    if (this.TryEmitVirtualInstruction(instruction, resolver, target, out error))
      return true;

    // Reuse the existing exact scalar lowerings (MOVSD, CMOVcc, BSWAP, 8->16 MOVZX/MOVSX). Mask
    // only the requested feature so a forced-emulation test on newer hardware still reaches them.
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
