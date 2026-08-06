using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// O0100 over a LONG <c>SELECT</c> subject — the third and last of the dispatch passes that refused
/// anything but an INTEGER subject.
///
/// The keys must fit an int16 to survive the fold, so the 16-bit table serves a 32-bit subject as
/// well, once the subject is proven to BE its own int16 low half: <c>CWD</c> against the real high
/// word, parked in BX (free until the slot index is computed). A subject failing that cannot be any
/// key and goes straight to the default path.
///
/// Here the guard buys correctness, not just a skipped table read. The verify step compares the
/// subject against the key stored at its slot — but it compares the TRUNCATED low word, so 0001_03E8h
/// hashes to 1000's slot, matches the key 1000 stored there, and takes that arm. The verify cannot
/// catch what it never sees.
///
/// The values are chosen so this pass is the one that answers: their low three bits are 0..7, which
/// is the perfect mask the search looks for, while their ~7000-wide span makes the jump table (tried
/// first) decline as far too costly. The injected-fault check confirms it — compiling the guard out
/// fails exactly the low-word cases.
/// </summary>
[TestFixture]
public sealed class LongSubjectPerfectHashTests {

  private static byte[] Compile(string source, bool optimize) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return image;
  }

  /// <summary>Eight keys with distinct low three bits (0..7) spread across a ~7000-wide span.</summary>
  private static string Dispatch(long value, bool optimize) =>
    Cpu8086.Run(Compile($"""
      $OPTIMIZE SPEED
      DIM v AS LONG
      READ v
      SELECT CASE v
        CASE 1000
          PRINT "a"
        CASE 2001
          PRINT "b"
        CASE 3002
          PRINT "c"
        CASE 4003
          PRINT "d"
        CASE 5004
          PRINT "e"
        CASE 6005
          PRINT "f"
        CASE 7006
          PRINT "g"
        CASE 8007
          PRINT "h"
        CASE ELSE
          PRINT "z"
      END SELECT
      DATA {value}
      END
      """, optimize)).Output.Trim();

  [TestCase(1000L, "a")]
  [TestCase(2001L, "b")]
  [TestCase(3002L, "c")]
  [TestCase(4003L, "d")]
  [TestCase(5004L, "e")]
  [TestCase(6005L, "f")]
  [TestCase(7006L, "g")]
  [TestCase(8007L, "h")]
  [TestCase(0L, "z")]
  [TestCase(1001L, "z")]
  [TestCase(8008L, "z")]
  public void Select_GivenALongSubject_ThenTheHashReachesTheRightArm(long value, string expected) {
    Assert.Multiple(() => {
      Assert.That(Dispatch(value, optimize: true), Is.EqualTo(expected));
      Assert.That(Dispatch(value, optimize: false), Is.EqualTo(expected), "and unoptimized agrees");
    });
  }

  /// <summary>
  /// The guard's reason for existing, and the case the slot verify cannot catch by itself: each of
  /// these truncates to a real key, so it hashes to that key's slot and matches the key stored
  /// there. Only comparing the high word rejects it.
  /// </summary>
  [TestCase(66536L)]      // 0001_03E8h - low word 1000
  [TestCase(67537L)]      // 0001_07D1h - low word 2001
  [TestCase(134074L)]     // 0002_0BDAh - low word 3002
  public void Select_GivenALongWhoseLowWordIsAKey_ThenItDoesNotMatch(long value) {
    Assert.Multiple(() => {
      Assert.That(Dispatch(value, optimize: true), Is.EqualTo("z"),
        "the slot verify compares the truncated word, so the high word must be checked before it");
      Assert.That(Dispatch(value, optimize: false), Is.EqualTo("z"));
    });
  }

  /// <summary>A negative subject passes the sign-extension check and then simply misses every key.</summary>
  [TestCase(-1000L)]
  [TestCase(-1L)]
  public void Select_GivenANegativeLongSubject_ThenItReachesTheElseArm(long value) =>
    Assert.That(Dispatch(value, optimize: true), Is.EqualTo("z"));

  /// <summary>The INTEGER subject the hash already served must be unaffected.</summary>
  [TestCase(5004, "e")]
  [TestCase(5005, "z")]
  public void Select_GivenAnIntegerSubject_ThenTheHashStillDispatches(int value, string expected) =>
    Assert.That(Cpu8086.Run(Compile($"""
      $OPTIMIZE SPEED
      DIM v AS INTEGER
      READ v
      SELECT CASE v
        CASE 1000
          PRINT "a"
        CASE 2001
          PRINT "b"
        CASE 3002
          PRINT "c"
        CASE 4003
          PRINT "d"
        CASE 5004
          PRINT "e"
        CASE 6005
          PRINT "f"
        CASE 7006
          PRINT "g"
        CASE 8007
          PRINT "h"
        CASE ELSE
          PRINT "z"
      END SELECT
      DATA {value}
      END
      """, optimize: true)).Output.Trim(), Is.EqualTo(expected));
}
