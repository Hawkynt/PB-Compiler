using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// An array crosses a procedure boundary as one near pointer to its DESCRIPTOR, never as element
/// storage. The callee reads the data pointer and the per-dimension bounds out of that block, which
/// is why an array parameter declares no bounds of its own and why <c>LBOUND</c>/<c>UBOUND</c> inside
/// the callee are descriptor reads rather than constants.
///
/// <para>
/// The block's layout is the DIRECT emitter's, deliberately: it belongs to the caller, so its shape
/// is settled by the ABI rather than by whichever emitter compiled the callee. That is what lets a
/// routed callee be handed a descriptor a directly-emitted caller wrote, and the reverse.
/// </para>
/// <para>
/// Every case executes the routed image against the direct one under the 8086 interpreter, and
/// asserts the procedure ROUTED first - without that the two builds would be the same image compared
/// with itself. No array here is indexed from zero, because a descriptor read that dropped the lower
/// bound still prints numbers.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendArrayParameterRoutingTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static void AssertRoutedMatchesDirect(string source, string procedure, bool optimize) {
    var routed = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = true };
    var routedImage = routed.EmitExecutable();
    Assert.That(routed.Errors, Is.Empty, "routed: " + string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain(procedure),
      $"{procedure} did not route - the comparison below would have compiled the same image twice");

    var direct = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = false };
    var directImage = direct.EmitExecutable();
    Assert.That(direct.Errors, Is.Empty, "direct: " + string.Join("; ", direct.Errors));

    var expected = Cpu8086.Run(directImage);
    var actual = Cpu8086.Run(routedImage);
    Assert.That((actual.Output, actual.ExitCode), Is.EqualTo((expected.Output, expected.ExitCode)));
  }

  /// <summary>
  /// A STATIC array argument. Its bounds are known where it is declared and NOT known inside the
  /// callee, so the caller has to hand over a descriptor describing them; a lower bound of 3 makes a
  /// dropped one visible, because element 3 and element 0 hold different values. The write-back is
  /// what proves the callee reached the caller's own storage rather than a copy of it.
  /// </summary>
  private const string _staticArray = """
    SUB S(a%()) NOINLINE
      a%(4) = a%(3) + a%(5)
    END SUB
    DIM v%(3 TO 5)
    v%(3) = 11
    v%(5) = 22
    S v%()
    PRINT v%(3); v%(4); v%(5)
    """;

  [TestCase(false)]
  [TestCase(true)]
  public void Route_GivenStaticArrayArgument_ThenElementsCrossTheBoundary(bool optimize)
    => AssertRoutedMatchesDirect(_staticArray, "S", optimize);

  /// <summary>
  /// A DYNAMIC array argument, whose descriptor is filled in at run time by the REDIM rather than at
  /// compile time - and whose storage is in the far array heap rather than the data segment, so the
  /// descriptor's segment word is a different one from the static case above.
  /// </summary>
  private const string _dynamicArray = """
    SUB S(a%()) NOINLINE
      a%(2) = a%(1) * 10
    END SUB
    DIM v%()
    REDIM v%(1 TO 4)
    v%(1) = 7
    S v%()
    PRINT v%(1); v%(2)
    """;

  [TestCase(false)]
  [TestCase(true)]
  public void Route_GivenDynamicArrayArgument_ThenTheCallersFarBlockIsWritten(bool optimize)
    => AssertRoutedMatchesDirect(_dynamicArray, "S", optimize);

  /// <summary>
  /// The bounds themselves, which the callee can only get from the descriptor. Declaring 3 TO 7 makes
  /// both halves wrong in different ways if either field is misread: a dropped lower bound answers 0,
  /// and an extent mistaken for an upper bound answers 5.
  /// </summary>
  private const string _bounds = """
    SUB S(a%()) NOINLINE
      PRINT LBOUND(a%); UBOUND(a%)
    END SUB
    DIM v%(3 TO 7)
    S v%()
    """;

  [TestCase(false)]
  [TestCase(true)]
  public void Route_GivenArrayParameter_ThenBoundsComeFromTheCallersDescriptor(bool optimize)
    => AssertRoutedMatchesDirect(_bounds, "S", optimize);

  /// <summary>
  /// Two different arrays through the SAME parameter, with different lower bounds and different
  /// lengths. One call site would let the descriptor be proven constant and folded into the callee,
  /// which would pass while measuring nothing.
  /// </summary>
  private const string _twoArrays = """
    SUB S(a%()) NOINLINE
      a%(LBOUND(a%)) = 99
    END SUB
    DIM p%(1 TO 3), q%(4 TO 9)
    S p%()
    S q%()
    PRINT p%(1); q%(4)
    """;

  [TestCase(false)]
  [TestCase(true)]
  public void Route_GivenTwoArraysThroughOneParameter_ThenEachCallSeesItsOwnDescriptor(bool optimize)
    => AssertRoutedMatchesDirect(_twoArrays, "S", optimize);

  /// <summary>
  /// Forwarding a parameter onward. The inner callee must be handed the ORIGINAL caller's segment and
  /// offset, not the array heap's - the middle procedure has nothing of its own to describe, and a
  /// forward that rebuilt the descriptor from the far-heap segment would silently address the wrong
  /// memory whenever the outermost array was a static one, as it is here.
  /// </summary>
  private const string _forwarded = """
    SUB Inner(a%()) NOINLINE
      a%(2) = a%(1) + 5
    END SUB
    SUB Outer(a%()) NOINLINE
      Inner a%()
    END SUB
    DIM v%(1 TO 3)
    v%(1) = 40
    Outer v%()
    PRINT v%(2)
    """;

  [TestCase(false)]
  [TestCase(true)]
  public void Route_GivenArrayParameterForwarded_ThenTheOriginalDescriptorTravelsOn(bool optimize)
    => AssertRoutedMatchesDirect(_forwarded, "Outer", optimize);

  /// <summary>
  /// A STRING array parameter READ. A string element is a handle - one word - so reading it through
  /// the far element address is an ordinary scalar load, and this executes against the direct build
  /// rather than merely being accepted: an address that lost its segment would read the program's own
  /// data and print something, which is exactly the failure mode a routing assertion alone misses.
  /// </summary>
  private const string _stringArrayRead = """
    SUB S(a$()) NOINLINE
      PRINT a$(1); a$(3)
    END SUB
    DIM v$(1 TO 3)
    v$(1) = "ab"
    v$(3) = "cd"
    S v$()
    """;

  [TestCase(false)]
  [TestCase(true)]
  public void Route_GivenStringArrayParameterRead_ThenTheHandleCrossesCorrectly(bool optimize)
    => AssertRoutedMatchesDirect(_stringArrayRead, "S", optimize);

  /// <summary>
  /// Assigning INTO a string array parameter declines, for the reason a <c>DIM ... AT</c> array's
  /// string elements decline: the assignment hands the element's ADDRESS to the string runtime, whose
  /// routines take a near pointer. Losing the segment there does not fail - it writes the program's
  /// own data instead - so the lowering refuses rather than silently corrupting memory.
  /// </summary>
  [Test]
  public void Route_GivenStringArrayParameterAssignment_ThenItDeclinesRatherThanLosingTheSegment() {
    const string source = """
      SUB S(a$()) NOINLINE
        a$(3) = a$(1) + "!"
      END SUB
      DIM v$(1 TO 3)
      v$(1) = "ab"
      S v$()
      PRINT v$(3)
      """;
    var routed = new CodeGenerator(Bind(source)) { Optimize = false, UseExperimentalBackend = true };
    var image = routed.EmitExecutable();

    Assert.Multiple(() => {
      Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
      Assert.That(routed.BackendRoutedNames, Does.Not.Contain("S"));
      Assert.That(image, Is.Not.Empty, "the program still compiles - the procedure falls back to the direct emitter");
    });
  }

  /// <summary>
  /// REDIM through an array parameter declines. The callee widened the caller's descriptor into its
  /// own frame, so rewriting those cells would change only the copy: the caller would still describe
  /// the old block, and after a REDIM that block has been freed. Element writes are unaffected, which
  /// the tests above rely on.
  /// </summary>
  [Test]
  public void Route_GivenRedimOfAnArrayParameter_ThenItDeclinesRatherThanWritingACopy() {
    const string source = """
      SUB S(a%()) NOINLINE
        REDIM a%(1 TO 9)
      END SUB
      DIM v%()
      REDIM v%(1 TO 2)
      S v%()
      PRINT UBOUND(v%)
      """;
    var routed = new CodeGenerator(Bind(source)) { Optimize = false, UseExperimentalBackend = true };
    var image = routed.EmitExecutable();

    Assert.Multiple(() => {
      Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
      Assert.That(routed.BackendRoutedNames, Does.Not.Contain("S"));
      Assert.That(routed.BackendDeclines.Any(d => d.Name == "S" && d.Reason.Contains("PARAMETER", StringComparison.Ordinal)),
        Is.True, string.Join(" | ", routed.BackendDeclines.Select(d => d.Name + ": " + d.Reason)));
      Assert.That(image, Is.Not.Empty);
    });
  }

  /// <summary>
  /// The bodies that used to exhaust the register allocator. None of them is exotic: bounds plus a
  /// read-modify-write, and a summing loop over the whole array. They declined with
  /// <c>allocation: no register assignment, and nothing left that can move to memory</c> while the
  /// IDENTICAL body over a shared dynamic array allocated fine - the difference was that a shared
  /// array's descriptor fields are absolute data cells, while a parameter's are reached through a
  /// pointer, and every field's GEP was materialized into a base register of its own.
  ///
  /// <para>
  /// These are execution tests rather than "it routes" tests on purpose: the allocator declining is a
  /// safe fallback today, so the thing worth pinning is that the routed image is CORRECT once it
  /// stops declining. They matter for retirement specifically - after the direct emitter is gone, a
  /// decline here is a compile failure, not a fallback.
  /// </para>
  /// </summary>
  private const string _boundsAndReadModifyWrite = """
    SUB S(a%()) NOINLINE
      PRINT LBOUND(a%); UBOUND(a%)
      a%(2) = a%(1) * 10
    END SUB
    DIM v%(1 TO 4)
    v%(1) = 7
    S v%()
    PRINT v%(2)
    """;

  [TestCase(false)]
  [TestCase(true)]
  public void Route_GivenBoundsAndAReadModifyWrite_ThenTheAllocatorStillFindsRegisters(bool optimize)
    => AssertRoutedMatchesDirect(_boundsAndReadModifyWrite, "S", optimize);

  private const string _summingLoop = """
    FUNCTION Total%(a%()) NOINLINE
      DIM t%
      FOR i% = LBOUND(a%) TO UBOUND(a%)
        t% = t% + a%(i%)
      NEXT i%
      Total% = t%
    END FUNCTION
    DIM p%(1 TO 3), q%(2 TO 6)
    FOR i% = 1 TO 3
      p%(i%) = i%
    NEXT i%
    FOR i% = 2 TO 6
      q%(i%) = i% * 2
    NEXT i%
    PRINT Total%(p%()); Total%(q%())
    """;

  [TestCase(false)]
  [TestCase(true)]
  public void Route_GivenALoopOverTheWholeArray_ThenBothCallersGetTheirOwnBounds(bool optimize)
    => AssertRoutedMatchesDirect(_summingLoop, "Total", optimize);

  private const string _severalElementsAndBounds = """
    SUB S(a%()) NOINLINE
      PRINT LBOUND(a%); UBOUND(a%)
      PRINT a%(3); a%(5)
      a%(4) = a%(3) + a%(5)
    END SUB
    DIM v%(3 TO 5)
    v%(3) = 11
    v%(5) = 22
    S v%()
    PRINT v%(3); v%(4); v%(5)
    """;

  [TestCase(false)]
  [TestCase(true)]
  public void Route_GivenSeveralElementsAndBothBounds_ThenTheRoutedImageStillAgrees(bool optimize)
    => AssertRoutedMatchesDirect(_severalElementsAndBounds, "S", optimize);
}
