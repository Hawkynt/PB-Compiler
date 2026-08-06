using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// O0080: <c>x \ -1</c> becomes <c>NEG</c>, but only where MININT is ruled out.
///
/// The two are not interchangeable at one value. <c>IDIV</c> traps (#DE) on <c>-32768 \ -1</c>
/// because the quotient +32768 does not fit the destination, whereas <c>NEG 8000h</c> is
/// <c>8000h</c> and reports nothing - so folding unconditionally would delete a trap the hardware
/// takes. That single value is why this case sat unimplemented while the rest of O0080 folded.
///
/// The interval domain settles it: a range whose low end is above MININT cannot contain MININT.
/// When it does not prove that, the IDIV stays and the trap with it. Both directions are pinned
/// below, because a fold that fired everywhere would pass the first test and a fold that never
/// fired would pass the second.
/// </summary>
[TestFixture]
public sealed class DivideByMinusOneTests {

  private static byte[] Compile(string source, bool optimize = true) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return image;
  }

  private static string Run(string source, bool optimize = true) =>
    Cpu8086.Run(Compile(source, optimize)).Output.Trim().Replace("\r\n", "|");

  // Measured by SIZE, not by a byte marker, and two failed markers are why. A vanished IDIV reads
  // zero either way: PB widens `\` to LONG and calls the software rt_longdiv, which divides by
  // shift-and-subtract, so no hardware IDIV is in the image at all. The LONG negate the fold leaves
  // behind (NOT DX / NEG AX / SBB DX,-1) is present either way too - rt_longdiv negates its own
  // operands to do a signed divide, so the sequence sits inside the very routine whose absence was
  // supposed to be the evidence. Both scans "passed" while measuring nothing.
  //
  // The size gap is unambiguous and large: folding leaves rt_longdiv unreferenced, and Tier 3
  // trims it, which is worth ~500 bytes on these programs. The A/B pair below differs only in the
  // operand, so nothing else can account for it.

  // The A/B pair: identical but for the operand. `i` is bounded by its FOR loop and so provably
  // above MININT; `x` comes from READ and is not provable. Everything else - the loop, the
  // accumulation, the PRINT - is the same, so a difference between the two images is the fold and
  // not some unrelated pass reacting to a differently-shaped program.
  private const string Provable = """
    DIM x AS INTEGER, i AS INTEGER, s AS INTEGER
    READ x
    FOR i = 1 TO 5
      s = s + (i \ -1)
    NEXT i
    PRINT s
    DATA 7
    END
    """;

  private const string Unprovable = """
    DIM x AS INTEGER, i AS INTEGER, s AS INTEGER
    READ x
    FOR i = 1 TO 5
      s = s + (x \ -1)
    NEXT i
    PRINT s
    DATA 7
    END
    """;

  /// <summary>
  /// A counter bounded by its FOR loop folds to a negate; an unprovable operand keeps the divide.
  /// The size gap is the software divide routine going unreferenced and being trimmed with it.
  /// </summary>
  [Test]
  public void Divide_GivenAProvablyBoundedValue_ThenItFoldsToANegate() =>
    Assert.That(Compile(Unprovable).Length - Compile(Provable).Length, Is.GreaterThan(200),
      "folding \\ -1 drops the software long-divide, which Tier 3 then trims as unreferenced");

  /// <summary>And it still computes the negation - -(1+2+3+4+5).</summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Divide_GivenAProvablyBoundedValue_ThenItStillNegates(bool optimize) =>
    Assert.That(Run("""
      DIM i AS INTEGER, s AS INTEGER
      FOR i = 1 TO 5
        s = s + (i \ -1)
      NEXT i
      PRINT s
      END
      """, optimize), Is.EqualTo("-15"));

  /// <summary>
  /// An unprovable operand keeps the real divide, which is what preserves the MININT behaviour.
  /// READ/DATA so the value cannot be tracked back to a literal.
  /// </summary>
  /// <summary>
  /// The fold is off without --optimize, so the unoptimized build of the provable program keeps the
  /// divide too - the same size argument in the other direction, against the same program.
  /// </summary>
  [Test]
  public void Divide_WhenNotOptimized_ThenTheProvableCaseKeepsTheDivideToo() =>
    Assert.That(Compile(Provable, optimize: false).Length - Compile(Provable).Length, Is.GreaterThan(200),
      "the fold is gated on --optimize; the faithful build still divides");

  /// <summary>The unprovable case is still correct for ordinary values.</summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Divide_GivenAnUnprovableValue_ThenItIsStillRight(bool optimize) =>
    Assert.That(Run("""
      DIM x AS INTEGER
      READ x
      PRINT x \ -1
      DATA 7
      END
      """, optimize), Is.EqualTo("-7"));

  /// <summary>
  /// The LONG path negates through the pair (NOT DX / NEG AX / SBB DX,-1) rather than one NEG, so
  /// it is exercised separately - a 16-bit-only fold would give the wrong high word here.
  /// </summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Divide_GivenALongOperand_ThenThePairIsNegated(bool optimize) =>
    Assert.That(Run("""
      DIM i AS LONG, s AS LONG
      FOR i = 100000 TO 100002
        s = s + (i \ -1)
      NEXT i
      PRINT s
      END
      """, optimize), Is.EqualTo("-300003"));

  /// <summary>
  /// The optimizer may not change what the program prints, including at MININT itself: the operand
  /// is unprovable here, so both builds keep the IDIV and both take the same trap.
  /// </summary>
  [Test]
  public void Divide_GivenMinIntItself_ThenOptimizedMatchesUnoptimized() {
    const string source = """
      DIM x AS INTEGER
      READ x
      PRINT x \ -1
      DATA -32768
      END
      """;
    Assert.That(Cpu8086.Run(Compile(source)).Output, Is.EqualTo(Cpu8086.Run(Compile(source, optimize: false)).Output));
  }
}
