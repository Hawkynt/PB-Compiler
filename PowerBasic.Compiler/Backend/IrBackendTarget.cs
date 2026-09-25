using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Backend.Targets;

namespace PowerBasic.Compiler.Backend;

/// <summary>Output target selected after the shared IR middle end.</summary>
public enum IrBackendTarget {
  Mos6502,
  X86_16,
  C,
  Llvm,
  PowerBasic35,
}

/// <summary>Input-stage contract for the shared target emitters.</summary>
public static class IrBackendTargetContract {
  public static SelectionTarget SelectionTarget(IrBackendOptions options) => options.Target switch {
    IrBackendTarget.X86_16 => new(options.Optimize, options.OptimizeForSpeed, options.OptimizeForSize, CpuLevel: 86, TargetFamily: MachineTargetFamily.X86_16),
    _ => global::PowerBasic.Compiler.Backend.SelectionTarget.Baseline,
  };

  public static IMachineTarget? CreateMachineTarget(IrBackendTarget target) => target switch {
    IrBackendTarget.Mos6502 => new Mos6502MachineTarget(),
    _ => null,
  };

  public static IrRepresentationStage RequiredInputStage(IrBackendTarget target) => target switch {
    IrBackendTarget.C or IrBackendTarget.Llvm or IrBackendTarget.PowerBasic35 or IrBackendTarget.X86_16
      or IrBackendTarget.Mos6502
      => IrRepresentationStage.LowIr,
    _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
  };
}
