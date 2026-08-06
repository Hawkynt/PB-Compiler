using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// O0299 asked for an identity comparison between interned literals — two pool references being
/// equal when their (offset, length) match, instead of a byte compare. Measuring first says there
/// is nothing left to build, for two reasons that between them close the entry.
///
/// Its own worked example does not qualify. <c>m$ = %Mode : IF m$ = %Mode</c> copies the pool bytes
/// into a DYNAMIC string, a different allocation with the same contents — comparing addresses there
/// would be wrong, as that page's own "what it needs" section says.
///
/// And the case that does qualify — literal against literal — never reaches a comparison at all:
/// the constant folder answers it, and the literals then die with it. That is what these tests pin,
/// so the behaviour O0299 wanted is covered rather than merely believed.
/// </summary>
[TestFixture]
public sealed class LiteralStringComparisonFoldTests {

  private static byte[] Compile(string source, bool optimize = true) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return image;
  }

  private static bool Contains(byte[] image, string text) {
    var needle = System.Text.Encoding.ASCII.GetBytes(text);
    for (var i = 0; i + needle.Length <= image.Length; ++i) {
      var hit = true;
      for (var j = 0; j < needle.Length; ++j)
        if (image[i + j] != needle[j]) { hit = false; break; }
      if (hit)
        return true;
    }
    return false;
  }

  private const string Literal = """
    IF "fast" = "fast" THEN PRINT 1 ELSE PRINT 0
    END
    """;

  /// <summary>
  /// A comparison between two identical literals folds away entirely - the operand bytes are not
  /// even in the image, because nothing reads them once the answer is a constant.
  ///
  /// The dynamic variant is what makes that evidence rather than a coincidence: the same text
  /// reaching the same comparison through READ/DATA IS in its image, so a search for it can tell
  /// the two apart. Without that half, an image that never contained the string for some unrelated
  /// reason would pass.
  /// </summary>
  [Test]
  public void Compare_GivenTwoIdenticalLiterals_ThenItFoldsAndTheOperandsDieWithIt() {
    Assert.Multiple(() => {
      Assert.That(Contains(Compile(Literal), "fast"), Is.False,
        "the folded comparison leaves no operand to compare");
      Assert.That(Contains(Compile("""
        DIM a AS STRING, b AS STRING
        READ a
        READ b
        IF a = b THEN PRINT 1 ELSE PRINT 0
        DATA "fast","fast"
        END
        """), "fast"), Is.True,
        "the same text through READ/DATA is real data - which is what makes the check above mean something");
    });
  }

  /// <summary>And it folds to the right answer, both ways, for equal and unequal literals.</summary>
  [TestCase("\"fast\" = \"fast\"", "1")]
  [TestCase("\"fast\" = \"slow\"", "0")]
  [TestCase("\"fast\" <> \"slow\"", "1")]
  [TestCase("\"abc\" < \"abd\"", "1")]
  [TestCase("\"abd\" < \"abc\"", "0")]
  [TestCase("\"ab\" < \"abc\"", "1")]
  public void Compare_GivenLiteralOperands_ThenTheFoldedAnswerIsRight(string condition, string expected) {
    var source = $"IF {condition} THEN PRINT 1 ELSE PRINT 0\nEND\n";
    Assert.Multiple(() => {
      Assert.That(Cpu8086.Run(Compile(source)).Output.Trim(), Is.EqualTo(expected));
      Assert.That(Cpu8086.Run(Compile(source, optimize: false)).Output.Trim(), Is.EqualTo(expected),
        "and the faithful build agrees - the fold may not change the answer");
    });
  }

  /// <summary>
  /// The case O0299's example actually describes, kept as the counter-example: a value copied into a
  /// dynamic string is a different allocation, so it compares by content and must keep doing so.
  /// </summary>
  [Test]
  public void Compare_GivenALiteralCopiedIntoADynamicString_ThenItStillComparesByContent() =>
    Assert.That(Cpu8086.Run(Compile("""
      DIM m AS STRING
      m = "fast"
      IF m = "fast" THEN PRINT 1 ELSE PRINT 0
      END
      """)).Output.Trim(), Is.EqualTo("1"));
}
