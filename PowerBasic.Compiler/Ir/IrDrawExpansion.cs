using PowerBasic.Compiler.Semantics;

namespace PowerBasic.Compiler.Ir;

/// <summary>The shared IR expansion for the currently modeled DRAW movement/colour commands.</summary>
internal static class IrDrawExpansion {
  internal static void Emit(IrModule module, IrBuilder builder, IReadOnlyList<DrawStep> steps) {
    var x1 = GetCell(module, "rt_gx1");
    var y1 = GetCell(module, "rt_gy1");
    var x2 = GetCell(module, "rt_gx2");
    var y2 = GetCell(module, "rt_gy2");
    builder.Store(new IrConstantInt(IrType.I16, -1), GetCell(module, "rt_gstyle"));
    foreach (var step in steps) {
      if (step.Kind == DrawStepKind.Colour) {
        builder.Store(new IrConstantInt(IrType.I16, step.X), GetCell(module, "rt_gcolor"));
        continue;
      }
      builder.Store(Endpoint(builder, x1, step.X, step.Kind), x2);
      builder.Store(Endpoint(builder, y1, step.Y, step.Kind), y2);
      if (step.Blank) {
        if (!step.NoUpdate) {
          builder.Store(builder.Load(IrType.I16, x2), x1);
          builder.Store(builder.Load(IrType.I16, y2), y1);
        }
        continue;
      }
      var keptX = step.NoUpdate ? builder.Load(IrType.I16, x1) : null;
      var keptY = step.NoUpdate ? builder.Load(IrType.I16, y1) : null;
      builder.Call(IrType.Void, module.FindFunction("rt_line")
        ?? module.AddFunction(new IrFunction("rt_line", IrType.Void)));
      if (keptX is null || keptY is null)
        continue;
      builder.Store(keptX, x1);
      builder.Store(keptY, y1);
    }
  }

  private static IrGlobalVariable GetCell(IrModule module, string name)
    => module.FindGlobal(name) ?? module.AddGlobal(new IrGlobalVariable(name, IrType.I16));

  private static IrValue Endpoint(IrBuilder builder, IrGlobalVariable from, int value, DrawStepKind kind) {
    var constant = new IrConstantInt(IrType.I16, value);
    if (kind != DrawStepKind.Relative)
      return constant;
    var start = builder.Load(IrType.I16, from);
    return value == 0 ? start : builder.Add(start, constant);
  }
}
