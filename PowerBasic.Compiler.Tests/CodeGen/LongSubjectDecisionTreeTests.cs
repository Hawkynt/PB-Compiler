using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// O0098 over a LONG <c>SELECT</c> subject.
///
/// A sparse SELECT with many single-constant arms dispatches through a balanced binary decision
/// tree instead of a linear compare chain. It bailed on anything but an INTEGER subject, so
/// <c>SELECT CASE l&amp;</c> paid a compare per arm.
///
/// The widening is the same one O0099 needed and for the same reason. Every tree point must fit an
/// int16 to survive the fold, and the tree compares AX — so a 32-bit subject is first proven to BE
/// its own int16 low half (<c>CWD</c> against the real high word, kept in CX across the check since
/// the tree only ever touches AX). A subject failing that cannot equal any point, so it goes
/// straight to the default path. Without the check the tree compares a truncated low word and takes
/// an arm the program never selected: 65636 is 0001_0064h, whose low word is 100.
///
/// Reaching the tree at all takes some care, because it is the third strategy tried. The values
/// below are deliberately sparse across a ~2900-wide span so the jump table declines it as too
/// costly, and the perfect hash still refuses any non-INTEGER subject — which leaves the tree. The
/// injected-fault check backs that up: compiling the guard out fails the low-word cases and nothing
/// else, which could not happen if some other strategy were answering.
/// </summary>
[TestFixture]
public sealed class LongSubjectDecisionTreeTests {

  private static byte[] Compile(string source, bool optimize) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return image;
  }

  /// <summary>Eight sparse points - enough for the tree (it wants 8+), too spread out for a table.</summary>
  private static string Dispatch(long value, bool optimize) =>
    Cpu8086.Run(Compile($"""
      $OPTIMIZE SPEED
      DIM v AS LONG
      READ v
      SELECT CASE v
        CASE 100
          PRINT "a"
        CASE 250
          PRINT "b"
        CASE 400
          PRINT "c"
        CASE 730
          PRINT "d"
        CASE 1100
          PRINT "e"
        CASE 1500
          PRINT "f"
        CASE 2000
          PRINT "g"
        CASE 3000
          PRINT "h"
        CASE ELSE
          PRINT "z"
      END SELECT
      DATA {value}
      END
      """, optimize)).Output.Trim();

  [TestCase(100L, "a")]
  [TestCase(250L, "b")]
  [TestCase(400L, "c")]
  [TestCase(730L, "d")]
  [TestCase(1100L, "e")]
  [TestCase(1500L, "f")]
  [TestCase(2000L, "g")]
  [TestCase(3000L, "h")]
  [TestCase(0L, "z")]
  [TestCase(99L, "z")]
  [TestCase(3001L, "z")]
  public void Select_GivenALongSubject_ThenTheTreeReachesTheRightArm(long value, string expected) {
    Assert.Multiple(() => {
      Assert.That(Dispatch(value, optimize: true), Is.EqualTo(expected));
      Assert.That(Dispatch(value, optimize: false), Is.EqualTo(expected), "and unoptimized agrees");
    });
  }

  /// <summary>
  /// The guard's reason for existing: each of these has a low word that IS a tree point, while the
  /// value is not. There is no comparison arm here, so the right answer is the ELSE arm.
  /// </summary>
  [TestCase(65636L)]      // 0001_0064h - low word 100
  [TestCase(65786L)]      // 0001_00FAh - low word 250
  [TestCase(131472L)]     // 0002_0190h - low word 400
  public void Select_GivenALongWhoseLowWordIsATreePoint_ThenItDoesNotMatch(long value) {
    Assert.Multiple(() => {
      Assert.That(Dispatch(value, optimize: true), Is.EqualTo("z"),
        "the high word must be checked - the low half alone is not the value");
      Assert.That(Dispatch(value, optimize: false), Is.EqualTo("z"));
    });
  }

  /// <summary>
  /// A negative subject passes the sign-extension check (-100 is FF9C/FFFF) and then simply misses
  /// every point, so the guard is not rejecting everything negative out of hand.
  /// </summary>
  [TestCase(-100L)]
  [TestCase(-1L)]
  public void Select_GivenANegativeLongSubject_ThenItReachesTheElseArmNormally(long value) =>
    Assert.That(Dispatch(value, optimize: true), Is.EqualTo("z"));

  /// <summary>The INTEGER subject the tree already served must be unaffected.</summary>
  [TestCase(730, "d")]
  [TestCase(731, "z")]
  public void Select_GivenAnIntegerSubject_ThenTheTreeStillDispatches(int value, string expected) =>
    Assert.That(Cpu8086.Run(Compile($"""
      $OPTIMIZE SPEED
      DIM v AS INTEGER
      READ v
      SELECT CASE v
        CASE 100
          PRINT "a"
        CASE 250
          PRINT "b"
        CASE 400
          PRINT "c"
        CASE 730
          PRINT "d"
        CASE 1100
          PRINT "e"
        CASE 1500
          PRINT "f"
        CASE 2000
          PRINT "g"
        CASE 3000
          PRINT "h"
        CASE ELSE
          PRINT "z"
      END SELECT
      DATA {value}
      END
      """, optimize: true)).Output.Trim(), Is.EqualTo(expected));
}
