using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// PB 3.2 data pointers on the retargetable path: <c>VARPTR32</c> forms one, <c>@p</c> reads and
/// writes through it, <c>@p[i]</c> steps it by whole targets and <c>@q.Field</c> selects inside the
/// record it names.
///
/// Every case asserts the VALUE the program should print as well as agreement with the direct
/// emitter. Agreement alone would pass on a shared misunderstanding - a dereference that read the
/// wrong cell in both paths prints the same wrong number twice - and the point of a pointer is
/// precisely which cell it reaches.
/// </summary>
[TestFixture]
public sealed class BackendPointerTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static (string Direct, string Routed, IEnumerable<string> RoutedNames) RunBothWays(string source) {
    var direct = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };
    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();
    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));

    string Execute(byte[] image, string which) {
      try {
        return Cpu8086.Run(image).Output;
      } catch (Cpu8086Exception e) {
        Assert.Ignore($"the interpreter cannot run the {which} image: {e.Message}");
        return "";
      }
    }

    return (Execute(directImage, "direct"), Execute(routedImage, "routed"), routed.BackendRoutedNames);
  }

  /// <summary>The reason the whole-module lowering declined, or null when it took the program.</summary>
  private static string? DeclineReason(string source) {
    var module = IrLowering.TryLowerModule(Bind(source), out var why);
    return module is null ? why ?? "unknown" : null;
  }

  [Test]
  public void Deref_GivenAPointerToAScalar_ThenItReadsAndWritesThatVariablesOwnCell() {
    var (direct, routed, names) = RunBothWays("""
      DIM p AS INTEGER PTR
      x% = 11
      y% = 77
      p = VARPTR32(x%)
      PRINT @p
      @p = 42
      PRINT x%
      PRINT y%
      """);

    Assert.That(names, Does.Contain("main"), "the back end did not take the module body under test");
    Assert.That(routed, Is.EqualTo(direct));
    // the value read back is the one the variable held, the write lands in x% - and NOT in the
    // variable next to it, which is what a wrongly-formed address would show
    Assert.That(routed.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.Trim()).ToArray(), Is.EqualTo(new[] { "11", "42", "77" }));
  }

  [Test]
  public void Deref_GivenAnIndexedPointerIntoAnArray_ThenTheIndexStepsByWholeElements() {
    var (direct, routed, names) = RunBothWays("""
      DIM p AS INTEGER PTR
      DIM a%(1 TO 5)
      FOR i% = 1 TO 5
        a%(i%) = i% * 10
      NEXT i%
      p = VARPTR32(a%(1))
      PRINT @p[0]
      PRINT @p[2]
      @p[4] = 99
      PRINT a%(5)
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    // zero-based whatever the array's own lower bound is, and scaled by the TARGET's size: @p[2] is
    // a%(3) rather than a%(2) or the byte two along
    Assert.That(routed.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.Trim()).ToArray(), Is.EqualTo(new[] { "10", "30", "99" }));
  }

  [Test]
  public void Deref_GivenAPointerToARecord_ThenAFieldSelectsAtItsOwnOffset() {
    var (direct, routed, names) = RunBothWays("""
      TYPE Pt
        X AS INTEGER
        Y AS INTEGER
      END TYPE
      DIM q AS Pt PTR
      DIM v AS Pt
      v.X = 7
      v.Y = -3
      q = VARPTR32(v)
      PRINT @q.X
      PRINT @q.Y
      @q.Y = 33
      PRINT v.Y
      PRINT v.X
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    // the second field is reached at its offset, and writing it leaves the first alone
    Assert.That(routed.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.Trim()).ToArray(), Is.EqualTo(new[] { "7", "-3", "33", "7" }));
  }

  [Test]
  public void Call_GivenAByValPointerAgainstAByRefParameter_ThenTheCalleeWritesThroughIt() {
    var (direct, routed, names) = RunBothWays("""
      DECLARE SUB Bump (v AS INTEGER)
      DIM p AS INTEGER PTR
      x% = 10
      p = VARPTR32(x%)
      CALL Bump(BYVAL p)
      PRINT x%

      SUB Bump (v AS INTEGER)
        v = v + 1
      END SUB
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(routed.Trim(), Is.EqualTo("11"), "the pointer's own value was the address the callee wrote through");
  }

  [Test]
  public void Deref_GivenAPointerAssignedFromAnotherPointer_ThenBothReachTheSameCell() {
    var (direct, routed, names) = RunBothWays("""
      DIM p AS INTEGER PTR
      DIM r AS INTEGER PTR
      x% = 5
      p = VARPTR32(x%)
      r = p
      @r = 64
      PRINT x%
      PRINT @p
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(routed.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.Trim()).ToArray(), Is.EqualTo(new[] { "64", "64" }));
  }

  /// <summary>
  /// <c>VARPTR</c> is an address, and the only thing about an address that both back ends can be held
  /// to is the DISTANCE between two of them: the absolute offset a variable happens to land on is a
  /// layout fact, and the direct emitter's data cell and the routed frame slot are not the same place.
  /// Array elements are the case where the distance IS the language's own promise - PB programs walk
  /// an array by adding SIZEOF(element) to VARPTR of its first - so that is what is pinned here.
  /// </summary>
  [Test]
  public void VarPtr_GivenAdjacentArrayElements_ThenTheAddressesDifferByTheElementSize() {
    var (direct, routed, names) = RunBothWays("""
      DIM a%(1 TO 4)
      DIM b&(1 TO 4)
      DIM first AS LONG
      DIM second AS LONG
      first = VARPTR(a%(1))
      second = VARPTR(a%(2))
      PRINT second - first
      PRINT CLNG(VARPTR(a%(4))) - first
      PRINT CLNG(VARPTR(b&(2))) - CLNG(VARPTR(b&(1)))
      """);

    Assert.That(names, Does.Contain("main"), "the back end did not take the module body under test");
    Assert.That(routed, Is.EqualTo(direct));
    // two bytes an INTEGER, four a LONG, and the stride multiplies rather than repeating
    Assert.That(routed.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.Trim()).ToArray(), Is.EqualTo(new[] { "2", "6", "4" }));
  }

  /// <summary>
  /// The round trip that makes VARPTR mean anything: an address handed to POKE reaches the variable
  /// itself, and PEEK at the same address reads the byte back. Both halves are asserted, because a
  /// POKE that landed somewhere else and a PEEK that read the same wrong place would agree with each
  /// other and say nothing about the variable.
  /// </summary>
  [Test]
  public void VarPtr_WhenPokedAndPeekedThrough_ThenItReachesTheVariablesOwnByte() {
    var (direct, routed, names) = RunBothWays("""
      DIM v AS WORD
      DIM w AS WORD
      v = 0
      w = 999
      DEF SEG = VARSEG(v)
      POKE VARPTR(v), 65
      PRINT PEEK(VARPTR(v))
      PRINT v
      PRINT w
      DEF SEG
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    // the byte read back is the byte written, the variable itself now holds it - and the variable
    // beside it is untouched, which is what a VARPTR off by a cell would show
    Assert.That(routed.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.Trim()).ToArray(), Is.EqualTo(new[] { "65", "65", "999" }));
  }

  /// <summary>
  /// The same round trip against a SHARED variable, which is not a frame slot but the direct
  /// emitter's own data cell - so the address is the label's offset rather than a register the frame
  /// put one in. Both back ends address the very same storage here, which is why the value the
  /// procedure poked is visible to the module body afterwards.
  /// </summary>
  [Test]
  public void VarPtr_GivenASharedVariable_ThenItAddressesTheModulesOwnDataCell() {
    var (direct, routed, names) = RunBothWays("""
      DECLARE FUNCTION Poked% ()
      DIM g AS SHARED WORD
      g = 5
      PRINT Poked%()
      PRINT g
      END

      FUNCTION Poked% ()
        SHARED g AS WORD
        DEF SEG = VARSEG(g)
        POKE VARPTR(g), 77
        Poked% = PEEK(VARPTR(g))
        DEF SEG
      END FUNCTION
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(names, Does.Contain("Poked"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(routed.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.Trim()).ToArray(), Is.EqualTo(new[] { "77", "77" }));
  }

  /// <summary>
  /// VARPTR names a CELL, never a value: taking the address of a variable does not read it, and the
  /// variable it names has to survive in memory for the address to reach anything. A slot promoted to
  /// an SSA register would leave the POKE above writing to a frame cell nobody reads again.
  /// </summary>
  [Test]
  public void VarPtr_GivenTheSameVariableTwice_ThenItAnswersTheSameAddress() {
    var (direct, routed, names) = RunBothWays("""
      DIM v AS INTEGER
      DIM here AS LONG
      v = 3
      here = VARPTR(v)
      PRINT CLNG(VARPTR(v)) - here
      v = v + 1
      PRINT v
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(routed.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.Trim()).ToArray(), Is.EqualTo(new[] { "0", "4" }));
  }

  // ---- VARSEG of storage that is not in DS ----------------------------------
  //
  // VARSEG used to be one unconditional call to rt_varseg, which is MOV AX, DS - so it answered DS
  // for arguments whose storage is somewhere else entirely, and never looked at the operand to find
  // out. The direct emitter answers `place.Far ? ES : DS`, and genuine PBC 3.50 agrees with it: run
  // through the pb35 oracle, VARSEG of a dynamic array's element is 3144 paragraphs above VARSEG of a
  // scalar and an AT array's is the segment it names. The routed answer of 0 was a wrong address, and
  // a DEF SEG built out of it reads and writes the program's own data.
  //
  // Each case below is a DIFFERENCE against a scalar's VARSEG rather than an absolute segment: the
  // two back ends lay data out differently and only the relationship is a property of the language.

  /// <summary>
  /// A dynamic array's elements live in the far array heap, one segment for the whole image, which is
  /// the <c>rt_arrseg</c> cell the direct emitter loads <c>ES</c> from before every element access.
  /// The subscript comes back through a two-call-site <c>NOINLINE</c> function so nothing about the
  /// access folds; the segment does not depend on it either way, which is the point.
  /// </summary>
  [Test]
  public void VarSeg_GivenADynamicArrayElement_ThenItAnswersTheFarHeapSegmentRatherThanDs() {
    var (direct, routed, names) = RunBothWays("""
      DECLARE FUNCTION Given%(BYVAL v%)
      DIM k AS INTEGER
      DIM scal AS INTEGER
      REDIM a%(0 TO 7)
      k = Given%(2)
      scal = Given%(1)
      a%(k) = Given%(33)
      PRINT VARSEG(a%(k)) - VARSEG(scal);
      DEF SEG = VARSEG(a%(k))
      PRINT PEEK(VARPTR(a%(k)));
      DEF SEG
      PRINT a%(k)
      END

      FUNCTION Given%(BYVAL v%) NOINLINE
        Given% = v%
      END FUNCTION
      """);

    Assert.That(names, Does.Contain("main"), "the back end did not take the module body under test");
    Assert.That(routed, Is.EqualTo(direct));
    // the far heap is 0x2000 paragraphs above DGROUP here, and PEEK through the pair reads the very
    // byte the element holds - which is the half a DS-shaped answer got wrong
    Assert.That(routed.Trim(), Is.EqualTo("8192  33  33"));
  }

  /// <summary>
  /// <c>DIM ... AT</c> is the other far class, and its segment is the compile-time constant the
  /// declaration named - so VARSEG of one of its elements is that segment and nothing else.
  /// </summary>
  [Test]
  public void VarSeg_GivenAnAbsoluteArrayElement_ThenItAnswersTheSegmentTheDimNamed() {
    var (direct, routed, names) = RunBothWays("""
      DECLARE FUNCTION Given%(BYVAL v%)
      DIM DYNAMIC vid%(0 TO 7) AT &HB800
      DIM v AS INTEGER
      v = Given%(1)
      vid%(Given%(0)) = &H0F41
      PRINT VARSEG(vid%(0)) - VARSEG(v)
      END

      FUNCTION Given%(BYVAL v%) NOINLINE
        Given% = v%
      END FUNCTION
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(routed.Trim(), Is.Not.EqualTo("0"), "an AT array is not in DGROUP, whatever DGROUP is");
  }

  /// <summary>
  /// ...and the control: an ordinary scalar and a STATIC array element ARE in the data segment, so
  /// the answer is still <c>DS</c> and the change did not make everything far.
  /// </summary>
  [Test]
  public void VarSeg_GivenNearStorage_ThenItStillAnswersTheDataSegment() {
    var (direct, routed, names) = RunBothWays("""
      DECLARE FUNCTION Given%(BYVAL v%)
      DIM a%(0 TO 7)
      DIM v AS INTEGER
      DIM s AS STRING
      v = Given%(1)
      s = "x"
      a%(Given%(2)) = Given%(5)
      PRINT VARSEG(a%(0)) - VARSEG(v); VARSEG(s) - VARSEG(v)
      END

      FUNCTION Given%(BYVAL v%) NOINLINE
        Given% = v%
      END FUNCTION
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(routed.Trim(), Is.EqualTo("0  0"),
      "a static array's elements and a string's HANDLE are both in DGROUP");
  }

  /// <summary>
  /// <c>VARSEG</c> ADDRESSES its operand, so a subscript with a side effect happens. The direct
  /// emitter forms the place before asking which segment it is in, and genuine PBC 3.50 does the
  /// same - the pb35 oracle prints <c>hits after varseg 1</c> for this program. Answering out of the
  /// symbol alone skipped the call, and a <c>$ERROR BOUNDS ON</c> check with it.
  ///
  /// <para>
  /// <c>VARPTR</c> is the control on the same line: it always evaluated, so the second count moving
  /// by one and not by two is what says the fix landed on VARSEG and nowhere else.
  /// </para>
  /// </summary>
  [Test]
  public void VarSeg_GivenASubscriptWithASideEffect_ThenTheSubscriptIsStillEvaluated() {
    var (direct, routed, names) = RunBothWays("""
      DECLARE FUNCTION Side%(BYVAL v%)
      DIM hits AS SHARED INTEGER
      DIM a%(0 TO 3)
      DIM v AS INTEGER
      hits = 0
      a%(1) = 5
      PRINT VARSEG(a%(Side%(1))) - VARSEG(v); hits;
      PRINT CLNG(VARPTR(a%(Side%(1)))) - CLNG(VARPTR(a%(0))); hits
      END

      FUNCTION Side%(BYVAL v%) NOINLINE
        SHARED hits AS INTEGER
        hits = hits + 1
        Side% = v%
      END FUNCTION
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(routed.Trim(), Is.EqualTo("0  1  2  2"),
      "the VARSEG subscript ran once, and the VARPTR one after it took the count to two");
  }

  /// <summary>
  /// The paged classes decline instead of answering. Their segment is recomputed per element - HUGE
  /// steps it by <c>byteOffset >> 4</c> and the EMS pair by which page is in the window - so there is
  /// no one segment to name, and inventing one would be the same defect the two cases above were.
  /// </summary>
  [Test]
  public void Lowering_GivenVarSegOfAPagedArrayElement_ThenItDeclines() {
    Assert.That(DeclineReason("""
      DIM HUGE h%(0 TO 40000)
      h%(1) = 2
      PRINT VARSEG(h%(1))
      """), Is.EqualTo("VARSEG of an element of the Huge array h"));
  }

  /// <summary>
  /// A PB pointer is a near cell here, so a far-heap address cannot be put in one: the segment is
  /// dropped on the way in and every later use reads DGROUP. <c>@p</c> masked it - the promoted slot
  /// still carried the far value, so the first dereference was right - and <c>@p[1]</c>, whose GEP
  /// takes the pointer's own near type, read the program's data instead.
  /// </summary>
  [Test]
  public void Lowering_GivenAPointerToADynamicArrayElement_ThenItDeclines() {
    Assert.That(DeclineReason("""
      DIM p AS INTEGER PTR
      REDIM a%(0 TO 7)
      a%(2) = 33
      p = VARPTR32(a%(2))
      PRINT @p[1]
      """), Is.EqualTo("VARPTR32 of storage outside the default data segment"));
  }

  /// <summary>
  /// A pointer made out of a NUMBER declines rather than lowering. The IR's pointer is a near offset
  /// and PB's is a seg:off pair, so a DWORD carries a segment this path has no way to honour;
  /// answering it with the low word would be a silently wrong address rather than a missing feature.
  /// </summary>
  [Test]
  public void Lowering_GivenAPointerMadeFromANumber_ThenItDeclines() {
    Assert.That(DeclineReason("""
      DIM p AS INTEGER PTR
      d& = 12345
      p = d&
      @p = 1
      """), Is.EqualTo("unsupported pointer value"));
  }

  /// <summary>
  /// A pointer a PROCEDURE also reads declines too, and for the layout reason rather than the value
  /// one: shared storage is the direct emitter's own 4-byte data cell, and a 2-byte near offset
  /// written into it leaves the segment half holding whatever was there before.
  /// </summary>
  [Test]
  public void Lowering_GivenAPointerSharedWithAProcedure_ThenItDeclines() {
    Assert.That(DeclineReason("""
      DIM p AS INTEGER PTR
      DECLARE SUB Poke ()
      x% = 1
      p = VARPTR32(x%)
      CALL Poke
      PRINT x%

      SUB Poke ()
        SHARED p AS INTEGER PTR
        @p = 9
      END SUB
      """), Is.EqualTo("pointer variable with shared storage"));
  }
}
