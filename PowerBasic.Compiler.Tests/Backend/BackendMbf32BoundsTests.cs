using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

[TestFixture]
public sealed class BackendMbf32BoundsTests {
  private static readonly object[] _dialectsAndModes = [
    new object[] { Dialect.Basica, false },
    new object[] { Dialect.Basica, true },
    new object[] { Dialect.Gw, false },
    new object[] { Dialect.Gw, true },
  ];

  private static string Run(string source, Dialect dialect, bool optimize) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    Assert.That(generator.BackendRoutedNames, Does.Contain("main"), "the MBF32 body fell back");
    return string.Join('|', Cpu8086.Run(image, exactFloatingPoint: true).Output
      .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
      .Select(line => line.Trim()));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Store_GivenAValueAboveTheMbf32Range_ThenRaisesOverflow(Dialect dialect, bool optimize) {
    var output = Run("""
      10 ON ERROR GOTO 50
      20 X! = 1.7014118D38
      30 PRINT "missed"
      40 END
      50 PRINT ERR
      60 END
      """, dialect, optimize);

    Assert.That(output, Is.EqualTo("6"));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Store_GivenTheSameValueAsDouble_ThenItFitsMbf64(Dialect dialect, bool optimize) {
    var output = Run("""
      10 X# = 1.7014118D38
      20 P% = VARPTR(X#)
      30 PRINT PEEK(P% + 7)
      40 END
      """, dialect, optimize);

    Assert.That(output, Is.EqualTo("255"));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Store_GivenARepresentableLargeValue_ThenKeepsTheMbf32Exponent(Dialect dialect, bool optimize) {
    var output = Run("""
      10 X! = 1D38
      20 P% = VARPTR(X!)
      30 PRINT PEEK(P% + 3)
      40 END
      """, dialect, optimize);

    Assert.That(output, Is.EqualTo("255"));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Store_GivenAValueBelowTheMbf32Range_ThenStoresCanonicalZero(Dialect dialect, bool optimize) {
    var output = Run("""
      10 X! = 1D-40
      20 P% = VARPTR(X!)
      30 S% = 0
      40 FOR I% = 0 TO 3
      50 S% = S% + PEEK(P% + I%)
      60 NEXT I%
      70 PRINT S%
      80 END
      """, dialect, optimize);

    Assert.That(output, Is.EqualTo("0"));
  }
}
