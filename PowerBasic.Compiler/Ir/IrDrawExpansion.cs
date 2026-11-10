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
    // DRAW state is persistent across calls: only assignments inside this
    // particular string justify assuming a scale or angle. The startup
    // defaults must never be assumed by an independent DRAW statement.
    int? scale = null;
    int? angle = null;
    foreach (var step in steps) {
      if (step.Kind is DrawStepKind.Colour or DrawStepKind.Scale or DrawStepKind.Angle) {
        var cell = step.Kind switch {
          DrawStepKind.Colour => "rt_draw_color",
          DrawStepKind.Scale => "rt_draw_scale",
          _ => "rt_draw_angle",
        };
        builder.Store(new IrConstantInt(IrType.I16, step.Kind == DrawStepKind.Colour ? step.X + 1 : step.X),
          GetCell(module, cell));
        if (step.Kind == DrawStepKind.Scale)
          scale = step.X;
        else if (step.Kind == DrawStepKind.Angle)
          angle = step.X;
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
      // Absolute M does not use the previous scaling/rotation. Relative
      // movement is equivalent to an unscaled delta only after an explicit
      // S4 + A0 / TA0, and only when the signed 16-bit S4 multiplication
      // cannot overflow before division by four.
      var direct = step.Kind == DrawStepKind.Absolute
        || step.Kind == DrawStepKind.Relative && scale == 4 && angle == 0
        && step.X is >= -8192 and <= 8191 && step.Y is >= -8192 and <= 8191;
      if (direct) {
        EmitDirectLine(module, builder, step);
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

  private static void EmitDirectLine(IrModule module, IrBuilder builder, DrawStep step) {
    // Exact rt_draw_move contract for a proven identity transform. Keep
    // the graphics cursor and line endpoints distinct; N restores the
    // original cursor even though the line has already been rasterized.
    var oldX = builder.Load(IrType.I16, GetCell(module, "rt_gx1"));
    var oldY = builder.Load(IrType.I16, GetCell(module, "rt_gy1"));
    var x = new IrConstantInt(IrType.I16, step.X);
    var y = new IrConstantInt(IrType.I16, step.Y);
    IrValue targetX = step.Kind == DrawStepKind.Relative ? builder.Add(oldX, x) : x;
    IrValue targetY = step.Kind == DrawStepKind.Relative ? builder.Add(oldY, y) : y;
    builder.Store(targetX, GetCell(module, "rt_gx2"));
    builder.Store(targetY, GetCell(module, "rt_gy2"));
    builder.Store(new IrConstantInt(IrType.I16, -1), GetCell(module, "rt_gstyle"));
    var color = builder.Load(IrType.I16, GetCell(module, "rt_draw_color"));
    var fallbackColor = builder.Load(IrType.I16, GetCell(module, "rt_fgcolor"));
    builder.Store(builder.Select(builder.Cmp(IrCmpPred.Eq, color, new IrConstantInt(IrType.I16, 0)),
      fallbackColor, builder.Sub(color, new IrConstantInt(IrType.I16, 1))),
      GetCell(module, "rt_gcolor"));
    if (!step.Blank) {
      var line = module.FindFunction("rt_line") ?? module.AddFunction(new IrFunction("rt_line", IrType.Void));
      builder.Call(IrType.Void, line);
      if (!step.NoUpdate) return;
    } else if (!step.NoUpdate) {
      builder.Store(targetX, GetCell(module, "rt_gx1"));
      builder.Store(targetY, GetCell(module, "rt_gy1"));
      return;
    } else
      return;
    builder.Store(oldX, GetCell(module, "rt_gx1"));
    builder.Store(oldY, GetCell(module, "rt_gy1"));
  }

  private static IrGlobalVariable GetCell(IrModule module, string name)
    => module.FindGlobal(name) ?? module.AddGlobal(new IrGlobalVariable(name, IrType.I16));
}
