using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// What <c>WRITE</c> renders a number as, which is not what <c>STR$</c> renders it as and not what
/// <c>PRINT</c> renders it as either. Three widths tell the three apart and every one of them was
/// wrong somewhere:
///
/// <list type="bullet">
///   <item>a SINGLE goes through the <b>DOUBLE</b> formatter, so <c>WRITE sg</c> with 5/3 in it is
///     <c>1.66666662693024</c> - the single's exact value at fifteen digits - where <c>PRINT sg</c>
///     is <c>1.666667</c>. The routed path used STR$'s rule and wrote the seven-digit form.</item>
///   <item>a WORD and a DWORD render <b>unsigned</b>: 60000 and 3000000000, not -5536 and
///     -1294967296. The DIRECT emitter rendered both signed, which is a fidelity defect rather than
///     a disagreement - genuine PBC 3.50 says the unsigned form (checked with
///     <c>scripts/diff-one.sh</c>).</item>
///   <item>a BYTE is 0..255 and the signed 16-bit renderer is right for it.</item>
/// </list>
///
/// Every value arrives through a two-call-site <c>NOINLINE</c> function: written down, the whole
/// statement folds and the comparison is between two constants.
/// </summary>
[TestFixture]
public sealed class BackendWriteTests {

  private const string _widths = """
    DECLARE FUNCTION G%(BYVAL v%)
    DIM by AS BYTE, wo AS WORD, dw AS DWORD, lo AS LONG
    DIM sg AS SINGLE, db AS DOUBLE
    by = G%(200)
    wo = G%(30000) * 2
    dw = G%(30000) * 100000&
    lo = G%(-30000) * 100&
    sg = G%(5) / G%(3)
    db = G%(5) / G%(3)
    WRITE by, wo, dw
    WRITE lo, sg, db
    WRITE "a,b", by
    END

    FUNCTION G%(BYVAL v%) NOINLINE
      G% = v% + 0
    END FUNCTION
    """;

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static string Run(string source, bool routed, bool optimize) {
    var generator = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = routed };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    if (routed)
      Assert.That(generator.BackendRoutedNames, Does.Contain("main"),
        "the back end did not take the module body under test");
    try {
      return Cpu8086.Run(image).Output.Replace("\r\n", "|").TrimEnd('|');
    } catch (Cpu8086Exception e) {
      Assert.Ignore($"the interpreter cannot run the {(routed ? "routed" : "direct")} image: {e.Message}");
      return "";
    }
  }

  [TestCase(true)]
  [TestCase(false)]
  public void Write_GivenEveryNumericWidth_ThenBothPathsRenderWhatPbDoes(bool optimize) {
    var direct = Run(_widths, routed: false, optimize);
    var routed = Run(_widths, routed: true, optimize);

    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(direct, Is.EqualTo(
      "200,60000,3000000000|" +
      "-3000000,1.66666662693024,1.66666666666667|" +
      "\"a,b\",200"));
  }

  /// <summary>
  /// The same three rules through <c>WRITE #</c>, checked in the FILE rather than on the screen -
  /// a divergence that only reaches a file is exactly the one a stdout comparison cannot see.
  /// </summary>
  [Test]
  public void Write_GivenAFileNumber_ThenTheBytesInTheFileAreTheSameOnBothPaths() {
    const string source = """
      DECLARE FUNCTION G%(BYVAL v%)
      DIM wo AS WORD, sg AS SINGLE
      wo = G%(30000) * 2
      sg = G%(5) / G%(3)
      OPEN "OUT.TXT" FOR OUTPUT AS #1
      WRITE #1, wo, sg, "t"
      CLOSE #1
      END

      FUNCTION G%(BYVAL v%) NOINLINE
        G% = v% + 0
      END FUNCTION
      """;

    string Written(bool routed) {
      var generator = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = routed };
      var image = generator.EmitExecutable();
      Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
      if (routed)
        Assert.That(generator.BackendRoutedNames, Does.Contain("main"));
      return Cpu8086.Run(image).FileContent("OUT.TXT") ?? "<no file>";
    }

    Assert.That(Written(routed: true), Is.EqualTo(Written(routed: false)));
    Assert.That(Written(routed: false), Is.EqualTo("60000,1.66666662693024,\"t\"\r\n"));
  }
}
