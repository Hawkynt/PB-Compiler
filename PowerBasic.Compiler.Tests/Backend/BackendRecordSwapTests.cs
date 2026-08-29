using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// <c>SWAP</c> of two RECORDS on the x86-16 back end.
///
/// <para>
/// A record has no single value to load, so the exchange is three block copies through a frame
/// temporary where a scalar is a load/store pair. The direct emitter exchanges the bytes in place
/// with <c>rt_swap</c> instead; the two sequences are different instructions for the same observable
/// move, which is what these tests compare. Before this, <c>SWAP p, q</c> over a UDT reached
/// <c>LValue</c>, which knows only scalars, and the "unsupported lvalue" it raised took the whole
/// module off the IR path - not just the statement.
/// </para>
/// <para>
/// The values come out of a FILE. A record initialised from literals is a record SCCP can carry
/// through the swap, and the two builds then agree about a program in which nothing moved.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendRecordSwapTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static (string Direct, string Routed, IReadOnlyList<string> RoutedNames) RunBothWays(string source, bool optimize) {
    var direct = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = true };
    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();
    Assert.That(direct.Errors, Is.Empty, "direct: " + string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, "routed: " + string.Join("; ", routed.Errors));
    return (Cpu8086.Run(directImage).Output, Cpu8086.Run(routedImage).Output,
      routed.BackendRoutedNames.ToList());
  }

  private const string _Prologue = """
    TYPE R
      a AS INTEGER
      b AS LONG
      c AS STRING * 4
    END TYPE
    OPEN "IN.TXT" FOR OUTPUT AS #1
    PRINT #1, "1"
    PRINT #1, "2"
    CLOSE #1
    DIM n AS INTEGER
    OPEN "IN.TXT" FOR INPUT AS #1
    INPUT #1, n

    """;

  [TestCase(true, TestName = "Run_GivenTwoRecords_WhenOptimized_ThenBothPathsExchangeEveryField")]
  [TestCase(false, TestName = "Run_GivenTwoRecords_WhenUnoptimized_ThenBothPathsExchangeEveryField")]
  public void Run_GivenTwoRecords_ThenBothPathsExchangeEveryField(bool optimize) {
    var (direct, routed, names) = RunBothWays(_Prologue + """
      DIM p AS R, q AS R
      p.a = n     : p.b = n * 100  : p.c = "aa"
      q.a = n + 1 : q.b = n * 200  : q.c = "bb"
      SWAP p, q
      PRINT p.a; p.b; p.c; q.a; q.b; q.c
      END
      """, optimize);

    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("main"), "the module body did not route - nothing was measured");
      Assert.That(routed, Is.EqualTo(direct));
      Assert.That(routed.Trim(), Is.EqualTo("2  200 bb   1  100 aa"),
        "every field must cross, including the fixed-length string");
    });
  }

  /// <summary>
  /// The degenerate case the three-copy form exists to keep correct: both operands name the SAME
  /// storage. An exchange with itself must leave the record as it was; a two-copy version would
  /// overwrite the source with the destination and lose it.
  /// </summary>
  [TestCase(true, TestName = "Run_GivenARecordSwappedWithItself_WhenOptimized_ThenItIsUnchanged")]
  [TestCase(false, TestName = "Run_GivenARecordSwappedWithItself_WhenUnoptimized_ThenItIsUnchanged")]
  public void Run_GivenARecordSwappedWithItself_ThenItIsUnchanged(bool optimize) {
    var (direct, routed, names) = RunBothWays(_Prologue + """
      DIM p AS R
      p.a = n : p.b = n * 100 : p.c = "aa"
      SWAP p, p
      PRINT p.a; p.b; p.c
      END
      """, optimize);

    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("main"), "the module body did not route - nothing was measured");
      Assert.That(routed, Is.EqualTo(direct));
      Assert.That(routed.Trim(), Is.EqualTo("1  100 aa"));
    });
  }

  /// <summary>Two elements of a record ARRAY, which reach their storage through a subscript.</summary>
  [TestCase(true, TestName = "Run_GivenTwoRecordArrayElements_WhenOptimized_ThenBothPathsExchangeThem")]
  [TestCase(false, TestName = "Run_GivenTwoRecordArrayElements_WhenUnoptimized_ThenBothPathsExchangeThem")]
  public void Run_GivenTwoRecordArrayElements_ThenBothPathsExchangeThem(bool optimize) {
    var (direct, routed, names) = RunBothWays(_Prologue + """
      DIM t(1 TO 3) AS R
      t(1).a = n     : t(1).b = n * 10
      t(2).a = n + 5 : t(2).b = n * 20
      SWAP t(1), t(2)
      PRINT t(1).a; t(1).b; t(2).a; t(2).b
      END
      """, optimize);

    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("main"), "the module body did not route - nothing was measured");
      Assert.That(routed, Is.EqualTo(direct));
      Assert.That(routed.Trim(), Is.EqualTo("6  20  1  10"));
    });
  }
}
