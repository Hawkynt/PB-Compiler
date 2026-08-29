using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  /// <summary>
  /// Inline-asm entry point for the software x87 backend. The actual instruction emission is shared
  /// with the assembler-wide <see cref="SoftwareX87Backend"/> so compiler-generated floating point
  /// and runtime floating point use exactly the same virtual stack and 80-bit arithmetic engine.
  /// </summary>
  private bool TryEmitSoftwareX87Instruction(InlineInstruction instruction, InlineAsmResolver resolver, RuntimeTarget target, out string? error) {
    error = null;
    var backend = this.EnsureSoftwareX87Backend();
    if (!backend.TryEmitInline(instruction.Mnemonic, instruction.Operands, resolver, out error))
      error ??= $"software x87 backend does not implement {instruction.Mnemonic}";
    return true;
  }

  private SoftwareX87Backend? _softwareX87Backend;

  private SoftwareX87Backend EnsureSoftwareX87Backend() =>
    this._softwareX87Backend ??= new SoftwareX87Backend(this._asm);
}
