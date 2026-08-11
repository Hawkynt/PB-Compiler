using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The measurement the retargetable path has been missing: the same program compiled BOTH ways, both
/// images <b>executed</b>, and their output compared.
///
/// Everything else about the x86-16 back end is checked statically - what selects, what allocates,
/// which registers an ABI names, whether an image assembles. None of that says the emitted code
/// computes the right thing. This does, and it needs no vintage oracle to do it: byte-identity with
/// PBC 3.50 is the direct emitter's job, and the IR path will never match those bytes because it is a
/// different code generator. What it must match is what the program PRINTS, and the direct emitter -
/// which the golden battery holds to the genuine compiler - is the reference for that.
///
/// A program <see cref="Cpu8086"/> cannot run is skipped, never passed: the interpreter throws on any
/// opcode or DOS call it does not implement, so a green test here means the code really ran.
/// </summary>
[TestFixture]
public sealed class BackendDifferentialTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static (string Output, string Routed, IEnumerable<string> RoutedNames) RunBothWays(string source) {
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

  [Test]
  public void Run_GivenAnIntegerFunction_ThenBothPathsPrintTheSameThing() {
    var (direct, routed, names) = RunBothWays("""
      FUNCTION Twice%(BYVAL v%)
        Twice% = v% + v%
      END FUNCTION

      PRINT Twice%(21)
      """);

    Assert.That(names, Does.Contain("Twice"), "the back end did not take the function under test");
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(direct.Trim(), Is.EqualTo("42"), "and the answer is the one BASIC gives");
  }

  [Test]
  public void Run_GivenAConstantDivide_ThenBothPathsAgree() {
    var (direct, routed, names) = RunBothWays("""
      FUNCTION Tenth%(BYVAL v%)
        Tenth% = v% \ 10
      END FUNCTION

      PRINT Tenth%(250)
      PRINT Tenth%(-7)
      """);

    Assert.That(names, Does.Contain("Tenth"));
    Assert.That(routed, Is.EqualTo(direct));
  }

  [Test]
  public void Run_GivenAModuleBodyTheBackEndOwns_ThenTheWholeProgramAgrees() {
    var (direct, routed, names) = RunBothWays("""
      DIM n AS INTEGER
      n = 42
      PRINT "n="
      PRINT n
      """);

    Assert.That(names, Does.Contain("main"), "this is the whole-program case, not the per-function one");
    Assert.That(routed, Is.EqualTo(direct));
  }

  [Test]
  public void Run_GivenAValueLiveAcrossACall_ThenTheSpilledFormComputesTheSameAnswer() {
    // the parameter is live across a PRINT, so the back end spills it into the caller's own word -
    // this is the first check that the spill actually preserves the value rather than merely allocating
    var (direct, routed, names) = RunBothWays("""
      FUNCTION Twice%(BYVAL v%)
        PRINT "in"
        Twice% = v% + v%
      END FUNCTION

      PRINT Twice%(21)
      """);

    Assert.That(names, Does.Contain("Twice"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(routed, Does.Contain("42"));
  }

  [Test]
  public void Run_GivenALoopAndAControlFlowMerge_ThenBothPathsAgree() {
    var (direct, routed, names) = RunBothWays("""
      FUNCTION SumTo%(BYVAL n%)
        DIM i AS INTEGER
        DIM total AS INTEGER
        total = 0
        FOR i = 1 TO n%
          IF i MOD 2 = 0 THEN
            total = total + i
          ELSE
            total = total - 1
          END IF
        NEXT i
        SumTo% = total
      END FUNCTION

      PRINT SumTo%(10)
      """);

    Assert.That(names, Does.Contain("SumTo"));
    Assert.That(routed, Is.EqualTo(direct));
  }

  [Test]
  public void Run_GivenASharedGlobal_ThenBothPathsAddressTheSameStorage() {
    var (direct, routed, names) = RunBothWays("""
      DIM g AS SHARED INTEGER

      FUNCTION AddG%(BYVAL v%)
        AddG% = v% + g
      END FUNCTION

      g = 40
      PRINT AddG%(2)
      """);

    Assert.That(names, Does.Contain("AddG"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(routed, Does.Contain("42"), "the routed function read the global the direct path wrote");
  }

  // ---- dynamic arrays -------------------------------------------------------
  //
  // Dynamic array storage is the one memory a generated program reaches that is not its own: the
  // runtime bump-allocates it out of the far array heap, whose segment lives in rt_arrseg. The IR says
  // so with an address space on the pointer type and the back end turns that into the ES override the
  // direct emitter writes by hand. These tests are about the VALUES read back, because that is the
  // part a wrong segment does not disturb: an element written and read through the same wrong address
  // still round-trips, and the first version of this work printed the right numbers while quietly
  // overwriting the program's own code with them.

  [Test]
  public void Run_GivenADynamicArrayFillingTheHeap_ThenValuesReadBackAndBothPathsAgree() {
    // 32760 INTEGERs is 65520 bytes - the largest block the bump allocator will hand out (it refuses
    // anything that would carry the top past 0xFFF0). The exact-fit boundary, from below.
    var (direct, routed, names) = RunBothWays("""
      REDIM a(1 TO 32760) AS INTEGER
      a(1) = 11
      a(32760) = 99
      PRINT a(1); a(32760); a(16000)
      """);

    Assert.That(names, Does.Contain("main"), "the back end did not take the module body under test");
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(direct.Trim(), Is.EqualTo("11  99  0"), "both ends of a full segment survive, and the middle starts zeroed");
  }

  [Test]
  public void Run_GivenADynamicArrayPastASegment_ThenBothPathsRefuseItRatherThanWrapping() {
    // 20000 LONGs is 80000 bytes. The count fits a word and the element size fits a word, but the
    // PRODUCT does not - and 80000 mod 65536 is 14464, so a 16-bit multiply would allocate 14464 bytes
    // and let a(20000) write 65 KB past the end of it. Computing the byte count at 32 bits is what
    // turns that into the runtime's own refusal, which is also what the direct emitter does.
    var (direct, routed, names) = RunBothWays("""
      REDIM a(1 TO 20000) AS LONG
      a(20000) = 7
      PRINT a(20000)
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(direct, Does.Contain("OUT OF ARRAY SPACE"), "the oversized allocation is refused");
    Assert.That(direct, Does.Not.Contain("7"), "and nothing after it runs");
  }

  [Test]
  public void Run_GivenARedimPreserveThatGrows_ThenTheOldContentsSurviveAndTheTailIsZero() {
    var (direct, routed, names) = RunBothWays("""
      DIM i AS INTEGER
      REDIM a(1 TO 5) AS LONG
      FOR i = 1 TO 5
        a(i) = i * 1000&
      NEXT i
      REDIM PRESERVE a(1 TO 9)
      a(9) = -1
      PRINT a(1); a(5); a(6); a(9)
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(direct.Trim(), Is.EqualTo("1000  5000  0 -1"),
      "the prefix carries over, the grown tail reads as zero, and the new top element is writable");
  }

  [Test]
  public void Run_GivenARedimPreserveThatShrinks_ThenOnlyWhatFitsIsCopied() {
    // PB lets the outer bound shrink, and the copy is min(old, new) - copying the old length into the
    // shorter block would run past the end of it.
    var (direct, routed, names) = RunBothWays("""
      DIM i AS INTEGER
      REDIM a(1 TO 6) AS INTEGER
      FOR i = 1 TO 6
        a(i) = i * 11
      NEXT i
      REDIM PRESERVE a(1 TO 2)
      PRINT a(1); a(2); UBOUND(a)
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(direct.Trim(), Is.EqualTo("11  22  2"));
  }

  /// <summary>
  /// <c>REDIM PRESERVE</c> as the FIRST sizing of an array: there is nothing to preserve, and the
  /// descriptor the old size is read from is still all zeroes. The direct emitter spells the case as a
  /// test of the descriptor's segment word; here it falls out as a copy of zero bytes from a null
  /// block, which is why no first-time guard is needed on either side.
  /// </summary>
  [Test]
  public void Run_GivenARedimPreserveOfANeverAllocatedArray_ThenItSimplyAllocates() {
    var (direct, routed, names) = RunBothWays("""
      REDIM PRESERVE a(1 TO 3) AS INTEGER
      a(2) = 5
      PRINT a(1); a(2); UBOUND(a)
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(direct.Trim(), Is.EqualTo("0  5  3"));
  }

  [Test]
  public void Run_GivenEraseThenRedim_ThenTheFreshArrayReadsZero() {
    var (direct, routed, names) = RunBothWays("""
      DIM i AS INTEGER
      REDIM a(1 TO 4) AS INTEGER
      FOR i = 1 TO 4
        a(i) = 77
      NEXT i
      ERASE a
      REDIM a(1 TO 4)
      PRINT a(1); a(4); UBOUND(a)
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(direct.Trim(), Is.EqualTo("0  0  4"), "ERASE gives the block back and the next REDIM starts zeroed");
  }

  /// <summary>
  /// A dynamic array of STRINGS is the case the count-taking entries exist for: its element is a
  /// runtime handle, whose width only the runtime knows, so <c>rt_arr_alloc_ptr</c> scales the count
  /// instead of the lowering. The variable subscript also exercises the element-indexed GEP, where the
  /// index has to be scaled into a register of its own - the 8086 has no scaled index.
  /// </summary>
  [Test]
  public void Run_GivenADynamicStringArrayWithAVariableIndex_ThenBothPathsAgree() {
    var (direct, routed, names) = RunBothWays("""
      DIM i AS INTEGER
      REDIM s(1 TO 4) AS STRING
      FOR i = 1 TO 4
        s(i) = "v" + CHR$(48 + i)
      NEXT i
      FOR i = 4 TO 1 STEP -1
        PRINT s(i);
      NEXT i
      PRINT
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(direct.Trim(), Is.EqualTo("v4v3v2v1"));
  }

  [Test]
  public void Run_GivenAStringArrayGrownAndErased_ThenBothPathsAgree() {
    var (direct, routed, names) = RunBothWays("""
      REDIM s(1 TO 2) AS STRING
      s(1) = "A"
      s(2) = "B"
      REDIM PRESERVE s(1 TO 4)
      s(4) = "D"
      PRINT s(1); s(2); "["; s(3); "]"; s(4)
      ERASE s
      REDIM s(1 TO 2)
      PRINT "["; s(1); "]"
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(direct.Replace("\r", "").Trim(), Is.EqualTo("AB[]D\n[]"),
      "the prefix survives, the grown tail is the empty string");
  }

  [Test]
  public void Run_GivenATwoDimensionalDynamicArray_ThenEveryElementReadsBack() {
    var (direct, routed, names) = RunBothWays("""
      DIM r AS INTEGER, c AS INTEGER
      REDIM g(1 TO 3, 1 TO 4) AS INTEGER
      FOR r = 1 TO 3
        FOR c = 1 TO 4
          g(r, c) = r * 10 + c
        NEXT c
      NEXT r
      PRINT g(1, 1); g(2, 3); g(3, 4); UBOUND(g, 2)
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(direct.Trim(), Is.EqualTo("11  23  34  4"),
      "the row-major flattening reaches every element of the far-heap block");
  }
}
