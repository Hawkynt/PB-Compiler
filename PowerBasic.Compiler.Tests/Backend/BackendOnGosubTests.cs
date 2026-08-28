using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// <c>ON n GOSUB</c> on the routed path. It is <c>ON n GOTO</c>'s dispatch over the <c>GOSUB</c>
/// machinery, and the join between the two is where it can go wrong: the return id has to be pushed
/// in the ARM rather than in front of the switch, because the default arm is a fall-through that
/// never returns and an id pushed on that path would be popped by whatever <c>RETURN</c> came next.
///
/// <para>
/// The selector is read out of <c>DATA</c> in every fixture here, and each program dispatches exactly
/// once. Written as a literal the selector folds - <c>SCCP</c> resolves the dispatch outright and the
/// test then measures a <c>GOTO</c> - and <c>READ</c> takes its value through the runtime's own data
/// cursor, which no pass sees through. Each case runs with the optimizer both off and on, because the
/// unoptimized build is the one with no passes to rescue the selection.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendOnGosubTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36),
      Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static (Cpu8086 Direct, Cpu8086 Routed, IReadOnlyList<string> RoutedNames) Execute(
      string source, bool optimize) {
    var direct = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = true };
    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());
    Assert.Multiple(() => {
      Assert.That(direct.Errors, Is.Empty, "direct: " + string.Join("; ", direct.Errors));
      Assert.That(routed.Errors, Is.Empty, "routed: " + string.Join("; ", routed.Errors));
    });
    return (directCpu, routedCpu, routed.BackendRoutedNames.ToList());
  }

  /// <summary>The printed numbers, free of PB's sign slot and column padding.</summary>
  private static string Numbers(string output)
    => string.Join(" ", output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

  private static string DispatchesOnce(int selector) => $"""
    DIM n AS INTEGER, hit AS INTEGER
    READ n
    hit = 0
    ON n GOSUB one, two, three
    hit = hit + 100
    PRINT n; hit
    END
    one:
      hit = 1
      RETURN
    two:
      hit = 2
      RETURN
    three:
      hit = 3
      RETURN
    DATA {selector}
    """;

  /// <summary>
  /// The fall-through path followed by a plain <c>GOSUB</c>. A return id pushed in front of the
  /// dispatch survives an out-of-range selector, and this <c>RETURN</c> then comes back to the
  /// <c>ON</c> statement instead of to the <c>GOSUB</c> beneath it - so the build either loops or
  /// prints the wrong number, and never merely leaks. The taken arms nest a second <c>GOSUB</c> inside
  /// the dispatched one, which is the other order the shadow stack has to keep.
  /// </summary>
  private static string DispatchesThenGosubs(int selector) => $"""
    DIM n AS INTEGER, r AS INTEGER
    READ n
    r = 0
    ON n GOSUB armA, armB
    GOSUB tail
    PRINT n; r
    END
    armA:
      r = r + 10
      GOSUB tail
      RETURN
    armB:
      r = r + 20
      RETURN
    tail:
      r = r + 1
      RETURN
    DATA {selector}
    """;

  /// <summary>Inside a procedure, where the labels and the return stack are the procedure's own.</summary>
  private const string _dispatchesInsideAProcedure = """
    SUB Pick(BYVAL n AS INTEGER)
      DIM hit AS INTEGER
      hit = 0
      ON n GOSUB one, two
      hit = hit + 100
      PRINT n; hit
      EXIT SUB
    one:
      hit = 1
      RETURN
    two:
      hit = 2
      RETURN
    END SUB
    DIM n AS INTEGER, i AS INTEGER
    FOR i = 1 TO 4
      READ n
      Pick n
    NEXT i
    DATA 0, 1, 2, 3
    """;

  // selector, then what the program must print: an in-range arm sets its own number and RETURNs to
  // the statement after the dispatch; 0, 4 and -1 reach that statement without an arm having run
  [TestCase(0, "0 100")]
  [TestCase(1, "1 101")]
  [TestCase(2, "2 102")]
  [TestCase(3, "3 103")]
  [TestCase(4, "4 100")]
  [TestCase(-1, "-1 100")]
  public void Execute_GivenAnOnGosubSelector_WhenRouted_ThenItDispatchesAndReturnsLikeTheDirectBuild(
      int selector, string expected) {
    foreach (var optimize in new[] { false, true }) {
      var (direct, routed, routedNames) = Execute(DispatchesOnce(selector), optimize);

      Assert.Multiple(() => {
        Assert.That(routedNames, Does.Contain("main"), $"the module body did not route (optimize={optimize})");
        Assert.That(Numbers(routed.Output), Is.EqualTo(Numbers(direct.Output)),
          $"routed and direct disagree (optimize={optimize})");
        // spelled out as well, so a change that made BOTH paths wrong the same way still fails
        Assert.That(Numbers(routed.Output), Is.EqualTo(expected), $"optimize={optimize}");
      });
    }
  }

  [TestCase(0, "0 1")]
  [TestCase(1, "1 12")]
  [TestCase(2, "2 21")]
  [TestCase(9, "9 1")]
  public void Execute_GivenAPlainGosubAfterTheDispatch_WhenRouted_ThenTheReturnStackStaysBalanced(
      int selector, string expected) {
    foreach (var optimize in new[] { false, true }) {
      var (direct, routed, routedNames) = Execute(DispatchesThenGosubs(selector), optimize);

      Assert.Multiple(() => {
        Assert.That(routedNames, Does.Contain("main"), $"the module body did not route (optimize={optimize})");
        Assert.That(Numbers(routed.Output), Is.EqualTo(Numbers(direct.Output)),
          $"routed and direct disagree (optimize={optimize})");
        Assert.That(Numbers(routed.Output), Is.EqualTo(expected), $"optimize={optimize}");
      });
    }
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenAnOnGosubInsideAProcedure_WhenRouted_ThenTheProcedureRoutesAndAgrees(
      bool optimize) {
    var (direct, routed, routedNames) = Execute(_dispatchesInsideAProcedure, optimize);

    Assert.Multiple(() => {
      Assert.That(routedNames, Does.Contain("Pick"), "the procedure holding the dispatch did not route");
      Assert.That(Numbers(routed.Output), Is.EqualTo(Numbers(direct.Output)));
      Assert.That(Numbers(routed.Output), Is.EqualTo("0 100 1 101 2 102 3 100"));
    });
  }
}
