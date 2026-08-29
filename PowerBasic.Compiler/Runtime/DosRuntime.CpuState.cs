using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class DosRuntime {
  /// <summary>Turns on the CPU state promised by the compile-time target before user/runtime SIMD executes.</summary>
  private void EmitTargetCpuStateInit(Assembler asm) {
    if (!this.Target.HasSse)
      return;
    asm.EnableExtendedVectorState(this.Target.HasAvx, this.Target.HasAvx512);
  }
}
