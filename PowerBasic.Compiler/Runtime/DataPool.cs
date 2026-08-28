using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// The order <c>DATA</c> items and their labels appear in a module, read once and shared by both code
/// generators.
///
/// <para>
/// This lives beside <see cref="UsingFormat"/> and for the same reason: it is a CONTRACT, not a
/// convenience. <c>DATA</c> is not executable - it contributes to a pool the program reads with
/// <c>READ</c> and rewinds with <c>RESTORE</c>, and both back ends must build the SAME pool from the
/// same source. Two readings of that agree with each other only until somebody touches one of them,
/// and the differential harness compares the two emitters against each other, so a shared misreading
/// would at least be visible while a divergent one is a wrong answer in whichever path is asked.
/// </para>
/// <para>
/// It was divergent. The direct emitter walked <c>MainBody</c> and nothing else, so a <c>DATA</c>
/// statement written inside an <c>IF</c>, <c>FOR</c>, <c>DO</c> or <c>SELECT</c> block was silently
/// absent from its pool while the IR lowering, which recursed, had it. Genuine PBC 3.50 collects them
/// (checked with <c>scripts/diff-one.sh</c>), so the direct build read the wrong items or ran out of
/// data altogether, and <c>RESTORE</c> to a label written inside a block was refused as unsupported.
/// A block is where a <c>DATA</c> statement naturally lands when it is written next to the code that
/// reads it, and no corpus program does it, which is why this stood.
/// </para>
/// </summary>
internal static class DataPool {

  /// <summary>One pool contribution in source order: a label that names the offset reached so far, or an item.</summary>
  internal readonly record struct Entry(string? Label, string? Item);

  /// <summary>
  /// Every <c>DATA</c> item and every label, in source order, descending into every block a statement
  /// carries. The caller does its own byte accounting - the two back ends store the pool differently
  /// and only the ORDER has to be one thing.
  /// </summary>
  internal static IEnumerable<Entry> Walk(IReadOnlyList<Statement> statements) {
    foreach (var statement in statements)
      switch (statement) {
        case LabelStmt label:
          yield return new(label.Name, null);
          break;

        case DataStmt data:
          foreach (var item in data.Items)
            yield return new(null, item);
          break;

        case IfStmt i:
          foreach (var entry in Walk(i.Then))
            yield return entry;
          foreach (var (_, body) in i.ElseIfs)
            foreach (var entry in Walk(body))
              yield return entry;
          if (i.Else is { } otherwise)
            foreach (var entry in Walk(otherwise))
              yield return entry;
          break;

        case SelectStmt select:
          foreach (var arm in select.Arms)
            foreach (var entry in Walk(arm.Body))
              yield return entry;
          break;

        case ForStmt f:
          foreach (var entry in Walk(f.Body))
            yield return entry;
          break;

        case ForEachStmt fe:
          foreach (var entry in Walk(fe.Body))
            yield return entry;
          break;

        case DoLoopStmt dl:
          foreach (var entry in Walk(dl.Body))
            yield return entry;
          break;

        case GroupStmt g:
          foreach (var entry in Walk(g.Body))
            yield return entry;
          break;

        case TryStmt t:
          foreach (var entry in Walk(t.Body))
            yield return entry;
          if (t.Catch is { } caught)
            foreach (var entry in Walk(caught))
              yield return entry;
          if (t.Finally is { } cleanup)
            foreach (var entry in Walk(cleanup))
              yield return entry;
          break;
      }
  }
}
