using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>Checked arithmetic must retain its PowerBASIC Error 6 path after IR loop transforms.</summary>
[TestFixture]
public sealed class BackendOverflowTests {

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  [TestCase("32767", "RUNTIME ERROR")]
  [TestCase("1", "missed")]
  public void Execute_GivenInvariantCheckedAddInsideLoop_ThenErrorSixFiresOnlyWhenTheAddOverflows(
      string input, string expected) {
    var source = $$"""
      $ERROR OVERFLOW ON
      $OPTIMIZE SPEED
      OPEN "IN.TXT" FOR OUTPUT AS #1
      PRINT #1, "{{input}}"
      CLOSE #1
      DIM k AS INTEGER
      OPEN "IN.TXT" FOR INPUT AS #1
      INPUT #1, k
      CLOSE #1
      FOR i% = 1 TO 100
        x% = k + 1
      NEXT i%
      PRINT "missed"
      END
      """;
    var routed = new CodeGenerator(Bind(source));

    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("main"), "the loop must exercise the routed body");
    Assert.Multiple(() => {
      Assert.That(routedCpu.Output, Does.Contain(expected));
      // 32767 + 1 overflows on the first pass, so the trap stops the program before the PRINT
      if (expected != "missed")
        Assert.That(routedCpu.Output, Does.Not.Contain("missed"), "the program ran on past the trap");
      else
        Assert.That(routedCpu.Output, Does.Not.Contain("RUNTIME ERROR"), "1 + 1 does not overflow");
    });
  }
}
