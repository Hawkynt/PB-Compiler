namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Answers "is this string empty?" by looking at the handle instead of calling the runtime.
///
/// <para>
/// This is the IR half of the direct emitter's pb36 O0181, and it is worth its own pass because of
/// how common the question is - every <c>LINE INPUT</c> loop in the corpus ends with one. A PB string
/// is empty exactly when its handle is null: the allocator answers a zero-length request with handle
/// 0, so an empty string has no other representation. Both spellings the language offers -
/// <c>s$ = ""</c> and <c>LEN(s$) = 0</c> - therefore reduce to the same null test, which is also why
/// they compile to the same image once this has run.
/// </para>
///
/// <para>
/// The string has to be a BORROW of storage - the copy the lowering makes when a variable or an array
/// element is read. That restriction is the direct emitter's and it is about ownership rather than
/// about the answer: a borrow can simply not be taken, since the test reads no bytes, whereas a
/// temporary from somewhere else is a handle someone has to release, and inserting the free would
/// give back what the test just saved. The <c>rt_strcmp</c> the comparison no longer calls is often
/// the program's only one, in which case the runtime trimmer drops the whole routine.
/// </para>
///
/// <para>
/// A FIXED string is the case worth checking rather than assuming, and the borrow requirement
/// already excludes it: it is space-padded to its declared width and reaches a comparison through
/// <c>rt_str_from_fixed</c> rather than through a copy, so this never sees one. What the pass will
/// also not touch is a comparison against a NON-empty literal, or an ordering one - neither is a
/// question about emptiness.
/// </para>
/// </summary>
public static class StringEmptinessTest {

  private const string _LEN = "rt_str_len";
  private const string _COMPARE = "rt_str_compare";
  private const string _COMPARE_EQ = "rt_str_compare_eq";
  private const string _CONST = "rt_str_const";
  private const string _DUP = "rt_str_dup";

  /// <summary>Rewrites emptiness questions as handle tests; the number rewritten.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var rewritten = 0;
    foreach (var function in module.Functions.ToList()) {
      if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
        continue;
      foreach (var compare in function.AllInstructions.OfType<IrCmp>().ToList()) {
        if (compare.Parent is null || compare.Pred is not (IrCmpPred.Eq or IrCmpPred.Ne))
          continue;
        if (AgainstZero(compare) is not IrCall answer)
          continue;
        if (EmptinessSubject(answer) is not { } subject)
          continue;

        var test = new IrCmp(compare.Pred, subject.Handle, new IrNullPtr());
        compare.Parent!.InsertBefore(test, compare);
        compare.ReplaceAllUsesWith(test);
        compare.EraseFromParent();
        answer.EraseFromParent();                  // its operands lose their user here...
        foreach (var consumed in subject.Consumed)
          consumed.EraseFromParent();              // ...which is what leaves these unused
        ++rewritten;
      }
    }
    return rewritten;
  }

  /// <summary>The other side of a comparison against the integer zero, or null.</summary>
  private static IrValue? AgainstZero(IrCmp compare) {
    if (compare.Rhs is IrConstantInt { Value: 0 })
      return compare.Lhs;
    return compare.Lhs is IrConstantInt { Value: 0 } ? compare.Rhs : null;
  }

  /// <summary>
  /// The borrowed handle whose emptiness <paramref name="answer"/> computes, plus the calls that go
  /// with it - or null when the call is not an emptiness question this pass may rewrite.
  /// </summary>
  private static (IrValue Handle, IrCall[] Consumed)? EmptinessSubject(IrCall answer) {
    if (answer.Callee is not IrFunction callee || answer.Users.Count != 1)
      return null;
    switch (callee.Name) {
      case _LEN when answer.ArgCount == 1:
        return Borrowed(answer.GetOperand(1)) is { } length ? (length.Handle, [length.Borrow]) : null;
      case _COMPARE or _COMPARE_EQ when answer.ArgCount == 2: {
        // whichever side is the empty literal; the other is the string being asked about
        var literalIndex = IsEmptyLiteral(answer.GetOperand(2)) ? 2 : IsEmptyLiteral(answer.GetOperand(1)) ? 1 : 0;
        if (literalIndex == 0)
          return null;
        if (Borrowed(answer.GetOperand(literalIndex == 2 ? 1 : 2)) is not { } compared)
          return null;
        return (compared.Handle, [compared.Borrow, (IrCall)answer.GetOperand(literalIndex)]);
      }
      default:
        return null;
    }
  }

  /// <summary>The storage handle behind a borrow this call is the only reader of.</summary>
  private static (IrCall Borrow, IrValue Handle)? Borrowed(IrValue value)
    => value is IrCall { Callee: IrFunction { Name: _DUP }, ArgCount: 1 } borrow && borrow.Users.Count == 1
      ? (borrow, borrow.GetOperand(1))
      : null;

  /// <summary>Whether the value is the empty string literal, produced here and read only here.</summary>
  private static bool IsEmptyLiteral(IrValue value)
    => value is IrCall { Callee: IrFunction { Name: _CONST }, ArgCount: 2 } literal
       && literal.Users.Count == 1
       && literal.GetOperand(2) is IrConstantInt { Value: 0 };
}
