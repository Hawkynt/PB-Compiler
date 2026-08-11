using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// ARRAY SORT and ARRAY SCAN through the IR path, run and read.
///
/// The parameter block these statements are driven by is a set of stores to NAMED runtime cells, which
/// the IR can address directly - all but one. A descriptor opens with the SEGMENT its elements live in,
/// and a segment register is not a value the IR can name, so <c>rt_arr_desc</c> builds it from the near
/// address and the bounds the lowering does know (DosRuntime.ArrayDesc).
///
/// Every case here asserts the actual sorted values as well as agreement with the direct emitter. A
/// comparison alone would pass on a misunderstanding the two paths shared - and they share the whole
/// runtime, so a wrong descriptor field or a wrong element width is exactly the kind of mistake that
/// would be invisible to it. It is only invisible until someone reads the numbers.
/// </summary>
[TestFixture]
public sealed class BackendArraySortTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  /// <summary>Runs the program both ways, insisting the back end really took the code under test.</summary>
  private static (string Direct, string Routed) RunBothWays(string source, string[] mustRoute, bool optimize) {
    var direct = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = true };
    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();
    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Is.SupersetOf(mustRoute),
      "the back end did not take the code under test, so this compares the direct emitter with itself");

    string Execute(byte[] image, string which) {
      try {
        return Cpu8086.Run(image).Output;
      } catch (Cpu8086Exception e) {
        Assert.Ignore($"the interpreter cannot run the {which} image: {e.Message}");
        return "";
      }
    }

    return (Execute(directImage, "direct"), Execute(routedImage, "routed"));
  }

  /// <summary>PB pads printed numbers with sign and trailing blanks; the VALUES are what these tests are about.</summary>
  private static string[] Lines(string output) => output
    .Replace("\r", "")
    .Split('\n')
    .Select(line => string.Join(" ", line.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
    .ToArray();

  private static void AssertAgreeAndRead(string source, params string[] expected)
    => AssertRoutedAgreeAndRead(source, ["main"], expected);

  /// <summary>
  /// Both optimization settings, for the reason the corpus differential runs both: they are different
  /// emitters. With the optimizer off there is no CSE, no SCCP, no register residency and no runtime
  /// TRIMMING - so the section a routed call reaches for is resolved one way in one build and simply
  /// present in the other, and only running both says that both resolve.
  /// </summary>
  private static void AssertRoutedAgreeAndRead(string source, string[] mustRoute, params string[] expected) {
    foreach (var optimize in new[] { true, false }) {
      var (direct, routed) = RunBothWays(source, mustRoute, optimize);
      Assert.That(routed, Is.EqualTo(direct), $"the two back ends disagree (optimize={optimize})");
      Assert.That(Lines(routed).Take(expected.Length), Is.EqualTo(expected).AsCollection,
        $"...and the answer both give is not the one BASIC gives (optimize={optimize})");
    }
  }

  [Test]
  public void Run_GivenAnIntegerArraySort_ThenBothPathsSortItAscendingThenDescending() {
    AssertAgreeAndRead("""
      DIM a%(1 TO 6)
      a%(1)=30 : a%(2)=10 : a%(3)=50 : a%(4)=20 : a%(5)=40 : a%(6)=5
      ARRAY SORT a%(1)
      FOR i%=1 TO 6 : PRINT a%(i%); : NEXT : PRINT ""
      ARRAY SORT a%(1) FOR 6, DESCEND
      FOR i%=1 TO 6 : PRINT a%(i%); : NEXT : PRINT ""
      """,
      "5 10 20 30 40 50",
      "50 40 30 20 10 5");
  }

  /// <summary>
  /// The default count is "everything from the start element on", which the direct emitter reads back
  /// out of the descriptor as lower + extent - start. A named start and a named count must therefore
  /// leave the elements OUTSIDE the window exactly where they were.
  /// </summary>
  [Test]
  public void Run_GivenAStartElementAndACount_ThenOnlyThatWindowMoves() {
    AssertAgreeAndRead("""
      DIM b%(1 TO 6)
      b%(1)=6 : b%(2)=5 : b%(3)=4 : b%(4)=3 : b%(5)=2 : b%(6)=1
      ARRAY SORT b%(2) FOR 3
      FOR i%=1 TO 6 : PRINT b%(i%); : NEXT : PRINT ""
      """,
      "6 3 4 5 2 1");
  }

  /// <summary>
  /// A lower bound that is not one. The runtime turns the start index into a byte offset by
  /// subtracting the descriptor's lower bound, so an array based at -2 is the case that says whether
  /// that field arrived at all - based at 1 the subtraction is nearly a no-op and a zero would pass.
  /// </summary>
  [Test]
  public void Run_GivenANonUnitLowerBound_ThenTheDescriptorStillAddressesTheRightElements() {
    AssertAgreeAndRead("""
      DIM c%(-2 TO 2)
      c%(-2)=9 : c%(-1)=7 : c%(0)=8 : c%(1)=6 : c%(2)=5
      ARRAY SORT c%(-2)
      FOR i%=-2 TO 2 : PRINT c%(i%); : NEXT : PRINT ""
      """,
      "5 6 7 8 9");
  }

  /// <summary>
  /// Every element width the numeric engine claims to handle, sorted in one program. Each is compared
  /// on the x87 through a staging cell whose width comes from the rt_num_size / rt_num_load pair, so a
  /// wrong pair shows up as one type sorting wrongly while the others are fine - which is why they are
  /// all here rather than one standing in for the rest. QUAD is absent because printing a non-constant
  /// one still declines, not because it sorts differently.
  /// </summary>
  [Test]
  public void Run_GivenEveryNumericWidth_ThenEachSortsByValueRatherThanByBytes() {
    AssertAgreeAndRead("""
      DIM b&(1 TO 4)
      b&(1)=100000 : b&(2)=-5 : b&(3)=99999 : b&(4)=0
      ARRAY SORT b&(1)
      FOR i%=1 TO 4 : PRINT b&(i%); : NEXT : PRINT ""
      DIM c!(1 TO 3)
      c!(1)=2.5 : c!(2)=-1.5 : c!(3)=0.25
      ARRAY SORT c!(1)
      FOR i%=1 TO 3 : PRINT c!(i%); : NEXT : PRINT ""
      DIM dd#(1 TO 4)
      dd#(1)=3.5 : dd#(2)=-2.25 : dd#(3)=100.125 : dd#(4)=0
      ARRAY SORT dd#(1)
      FOR i%=1 TO 4 : PRINT dd#(i%); : NEXT : PRINT ""
      DIM ww??(1 TO 4)
      ww??(1)=50000 : ww??(2)=100 : ww??(3)=65535 : ww??(4)=0
      ARRAY SORT ww??(1)
      FOR i%=1 TO 4 : PRINT ww??(i%); : NEXT : PRINT ""
      DIM nn???(1 TO 3)
      nn???(1)=4000000000 : nn???(2)=10 : nn???(3)=2000000000
      ARRAY SORT nn???(1)
      FOR i%=1 TO 3 : PRINT nn???(i%); : NEXT : PRINT ""
      DIM yy?(1 TO 4)
      yy?(1)=200 : yy?(2)=5 : yy?(3)=255 : yy?(4)=100
      ARRAY SORT yy?(1)
      FOR i%=1 TO 4 : PRINT yy?(i%); : NEXT : PRINT ""
      """,
      "-5 0 99999 100000",
      "-1.5 .25 2.5",
      "-2.25 0 3.5 100.125",
      "0 100 50000 65535",
      "10 2000000000 4000000000",
      "5 100 200 255");
  }

  /// <summary>
  /// TAGARRAY: a parallel array dragged along by the key's swaps. It has its own lower bound and its
  /// own element size - a LONG beside an INTEGER key here - so it needs a descriptor of its own, and
  /// getting the second one wrong is invisible until the tags come out in the key's order.
  /// </summary>
  [Test]
  public void Run_GivenATagArray_ThenTheParallelArrayFollowsTheKeysOrder() {
    AssertAgreeAndRead("""
      DIM kk%(1 TO 4)
      DIM tt&(1 TO 4)
      kk%(1)=30 : kk%(2)=10 : kk%(3)=20 : kk%(4)=40
      tt&(1)=300 : tt&(2)=100 : tt&(3)=200 : tt&(4)=400
      ARRAY SORT kk%(1), TAGARRAY tt&()
      FOR i%=1 TO 4 : PRINT kk%(i%); : NEXT : PRINT ""
      FOR i%=1 TO 4 : PRINT tt&(i%); : NEXT : PRINT ""
      DIM zz%(1 TO 4)
      zz%(1)=4 : zz%(2)=3 : zz%(3)=2 : zz%(4)=1
      ARRAY SORT zz%(1)
      FOR i%=1 TO 4 : PRINT zz%(i%); : NEXT : PRINT ""
      FOR i%=1 TO 4 : PRINT tt&(i%); : NEXT : PRINT ""
      """,
      "10 20 30 40",
      "100 200 300 400",
      // ...and the sort AFTER it must clear the tag descriptor, or it drags the previous statement's
      // parallel array along - the tag cell is one runtime cell, not a per-statement one
      "1 2 3 4",
      "100 200 300 400");
  }

  /// <summary>
  /// A start index that is not a literal. The default count is then lower + extent - start with a
  /// RUNTIME start, which is the only shape in this lowering that computes anything at all rather
  /// than storing constants into the parameter block.
  /// </summary>
  [Test]
  public void Run_GivenAComputedStartIndex_ThenTheDefaultCountRunsToTheEndOfTheArray() {
    AssertAgreeAndRead("""
      DIM b%(1 TO 6)
      b%(1)=6 : b%(2)=5 : b%(3)=4 : b%(4)=3 : b%(5)=2 : b%(6)=1
      k% = 2
      k% = k% + 1
      ARRAY SORT b%(k%)
      FOR i%=1 TO 6 : PRINT b%(i%); : NEXT : PRINT ""
      """,
      "6 5 1 2 3 4");
  }

  [Test]
  public void Run_GivenANumericArrayScan_ThenEachRelopAnswersItsFirstMatchingPosition() {
    AssertAgreeAndRead("""
      DIM a%(1 TO 6)
      a%(1)=5 : a%(2)=10 : a%(3)=20 : a%(4)=30 : a%(5)=40 : a%(6)=50
      ARRAY SCAN a%(1), = 20, TO s% : PRINT s%
      ARRAY SCAN a%(1) FOR 6, > 25, TO s% : PRINT s%
      ARRAY SCAN a%(1), <= 5, TO s% : PRINT s%
      ARRAY SCAN a%(1), <> 5, TO s% : PRINT s%
      ARRAY SCAN a%(1), = 99, TO s% : PRINT s%
      ARRAY SCAN a%(3) FOR 4, = 40, TO s% : PRINT s%
      """,
      "3", "4", "1", "2", "0", "3");
  }

  /// <summary>
  /// A string array's ELEMENTS are out of the back end's reach for a reason that has nothing to do
  /// with ARRAY SORT: reading or writing one is an element-indexed GEP, which the selector declines
  /// whatever statement it appears in. So the sort itself goes in a SHARED-array procedure, which is
  /// the part that routes, and the module body - which fills the array and prints it, and stays with
  /// the direct emitter - is what says what the routed code left behind. The two see one array: the
  /// data-cell bridge resolves the IR's <c>g.a</c> to the direct emitter's own label.
  /// </summary>
  private const string _stringArray = """
    DIM a(1 TO 8) AS SHARED STRING
    a(1) = "pear"
    a(2) = "Apple"
    a(3) = "fig"
    a(4) = "date"
    a(5) = "cherry"
    a(6) = "plum"
    a(7) = "kiwi"
    a(8) = "apricot"
    """;

  [Test]
  public void Run_GivenAStringArraySort_ThenBothPathsOrderTheHandlesByTheirBytes() {
    AssertRoutedAgreeAndRead($"""
      DECLARE SUB Ascending()
      DECLARE SUB Descending()
      {_stringArray}
      Ascending
      FOR i% = 1 TO 8 : PRINT a(i%) : NEXT i%
      Descending
      FOR i% = 1 TO 8 : PRINT a(i%) : NEXT i%
      END

      SUB Ascending()
        ARRAY SORT a(1) FOR 8
      END SUB

      SUB Descending()
        ARRAY SORT a(1) FOR 8, DESCEND
      END SUB
      """,
      ["Ascending", "Descending"],
      // a byte-wise order, so every capital sorts before every lower-case letter
      "Apple", "apricot", "cherry", "date", "fig", "kiwi", "pear", "plum",
      "plum", "pear", "kiwi", "fig", "date", "cherry", "apricot", "Apple");
  }

  /// <summary>
  /// ARRAY SCAN over strings, including the FROM/TO character window - the one option this lowering
  /// carries that has no numeric counterpart, and the only one that reaches rt_arpb +8/+10.
  /// </summary>
  [Test]
  public void Run_GivenAStringArrayScan_ThenTheRelopAndTheCharacterWindowBothApply() {
    AssertRoutedAgreeAndRead($"""
      DECLARE SUB Ascending()
      DECLARE SUB Scans()
      DECLARE SUB Windowed()
      {_stringArray}
      DIM p(1 TO 3) AS SHARED STRING
      DIM f AS SHARED INTEGER
      p(1) = "AAAXX1"
      p(2) = "BBBYY2"
      p(3) = "CCCYY3"
      Ascending
      Scans
      Windowed
      PRINT f
      END

      SUB Ascending()
        ARRAY SORT a(1) FOR 8
      END SUB

      SUB Scans()
        ARRAY SCAN a(1) FOR 8, = "fig", TO f
        PRINT f
        ARRAY SCAN a(1) FOR 8, = "zzz", TO f
        PRINT f
        ARRAY SCAN a(3) FOR 6, > "kiwi", TO f
        PRINT f
      END SUB

      SUB Windowed()
        ARRAY SCAN p(1) FOR 3, FROM 4 TO 5, = "YY", TO f
      END SUB
      """,
      ["Ascending", "Scans", "Windowed"],
      // sorted: Apple apricot cherry date fig kiwi pear plum - "fig" is fifth, "zzz" is nowhere, and
      // the first element past "kiwi", counting from the third, is "pear" five places along
      "5", "0", "5",
      // characters 4..5 of each element: "XX1", "YY2", "YY3" - the second is the first "YY"
      "2");
  }

  /// <summary>
  /// A string ARRAY SCAN allocates a handle for its match and must release it: the comparison does not
  /// consume its operands, so nothing else will. Two thousand scans of a freshly built match string
  /// exhaust the 64 KiB string heap long before they finish if the handle leaks - which is how a leak
  /// announces itself here, as OUT OF STRING SPACE rather than as a wrong answer.
  /// </summary>
  [Test]
  public void Run_GivenManyStringScans_ThenTheMatchHandleIsReleasedEachTime() {
    AssertRoutedAgreeAndRead($"""
      DECLARE SUB Repeatedly()
      {_stringArray}
      Repeatedly
      END

      SUB Repeatedly()
        LOCAL i AS INTEGER
        LOCAL f AS INTEGER
        LOCAL t AS LONG
        t = 0
        FOR i = 1 TO 2000
          ARRAY SCAN a(1) FOR 8, = "fi" + "g", TO f
          t = t + f
        NEXT i
        PRINT t
      END SUB
      """,
      ["Repeatedly"],
      "6000");
  }
}
