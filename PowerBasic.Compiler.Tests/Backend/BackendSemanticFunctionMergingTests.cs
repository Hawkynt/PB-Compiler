using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>End-to-end coverage for O0284's ABI-preserving native x86 entry-thunk form.</summary>
[TestFixture]
public sealed class BackendSemanticFunctionMergingTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(
      Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36),
      Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  [Test]
  public void Execute_GivenSemanticMergeUnderOptimizeSize_WhenRouted_ThenSharedHelperMatchesDirectEmitter() {
    const string source = """
      $OPTIMIZE SIZE
      DECLARE FUNCTION First%(BYVAL x%)
      DECLARE FUNCTION Second%(BYVAL x%)

      PRINT First%(10)
      PRINT Second%(10)
      PRINT First%(-3)
      PRINT Second%(-3)
      END

      FUNCTION First%(BYVAL x%)
        y% = x% + 3
        y% = y% XOR 257
        y% = y% + 17
        y% = y% XOR 513
        y% = y% - 9
        y% = y% XOR 1025
        y% = y% + 23
        y% = y% XOR 2049
        First% = y% - 31
      END FUNCTION

      FUNCTION Second%(BYVAL x%)
        y% = x% + 7
        y% = y% XOR 257
        y% = y% + 17
        y% = y% XOR 513
        y% = y% - 9
        y% = y% XOR 1025
        y% = y% + 23
        y% = y% XOR 2049
        Second% = y% - 31
      END FUNCTION
      """;

    static (byte[] Image, CodeGenerator Generator) Compile(string source, bool routed) {
      var generator = new CodeGenerator(Bind(source)) {
        Optimize = true,
        OptimizeSize = true,
        UseExperimentalBackend = routed,
      };
      var image = generator.EmitExecutable();
      Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
      return (image, generator);
    }

    var direct = Compile(source, routed: false);
    var routed = Compile(source, routed: true);
    var directResult = Cpu8086.Run(direct.Image);
    var routedResult = Cpu8086.Run(routed.Image);
    var routes = routed.Generator.BackendRoutedNames.ToList();
    var helpers = routed.Generator.BackendSemanticMergeHelpers.ToList();

    Assert.Multiple(() => {
      Assert.That(routes, Does.Contain("First").And.Contain("Second"),
        "both public entry thunks must stay on the native backend");
      Assert.That(helpers, Has.Count.EqualTo(1),
        "O0284 must replace the two native bodies with one private parameterized helper");
      Assert.That(helpers[0], Does.StartWith("__o0284_").IgnoreCase);
      Assert.That((routedResult.Output, routedResult.ExitCode),
        Is.EqualTo((directResult.Output, directResult.ExitCode)),
        "the thunk/helper ABI must be observationally identical to the direct emitter");
      Assert.That(routedResult.Output, Is.Not.Empty);
    });
  }

  [Test]
  public void Execute_GivenVaryingCallTargetsUnderOptimizeSize_WhenRouted_ThenIndirectHelperMatchesDirectEmitter() {
    const string source = """
      $OPTIMIZE SIZE
      DECLARE FUNCTION PlusOne%(BYVAL x%)
      DECLARE FUNCTION MinusOne%(BYVAL x%)
      DECLARE FUNCTION First%(BYVAL x%)
      DECLARE FUNCTION Second%(BYVAL x%)

      PRINT First%(10)
      PRINT Second%(10)
      PRINT First%(-3)
      PRINT Second%(-3)
      END

      FUNCTION PlusOne%(BYVAL x%) NOINLINE
        PlusOne% = x% + 1
      END FUNCTION

      FUNCTION MinusOne%(BYVAL x%) NOINLINE
        MinusOne% = x% - 1
      END FUNCTION

      FUNCTION First%(BYVAL x%)
        y% = PlusOne%(x%)
        y% = y% XOR 257
        y% = y% + 17
        y% = y% XOR 513
        y% = y% - 9
        y% = y% XOR 1025
        y% = y% + 23
        y% = y% XOR 2049
        First% = y% - 31
      END FUNCTION

      FUNCTION Second%(BYVAL x%)
        y% = MinusOne%(x%)
        y% = y% XOR 257
        y% = y% + 17
        y% = y% XOR 513
        y% = y% - 9
        y% = y% XOR 1025
        y% = y% + 23
        y% = y% XOR 2049
        Second% = y% - 31
      END FUNCTION
      """;

    static (byte[] Image, CodeGenerator Generator) Compile(string source, bool routed) {
      var generator = new CodeGenerator(Bind(source)) {
        Optimize = true,
        OptimizeSize = true,
        UseExperimentalBackend = routed,
      };
      var image = generator.EmitExecutable();
      Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
      return (image, generator);
    }

    var direct = Compile(source, routed: false);
    var routed = Compile(source, routed: true);
    var directResult = Cpu8086.Run(direct.Image);
    var routedResult = Cpu8086.Run(routed.Image);
    var routes = routed.Generator.BackendRoutedNames.ToList();
    var helpers = routed.Generator.BackendSemanticMergeHelpers.ToList();

    Assert.Multiple(() => {
      Assert.That(routes, Does.Contain("First").And.Contain("Second"),
        "both call-target thunks must stay on the native backend");
      Assert.That(helpers, Has.Count.EqualTo(1),
        "the differing direct callees must become one indirect call in the shared helper");
      Assert.That((routedResult.Output, routedResult.ExitCode),
        Is.EqualTo((directResult.Output, directResult.ExitCode)),
        "near-indirect O0284 dispatch must preserve direct-emitter behavior");
      Assert.That(routedResult.Output, Is.Not.Empty);
    });
  }

}
