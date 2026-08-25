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
      SUB Bump(n AS INTEGER)
        n = n + 1
      END SUB
      DIM n AS INTEGER
      n = -32768
      Bump n
      PRINT n
      """).SetName("INTEGER storage"),
    new TestCaseData("""
      SUB Bump(n AS WORD)
        n = n + 1
      END SUB
      DIM n AS WORD
      n = 65534
      Bump n
      PRINT n
      """).SetName("WORD storage"),
    new TestCaseData("""
      SUB Bump(n AS LONG)
        n = n + 2
      END SUB
      DIM n AS LONG
      n = 65535
      Bump n
      PRINT n
      """).SetName("LONG storage"),
    new TestCaseData("""
      SUB Bump(n AS DWORD)
        n = n + 1
      END SUB
      DIM n AS DWORD
      n = 4000000000
      Bump n
      PRINT n
      """).SetName("DWORD storage"),
    new TestCaseData("""
      SUB Bump(n AS SINGLE)
        n = n + .25
      END SUB
      DIM n AS SINGLE
      n = 1.5
      Bump n
      PRINT n
      """).SetName("SINGLE storage"),
    new TestCaseData("""
      SUB Bump(n AS DOUBLE)
        n = n + .125
      END SUB
      DIM n AS DOUBLE
      n = 1.5
      Bump n
      PRINT n
      """).SetName("DOUBLE storage"),
  ];

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static (Cpu8086 Direct, Cpu8086 Routed, IReadOnlyList<string> RoutedNames) Execute(
      string source, bool optimize, bool optimizeSpeed = false) {
    var direct = new CodeGenerator(Bind(source)) {
      Optimize = optimize,
      OptimizeSpeed = optimizeSpeed,
      UseExperimentalBackend = false,
    };
    var routed = new CodeGenerator(Bind(source)) {
      Optimize = optimize,
      OptimizeSpeed = optimizeSpeed,
      UseExperimentalBackend = true,
    };
    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.Multiple(() => {
      Assert.That(direct.Errors, Is.Empty, "direct: " + string.Join("; ", direct.Errors));
      Assert.That(routed.Errors, Is.Empty, "routed: " + string.Join("; ", routed.Errors));
    });
    return (directCpu, routedCpu, routed.BackendRoutedNames.ToList());
  }

  [TestCaseSource(nameof(_numericCases))]
  public void Execute_GivenANearNumericByRefParameter_WhenTheCalleeMutatesIt_ThenTheWriteReachesTheCaller(
      string source) {
    foreach (var optimize in new[] { false, true }) {
      var (direct, routed, routedNames) = Execute(source, optimize);

      Assert.Multiple(() => {
        Assert.That(routedNames, Does.Contain("Bump"), $"the BYREF callee did not route (optimize={optimize})");
        Assert.That((routed.Output, routed.ExitCode), Is.EqualTo((direct.Output, direct.ExitCode)),
          $"the routed BYREF write changed behavior (optimize={optimize})");
      });
    }
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenTwoByRefParametersAliasingOneCell_WhenTheCalleeWritesBoth_ThenTheAliasSurvives(
      bool optimize) {
    const string source = """
      SUB Mutate(a AS INTEGER, b AS INTEGER)
        a = 10
        b = b + 1
      END SUB
      DIM value AS INTEGER
      value = 1
      Mutate value, value
      PRINT value
      """;

    var (direct, routed, routedNames) = Execute(source, optimize);

    Assert.Multiple(() => {
      Assert.That(routedNames, Does.Contain("Mutate"), "the aliasing BYREF callee did not route");
      Assert.That(routed.Output, Is.EqualTo(direct.Output));
      Assert.That(routed.Output.Trim(), Is.EqualTo("11"));
    });
  }

  [Test]
  public void Execute_GivenSpeedOptimization_WhenMainCallsAByRefProcedure_ThenBothSidesRouteWithTheStackAbi() {
    const string source = """
      SUB Bump(value AS LONG)
        value = value + 1
      END SUB
      DIM value AS LONG
      value = 41
      Bump value
      PRINT value
      """;

    var (direct, routed, routedNames) = Execute(source, optimize: true, optimizeSpeed: true);

    Assert.Multiple(() => {
      Assert.That(routedNames, Does.Contain("main"));
      Assert.That(routedNames, Does.Contain("Bump"),
        "SPEED may not leave a register-converted direct callee behind a stack-ABI caller");
      Assert.That(routed.Output, Is.EqualTo(direct.Output));
      Assert.That(routed.Output.Trim(), Is.EqualTo("42"));
    });
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenARecursiveByRefCall_WhenParametersAreForwarded_ThenTheOriginalCellsAreMutated(
      bool optimize) {
    const string source = """
      SUB CountDown(n AS INTEGER, total AS LONG)
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

    var (direct, routed, routedNames) = Execute(source, optimize);

    Assert.Multiple(() => {
      Assert.That(routedNames, Does.Contain("CountDown"), "the recursive BYREF callee did not route");
      Assert.That(routed.Output, Is.EqualTo(direct.Output));
      Assert.That(routed.Output.Replace(" ", "").Trim(), Is.EqualTo("06"));
    });
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Route_GivenAFarDynamicArrayElementPassedByRef_ThenItDeclinesRatherThanDroppingTheSegment(
      bool optimize) {
    const string source = """
      REDIM values%(0 TO 7)
      values%(2) = 10
      Bump values%(2)
      PRINT values%(2)
      SUB Bump(value AS INTEGER) NOINLINE
        value = value + 1
      END SUB
      """;
    var generator = new CodeGenerator(Bind(source)) {
      Optimize = optimize,
      UseExperimentalBackend = true,
    };

    generator.EmitExecutable();

    Assert.Multiple(() => {
      Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
      Assert.That(generator.BackendRoutedNames, Does.Not.Contain("main"),
        "a far element cannot enter a near-pointer call");
      Assert.That(generator.BackendRoutedNames, Does.Not.Contain("Bump"),
        "a whole-module lowering decline must not leave a partly routed callee");
      Assert.That(generator.BackendDeclines.Select(d => d.Reason),
        Has.Some.Contains("far pointer passed BYREF"));
    });
  }
}
