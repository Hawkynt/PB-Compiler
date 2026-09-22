using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Backend;

/// <summary>Output target selected after the shared IR middle end.</summary>
public enum IrBackendTarget {
  Mos6502,
  X86_16,
  X86_32,
  X86_64,
  C,
  PowerBasic35,
}

/// <summary>Input-stage contract for the shared target emitters.</summary>
public static class IrBackendTargetContract {
  public static IrRepresentationStage RequiredInputStage(IrBackendTarget target) => target switch {
    IrBackendTarget.C or IrBackendTarget.PowerBasic35 or IrBackendTarget.X86_16
      or IrBackendTarget.X86_32 or IrBackendTarget.X86_64 => IrRepresentationStage.OptimizedSsa,
    IrBackendTarget.Mos6502 => IrRepresentationStage.LowIr,
    _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
  };
}
