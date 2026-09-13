using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// A BYTE widened to an INTEGER or a LONG.
///
/// <para>
/// The selector had cases for a bool source, for a word reaching a dword and for either reaching a
/// qword, and none for a BYTE - so every <c>u8 -&gt; i16</c> declined the whole function to the
/// direct emitter. Over the SVGA corpus that one shape was 339 declines, and with its i32 and u16
/// siblings 448: the largest single reason the routing could not take a program.
/// </para>
/// <para>
/// <c>u8 -&gt; i32</c> is deliberately absent from the routing claim below. A version of the selector
/// case that also built the 32-bit pair made five DRAW_* corpus suites fail with
/// <c>Operand size mismatch: DX vs [BP-90]</c> - the long-result convention reading its high half
/// from a byte-sized slot - and removing only that half made them clean again. So the widening to a
/// LONG still declines to the direct emitter, correctly, and 94 declines wait on it.
/// </para>
/// <para>
/// The VALUES are what this asserts, not the encoding. A byte is unsigned in PowerBASIC, so 200
/// widens to 200 and not to -56 - which is exactly what a sign-extension or a half-written move into
/// a register whose high byte still holds something would produce. 200 is chosen because it has the
/// top bit set; 7 would pass against a broken extension.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendByteWideningTests {

  private static (string Output, IEnumerable<string> Routed) Run(string source, bool optimize) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return (Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|"), generator.BackendRoutedNames.ToList());
  }

  private const string _widen = """
    FUNCTION Widen%(BYVAL b AS BYTE) NOINLINE
      Widen% = b
    END FUNCTION

    FUNCTION WidenLong&(BYVAL b AS BYTE) NOINLINE
      WidenLong& = b
    END FUNCTION

    DIM hi AS BYTE
    hi = 200
    PRINT Widen%(hi)
    PRINT WidenLong&(hi)
    PRINT Widen%(255)
    PRINT WidenLong&(255)
    """;

  [TestCase(false)]
  [TestCase(true)]
  public void Run_GivenAByteWidened_ThenTheValueIsUnsigned(bool optimize) {
    var (output, _) = Run(_widen, optimize);

    Assert.That(output, Is.EqualTo("200 | 200 | 255 | 255"),
      "a BYTE is unsigned: 200 widens to 200, not -56, and the high half must be zero");
  }

  /// <summary>
  /// The premise: these functions must actually ROUTE. Before the selector learned the byte source
  /// they declined, and the values above would then be the direct emitter's - correct, and proving
  /// nothing about this back end.
  /// </summary>
  [Test]
  public void Route_GivenAByteWidened_ThenTheBackEndTakesTheFunctions() {
    var (_, routed) = Run(_widen, optimize: false);

    Assert.Multiple(() => {
      Assert.That(routed, Does.Contain("Widen"), "u8 -> i16 must route");
      Assert.That(routed, Does.Not.Contain("WidenLong"),
        "u8 -> i32 does NOT route yet - when it does, this line is the one to flip, not to delete");
    });
  }
}
