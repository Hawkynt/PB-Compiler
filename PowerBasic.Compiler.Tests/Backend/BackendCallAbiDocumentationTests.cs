using System.Text;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// <c>docs/BACKENDS.md</c> states, per calling convention, which registers carry the leading arguments,
/// which way the stack arguments are pushed and who cleans them up. That statement was wrong for a
/// week: it said FASTCALL/WATCALL calls "still decline per callee until register-argument staging is
/// selectable" while the selector had been staging those registers since the sentence beside it was
/// written, and the definition side had shipped too. Nothing caught it, because a sentence has no way
/// of noticing that the code under it moved.
///
/// <para>
/// So the status is a table now rather than prose, and the table is re-derived here from
/// <see cref="X86CallAbi"/> rather than proof-read. A descriptor that changes, or a convention added
/// without a row, fails this fixture - which is the only reason the document can be trusted between
/// the day somebody edits it and the day somebody reads it.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendCallAbiDocumentationTests {

  private static readonly string _root = Path.GetFullPath(Path.Combine(
    TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  /// <summary>
  /// The conventions a program can DECLARE. <see cref="IrCallConvention.BasicClosure"/> is deliberately
  /// absent: it is a call-site-only identity the pb36 delegate lowering synthesizes, it has no source
  /// spelling for a reader to look up, and it is the one far descriptor here.
  /// </summary>
  private static readonly IrCallConvention[] _declarable = [
    IrCallConvention.Basic, IrCallConvention.Pascal, IrCallConvention.Cdecl,
    IrCallConvention.Stdcall, IrCallConvention.Fastcall, IrCallConvention.Watcall,
  ];

  /// <summary>The row the document should carry for <paramref name="convention"/>, built from its descriptor.</summary>
  private static string Row(IrCallConvention convention) {
    var abi = X86CallAbi.For(convention);
    var registers = abi.ArgumentRegisters.Count == 0 ? "-" : string.Join(", ", abi.ArgumentRegisters);
    var order = abi.StackArgumentOrder == X86StackArgumentOrder.LeftToRight ? "left-to-right" : "right-to-left";
    var cleanup = abi.StackCleanup == X86StackCleanup.Caller ? "caller" : "callee";
    return $"| `{convention.ToString().ToUpperInvariant()}` | {registers} | {order} | {cleanup} |";
  }

  /// <summary>
  /// The marked table, normalized to one space around each cell so that re-flowing the document does
  /// not fail the comparison - the claim is what the cells say, not how they are padded.
  /// </summary>
  private static IReadOnlyList<string> DocumentedRows() {
    var lines = File.ReadAllLines(Path.Combine(_root, "docs", "BACKENDS.md"));
    var marker = Array.FindIndex(lines, line => line.Contains("<!-- x86-call-abi:", StringComparison.Ordinal));
    Assert.That(marker, Is.GreaterThanOrEqualTo(0),
      "docs/BACKENDS.md lost the x86-call-abi marker this fixture reads its table from");

    return lines.Skip(marker + 1)
      .Take(4 + _declarable.Length)               // a blank line, the header, its rule, then the rows
      .SkipWhile(line => !line.StartsWith('|'))
      .TakeWhile(line => line.StartsWith('|'))
      .Select(line => "| " + string.Join(" | ", line.Trim('|', ' ').Split('|').Select(cell => cell.Trim())) + " |")
      .Where(line => !line.Contains("---", StringComparison.Ordinal))
      .Skip(1)                                    // the header row
      .ToList();
  }

  [Test]
  public void Table_GivenEveryDeclarableConvention_ThenTheDocumentedRowIsItsDescriptor() {
    var documented = DocumentedRows();
    var disagreeing = new StringBuilder();
    foreach (var convention in _declarable) {
      var expected = Row(convention);
      var actual = documented.FirstOrDefault(row =>
        row.StartsWith($"| `{convention.ToString().ToUpperInvariant()}` ", StringComparison.Ordinal));
      if (actual is null)
        disagreeing.AppendLine($"  {convention}: no row; expected {expected}");
      else if (actual != expected)
        disagreeing.AppendLine($"  {convention}: doc says {actual}, X86CallAbi says {expected}");
    }

    Assert.That(disagreeing.ToString(), Is.Empty,
      "the table is a view of X86CallAbi; change the descriptor, then the table");
  }

  /// <summary>
  /// The other direction, and the one that catches a convention being RETIRED: a row nobody derives
  /// any more reads exactly as true as the five beside it.
  /// </summary>
  [Test]
  public void Table_GivenTheDocumentedRows_ThenTheyNameTheDeclarableConventionsAndNothingElse() {
    var named = DocumentedRows()
      .Select(row => row.Split('`') is [_, var name, ..] ? name : row)
      .ToList();
    var expected = _declarable.Select(c => c.ToString().ToUpperInvariant()).ToList();

    Assert.Multiple(() => {
      Assert.That(named.Except(expected, StringComparer.Ordinal), Is.Empty,
        "the table has a row for something no source program can declare");
      Assert.That(expected.Except(named, StringComparer.Ordinal), Is.Empty,
        "a declarable convention has no row, so the document is silent about an ABI it selects");
      Assert.That(named, Is.Unique, "one convention, one row");
    });
  }
}
