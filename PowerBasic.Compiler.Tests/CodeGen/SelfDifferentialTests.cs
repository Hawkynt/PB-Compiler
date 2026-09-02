using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// The optimizer's one inviolable rule, stated as a test: the same source compiled with and
/// without the optimizer must print the same thing. Every program here is a shape that made that
/// rule hard - the ones where an analysis has to know a dialect's arithmetic to stay honest.
///
/// This complements the differential harness rather than duplicating it. That one compares us
/// against the genuine vintage compilers and needs their binaries; this one compares us against
/// ourselves and needs only DOSBox, so it covers the combinations the oracle batteries do not -
/// notably a NON-pb35 dialect with the optimizer ON, which no other harness compiles.
/// </summary>
[TestFixture, Category("Slow")]
public sealed class SelfDifferentialTests {

  /// <summary>One program, and the dialect whose arithmetic makes it interesting.</summary>
  public static IEnumerable<TestCaseData> Programs() {
    // A dialect of the Microsoft family wraps integer arithmetic IN PLACE, where PB 2.0+ promotes
    // it to floating point. So "(i% + i%) \ 6000" is 40000\6000 = 6 under PB's rules but
    // (-25536)\6000 = -4 here - and an interval that composed the mathematical value would fold
    // the comparison the wrong way. The range analysis has to notice that the add left INTEGER.
    yield return new TestCaseData(Dialect.Qb45, """
      FOR i% = 20000 TO 20000
        IF (i% + i%) \ 6000 > 0 THEN
          PRINT "positive"
        ELSE
          PRINT "negative"
        END IF
        PRINT (i% + i%) \ 6000
      NEXT i%
      END
      """) { TestName = "SelfDiff_WrapInPlaceDialect_RangeMustNotComposeThroughAWrap" };

    // The same shape one dialect up the family, through a multiply rather than an add
    yield return new TestCaseData(Dialect.Qb45, """
      FOR i% = 300 TO 300
        j% = i% * i%
        PRINT j%; (i% * i%) \ 1000
      NEXT i%
      END
      """) { TestName = "SelfDiff_WrapInPlaceDialect_MultiplyRange" };

    // The same constant written twice is the SAME subtree to a value-numbering CSE - and "-100"
    // is a subtree (a negate over a literal), not a leaf. The emitter folds the defining
    // occurrence to a literal and emits nothing for it, so a slot reserved for it would be read
    // by the second occurrence having never been written. Only a dialect that keeps integer
    // arithmetic integral reaches the affected path.
    yield return new TestCaseData(Dialect.Qb45, """
      FOR i% = 1 TO 3
        p% = i% * 3
        r% = (i% OR (-100 + 255))
        PRINT (15 + p%) - (-100)
      NEXT i%
      END
      """) { TestName = "SelfDiff_ConstantSubtreeTwice_MustNotTakeACseSlot" };

    // Constant folding computes in full precision. That is right for PB, which promotes integral
    // arithmetic to floating point, and wrong for a dialect that keeps it integral: 32767 + 18 is
    // 32785 folded and -32751 at run time, and the folded value must not be propagated.
    yield return new TestCaseData(Dialect.Qb45, """
      DIM t AS LONG
      t = 32767 + 18
      PRINT t
      t = ((1 * 8) * 8) * (32767 + (2 XOR 16))
      PRINT t
      END
      """) { TestName = "SelfDiff_ConstantFold_MustWrapLikeTheDialect" };

    // PB's own promotion: an out-of-range float-to-LONG store writes the x87's indefinite
    // sentinel instead of wrapping, and constant folding has to agree with the emitter about that
    yield return new TestCaseData(Dialect.Pb36, """
      DIM a AS LONG, r AS LONG
      a = 2147483647
      r = a + a : PRINT r
      a = 65536
      r = a * a : PRINT r
      a = 46341
      r = a * a : PRINT r
      END
      """) { TestName = "SelfDiff_PromotedStoreSaturates_FoldMustAgreeWithTheEmitter" };

    // ... and the same values reached at run time rather than folded
    yield return new TestCaseData(Dialect.Pb36, """
      DECLARE SUB Opaque(v&)
      DIM a AS LONG, b AS LONG, r AS LONG
      a = 2147483000 : b = 1000
      Opaque a : Opaque b
      r = a + b : PRINT r
      a = -2147483000
      Opaque a
      r = a - b : PRINT r
      END
      SUB Opaque(v&)
      END SUB
      """) { TestName = "SelfDiff_PromotedStoreSaturates_AtRuntime" };
  }

  [TestCaseSource(nameof(Programs))]
  public void Execute_GivenProgram_WhenOptimizedAndFaithful_ThenSameOutput(Dialect dialect, string source) {
    var faithful = DosBoxRunner.Normalize(DosBoxRunner.Run(Compile(source, dialect, optimize: false)));
    var optimized = DosBoxRunner.Normalize(DosBoxRunner.Run(Compile(source, dialect, optimize: true)));
    Assert.That(optimized, Is.EqualTo(faithful), "the optimizer changed what the program prints");
  }

  private static byte[] Compile(string source, Dialect dialect, bool optimize) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", dialect), "TEST.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return exe;
  }
}
