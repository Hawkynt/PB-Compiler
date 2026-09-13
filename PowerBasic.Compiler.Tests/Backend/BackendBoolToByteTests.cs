using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// A truth value stored into a BYTE.
///
/// <para>
/// BASIC truth is a FULL WORD of -1 or 0, so its low byte is 0xFF or 0x00 - which IS the byte truth
/// value, with nothing to do beyond naming the low half. The selector had cases for widening a bool
/// to a word and to a dword and none for narrowing it to a byte, so a <c>DIM f AS BYTE : f = (a &gt; b)</c>
/// declined and took the module body with it.
/// </para>
/// <para>
/// The VALUES are the assertion, and they are what a careless narrowing gets wrong: PowerBASIC's TRUE
/// is -1, and a BYTE holding it reads back as 255 rather than as 1 or as -1. A conversion that
/// produced 1 would pass a test asserting "nonzero" and fail this one.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendBoolToByteTests {

  private const string _source = """
    DIM b AS BYTE, n AS INTEGER
    n = 5
    b = (n > 3)
    PRINT b
    b = (n > 9)
    PRINT b
    IF b = 0 THEN PRINT "false is zero" ELSE PRINT "BAD"
    """;

  private static (string Output, IEnumerable<string> Routed) Run(bool optimize) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(_source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return (Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|"), generator.BackendRoutedNames.ToList());
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Run_GivenATruthValueInAByte_ThenItIsTheLowByteOfTheWord(bool optimize) {
    var (output, _) = Run(optimize);

    Assert.That(output, Is.EqualTo("255 | 0 |false is zero"),
      "TRUE is -1, so a BYTE holding it reads back as 255 - not 1");
  }

  /// <summary>The premise: before this the body declined and those values were the direct emitter's.</summary>
  [Test]
  public void Route_GivenATruthValueInAByte_ThenTheModuleBodyIsTakenByTheBackEnd() {
    var (_, routed) = Run(optimize: false);

    Assert.That(routed, Does.Contain("main"));
  }
}
