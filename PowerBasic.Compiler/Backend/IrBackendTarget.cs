using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Backend.Targets;

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
  public static IMachineTarget? CreateMachineTarget(IrBackendTarget target) => target switch {
    IrBackendTarget.Mos6502 => new Mos6502MachineTarget(),
    IrBackendTarget.X86_32 => new X86MachineTarget(X86Mode.Bit32, X86Abi.I386Cdecl),
    IrBackendTarget.X86_64 => new X86MachineTarget(X86Mode.Bit64, X86Abi.SysV64),
    _ => null,
  };

  public static IrRepresentationStage RequiredInputStage(IrBackendTarget target) => target switch {
    IrBackendTarget.C or IrBackendTarget.PowerBasic35 or IrBackendTarget.X86_16
      or IrBackendTarget.X86_32 or IrBackendTarget.X86_64 => IrRepresentationStage.OptimizedSsa,
    IrBackendTarget.Mos6502 => IrRepresentationStage.LowIr,
    _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
  };
}
