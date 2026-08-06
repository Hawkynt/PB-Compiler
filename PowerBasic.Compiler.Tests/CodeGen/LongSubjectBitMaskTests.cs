using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// O0099 over a LONG <c>SELECT</c> subject.
///
/// An arm listing several point values in a narrow window tests membership with one shift and a
/// bit-0 test instead of a compare per value. It was restricted to INTEGER subjects by a single
/// <c>kind == ValueKind.Int16</c> conjunct at the call site, so <c>SELECT CASE l&amp;</c> fell back to
/// the compare chain even though the mask machinery is already 32 bits wide.
///
/// The restriction was protecting something real. The mask is built over the arm's values — each of
/// which must fit an int16 to get this far — and indexed by the subject's LOW WORD. Testing that
/// word alone reads 0001_0005h as 5 and takes a <c>CASE 5</c> arm the program never selected. So a
/// wide subject is first proven to BE its own low word: <c>CWD</c> gives the sign-extension of the
/// low half and comparing it against the real high word answers exactly that, for a signed subject
/// (-5 is FFFB/FFFF and matches) and an unsigned one alike (65535 is FFFF/0000 and does not, and
/// correctly cannot equal any int16 case value). A subject failing the test takes the same
/// not-a-member exit.
///
/// The 65541 case below is the one that matters: its low word is 5 and it must NOT select
/// <c>CASE 5</c>. Without the guard it does, and every other test here still passes.
///
/// Every SELECT here carries a <c>CASE IS &gt; 1000</c> arm on purpose. The per-arm mask is the LAST
/// strategy tried — the whole-select jump table, perfect hash and decision tree all get first
/// refusal, and a dense little span like 1,3,5,9 is exactly what the jump table takes. Without a
/// comparison arm to make those decline, none of this reaches the code under test: the first draft
/// of this fixture passed identically with the guard compiled out.
/// </summary>
[TestFixture]
public sealed class LongSubjectBitMaskTests {

  private static byte[] Compile(string source, bool optimize) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return image;
  }

  /// <summary>
  /// `$OPTIMIZE SPEED` because the mask arm is speed-gated. READ/DATA so the subject is not a
  /// constant the folder would resolve before the dispatch is reached.
  /// </summary>
  private static string Dispatch(long value, bool optimize) =>
    Cpu8086.Run(Compile($"""
      $OPTIMIZE SPEED
      DIM v AS LONG
      READ v
      SELECT CASE v
        CASE 1, 3, 5, 9
          PRINT "set"
        CASE 2
          PRINT "two"
        CASE IS > 1000
          PRINT "big"
        CASE ELSE
          PRINT "else"
      END SELECT
      DATA {value}
      END
      """, optimize)).Output.Trim();

  [TestCase(1L, "set")]
  [TestCase(3L, "set")]
  [TestCase(5L, "set")]
  [TestCase(9L, "set")]
  [TestCase(2L, "two")]
  [TestCase(0L, "else")]
  [TestCase(4L, "else")]
  [TestCase(10L, "else")]
  public void Select_GivenALongSubjectInTheWindow_ThenTheRightArmRuns(long value, string expected) {
    Assert.Multiple(() => {
      Assert.That(Dispatch(value, optimize: true), Is.EqualTo(expected));
      Assert.That(Dispatch(value, optimize: false), Is.EqualTo(expected), "and unoptimized agrees");
    });
  }

  /// <summary>
  /// The guard's reason for existing. 65541 is 0001_0005h — its low word is 5, a member of the mask
  /// set, while the value itself is not. A mask test over the low half alone takes the wrong arm.
  ///
  /// The expected answer is "big", not "else": any value with a non-zero high word is at least
  /// 65536 and so satisfies the <c>CASE IS &gt; 1000</c> arm. It discriminates just as well — without
  /// the guard these print "set", because the masked arm is tested first and claims them.
  /// </summary>
  [TestCase(65541L)]      // 0001_0005h - low word 5
  [TestCase(65537L)]      // 0001_0001h - low word 1
  [TestCase(131081L)]     // 0002_0009h - low word 9
  public void Select_GivenALongWhoseLowWordIsInTheSet_ThenItDoesNotMatch(long value) {
    Assert.Multiple(() => {
      Assert.That(Dispatch(value, optimize: true), Is.EqualTo("big"),
        "the high word must be checked - the low half alone is not the value");
      Assert.That(Dispatch(value, optimize: false), Is.EqualTo("big"));
    });
  }

  /// <summary>
  /// A negative subject: -5 is FFFB/FFFF, so the sign-extension check passes and the mask sees
  /// -5, which is outside the window. The arm below covers a negative that IS a member, proving the
  /// guard does not simply reject everything negative.
  /// </summary>
  [TestCase(-5L, "else")]
  [TestCase(-1L, "neg")]
  [TestCase(-3L, "neg")]
  public void Select_GivenANegativeLongSubject_ThenTheSignExtensionCheckStillAdmitsIt(long value, string expected) {
    var source = $"""
      $OPTIMIZE SPEED
      DIM v AS LONG
      READ v
      SELECT CASE v
        CASE -1, -3, -7, -9
          PRINT "neg"
        CASE IS > 1000
          PRINT "big"
        CASE ELSE
          PRINT "else"
      END SELECT
      DATA {value}
      END
      """;
    Assert.Multiple(() => {
      Assert.That(Cpu8086.Run(Compile(source, optimize: true)).Output.Trim(), Is.EqualTo(expected));
      Assert.That(Cpu8086.Run(Compile(source, optimize: false)).Output.Trim(), Is.EqualTo(expected));
    });
  }

  /// <summary>The INTEGER subject the pass already served must be unaffected.</summary>
  [TestCase(3, "set")]
  [TestCase(4, "else")]
  public void Select_GivenAnIntegerSubject_ThenItStillDispatches(int value, string expected) =>
    Assert.That(Cpu8086.Run(Compile($"""
      $OPTIMIZE SPEED
      DIM v AS INTEGER
      READ v
      SELECT CASE v
        CASE 1, 3, 5, 9
          PRINT "set"
        CASE IS > 1000
          PRINT "big"
        CASE ELSE
          PRINT "else"
      END SELECT
      DATA {value}
      END
      """, optimize: true)).Output.Trim(), Is.EqualTo(expected));
}
