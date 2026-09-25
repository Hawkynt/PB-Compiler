using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// End-to-end coverage for near numeric BYREF parameters on the routed x86-16 stack ABI. The IR
/// argument is the caller's address, not a copied value: reads and writes in the callee must therefore
/// dereference it, aliases must stay aliases through optimization, and forwarding it recursively must
/// pass the original address rather than the callee's pointer cell.
/// </summary>
[TestFixture]
public sealed class BackendByRefRoutingTests {

  private static readonly TestCaseData[] _numericCases = [
    new TestCaseData("""
      SUB Bump(n AS INTEGER) NOINLINE
        n = n + 1
      END SUB
      DIM n AS INTEGER
      n = -32768
      Bump n
      PRINT n
      """, "-32767").SetName("INTEGER storage"),
    new TestCaseData("""
      SUB Bump(n AS WORD) NOINLINE
        n = n + 1
      END SUB
      DIM n AS WORD
      n = 65534
      Bump n
      PRINT n
      """, "65535").SetName("WORD storage"),
    new TestCaseData("""
      SUB Bump(n AS LONG) NOINLINE
        n = n + 2
      END SUB
      DIM n AS LONG
      n = 65535
      Bump n
      PRINT n
      """, "65537").SetName("LONG storage"),
    new TestCaseData("""
      SUB Bump(n AS DWORD) NOINLINE
        n = n + 1
      END SUB
      DIM n AS DWORD
      n = 4000000000
      Bump n
      PRINT n
      """, "4000000001").SetName("DWORD storage"),
    new TestCaseData("""
      SUB Bump(n AS SINGLE) NOINLINE
        n = n + .25
      END SUB
      DIM n AS SINGLE
      n = 1.5
      Bump n
      PRINT n
      """, "1.75").SetName("SINGLE storage"),
    new TestCaseData("""
      SUB Bump(n AS DOUBLE) NOINLINE
        n = n + .125
      END SUB
      DIM n AS DOUBLE
      n = 1.5
      Bump n
      PRINT n
      """, "1.625").SetName("DOUBLE storage"),
  ];

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static (Cpu8086 Routed, IReadOnlyList<string> RoutedNames) Execute(
      string source, bool optimize, bool optimizeSpeed = false) {
    var routed = new CodeGenerator(Bind(source)) {
      Optimize = optimize,
      OptimizeSpeed = optimizeSpeed,
    };
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(routed.Errors, Is.Empty, "routed: " + string.Join("; ", routed.Errors));
    return (routedCpu, routed.BackendRoutedNames.ToList());
  }

  [TestCaseSource(nameof(_numericCases))]
  public void Execute_GivenANearNumericByRefParameter_WhenTheCalleeMutatesIt_ThenTheWriteReachesTheCaller(
      string source, string expected) {
    foreach (var optimize in new[] { false, true }) {
      var (routed, routedNames) = Execute(source, optimize);

      Assert.Multiple(() => {
        Assert.That(routedNames, Does.Contain("Bump"), $"the BYREF callee did not route (optimize={optimize})");
        Assert.That((routed.Output.Trim(), routed.ExitCode), Is.EqualTo((expected, 0)),
          $"the callee's BYREF write did not reach the caller (optimize={optimize})");
      });
    }
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenTwoByRefParametersAliasingOneCell_WhenTheCalleeWritesBoth_ThenTheAliasSurvives(
      bool optimize) {
    const string source = """
      SUB Mutate(a AS INTEGER, b AS INTEGER) NOINLINE
        a = 10
        b = b + 1
      END SUB
      DIM value AS INTEGER
      value = 1
      Mutate value, value
      PRINT value
      """;

    var (routed, routedNames) = Execute(source, optimize);

    Assert.Multiple(() => {
      Assert.That(routedNames, Does.Contain("Mutate"), "the aliasing BYREF callee did not route");
      Assert.That(routed.Output.Trim(), Is.EqualTo("11"));
    });
  }

  [Test]
  public void Execute_GivenSpeedOptimization_WhenMainCallsAByRefProcedure_ThenCallerAndCalleeRouteWithTheStackAbi() {
    const string source = """
      SUB Bump(value AS LONG) NOINLINE
        value = value + 1
      END SUB
      DIM value AS LONG
      value = 41
      Bump value
      PRINT value
      """;

    var (routed, routedNames) = Execute(source, optimize: true, optimizeSpeed: true);

    Assert.Multiple(() => {
      Assert.That(routedNames, Does.Contain("main"));
      Assert.That(routedNames, Does.Contain("Bump"),
        "SPEED may not leave a register-converted callee behind a stack-ABI caller");
      Assert.That(routed.Output.Trim(), Is.EqualTo("42"));
    });
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenARecursiveByRefCall_WhenParametersAreForwarded_ThenTheOriginalCellsAreMutated(
      bool optimize) {
    const string source = """
      SUB CountDown(n AS INTEGER, total AS LONG) NOINLINE
        IF n <= 0 THEN EXIT SUB
        total = total + n
        n = n - 1
        CountDown n, total
      END SUB
      DIM n AS INTEGER, total AS LONG
      n = 3
      total = 0
      CountDown n, total
      PRINT n; total
      """;

    var (routed, routedNames) = Execute(source, optimize);

    Assert.Multiple(() => {
      Assert.That(routedNames, Does.Contain("CountDown"), "the recursive BYREF callee did not route");
      Assert.That(routed.Output.Replace(" ", "").Trim(), Is.EqualTo("06"));
    });
  }

  /// <summary>
  /// A DYNAMIC array's element passed BYREF. Its storage is in the far heap, so its address is not
  /// something a near-pointer parameter can receive: the callee would read the offset through
  /// <c>DS</c> and reach the program's own data.
  ///
  /// <para>
  /// The direct emitter's answer was a hidden stack temp, copy-IN only - its BYREF push takes an
  /// address only of a NEAR lvalue and copies anything else - so the callee's write lands in the temp
  /// and is discarded. The routed path does the same rather than declining the whole module, so the
  /// program prints 10: nothing writes the element back.
  /// </para>
  /// </summary>
  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenAFarDynamicArrayElementPassedByRef_ThenTheArgumentIsCopiedInOnly(bool optimize) {
    const string source = """
      REDIM values%(0 TO 7)
      values%(2) = 10
      Bump values%(2)
      PRINT values%(2)
      SUB Bump(value AS INTEGER) NOINLINE
        value = value + 1
      END SUB
      """;

    var (routed, routedNames) = Execute(source, optimize);

    Assert.Multiple(() => {
      Assert.That(routedNames, Does.Contain("main"), "a far element no longer takes the module body with it");
      Assert.That(routed.Output.Trim(), Is.EqualTo("10"), "copy-in only: the callee's write is discarded");
    });
  }
}
