using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>Exposes proven SCREEN 13 address arithmetic as an ordinary inlineable IR runtime body.</summary>
public static class PixelRuntimeInlining {
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    // Never reuse a same-named body supplied by a consumer as if it were our runtime template.
    if (module.FindFunction("rt_pset_mode13") is not null
        || module.FindFunction("rt_pset_mode13_offset") is not null)
      return 0;
    IrFunction? helper = null;
    var changed = 0;
    foreach (var function in module.Functions.Where(function => !function.IsDeclaration).ToArray())
      changed += new IrFunctionPassPipeline().Add("pixel-mode-inline", (fn, analyses) => {
        var modes = analyses.Get(IrAnalyses.ScreenModes);
        var calls = fn.AllInstructions.OfType<IrCall>().Where(call =>
          call.Callee is IrFunction { IsDeclaration: true, Name: "rt_pset" }
          && call.Convention == IrCallConvention.Basic
          && call.Type.IsVoid && call.ArgCount == 3 && call.Args.All(arg => arg.Type == IrType.I16)
          && modes.Before(call) == 13).ToArray();
        if (calls.Length == 0)
          return IrPassResult.Unchanged;
        helper ??= DefinePackedPixel(module);
        foreach (var call in calls)
          call.SetOperand(0, helper);
        return IrPassResult.ChangedPreservingSets(calls.Length, IrAnalysisSets.Cfg);
      }).Run(function);
    return changed;
  }

  private static IrFunction DefinePackedPixel(IrModule module) {
    var arguments = Enumerable.Range(0, 3).Select(index => new IrArgument(IrType.I16, index)).ToArray();
    var helper = module.AddFunction(new IrFunction("rt_pset_mode13", IrType.Void, arguments));
    var writer = module.AddFunction(new IrFunction("rt_pset_mode13_offset", IrType.Void,
      [new IrArgument(IrType.I16, 0), new IrArgument(IrType.I16, 1)]));
    var block = helper.CreateBlock("entry");
    // Same modulo-16-bit address formula as the existing packed DOS pixel routine.
    var row = block.Append(new IrBinary(IrBinaryOp.Mul, arguments[1], new IrConstantInt(IrType.I16, 320)));
    var offset = block.Append(new IrBinary(IrBinaryOp.Add, row, arguments[0]));
    block.Append(new IrCall(IrType.Void, writer, [offset, arguments[2]]));
    block.Append(new IrRet());
    return helper;
  }
}
