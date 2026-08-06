using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// O0102 for SINGLE and DOUBLE results.
///
/// The integer rule already forwarded a single-exit function's final assignment into <c>AX</c>
/// instead of storing it to the result slot and reloading it in the epilogue. A float returns on the
/// x87 stack rather than in a register, but the shape is identical: the epilogue's job is an
/// <c>FLD</c> from the slot, so a value the last statement already left in <c>ST(0)</c> is
/// where the caller expects it and both the <c>FSTP</c> and the <c>FLD</c> go.
///
/// The stack stays balanced because the exchange is one-for-one - the RHS leaves exactly one value
/// where the assignment would have popped it and the epilogue would have pushed it back.
///
/// BASICA and GW-BASIC floats are <c>MbfType</c>, a separate PbType rather than a float
/// <c>ScalarType</c>, so they cannot reach this rule. That is correct rather than incidental: their
/// epilogue CONVERTS Microsoft Binary Format to IEEE, and a conversion is not a load that can be
/// skipped because the value is "already in place".
/// </summary>
[TestFixture]
public sealed class FloatResultForwardingTests {

  private static byte[] Compile(string source, bool optimize = true) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return image;
  }

  /// <summary>
  /// The epilogue's reload, anchored to the teardown that always follows it: FLD [BP+disp8] - D9 /0
  /// for a SINGLE, DD /0 for a DOUBLE - immediately before MOV SP,BP (89 EC). The anchor is what
  /// makes this a real detector rather than a search for a common byte pair, and each test below
  /// checks it BOTH ways on programs that differ only in whether forwarding applies, so a marker
  /// that matched everywhere or nowhere would fail rather than pass quietly.
  /// </summary>
  private static bool HasFloatResultReload(byte[] image, byte opcode) {
    for (var i = 0; i + 4 < image.Length; ++i)
      if (image[i] == opcode && image[i + 1] == 0x46 && image[i + 3] == 0x89 && image[i + 4] == 0xEC)
        return true;
    return false;
  }

  private const string SinglePrologue = "$OPTIMIZE SPEED\nDECLARE FUNCTION f!(x!)\nPRINT f!(2.5)\nEND\n";
  private const string DoublePrologue = "$OPTIMIZE SPEED\nDECLARE FUNCTION g#(x#)\nPRINT g#(2.5)\nEND\n";

  private const string SingleForwarded = SinglePrologue + "FUNCTION f!(x!)\n f! = x! + 1.5\nEND FUNCTION";
  private const string SingleMultiExit = SinglePrologue
    + "FUNCTION f!(x!)\n IF x! > 99.0 THEN f! = 0.0 : EXIT FUNCTION\n f! = x! + 1.5\nEND FUNCTION";
  private const string DoubleForwarded = DoublePrologue + "FUNCTION g#(x#)\n g# = x# + 1.5\nEND FUNCTION";
  private const string DoubleMultiExit = DoublePrologue
    + "FUNCTION g#(x#)\n IF x# > 99.0 THEN g# = 0.0 : EXIT FUNCTION\n g# = x# + 1.5\nEND FUNCTION";

  [Test]
  public void Emit_GivenSingleExitSingleFunction_ThenTheEpilogueFldIsElided() {
    Assert.Multiple(() => {
      Assert.That(HasFloatResultReload(Compile(SingleForwarded), 0xD9), Is.False,
        "a single-exit SINGLE function leaves its result in ST(0)");
      Assert.That(HasFloatResultReload(Compile(SingleMultiExit), 0xD9), Is.True,
        "a multi-exit function can reach the epilogue with nothing on the stack, so it must reload");
    });
  }

  [Test]
  public void Emit_GivenSingleExitDoubleFunction_ThenTheEpilogueFldIsElided() {
    Assert.Multiple(() => {
      Assert.That(HasFloatResultReload(Compile(DoubleForwarded), 0xDD), Is.False,
        "a single-exit DOUBLE function leaves its result in ST(0)");
      Assert.That(HasFloatResultReload(Compile(DoubleMultiExit), 0xDD), Is.True,
        "a multi-exit function must reload");
    });
  }

  /// <summary>Forwarding must not change the answer, and 2.5 + 1.5 is exact in both widths.</summary>
  [TestCase(SingleForwarded)]
  [TestCase(SingleMultiExit)]
  [TestCase(DoubleForwarded)]
  [TestCase(DoubleMultiExit)]
  public void Run_GivenAForwardedFloatResult_ThenTheValueIsRight(string source) =>
    Assert.That(Cpu8086.Run(Compile(source)).Output.Trim(), Is.EqualTo("4"));

  /// <summary>
  /// And the optimizer changes nothing observable - the assertion the whole battery rests on, made
  /// directly because this one rewrites where a return value lives.
  /// </summary>
  [TestCase(SingleForwarded)]
  [TestCase(DoubleForwarded)]
  public void Run_WhenOptimized_ThenIdenticalToTheUnoptimizedRun(string source) =>
    Assert.That(Cpu8086.Run(Compile(source)).Output,
      Is.EqualTo(Cpu8086.Run(Compile(source, optimize: false)).Output));

  /// <summary>
  /// A function whose result is read back by its own final RHS still works: earlier assignments
  /// stored to the slot normally, so the read sees the right value before the forwarded store
  /// replaces it.
  /// </summary>
  [Test]
  public void Run_GivenAFinalRhsThatReadsTheResult_ThenItStillReadsTheSlot() =>
    Assert.That(Cpu8086.Run(Compile("""
      $OPTIMIZE SPEED
      DECLARE FUNCTION h!(x!)
      PRINT h!(2.0)
      END
      FUNCTION h!(x!)
        h! = x! + 1.0
        h! = h! + 1.0
      END FUNCTION
      """)).Output.Trim(), Is.EqualTo("4"));
}
