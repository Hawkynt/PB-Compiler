using PowerBasic.Compiler.Semantics;

namespace PowerBasic.Compiler.Ir;

/// <summary>The shared IR expansion for the currently modeled DRAW movement/colour commands.</summary>
internal static class IrDrawExpansion {
  internal static void Emit(IrModule module, IrBuilder builder, IReadOnlyList<DrawStep> steps) {
    var begin = module.FindFunction("rt_draw_begin")
      ?? module.AddFunction(new IrFunction("rt_draw_begin", IrType.Void));
    builder.Call(IrType.Void, begin);
    EmitCommands(module, builder, steps);
  }

  internal static void EmitOwnedPrefix(IrModule module, IrBuilder builder, IReadOnlyList<DrawStep> steps,
      IrValue handle) {
    var begin = module.FindFunction("rt_draw_begin_owned") ?? module.AddFunction(new IrFunction(
      "rt_draw_begin_owned", IrType.Void, [new IrArgument(IrType.Ptr, 0)]));
    builder.Call(IrType.Void, begin, handle);
    EmitCommands(module, builder, steps);
  }

  private static void EmitCommands(IrModule module, IrBuilder builder, IReadOnlyList<DrawStep> steps) {
    foreach (var step in steps) {
      if (step.Kind is DrawStepKind.Colour or DrawStepKind.Scale or DrawStepKind.Angle) {
        var cell = step.Kind switch {
          DrawStepKind.Colour => "rt_draw_color",
          DrawStepKind.Scale => "rt_draw_scale",
          _ => "rt_draw_angle",
        };
        builder.Store(new IrConstantInt(IrType.I16, step.Kind == DrawStepKind.Colour ? step.X + 1 : step.X),
          GetCell(module, cell));
        continue;
      }
      if (step.Kind == DrawStepKind.Paint) {
        builder.Store(new IrConstantInt(IrType.I16, step.X), GetCell(module, "rt_gcolor"));
        builder.Store(new IrConstantInt(IrType.I16, (step.X & 255) + 1),
          GetCell(module, "rt_draw_color"));
        builder.Store(new IrConstantInt(IrType.I16, step.Y), GetCell(module, "rt_gpbord"));
        var paint = module.FindFunction("rt_paint") ?? module.AddFunction(
          new IrFunction("rt_paint", IrType.Void));
        builder.Call(IrType.Void, paint);
        continue;
      }
      var move = module.FindFunction("rt_draw_move") ?? module.AddFunction(new IrFunction("rt_draw_move", IrType.Void,
        Enumerable.Range(0, 4).Select(index => new IrArgument(IrType.I16, index))));
      builder.Call(IrType.Void, move,
        new IrConstantInt(IrType.I16, step.Kind == DrawStepKind.Relative ? 0 : 1),
        new IrConstantInt(IrType.I16, step.X), new IrConstantInt(IrType.I16, step.Y),
        new IrConstantInt(IrType.I16, (step.Blank ? 1 : 0) | (step.NoUpdate ? 2 : 0)));
    }
  }

  private static IrGlobalVariable GetCell(IrModule module, string name)
    => module.FindGlobal(name) ?? module.AddGlobal(new IrGlobalVariable(name, IrType.I16));
}
