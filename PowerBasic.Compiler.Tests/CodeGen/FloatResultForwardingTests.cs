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

  private const string SinglePrologue = "$OPTIMIZE SPEED\nDECLARE FUNCTION f!(x!)\nPRINT f!(2.5); f!(INP(&H60))\nEND\n";
  private const string DoublePrologue = "$OPTIMIZE SPEED\nDECLARE FUNCTION g#(x#)\nPRINT g#(2.5); g#(INP(&H60))\nEND\n";

  private const string SingleForwarded = SinglePrologue + "FUNCTION f!(x!) NOINLINE\n f! = x! + 1.5\nEND FUNCTION";
  private const string SingleMultiExit = SinglePrologue
    + "FUNCTION f!(x!) NOINLINE\n IF x! > 99.0 THEN f! = 0.0 : EXIT FUNCTION\n f! = x! + 1.5\nEND FUNCTION";
  private const string DoubleForwarded = DoublePrologue + "FUNCTION g#(x#) NOINLINE\n g# = x# + 1.5\nEND FUNCTION";
  private const string DoubleMultiExit = DoublePrologue
    + "FUNCTION g#(x#) NOINLINE\n IF x# > 99.0 THEN g# = 0.0 : EXIT FUNCTION\n g# = x# + 1.5\nEND FUNCTION";

  /// <summary>
  /// The result travels at its own width. A SINGLE or DOUBLE result lives in a cell of that width,
  /// so the function body moves no ten-byte value at all: the one <c>FSTP m32</c> that rounds it (the
  /// rounding genuine PB 3.5 performs on the way out - FLTRET.BAS in the differential battery pins
  /// it) and the <c>FLD m32</c> that hands it back in <c>ST(0)</c>.
  ///
  /// <para>
  /// This used to demand that the epilogue's reload disappear, which is what the direct emitter's
  /// O0102 did: it left the unrounded eighty-bit sum in <c>ST(0)</c>, and <c>d# = f!(1)</c> with
  /// <c>f! = x! / 3</c> answered .333333333333333 where genuine PB answers .333333343267441. The
  /// reload IS the rounding; what can go is every wider copy around it.
  /// </para>
  /// </summary>
  [TestCase(SingleForwarded, "f")]
  [TestCase(SingleMultiExit, "f")]
  [TestCase(DoubleForwarded, "g")]
  [TestCase(DoubleMultiExit, "g")]
  public void Emit_GivenAFloatFunction_ThenItsResultNeverTravelsAsTenBytes(string source, string function) {
    var code = FunctionCode(source, function);
    var tbyte = 0;
    for (var i = 0; i + 1 < code.Length; ++i)
      if (code[i] == 0xDB && (code[i + 1] & 0xC0) != 0xC0 && ((code[i + 1] >> 3) & 7) is 5 or 7)
        ++tbyte;                                    // FLD m80 / FSTP m80
    Assert.That(tbyte, Is.Zero, "no FLD/FSTP TBYTE: the result and its operands live at their own width");
  }

  /// <summary>The named procedure's bytes, from the image listing.</summary>
  private static byte[] FunctionCode(string source, string name) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var generator = new CodeGenerator(model);
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    var listing = generator.DescribeImage();
    var code = DosImageCode.ByListingOffset(image);
    var target = listing.Procedures.First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    var end = listing.Procedures.Where(p => p.CodeOffset > target.CodeOffset).Select(p => p.CodeOffset)
      .Concat(listing.RuntimeLabels.Where(l => !l.IsConstant && l.Offset > target.CodeOffset).Select(l => l.Offset))
      .Append(Math.Min(listing.CodeLength, code.Length)).Min();
    return code.AsSpan(target.CodeOffset, end - target.CodeOffset).ToArray();
  }

  /// <summary>
  /// Forwarding must not change the answer, and 2.5 + 1.5 is exact in both widths. The interpreter's
  /// ports read 0, so the second, opaque call answers 1.5.
  /// </summary>
  [TestCase(SingleForwarded)]
  [TestCase(SingleMultiExit)]
  [TestCase(DoubleForwarded)]
  [TestCase(DoubleMultiExit)]
  public void Run_GivenAForwardedFloatResult_ThenTheValueIsRight(string source) =>
    Assert.That(Cpu8086.Run(Compile(source)).Output.Trim(), Is.EqualTo("4  1.5"));

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
