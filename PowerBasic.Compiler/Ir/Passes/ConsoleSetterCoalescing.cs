using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>Coalesces console setters without crossing an observer or a trap.</summary>
public static class ConsoleSetterCoalescing {

  public static int Run(IrFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    return IrFunctionPassPipeline.RunStandalone(function, "console-setters", Run);
  }

  internal static IrPassResult Run(IrFunction function, IrAnalysisManager analyses) {
    ArgumentNullException.ThrowIfNull(function);
    ArgumentNullException.ThrowIfNull(analyses);
    if (!ReferenceEquals(function, analyses.Function))
      throw new ArgumentException("Analysis manager belongs to a different function.", nameof(analyses));
    var removed = function.Blocks.Sum(CoalesceBlock);
    return removed == 0 ? IrPassResult.Unchanged
      : IrPassResult.ChangedPreservingSets(removed, IrAnalysisSets.Cfg);
  }

  private static int CoalesceBlock(IrBasicBlock block) {
    IrCall? locate = null;
    IrCall? clear = null;
    IrCall? screen = null;
    var removed = 0;
    foreach (var instruction in block.Instructions.ToArray()) {
      if (instruction is IrCall { Callee: IrFunction { IsDeclaration: true, Name: "rt_screen_mode" }, ArgCount: 1 } mode
          && mode.GetOperand(1) is IrConstantInt { Type: var type } value && type == IrType.I16) {
        if (screen is not null && ReferenceEquals(screen.Callee, mode.Callee)
            && screen.GetOperand(1) is IrConstantInt previous && previous.Value == value.Value)
          removed += Erase(mode); // Keep the first call: it is the one that may fail.
        else
          screen = mode;
        clear = locate = null;
        continue;
      }
      if (instruction is IrCall { Callee: IrFunction { IsDeclaration: true, Name: "rt_cls" }, ArgCount: 0 } cls) {
        removed += Erase(clear) + Erase(locate);
        clear = cls;
        locate = null;
        screen = null;
        continue;
      }
      if (instruction is IrCall { Callee: IrFunction { IsDeclaration: true, Name: "rt_locate" }, ArgCount: 2 } position
          && IsValidPosition(position)) {
        if (locate is not null && Covers(position, locate))
          removed += Erase(locate);
        locate = position;
        screen = null;
        continue;
      }
      if (instruction is IrAlloca or IrCmp or IrSelect or IrGep
          || instruction is IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.Sub or IrBinaryOp.Mul
            or IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor })
        continue;
      // Calls, memory access, inline assembly and trapping arithmetic are barriers. Distinct
      // mode requests cannot kill earlier setters: a mode may fail or keep existing state.
      clear = locate = screen = null;
    }
    return removed;
  }

  private static bool IsValidPosition(IrCall call)
    => call.GetOperand(1) is IrConstantInt { Value: >= 0 and <= 25 }
      && call.GetOperand(2) is IrConstantInt { Value: >= 0 and <= 40 };

  private static bool Covers(IrCall later, IrCall earlier)
    => Enumerable.Range(1, 2).All(index => earlier.GetOperand(index) is IrConstantInt { Value: 0 }
      || later.GetOperand(index) is IrConstantInt { Value: > 0 });

  private static int Erase(IrCall? call) {
    if (call is null)
      return 0;
    call.EraseFromParent();
    return 1;
  }
}
