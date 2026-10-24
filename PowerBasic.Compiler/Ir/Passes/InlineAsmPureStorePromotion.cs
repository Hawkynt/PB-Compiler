namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Makes compiler-owned 16-bit local immediate writes visible to SSA even when
/// their source spelling is inline x86 assembly. The entire function must have
/// a complete, flag-neutral and register-neutral ASM translation: a later Jcc
/// or register use must never observe code generated for a promoted store.
/// </summary>
public static class InlineAsmPureStorePromotion {
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var promoted = 0;

    foreach (var function in module.Functions.Where(function =>
        !function.IsDeclaration && function.HasInlineAsm && !function.HasErrorHandler)) {
      var blocks = function.AllInstructions.OfType<IrInlineAsm>().ToArray();
      if (blocks.Length == 0)
        continue;

      var plans = new List<(IrInlineAsm Block, List<(IrValue Address, IrType Type, long Value)> Stores)>();
      foreach (var block in blocks) {
        if (!block.Routable || !TryDescribeStores(block, out var stores)) {
          plans.Clear();
          break;
        }
        plans.Add((block, stores));
      }

      // Nothing may be rewritten piecemeal. An opaque ASM instruction anywhere
      // in the function could depend on real registers, flags or precise order.
      if (plans.Count != blocks.Length)
        continue;

      foreach (var (block, stores) in plans) {
        foreach (var (address, type, value) in stores)
          block.Parent!.InsertBefore(new IrStore(
            new IrConstantInt(type, type.Signed
              ? unchecked((short)value) : unchecked((ushort)value)), address), block);
        block.EraseFromParent();
        promoted += stores.Count;
      }
      function.HasInlineAsm = false;
    }

    return promoted;
  }

  private static bool TryDescribeStores(IrInlineAsm block,
      out List<(IrValue Address, IrType Type, long Value)> stores) {
    stores = [];
    foreach (var raw in block.Text.Split('\n')) {
      var text = raw.Split(';', 2)[0].Trim();
      if (text.Length == 0)
        continue;

      var whitespace = text.IndexOfAny([' ', '\t']);
      if (whitespace < 0 || !text[..whitespace].Equals("MOV", StringComparison.OrdinalIgnoreCase))
        return false;

      var operands = text[(whitespace + 1)..].Trim();
      var comma = operands.IndexOf(',');
      if (comma <= 0 || comma != operands.LastIndexOf(','))
        return false;

      // An exact previously bound BASIC name, never a register, indirect
      // operand, label, far pointer or asm-local equate.
      var name = operands[..comma].Trim();
      var index = block.Names.FindIndex(bound =>
        bound.Equals(name, StringComparison.OrdinalIgnoreCase));
      if (index < 0 || block.Sizes[index] != 2
          || block.GetOperand(index) is not IrAlloca { Count: 1, IsSourceVariable: true, Allocated: var type }
          || type != IrType.I16 && type != IrType.U16)
        return false;

      // Restrict to decimal immediates until the assembler's own operand AST
      // is reusable here. No guessing about numeric macros or register state.
      var literal = operands[(comma + 1)..].Trim();
      if (!long.TryParse(literal, System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
          || value is < short.MinValue or > ushort.MaxValue)
        return false;

      stores.Add((block.GetOperand(index), type, value));
    }
    return stores.Count > 0;
  }
}
