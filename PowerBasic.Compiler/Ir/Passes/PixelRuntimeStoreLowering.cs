namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Finishes Mode 13h pixel specialization after the last optimizing IR pass.
/// Earlier passes see the ordinary runtime call (an observable write), and the
/// native selector sees a segmented byte store instead of an opaque ABI call.
/// </summary>
public static class PixelRuntimeStoreLowering {
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var changes = 0;
    foreach (var function in module.Functions.Where(function => !function.IsDeclaration))
      foreach (var call in function.AllInstructions.OfType<IrCall>().Where(IsPackedWrite).ToArray()) {
        var block = call.Parent!;
        var byteColor = block.InsertBefore(new IrCast(IrCastOp.Trunc, call.GetOperand(2), IrType.U8), call);
        var pointer = block.InsertBefore(new IrFarPtr(
          new IrConstantInt(IrType.I16, unchecked((short)0xA000)), call.GetOperand(1)), call);
        block.InsertBefore(new IrStore(byteColor, pointer), call);
        call.EraseFromParent();
        ++changes;
      }
    return changes;
  }

  private static bool IsPackedWrite(IrCall call)
    => call.Callee is IrFunction { IsDeclaration: true, Name: "rt_pset_mode13_offset" }
       && call.Convention == IrCallConvention.Basic
       && call.Type.IsVoid
       && call.ArgCount == 2
       && call.Args.All(argument => argument.Type == IrType.I16);
}
