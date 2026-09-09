namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0333 — removes 256-byte read-only tables when every possible byte index follows a cheaper exact
/// formula. The target-neutral v1 only takes formulas that are never more expensive than an indexed
/// load: constant, identity, XOR-mask, or add-constant.
/// </summary>
public static class LookupTableElimination {

  private enum FormulaKind { Constant, Identity, Xor, Add }
  private readonly record struct Formula(FormulaKind Kind, byte Constant);
  private sealed record Access(IrLoad Load, IrGep Gep, IrValue Index);

  /// <summary>Eliminates qualifying tables; returns the number removed.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var eliminated = 0;
    foreach (var global in module.Globals.ToList())
      if (TryEliminate(module, global))
        ++eliminated;
    return eliminated;
  }

  private static bool TryEliminate(IrModule module, IrGlobalVariable global) {
    if (global.Name.StartsWith(".lut.", StringComparison.Ordinal) || global.ValueType.Bits != 8
        || global.Bytes is not { Length: 256 } bytes || global.Count != 256
        || !TryFormula(bytes, out var formula) || !Collect(global, out var accesses))
      return false;

    foreach (var access in accesses) {
      var replacement = Build(access.Load, access.Index, formula);
      access.Load.ReplaceAllUsesWith(replacement);
      access.Load.EraseFromParent();
    }
    foreach (var gep in accesses.Select(access => access.Gep).Distinct().ToList())
      if (gep.HasNoUsers)
        gep.EraseFromParent();
    System.Diagnostics.Debug.Assert(global.HasNoUsers,
      "the readonly-use proof must account for every table reference before the global is removed");
    module.RemoveGlobal(global);
    return true;
  }

  private static bool Collect(IrGlobalVariable global, out List<Access> accesses) {
    accesses = [];
    foreach (var user in global.Users.ToList()) {
      if (user is not IrGep { ElementType: { } element } gep || !element.SameStorage(IrType.I8)
          || !ReferenceEquals(gep.BasePtr, global) || !TryByteIndex(gep.ByteOffset, out var index))
        return false;
      foreach (var indexed in gep.Users.ToList()) {
        if (indexed.Parent?.Parent is { HasErrorHandler: true } or { HasInlineAsm: true }
            || indexed is not IrLoad load || !ReferenceEquals(load.Pointer, gep) || load.Type.Bits != 8
            || !load.Type.Equals(index.Type))
          return false;
        accesses.Add(new(load, gep, index));
      }
    }
    return accesses.Count > 0;
  }

  private static bool TryByteIndex(IrValue value, out IrValue index) {
    switch (value) {
      case IrCast { Op: IrCastOp.ZExt, Value.Type: { IsInteger: true, Bits: 8 } } widened:
        index = widened.Value;
        return true;
      case { Type: { IsInteger: true, IsUnsigned: true, Bits: 8 } }:
        index = value;
        return true;
      case IrConstantInt constant when constant.ZeroExtended <= byte.MaxValue:
        index = new IrConstantInt(IrType.U8, (long)constant.ZeroExtended);
        return true;
      default:
        index = null!;
        return false;
    }
  }

  private static IrValue Build(IrLoad load, IrValue index, Formula formula) {
    if (formula.Kind == FormulaKind.Constant)
      return new IrConstantInt(load.Type, formula.Constant);
    if (formula.Kind == FormulaKind.Identity)
      return index;

    var block = load.Parent!;
    var op = formula.Kind == FormulaKind.Xor ? IrBinaryOp.Xor : IrBinaryOp.Add;
    return block.InsertBefore(new IrBinary(op, index, new IrConstantInt(index.Type, formula.Constant)), load);
  }

  private static bool TryFormula(byte[] bytes, out Formula formula) {
    if (bytes.All(value => value == bytes[0])) {
      formula = new(FormulaKind.Constant, bytes[0]);
      return true;
    }
    if (bytes.Where((value, index) => value != (byte)index).Any() is false) {
      formula = new(FormulaKind.Identity, 0);
      return true;
    }

    var xor = bytes[0];
    if (!bytes.Where((value, index) => value != ((byte)index ^ xor)).Any()) {
      formula = new(FormulaKind.Xor, xor);
      return true;
    }

    var add = bytes[0];
    if (!bytes.Where((value, index) => value != unchecked((byte)(index + add))).Any()) {
      formula = new(FormulaKind.Add, add);
      return true;
    }

    formula = default;
    return false;
  }
}
