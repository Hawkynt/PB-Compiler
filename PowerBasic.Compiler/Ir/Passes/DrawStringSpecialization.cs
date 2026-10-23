using System.Text;
using PowerBasic.Compiler.Semantics;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>Expands DRAW text proved constant by the middle end, after consuming string folds.</summary>
public static class DrawStringSpecialization {
  private static readonly string[] _CELLS = ["rt_gx1", "rt_gy1", "rt_gx2", "rt_gy2", "rt_gstyle", "rt_gcolor"];

  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    // Runtime storage/entries must not silently acquire a different type or implementation.
    if (_CELLS.Any(name => module.FindGlobal(name) is { ValueType: var type } && type != IrType.I16)
        || module.FindFunction("rt_line") is { } line
          && (!line.IsDeclaration || line.Convention != IrCallConvention.Basic
            || !line.ReturnType.IsVoid || line.Parameters.Count != 0))
      return 0;
    var changed = 0;
    foreach (var function in module.Functions.Where(function => !function.IsDeclaration).ToArray()) {
      if (function.HasErrorHandler || function.HasInlineAsm)
        continue;
      foreach (var call in function.AllInstructions.OfType<IrCall>().ToArray())
        changed += TryExpand(module, call) ? 1 : 0;
    }
    return changed;
  }

  private static bool TryExpand(IrModule module, IrCall call) {
    if (call.Parent is not { } block || call.Convention != IrCallConvention.Basic
        || call.Callee is not IrFunction { IsDeclaration: true, Name: "rt_draw" }
        || !call.Type.IsVoid || call.ArgCount != 1
        || call.GetOperand(1) is not IrCall { Convention: IrCallConvention.Basic, ArgCount: 2 } producer
        || producer.Type != IrType.Ptr || producer.Users.Count != 1
        || producer.Callee is not IrFunction { IsDeclaration: true, Name: "rt_str_const" }
        || producer.GetOperand(1) is not IrGlobalVariable { Bytes: { } bytes }
        || producer.GetOperand(2) is not IrConstantInt { Type: var countType } count
        || countType != IrType.I32 || count.Value != bytes.Length)
      return false;
    List<DrawStep> steps;
    try {
      if (!MacroStringValidator.TryParseDraw(Encoding.Latin1.GetString(bytes), out steps, out _))
        return false;
    } catch (OverflowException) {
      // Unrepresentable commands remain runtime work, not a compiler-host exception.
      return false;
    }
    // Both signs of each movement must fit a word; do not turn an oversized macro number into a
    // modulo-i16 coordinate. Its original runtime range/error behavior is not proved here.
    if (steps.Any(step => step.X < -short.MaxValue || step.X > short.MaxValue
        || step.Y < -short.MaxValue || step.Y > short.MaxValue))
      return false;
    var expansion = new IrBasicBlock("draw.expansion");
    IrDrawExpansion.Emit(module, new IrBuilder(expansion), steps);
    foreach (var instruction in expansion.Instructions.ToArray()) {
      expansion.Remove(instruction);
      block.InsertBefore(instruction, call);
    }
    call.EraseFromParent();
    producer.EraseFromParent(); // The removed consuming call owned the producer's only handle.
    return true;
  }
}
