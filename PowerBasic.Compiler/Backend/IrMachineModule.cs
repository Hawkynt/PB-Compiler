using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend.Targets;

namespace PowerBasic.Compiler.Backend;

/// <summary>The selected and allocated representation of one IR function.</summary>
public sealed record IrMachineFunction(
    Ir.IrFunction Source,
    MFunction Function,
    IReadOnlyDictionary<int, Reg> Allocation,
    MachineTargetDescription Target);

/// <summary>
/// The machine-side compilation product. Selection produces virtual-register machine SSA;
/// allocation and the late machine rewrites produce the machine IR consumed by an emitter.
/// </summary>
public sealed class IrMachineModule(Ir.IrModule source, SelectionTarget target,
    IReadOnlyList<IrMachineFunction> functions) {
  public Ir.IrModule Source { get; } = source;
  public SelectionTarget Target { get; } = target;
  public IReadOnlyList<IrMachineFunction> Functions { get; } = functions;
}
