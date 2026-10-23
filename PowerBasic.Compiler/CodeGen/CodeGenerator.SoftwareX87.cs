using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  /// <summary>
  /// Inline-asm entry point for the canonical software x87 engine. The same instance is installed as
  /// the assembler-wide sink, so after software mode is selected compiler-generated floating point,
  /// runtime floating point and user inline x87 all share one stack/control/status image.
  /// </summary>
  private bool TryEmitSoftwareX87Instruction(InlineInstruction instruction, IAsmSymbolResolver resolver, RuntimeTarget target, out string? error) {
    error = null;
    var engine = this.EnsureSoftwareX87Engine();
    if (!engine.TryEmitInline(instruction.Mnemonic, instruction.Operands, resolver, out error))
      error ??= $"software x87 engine does not implement {instruction.Mnemonic}";
    return true;
  }

  private SoftwareX87Engine? _softwareX87Engine;

  private SoftwareX87Engine EnsureSoftwareX87Engine() {
    var engine = this._softwareX87Engine ??= new SoftwareX87Engine(this._asm, shareRoutines: true);
    this._asm.X87Sink = engine;
    return engine;
  }

  private void ConfigureSoftwareX87(RuntimeTarget target) {
    var policy = this.RuntimeIsaPolicyForRuntime();
    if (!(policy.TryGet("X87", out var mode) || policy.TryGet("FPU", out mode)
        || policy.TryGet("DEFAULT", out mode)))
      return;
    if (mode == IsaFallbackMode.Emulate || mode == IsaFallbackMode.Auto && !target.Has(RuntimeCpuFeatures.X87))
      this.EnsureSoftwareX87Engine();
  }
}
