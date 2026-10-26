using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Resolves proven Mode 13h POINT reads only at the final native IR boundary.
/// Delaying the segmented load prevents ordinary load GVN, hoisting and dead
/// load elimination from changing hardware framebuffer observations.
/// </summary>
public static class PixelRuntimeReadLowering {
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var changed = 0;
    foreach (var function in module.Functions.Where(function => !function.IsDeclaration))
      changed += new IrFunctionPassPipeline().Add("pixel-mode13-read", (fn, analyses) => {
        var modes = analyses.Get(IrAnalyses.ScreenModes);
        var calls = fn.AllInstructions.OfType<IrCall>()
        .Where(call => call.Callee is IrFunction { IsDeclaration: true, Name: "rt_point" }
          && call.Convention == IrCallConvention.Basic
          && call.Type == IrType.I32
          && call.ArgCount == 2
          && call.Args.All(argument => argument.Type == IrType.I16)
          && modes.Before(call) == 13).ToArray();
      foreach (var call in calls) {
        var block = call.Parent!;
        var row = block.InsertBefore(new IrBinary(IrBinaryOp.Mul, call.GetOperand(2),
          new IrConstantInt(IrType.I16, 320)), call);
        var offset = block.InsertBefore(new IrBinary(IrBinaryOp.Add, row,
          call.GetOperand(1)), call);
        var pointer = block.InsertBefore(new IrFarPtr(
          new IrConstantInt(IrType.I16, unchecked((short)0xA000)), offset), call);
        var color = block.InsertBefore(new IrLoad(IrType.U8, pointer) { IsDeviceRead = true }, call);
        var result = block.InsertBefore(new IrCast(IrCastOp.ZExt, color, IrType.I32), call);
        call.ReplaceAllUsesWith(result);
        call.EraseFromParent();
      }
      return calls.Length == 0 ? IrPassResult.Unchanged : IrPassResult.Changed(calls.Length);
    }).Run(function);
    return changed;
  }
}
