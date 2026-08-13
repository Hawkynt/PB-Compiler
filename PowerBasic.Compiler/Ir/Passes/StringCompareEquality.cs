namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Routes a string comparison whose answer is only ever tested against zero to the runtime's
/// equality-only entry, which decides unequal lengths without examining a byte.
///
/// <para>
/// This is the IR half of what the direct emitter does as pb36 O0298. <c>rt_str_compare</c> is a
/// three-way compare: it walks bytes to the first difference so it can say WHICH string sorts first.
/// A <c>=</c> or <c>&lt;&gt;</c> never needs that ordering, and two strings of different lengths are
/// unequal by inspection - so the equality entry reads two descriptor lengths, compares them, and
/// only scans content when they match (a word at a time, since by then the lengths are known equal).
/// </para>
///
/// <para>
/// The transform is a callee swap and nothing else: the two routines take the same two handles in
/// the same registers, consume them the same way, and answer in the same register. What differs is
/// the VALUE they answer with - the general one gives -1/0/1 and the equality one gives 0/1 - which
/// is the whole of the soundness condition below.
/// </para>
///
/// <para>
/// The swap is refused unless EVERY user of the result is an <c>icmp eq/ne</c> against zero. A user
/// that asks anything else about the number reads an ordering the equality entry does not compute:
/// <c>rt_str_compare(a, b) &lt; 0</c> is "a sorts first", while the equality entry answers 1 for any
/// inequality in either direction, so the same expression would become "a and b differ". One such
/// user is enough to keep the call where it is, because the result is a single value and both
/// spellings cannot be had from it.
/// </para>
/// </summary>
public static class StringCompareEquality {

  private const string _GENERAL = "rt_str_compare";
  private const string _EQUALITY_ONLY = "rt_str_compare_eq";

  /// <summary>Swaps qualifying comparisons onto the equality entry; the number swapped.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    IrFunction? equalityOnly = null;
    var swapped = 0;
    // a snapshot: declaring the equality entry appends to the module's own function list
    foreach (var function in module.Functions.ToList()) {
      // the same guard IrPassManager applies to its function passes: a body with an armed handler or
      // an asm block has readers the CFG does not show, so "every user" cannot be enumerated
      if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
        continue;
      foreach (var call in function.AllInstructions.OfType<IrCall>().ToList()) {
        if (call.Callee is not IrFunction { Name: _GENERAL } || call.ArgCount != 2)
          continue;
        if (call.Users.Count == 0 || !call.Users.All(IsEqualityTest))
          continue;
        equalityOnly ??= DeclareEqualityEntry(module);
        call.SetOperand(0, equalityOnly);
        ++swapped;
      }
    }
    return swapped;
  }

  /// <summary>Whether the user only asks whether the comparison answered zero.</summary>
  private static bool IsEqualityTest(IrInstruction user)
    => user is IrCmp { Pred: IrCmpPred.Eq or IrCmpPred.Ne } cmp
       && (cmp.Rhs is IrConstantInt { Value: 0 } || cmp.Lhs is IrConstantInt { Value: 0 });

  private static IrFunction DeclareEqualityEntry(IrModule module)
    => module.FindFunction(_EQUALITY_ONLY)
       ?? module.AddFunction(new IrFunction(_EQUALITY_ONLY, IrType.I32,
         [new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.Ptr, 1)]));
}
