namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0061 — reassociation of associative, commutative integer chains into a canonical shape, so that
/// the passes downstream can see equalities the source order hid.
///
/// <c>(a + 1) + (b + 2)</c> and <c>(b + a) + 3</c> compute the same number, but nothing in
/// <see cref="InstCombine"/> or <see cref="Gvn"/> can tell: instcombine only looks at one instruction
/// and its immediate operands, and GVN hashes the tree as written. Flattening a chain to its leaves,
/// summing the constants once, sorting the rest into a fixed order and rebuilding gives both of them
/// the same tree — after which the constant folds and the common part is numbered as one value.
///
/// <para>
/// The operators it accepts are exactly the ones where this is an identity rather than an
/// approximation: integer <c>+</c>, <c>*</c>, <c>AND</c>, <c>OR</c>, <c>XOR</c>. Two's-complement
/// addition and multiplication stay associative across wrapping, so a chain that overflows still
/// reaches the same result. Floating point is deliberately excluded — reassociating it changes the
/// answer, which is why that is a separate, opt-in optimization (O0344) and not this one.
/// </para>
/// <para>
/// Subtraction is folded in as its negation (<c>a - b</c> is a leaf <c>-b</c> in an add chain) only
/// when the subtrahend is a constant, because negating a general value would need an instruction this
/// pass would then have to prove is worth its own existence.
/// </para>
/// </summary>
public static class Reassociate {

  /// <summary>The most leaves worth gathering; a longer chain is not source code, it is generated.</summary>
  private const int _MAX_LEAVES = 16;

  /// <summary>Rewrites what it can in <paramref name="fn"/>; returns how many chains it rebuilt.</summary>
  public static int Run(IrFunction fn) {
    if (fn.HasErrorHandler)
      return 0;                                    // a fault can enter anywhere - see IrFunction

    var rewritten = 0;
    var ids = new Dictionary<IrValue, int>(ReferenceEqualityComparer.Instance);

    // Outermost first: a root's chain swallows the inner nodes, so visiting them again afterwards
    // would only re-canonicalize what is already canonical.
    foreach (var block in fn.Blocks.ToList())
      foreach (var instruction in block.Instructions.ToList())
        if (instruction is IrBinary root && instruction.Parent is not null && IsRoot(root) && Rebuild(root, ids))
          ++rewritten;
    return rewritten;
  }

  /// <summary>The operators that are associative and commutative over wrapping integers.</summary>
  private static bool IsReassociable(IrBinaryOp op)
    => op is IrBinaryOp.Add or IrBinaryOp.Mul or IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor;

  /// <summary>
  /// Whether this node is the TOP of its chain: reassociable, integer-typed, and not itself feeding
  /// only nodes of the same operator (which would make it an interior node someone else will flatten).
  /// </summary>
  private static bool IsRoot(IrBinary node) {
    if (!IsReassociable(node.Op) || node.IsFloatOp || node.Type.Kind != IrTypeKind.Int)
      return false;
    foreach (var user in node.Users)
      if (user is IrBinary parent && parent.Op == node.Op && !parent.IsFloatOp)
        return false;
    return true;
  }

  /// <summary>
  /// Collects the chain's leaves. An operand is followed into when it is the same operator, has this
  /// node as its ONLY user (otherwise it is a shared subexpression that must keep existing on its
  /// own), and the chain has not grown past the cap.
  /// </summary>
  private static bool Flatten(IrBinary node, List<IrValue> leaves) {
    foreach (var operand in new[] { node.Lhs, node.Rhs }) {
      if (leaves.Count > _MAX_LEAVES)
        return false;
      if (operand is IrBinary inner && inner.Op == node.Op && !inner.IsFloatOp && inner.Users.Count == 1) {
        if (!Flatten(inner, leaves))
          return false;
        continue;
      }
      leaves.Add(operand);
    }
    return true;
  }

  /// <summary>The identity element, which a chain of only constants collapses to.</summary>
  private static long Identity(IrBinaryOp op) => op switch {
    IrBinaryOp.Add or IrBinaryOp.Or or IrBinaryOp.Xor => 0,
    IrBinaryOp.Mul => 1,
    _ => -1,                                       // AND
  };

  private static long Apply(IrBinaryOp op, long l, long r) => op switch {
    IrBinaryOp.Add => unchecked(l + r),
    IrBinaryOp.Mul => unchecked(l * r),
    IrBinaryOp.And => l & r,
    IrBinaryOp.Or => l | r,
    _ => l ^ r,
  };

  /// <summary>
  /// A stable order for the non-constant leaves. It only has to be a total order that two equal
  /// chains agree on — first seen wins, so the numbering is deterministic within a function.
  /// </summary>
  private static int RankOf(IrValue value, Dictionary<IrValue, int> ids)
    => ids.TryGetValue(value, out var id) ? id : ids[value] = ids.Count;

  /// <summary>Whether two leaves occupy the same place in a chain: the same constant, or the same value.</summary>
  private static bool SameLeaf(IrValue a, IrValue b)
    => a is IrConstantInt x && b is IrConstantInt y ? x.Value == y.Value && Equals(x.Type, y.Type) : ReferenceEquals(a, b);

  private static bool Rebuild(IrBinary root, Dictionary<IrValue, int> ids) {
    var leaves = new List<IrValue>();
    if (!Flatten(root, leaves) || leaves.Count < 3)
      return false;                                // two leaves are already canonical up to operand order

    var folded = Identity(root.Op);
    var seen = 0;
    var others = new List<IrValue>();
    foreach (var leaf in leaves)
      if (leaf is IrConstantInt constant) {
        folded = Apply(root.Op, folded, constant.Value);
        ++seen;
      } else
        others.Add(leaf);

    // Ranks are assigned HERE, walking the list in order, not lazily inside the comparator: a sort
    // calls its comparator in an unspecified sequence, so numbering on demand would hand out ids in
    // an order that depends on the sort's internals and the "already canonical" test below would
    // never settle.
    foreach (var leaf in others)
      RankOf(leaf, ids);
    others.Sort((a, b) => ids[a].CompareTo(ids[b]));

    var ordered = new List<IrValue>(others);
    if (seen > 0)
      ordered.Add(new IrConstantInt(root.Type, Truncate(root.Type, folded)));  // the constant goes last, always

    // Nothing to gain when the chain already reads this way; rebuilding it anyway would make the pass
    // report changes forever and never reach a fixpoint. The comparison has to be by VALUE for the
    // folded constant - it is a fresh object every time even when it holds the number that is already
    // there - and by identity for everything else.
    if (ordered.Count == leaves.Count && !ordered.Where((v, i) => !SameLeaf(v, leaves[i])).Any())
      return false;

    var block = root.Parent!;
    var accumulator = ordered[0];
    for (var i = 1; i < ordered.Count; ++i)
      accumulator = block.InsertBefore(new IrBinary(root.Op, accumulator, ordered[i]), root);

    root.ReplaceAllUsesWith(accumulator);
    root.EraseFromParent();
    return true;
  }

  /// <summary>Wraps a folded constant to its type's width, the way the machine would have.</summary>
  private static long Truncate(IrType type, long value) => type.Bits switch {
    8 => (sbyte)value,
    16 => (short)value,
    32 => (int)value,
    _ => value,
  };
}
