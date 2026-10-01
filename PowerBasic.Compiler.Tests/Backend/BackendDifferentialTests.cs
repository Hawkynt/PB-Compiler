using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Programs compiled through the x86-16 back end, the images <b>executed</b>, and their output read
/// against the answer the BASIC source gives.
///
/// Everything else about the x86-16 back end is checked statically - what selects, what allocates,
/// which registers an ABI names, whether an image assembles. None of that says the emitted code
/// computes the right thing. This does, and it needs no vintage oracle to do it: byte-identity with
/// PBC 3.50 is the golden battery's job. What is checked here is what the program PRINTS, and each
/// expected line is worked out from the source.
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

  private static (string Output, IEnumerable<string> RoutedNames) Run(string source) {
    var generator = new CodeGenerator(Bind(source)) { Optimize = true};
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));

    try {
      return (Cpu8086.Run(image).Output, generator.BackendRoutedNames);
    } catch (Cpu8086Exception e) {
      Assert.Ignore($"the interpreter cannot run the image: {e.Message}");
      return ("", generator.BackendRoutedNames);
    }
  }

  [Test]
  public void Run_GivenAnIntegerFunction_ThenItPrintsTheSum() {
    var (output, names) = Run("""
      FUNCTION Twice%(BYVAL v%) NOINLINE
        Twice% = v% + v%
      END FUNCTION

      PRINT Twice%(21)
      """);

    Assert.That(names, Does.Contain("Twice"), "the back end did not take the function under test");
    Assert.That(output.Trim(), Is.EqualTo("42"), "and the answer is the one BASIC gives");
  }

  [Test]
  public void Run_GivenAConstantDivide_ThenTheQuotientTruncatesTowardZero() {
    var (output, names) = Run("""
      FUNCTION Tenth%(BYVAL v%) NOINLINE
        Tenth% = v% \ 10
      END FUNCTION

      PRINT Tenth%(250)
      PRINT Tenth%(-7)
      """);

    Assert.That(names, Does.Contain("Tenth"));
    Assert.That(output.Replace("\r", "").Trim(), Is.EqualTo("25 \n 0"), "-7 \\ 10 truncates toward zero");
  }

  [Test]
  public void Run_GivenAModuleBodyTheBackEndOwns_ThenTheWholeProgramPrints() {
    var (output, names) = Run("""
      DIM n AS INTEGER
      n = 42
      PRINT "n="
      PRINT n
      """);

    Assert.That(names, Does.Contain("main"), "this is the whole-program case, not the per-function one");
    Assert.That(output.Replace("\r", "").Trim(), Is.EqualTo("n=\n 42"));
  }

  [Test]
  public void Run_GivenAValueLiveAcrossACall_ThenTheSpilledFormComputesTheSameAnswer() {
    // the parameter is live across a PRINT, so the back end spills it into the caller's own word -
    // this is the first check that the spill actually preserves the value rather than merely allocating
    var (output, names) = Run("""
      FUNCTION Twice%(BYVAL v%) NOINLINE
        PRINT "in"
        Twice% = v% + v%
      END FUNCTION

      PRINT Twice%(21)
      """);

    Assert.That(names, Does.Contain("Twice"));
    Assert.That(output, Does.Contain("42"));
  }

  [Test]
  public void Run_GivenALoopAndAControlFlowMerge_ThenEachArmContributes() {
    var (output, names) = Run("""
      FUNCTION SumTo%(BYVAL n%) NOINLINE
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
    // the even i add 2+4+6+8+10 = 30, the five odd ones take 1 each
    Assert.That(output.Trim(), Is.EqualTo("25"));
  }

  /// <summary>
  /// <c>SHIFT LEFT</c> / <c>SHIFT RIGHT</c> on a 16-bit variable by a count the program computed. The
  /// 8086 takes a variable count only in <c>CL</c>, and the selector used to emit the shift against
  /// whatever register the allocator had chosen - which the assembler refuses, so this program ended
  /// the COMPILATION with an exception rather than with an answer or a decline.
  ///
  /// <para>
  /// Nothing had met it because the corpus shifts by a runtime count only at 32 and 64 bits, where the
  /// wide form declines instead. The counts here are taken through a two-call-site <c>NOINLINE</c>
  /// function on purpose: one call site would let interprocedural propagation prove the count and turn
  /// every shift back into the immediate form the bug is not in.
  /// </para>
  /// </summary>
  [Test]
  public void Run_GivenAShiftByAComputedCount_ThenEachWidthShiftsByThatAmount() {
    var (output, names) = Run("""
      DECLARE FUNCTION Given%(BYVAL v%)

      DIM a AS INTEGER, w AS WORD, n AS INTEGER
      n = Given%(3)
      a = Given%(-1234) : SHIFT RIGHT a, n
      PRINT a;
      a = Given%(-1234) : SHIFT LEFT a, n
      PRINT a;
      w = Given%(40000) : SHIFT RIGHT w, Given%(4)
      PRINT w; n
      END

      FUNCTION Given%(BYVAL v%) NOINLINE
        Given% = v%
      END FUNCTION
      """);

    Assert.That(names, Does.Contain("main"), "the back end did not take the module body under test");
    Assert.That(output.Trim(), Is.EqualTo("8037 -9872  2500  3"),
      "the right shift is logical, the count survives the shift, and both widths agree");
  }

  /// <summary>
  /// The same statement with a LITERAL count the immediate encoding cannot carry. <c>SHL r16, imm8</c>
  /// exists only for 1..31, so a count of 32 or 40 threw out of the assembler; the direct emitter puts
  /// every narrow count in <c>CL</c> and never meets the limit. What the part then does with a count it
  /// did not mask is a property of the part; the reading here is the 8086's, which applies the whole
  /// count, so both oversized shifts - the right one logical - leave zero.
  /// </summary>
  [Test]
  public void Run_GivenAShiftByALiteralOutsideTheImmediateWindow_ThenTheWholeCountIsApplied() {
    var (output, names) = Run("""
      DECLARE FUNCTION Given%(BYVAL v%)

      DIM a AS INTEGER
      a = Given%(-1234) : SHIFT LEFT a, 32
      PRINT a;
      a = Given%(-1234) : SHIFT RIGHT a, 40
      PRINT a;
      a = Given%(-1234) : SHIFT LEFT a, 0
      PRINT a
      END

      FUNCTION Given%(BYVAL v%) NOINLINE
        Given% = v%
      END FUNCTION
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(output.Trim(), Is.EqualTo("0  0 -1234"),
      "32 and 40 shift every bit out of the word, and a count of 0 leaves it alone");
  }

  /// <summary>
  /// A routed FUNCTION whose result comes back out of a runtime helper's own register pair. An
  /// unsigned 16-bit divide widens to 32 bits and goes through <c>rt_ldiv</c>, which answers in DX:AX;
  /// the selector copies the pair into virtual registers and copies the low half back into AX for the
  /// RET, and the peephole's copy-back fold deleted BOTH of those moves on the grounds that AX already
  /// held the value. Nothing then said AX was occupied between the call and the return, so the
  /// allocator gave AX to the unused high half and <c>MOV AX, DX</c> overwrote the answer on its way
  /// out: <c>PassW%</c> returned 0 for every input.
  ///
  /// <para>
  /// Two call sites with different arguments, through a <c>NOINLINE</c> function, because one would let
  /// interprocedural propagation fold the divide away entirely. The optimizer must be ON - the fold is
  /// gated on it - which is what <see cref="Run"/> already does.
  /// </para>
  /// </summary>
  [Test]
  public void Run_GivenAResultReturnedThroughARuntimeRegisterPair_ThenTheAnswerSurvivesTheReturn() {
    var (output, names) = Run("""
      DECLARE FUNCTION Given%(BYVAL v%)
      DECLARE FUNCTION Half%(BYVAL a AS WORD)

      PRINT Half%(Given%(-1)); Half%(Given%(4))
      END

      FUNCTION Given%(BYVAL v%) NOINLINE
        Given% = v%
      END FUNCTION
      FUNCTION Half%(BYVAL a AS WORD) NOINLINE
        Half% = a \ 2
      END FUNCTION
      """);

    Assert.That(names, Does.Contain("Half"), "the back end did not take the function under test");
    Assert.That(output.Trim(), Is.EqualTo("32767  2"), "the divide is unsigned, so 65535 \\ 2 is 32767");
  }

  /// <summary>
  /// A LONG result computed BEFORE the statement that ends the function, so it has to survive a call.
  /// The spill is fine; putting it back was not. <c>MOV AX, v</c> was the last mention of <c>AX</c> in
  /// the block - a <c>RET</c> declared no reads - so nothing said <c>AX</c> was occupied, and the
  /// allocator gave it to the very next value: the reload of the HIGH half. The emitted tail read
  /// <c>MOV AX,[lo] / MOV AX,AX / MOV AX,[hi] / MOV DX,AX</c>, the high word overwriting the low one on
  /// the way out, and the function answered 0 for every input in both optimizer modes.
  ///
  /// <para>
  /// The INTEGER twin was always right, and that is the tell rather than a coincidence: one register
  /// means no second reload to be given the first one's.
  /// </para>
  /// </summary>
  [Test]
  public void Run_GivenALongResultSetBeforeTheLastStatement_ThenTheWholeValueComesBack() {
    var (output, names) = Run("""
      DECLARE FUNCTION Given%(BYVAL v%)
      DECLARE FUNCTION Bumped&(BYVAL a&)

      PRINT Bumped&(Given%(1000))
      PRINT Bumped&(Given%(3))
      END

      FUNCTION Given%(BYVAL v%) NOINLINE
        Given% = v%
      END FUNCTION
      FUNCTION Bumped&(BYVAL a&) NOINLINE
        Bumped& = a& + 1
        PRINT "out";
      END FUNCTION
      """);

    Assert.That(names, Does.Contain("Bumped"), "the back end did not take the function under test");
    Assert.That(output.Replace("\r", "").Trim(), Is.EqualTo("out 1001 \nout 4"));
  }

  [Test]
  public void Run_GivenASharedGlobal_ThenTheFunctionReadsTheModulesStore() {
    var (output, names) = Run("""
      DIM g AS SHARED INTEGER

      FUNCTION AddG%(BYVAL v%) NOINLINE
        AddG% = v% + g
      END FUNCTION

      g = 40
      PRINT AddG%(2)
      """);

    Assert.That(names, Does.Contain("AddG"));
    Assert.That(output, Does.Contain("42"), "the function read the global the module body wrote");
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
  public void Run_GivenADynamicArrayFillingTheHeap_ThenValuesReadBack() {
    // 32760 INTEGERs is 65520 bytes - the largest block the bump allocator will hand out (it refuses
    // anything that would carry the top past 0xFFF0). The exact-fit boundary, from below.
    var (output, names) = Run("""
      REDIM a(1 TO 32760) AS INTEGER
      a(1) = 11
      a(32760) = 99
      PRINT a(1); a(32760); a(16000)
      """);

    Assert.That(names, Does.Contain("main"), "the back end did not take the module body under test");
    Assert.That(output.Trim(), Is.EqualTo("11  99  0"), "both ends of a full segment survive, and the middle starts zeroed");
  }

  [Test]
  public void Run_GivenADynamicArrayPastASegment_ThenItIsRefusedRatherThanWrapping() {
    // 20000 LONGs is 80000 bytes. The count fits a word and the element size fits a word, but the
    // PRODUCT does not - and 80000 mod 65536 is 14464, so a 16-bit multiply would allocate 14464 bytes
    // and let a(20000) write 65 KB past the end of it. Computing the byte count at 32 bits is what
    // turns that into the runtime's own refusal, which is also what the direct emitter does.
    var (output, names) = Run("""
      REDIM a(1 TO 20000) AS LONG
      a(20000) = 7
      PRINT a(20000)
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(output, Does.Contain("OUT OF ARRAY SPACE"), "the oversized allocation is refused");
    Assert.That(output, Does.Not.Contain("7"), "and nothing after it runs");
  }

  [Test]
  public void Run_GivenARedimPreserveThatGrows_ThenTheOldContentsSurviveAndTheTailIsZero() {
    var (output, names) = Run("""
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
    Assert.That(output.Trim(), Is.EqualTo("1000  5000  0 -1"),
      "the prefix carries over, the grown tail reads as zero, and the new top element is writable");
  }

  [Test]
  public void Run_GivenARedimPreserveThatShrinks_ThenOnlyWhatFitsIsCopied() {
    // PB lets the outer bound shrink, and the copy is min(old, new) - copying the old length into the
    // shorter block would run past the end of it.
    var (output, names) = Run("""
      DIM i AS INTEGER
      REDIM a(1 TO 6) AS INTEGER
      FOR i = 1 TO 6
        a(i) = i * 11
      NEXT i
      REDIM PRESERVE a(1 TO 2)
      PRINT a(1); a(2); UBOUND(a)
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(output.Trim(), Is.EqualTo("11  22  2"));
  }

  /// <summary>
  /// <c>REDIM PRESERVE</c> as the FIRST sizing of an array: there is nothing to preserve, and the
  /// descriptor the old size is read from is still all zeroes. The direct emitter spells the case as a
  /// test of the descriptor's segment word; here it falls out as a copy of zero bytes from a null
  /// block, which is why no first-time guard is needed on either side.
  /// </summary>
  [Test]
  public void Run_GivenARedimPreserveOfANeverAllocatedArray_ThenItSimplyAllocates() {
    var (output, names) = Run("""
      REDIM PRESERVE a(1 TO 3) AS INTEGER
      a(2) = 5
      PRINT a(1); a(2); UBOUND(a)
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(output.Trim(), Is.EqualTo("0  5  3"));
  }

  [Test]
  public void Run_GivenEraseThenRedim_ThenTheFreshArrayReadsZero() {
    var (output, names) = Run("""
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
    Assert.That(output.Trim(), Is.EqualTo("0  0  4"), "ERASE gives the block back and the next REDIM starts zeroed");
  }

  /// <summary>
  /// A dynamic array of STRINGS is the case the count-taking entries exist for: its element is a
  /// runtime handle, whose width only the runtime knows, so <c>rt_arr_alloc_ptr</c> scales the count
  /// instead of the lowering. The variable subscript also exercises the element-indexed GEP, where the
  /// index has to be scaled into a register of its own - the 8086 has no scaled index.
  /// </summary>
  [Test]
  public void Run_GivenADynamicStringArrayWithAVariableIndex_ThenEveryElementReadsBack() {
    var (output, names) = Run("""
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
    Assert.That(output.Trim(), Is.EqualTo("v4v3v2v1"));
  }

  [Test]
  public void Run_GivenAStringArrayGrownAndErased_ThenThePrefixSurvivesAndTheTailIsEmpty() {
    var (output, names) = Run("""
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
    Assert.That(output.Replace("\r", "").Trim(), Is.EqualTo("AB[]D\n[]"),
      "the prefix survives, the grown tail is the empty string");
  }

  [Test]
  public void Run_GivenATwoDimensionalDynamicArray_ThenEveryElementReadsBack() {
    var (output, names) = Run("""
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
    Assert.That(output.Trim(), Is.EqualTo("11  23  34  4"),
      "the row-major flattening reaches every element of the far-heap block");
  }

  /// <summary>
  /// A trap the program must take, taken. Everything else in this fixture reads an answer; this one
  /// reads a NON-answer - a program that dropped the check runs on to the PRINT the trap must keep it
  /// from reaching, and only running the thing tells the two apart.
  ///
  /// <para>
  /// The overflow check on <c>k% + 1</c> does not depend on the counter, so the middle end hoists it
  /// out of the loop and clones the loop on it. Both clones are then real code: one raises Error 6
  /// every iteration, the other does nothing, and the branch in front of them is the only thing that
  /// says which. A pass that deleted the empty one and the branch together left the program running to
  /// completion with the trap unreachable - see DeadLoopEliminationTests.
  /// </para>
  /// <para>
  /// The argument comes back through a NOINLINE function rather than being written down, and it is
  /// called twice with different values: one call site would let interprocedural constant propagation
  /// prove 32767 and fold the whole question away before any loop pass saw it.
  /// </para>
  /// </summary>
  [Test]
  public void Run_GivenAnOverflowTrapInsideALoop_ThenItIsRaised() {
    var (output, names) = Run("""
      $ERROR OVERFLOW ON
      $OPTIMIZE SPEED
      DECLARE FUNCTION Given%(BYVAL v%)

      k% = Given%(32767)
      n% = Given%(1)
      FOR i% = 1 TO 100
        x% = k% + 1
      NEXT i%
      PRINT "not reached"; n%

      FUNCTION Given%(BYVAL v%) NOINLINE
        Given% = v%
      END FUNCTION
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(output, Does.Not.Contain("not reached"),
      "32767 + 1 under $ERROR OVERFLOW ON must stop the program");
  }
}
