namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0011 — shares contained literal bytes after string folding has finished. Only bounded,
/// read-only runtime consumers are eligible; observable or escaping addresses remain distinct.
/// </summary>
public static class LiteralPooling {

  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    if (module.Functions.Any(function => function.HasInlineAsm))
      return 0;

    var literals = module.Globals.Where(IsCandidate)
      .OrderByDescending(global => global.Bytes!.Length)
      .ThenBy(global => global.Name, StringComparer.Ordinal)
      .ToArray();
    var hosts = new List<IrGlobalVariable>();
    var removed = 0;
    foreach (var literal in literals) {
      var host = hosts.FirstOrDefault(candidate => candidate.Bytes!.AsSpan().IndexOf(literal.Bytes!) >= 0);
      if (host is null) {
        hosts.Add(literal);
        continue;
      }
      var offset = host.Bytes!.AsSpan().IndexOf(literal.Bytes!);
      foreach (var user in literal.Users.Distinct<IrInstruction>(ReferenceEqualityComparer.Instance).ToArray()) {
        IrValue pointer = host;
        if (offset != 0)
          pointer = user.Parent!.InsertBefore(new IrGep(host, new IrConstantInt(IrType.I32, offset)), user);
        user.ReplaceOperand(literal, pointer);
      }
      module.RemoveGlobal(literal);
      ++removed;
    }
    return removed;
  }

  private static bool IsCandidate(IrGlobalVariable global) {
    if (!global.Name.StartsWith(".str", StringComparison.Ordinal)
        || global.Bytes is not { Length: > 0 } bytes || global.ValueType != IrType.I8 || global.HasNoUsers)
      return false;
    return global.Users.All(user => IsBoundedRead(user, global, bytes.Length));
  }

  private static bool IsBoundedRead(IrInstruction user, IrGlobalVariable literal, int size) {
    if (user.Parent is null || user is not IrCall { Callee: IrFunction { IsDeclaration: true } callee } call)
      return false;
    var pointerIndex = callee.Name switch {
      "rt_print_str" or "rt_str_const" when call.ArgCount == 2 => 1,
      "rt_fprint_str" or "rt_str_append_lit" when call.ArgCount == 3 => 2,
      _ => 0,
    };
    if (pointerIndex == 0 || !ReferenceEquals(call.GetOperand(pointerIndex), literal)
        || call.GetOperand(pointerIndex + 1) is not IrConstantInt { Value: var length }
        || length < 0 || length > size)
      return false;
    // Reject a second occurrence in another argument position: that is not a proven byte read.
    return call.Operands.Count(operand => ReferenceEquals(operand, literal)) == 1;
  }
}
