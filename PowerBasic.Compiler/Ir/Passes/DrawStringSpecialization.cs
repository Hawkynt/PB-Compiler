using System.Text;
using PowerBasic.Compiler.Semantics;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>Expands DRAW text proved constant by the middle end, after consuming string folds.</summary>
public static class DrawStringSpecialization {
  private static readonly string[] _CELLS = ["rt_gx1", "rt_gy1", "rt_gx2", "rt_gy2", "rt_gstyle", "rt_gcolor",
    "rt_draw_color", "rt_draw_scale", "rt_draw_angle", "rt_gpbord", "rt_fgcolor"];

  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    if (module.FindFunction("rt_draw_move") is { IsDeclaration: false }
        || module.FindFunction("rt_draw_begin") is { IsDeclaration: false })
      return 0;
    // Runtime storage/entries must not silently acquire a different type or implementation.
    if (_CELLS.Any(name => module.FindGlobal(name) is { } cell
          && (cell.ValueType != IrType.I16 || cell.Count != 1))
        || !HasCompatibleEntry(module, "rt_draw_begin", [])
        || !HasCompatibleEntry(module, "rt_draw_begin_owned", [IrType.Ptr])
        || !HasCompatibleEntry(module, "rt_draw_move", [IrType.I16, IrType.I16, IrType.I16, IrType.I16])
        || !HasCompatibleEntry(module, "rt_draw_resume", [IrType.Ptr, IrType.I16])
        || module.FindFunction("rt_line") is { } line
          && (!line.IsDeclaration || line.Convention != IrCallConvention.Basic
            || !line.ReturnType.IsVoid || line.Parameters.Count != 0)
        || module.FindFunction("rt_paint") is { } paint
          && (!paint.IsDeclaration || paint.Convention != IrCallConvention.Basic
            || !paint.ReturnType.IsVoid || paint.Parameters.Count != 0))
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
        || call.GetOperand(1) is not IrCall producer
        || !TryGetCommands(producer, out var bytes, out var complete, out var steps))
      return false;
    var expansion = new IrBasicBlock("draw.expansion");
    var builder = new IrBuilder(expansion);
    if (complete)
      IrDrawExpansion.Emit(module, builder, steps);
    else
      IrDrawExpansion.EmitOwnedPrefix(module, builder, steps, producer);
    if (!complete) {
      var resume = module.FindFunction("rt_draw_resume") ?? module.AddFunction(new IrFunction("rt_draw_resume",
        IrType.Void, [new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.I16, 1)]));
      builder.Call(IrType.Void, resume, producer, new IrConstantInt(IrType.I16, bytes.Length));
    }
    foreach (var instruction in expansion.Instructions.ToArray()) {
      expansion.Remove(instruction);
      block.InsertBefore(instruction, call);
    }
    call.EraseFromParent();
    if (complete)
      producer.EraseFromParent(); // The removed consuming call owned the producer's only handle.
    return true;
  }

  private static bool TryGetCommands(IrCall producer, out byte[] bytes, out bool complete,
      out List<DrawStep> steps) {
    steps = [];
    complete = TryGetOwnedLiteral(producer, out bytes);
    if (!complete && !TryGetLeadingLiteral(producer, out bytes))
      return false;
    var picture = Encoding.Latin1.GetString(bytes);
    // An arbitrary tail can extend a number or inherit B/N. A delimiter after a movement proves
    // both a lexical boundary and cleared prefixes. Keep the joined handle, and skip only its prefix.
    if (!complete && (!picture.TrimEnd(' ', '\t').EndsWith(';')
        || !"UDLREFGHM".Contains(char.ToUpperInvariant(picture.LastOrDefault(char.IsAsciiLetter)))))
      return false;
    if (picture.Any(character => char.IsWhiteSpace(character) && character is not (' ' or '\t')))
      return false;
    try {
      if (!MacroStringValidator.TryParseDraw(picture, out steps, out _))
        return false;
    } catch (OverflowException) {
      // Unrepresentable commands remain runtime work, not a compiler-host exception.
      return false;
    }
    // PAINT can fail with error 7. A partially constant DRAW still owns
    // the joined dynamic string until rt_draw_resume; direct PAINT would
    // raise before that parser could free the allocation. Fully constant
    // DRAW already consumes its literal producer and is safe to expand.
    if (!complete && steps.Any(step => step.Kind == DrawStepKind.Paint))
      return false;
    // Wide literals have terminator-dependent numeric errors. Leave them to the shared parser rather
    // than erasing those checks; this restriction changes specialization, not source acceptance.
    return steps.All(step => step.Kind is DrawStepKind.Colour or DrawStepKind.Scale
      or DrawStepKind.Angle or DrawStepKind.Paint
      || step.X is >= -9999 and <= 9999 && step.Y is >= -9999 and <= 9999);
  }

  private static bool HasCompatibleEntry(IrModule module, string name, IrType[] parameters)
    => module.FindFunction(name) is not { } entry || entry.IsDeclaration && entry.ReturnType.IsVoid
      && entry.Convention == IrCallConvention.Basic
      && entry.Parameters.Select(parameter => parameter.Type).SequenceEqual(parameters);

  private static bool TryGetOwnedLiteral(IrCall producer, out byte[] bytes) {
    bytes = [];
    if (producer is not { Convention: IrCallConvention.Basic, ArgCount: 2 }
        || producer.Type != IrType.Ptr || producer.Users.Count != 1
        || producer.Callee is not IrFunction { IsDeclaration: true, Name: "rt_str_const" }
        || producer.GetOperand(1) is not IrGlobalVariable { Bytes: { } contents }
        || producer.GetOperand(2) is not IrConstantInt { Type: var countType } count
        || countType != IrType.I32 || count.Value != contents.Length)
      return false;
    bytes = contents;
    return true;
  }

  private static bool TryGetLeadingLiteral(IrCall producer, out byte[] bytes) {
    bytes = [];
    var prefix = new List<byte>();
    // Traverse the concatenation in evaluation order. The first dynamic
    // subtree ends the provable prefix; known literals on its right are
    // NOT movable across it. Require unique ownership of every input,
    // since the partial specialization preserves the original string
    // handle for rt_draw_resume and skips exactly these prefix bytes.
    GatherPrefix(producer, prefix);
    if (prefix.Count == 0 || prefix.Count > short.MaxValue)
      return false;
    bytes = [.. prefix];
    return true;
  }

  private static bool GatherPrefix(IrCall producer, List<byte> prefix) {
    if (TryGetOwnedLiteral(producer, out var literal)) {
      if (prefix.Count + literal.Length > short.MaxValue)
        return false;
      prefix.AddRange(literal);
      return true;
    }
    if (producer is not { Convention: IrCallConvention.Basic, ArgCount: 2, Users.Count: 1 }
        || producer.Type != IrType.Ptr
        || producer.Callee is not IrFunction { IsDeclaration: true, Name: "rt_str_concat" }
        || producer.GetOperand(1) is not IrCall left
        || !GatherPrefix(left, prefix))
      return false;
    return producer.GetOperand(2) is IrCall right && GatherPrefix(right, prefix);
  }
}
