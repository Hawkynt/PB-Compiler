using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>One signed IDIV supplies both adjacent quotient and remainder results.</summary>
[TestFixture]
public sealed class BackendDivRemTests {

  private const string _program = """
    $OPTIMIZE SPEED
    DECLARE SUB DivideBoth(BYVAL n AS INTEGER, BYVAL d AS INTEGER)

    DivideBoth 17, 5
    DivideBoth -17, 5
    DivideBoth 17, -5
    DivideBoth -17, -5
    END

    SUB DivideBoth(BYVAL n AS INTEGER, BYVAL d AS INTEGER) NOINLINE
      DIM q AS INTEGER, r AS INTEGER
      q = n \ d
      r = n MOD d
      PRINT q; r
    END SUB
    """;

  private static SemanticModel Bind() {
    var model = Binder.Bind(
      Parser.Parse(Lexer.Tokenize(_program, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36),
      Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  [Test]
  public void Execute_GivenAdjacentSignedDivAndMod_WhenRouted_ThenOneIdivSuppliesBothResults() {
    var direct = new CodeGenerator(Bind()) {
      Optimize = true,
      OptimizeSpeed = true,
      UseExperimentalBackend = false,
    };
    var routed = new CodeGenerator(Bind()) {
      Optimize = true,
      OptimizeSpeed = true,
      UseExperimentalBackend = true,
    };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedImage = routed.EmitExecutable();
    var routedCpu = Cpu8086.Run(routedImage);

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.Multiple(() => {
      Assert.That(routed.BackendRoutedNames, Is.SupersetOf(new[] { "DivideBoth", "main" }),
        "the quotient/remainder producer and its callers must stay routed");
      Assert.That(CountIdiv(routedImage), Is.EqualTo(1), "the procedure contains one IDIV, not one per result");
      Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
      Assert.That(Normalize(routedCpu.Output), Is.EqualTo("3 2|-3 -2|-3 2|3 -2"));
    });
  }

  private static int CountIdiv(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0xF7 && (image[i + 1] & 0x38) == 0x38)
        ++count;
    return count;
  }

  private static string Normalize(string output)
    => string.Join("|", output.Trim().Split(["\r\n", "\n"], StringSplitOptions.None)
      .Select(line => string.Join(" ", line.Split(' ', StringSplitOptions.RemoveEmptyEntries))));
}
