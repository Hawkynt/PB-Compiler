using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Microsoft Binary Format DOUBLE storage for BASICA and GW-BASIC. The value computes on the x87,
/// but every eight-byte cell boundary must use MBF64 rather than IEEE64. These tests inspect that
/// boundary through the dialect's own VARPTR/PEEK operations and require the IR path to route.
/// </summary>
[TestFixture]
public sealed class BackendMbf64Tests {

  private static string Run(string source, Dialect dialect, bool optimize, bool routed) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) {
      Optimize = optimize,
    };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    if (routed)
      Assert.That(generator.BackendRoutedNames, Does.Contain("main"), "the MBF64 body fell back");
    return string.Join('|', Cpu8086.Run(image, exactFloatingPoint: true).Output
      .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
      .Select(line => line.Trim()));
  }

  private static void BothPathsAgree(string source, Dialect dialect, bool optimize, string expected) {
    var routed = Run(source, dialect, optimize, routed: true);
    Assert.That(routed, Is.EqualTo(Run(source, dialect, optimize, routed: false)),
      "the direct and retargetable x86-16 emitters disagree");
    Assert.That(routed, Is.EqualTo(expected));
  }

  private static readonly object[] _dialectsAndModes = [
    new object[] { Dialect.Basica, false },
    new object[] { Dialect.Basica, true },
    new object[] { Dialect.Gw, false },
    new object[] { Dialect.Gw, true },
  ];

  /// <summary>
  /// Given an interpreter-dialect DOUBLE containing one, when its bytes are observed through PEEK,
  /// then its seven mantissa/sign bytes are zero and its biased exponent is 129.
  /// </summary>
  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Store_GivenDoubleOne_ThenTheCellUsesTheEightByteMbfEncoding(Dialect dialect, bool optimize) =>
    BothPathsAgree("""
      10 X# = 1#
      20 P% = VARPTR(X#)
      30 FOR I% = 0 TO 7
      40 PRINT PEEK(P% + I%)
      50 NEXT I%
      60 END
      """, dialect, optimize, "0|0|0|0|0|0|0|129");

  /// <summary>
  /// Given one MBF64 scalar copied to another under optimization, when the destination is observed,
  /// then load forwarding must retain or eliminate both storage conversions as one boundary.
  /// </summary>
  [Test]
  public void Copy_GivenOptimizedMbf64Scalars_ThenTheConversionsRemainAddressBound() => BothPathsAgree("""
    10 X# = 1#
    20 Y# = X#
    30 P% = VARPTR(Y#)
    40 PRINT PEEK(P% + 7)
    50 END
    """, Dialect.Gw, optimize: true, "129");

  /// <summary>
  /// Given zero, a negative value, a copied value and an arithmetic result, when they cross MBF64
  /// cell boundaries, then zero is canonical, the sign is in byte six, loads reconstruct values and
  /// stores write the historical exponent.
  /// </summary>
  [TestCaseSource(nameof(_dialectsAndModes))]
  public void RoundTrip_GivenRepresentativeDoubles_ThenEveryCellBoundaryUsesMbf64(
      Dialect dialect, bool optimize) => BothPathsAgree("""
    10 Z# = 0#
    20 N# = -0.5#
    30 X# = 1#
    40 Y# = X#
    50 A# = 2.5#
    60 B# = 4#
    70 C# = A# * B#
    80 P% = VARPTR(Z#)
    90 S% = 0
    100 FOR I% = 0 TO 7
    110 S% = S% + PEEK(P% + I%)
    120 NEXT I%
    130 PRINT S%
    140 P% = VARPTR(N#)
    150 PRINT PEEK(P% + 6)
    160 PRINT PEEK(P% + 7)
    170 P% = VARPTR(Y#)
    180 PRINT PEEK(P% + 7)
    190 P% = VARPTR(C#)
    200 PRINT PEEK(P% + 7)
    210 END
    """, dialect, optimize, "0|128|128|129|132");

  /// <summary>
  /// Given a finite x87 value above MBF64's exponent range, when it crosses a DOUBLE cell boundary,
  /// then both emitters raise BASIC Error 6 instead of manufacturing an infinity MBF cannot encode.
  /// </summary>
  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Store_GivenAValueAboveTheMbf64Range_ThenItRaisesOverflow(
      Dialect dialect, bool optimize) => BothPathsAgree("""
    10 ON ERROR GOTO 50
    20 X# = 1D100
    30 PRINT "missed"
    40 END
    50 PRINT ERR
    60 END
    """, dialect, optimize, "6");

  /// <summary>
  /// Given a magnitude below MBF64's smallest normal, when it is stored, then the cell is canonical
  /// zero rather than a pseudo-denormal whose fraction bytes survive with a zero exponent.
  /// </summary>
  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Store_GivenAValueBelowTheMbf64Range_ThenItUnderflowsToCanonicalZero(
      Dialect dialect, bool optimize) => BothPathsAgree("""
    10 X# = 1D-100
    20 P% = VARPTR(X#)
    30 S% = 0
    40 FOR I% = 0 TO 7
    50 S% = S% + PEEK(P% + I%)
    60 NEXT I%
    70 PRINT S%
    80 END
    """, dialect, optimize, "0");

  /// <summary>
  /// Given the first representable MBF64 value above one, when x87 arithmetic produces it, then its
  /// 56th significant bit survives. IEEE binary64 would round this value back to one.
  /// </summary>
  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Store_GivenAValueThatNeedsAllFiftySixSignificantBits_ThenTheLowFractionBitSurvives(
      Dialect dialect, bool optimize) => BothPathsAgree("""
    10 A# = 1#
    20 B# = 36028797018963968#
    30 X# = A# + A# / B#
    40 P% = VARPTR(X#)
    50 PRINT PEEK(P%)
    60 PRINT PEEK(P% + 7)
    70 END
    """, dialect, optimize, "1|129");

  /// <summary>
  /// Given exact half-way values on either side of an odd retained mantissa, when stored, then the
  /// even neighbour wins in both directions. This executes the emitted rounding carry, not the C#
  /// value type used as the independent format oracle.
  /// </summary>
  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Store_GivenExactMbf64Ties_ThenItRoundsToNearestEven(
      Dialect dialect, bool optimize) => BothPathsAgree("""
    10 A# = 1#
    20 B# = 72057594037927936#
    30 E# = A# / B#
    40 X# = A# + E#
    50 Y# = A# + 3# * E#
    60 PX% = VARPTR(X#)
    70 PY% = VARPTR(Y#)
    80 PRINT PEEK(PX%)
    90 PRINT PEEK(PY%)
    100 END
    """, dialect, optimize, "0|2");

  /// <summary>
  /// Given an interpreter-dialect DOUBLE array, when an element is written and read through array
  /// addressing, then the element cell keeps the full MBF64 significand rather than IEEE64 bits.
  /// </summary>
  [TestCaseSource(nameof(_dialectsAndModes))]
  public void StaticArray_GivenAValueThatNeedsAllFiftySixSignificantBits_ThenEachElementUsesMbf64(
      Dialect dialect, bool optimize) => BothPathsAgree("""
    10 DIM A#(0 TO 1)
    20 A#(0) = 1#
    30 A#(1) = A#(0) + A#(0) / 36028797018963968#
    40 P% = VARPTR(A#(1))
    50 PRINT PEEK(P%)
    60 PRINT PEEK(P% + 7)
    70 END
    """, dialect, optimize, "1|129");

  /// <summary>
  /// Given adjacent SINGLE array elements, when the second element is inspected, then the four-byte
  /// stride reaches its own MBF32 sign and exponent bytes rather than an IEEE cell or its neighbour.
  /// </summary>
  [TestCaseSource(nameof(_dialectsAndModes))]
  public void StaticArray_GivenAdjacentSingles_ThenEachElementUsesMbf32(
      Dialect dialect, bool optimize) => BothPathsAgree("""
    10 DIM A!(0 TO 1)
    20 A!(0) = 1!
    30 A!(1) = -.5!
    40 P% = VARPTR(A!(1))
    50 PRINT PEEK(P% + 2)
    60 PRINT PEEK(P% + 3)
    70 END
    """, dialect, optimize, "128|128");
}
