using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// <c>SELECT CASE</c> dispatch through the x86-16 back end: the same five shapes the direct emitter
/// picks between - an unsigned range test, a compile-time membership mask, a word jump table, its
/// byte-indexed compression and a key-verified perfect hash - now selected from an
/// <see cref="Ir.IrSwitch"/> that <c>Ir/Passes/SwitchFormation.cs</c> put back together out of the
/// compare chain the lowering emits.
///
/// <para>
/// Each case pins two things, and needs both. The BYTES say the shape was actually chosen - a jump
/// table is <c>FF A7</c> and nothing else emits it, the byte-index table is <c>8A 9F</c>, the mask is
/// <c>D3 E8</c> - and the OUTPUT under the interpreter says the dispatch still answers what the compare
/// chain would. A shape assertion alone would pass just as well on a table with the arms in the wrong
/// order, which is the failure mode that matters here: every one of these reaches its arm through an
/// address it computed rather than through a branch anyone wrote.
/// </para>
///
/// <para>
/// The subject comes from <c>READ</c> in every program, never from a literal. A literal subject is
/// resolved by SCCP long before selection - the whole SELECT becomes one PRINT - so a fixture written
/// that way measures nothing, which is exactly what happened to four of the direct emitter's own
/// dispatch tests once pb36 started routing.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendSwitchDispatchTests {

  private static byte[] Compile(string source, bool size = false) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) {
      Optimize = true, OptimizeSpeed = !size, OptimizeSize = size, UseExperimentalBackend = true,
    };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendRoutedNames, Does.Contain("main"),
      "the module body must be ROUTED, or this fixture is measuring the direct emitter");
    return image;
  }

  private static bool Contains(byte[] image, params byte[] needle) {
    for (var at = 0; at + needle.Length <= image.Length; ++at) {
      var match = true;
      for (var i = 0; i < needle.Length; ++i)
        if (image[at + i] != needle[i]) { match = false; break; }
      if (match)
        return true;
    }
    return false;
  }

  private static string Run(string source, long subject, bool size = false) {
    var text = source.Replace("{v}", subject.ToString(System.Globalization.CultureInfo.InvariantCulture));
    return Cpu8086.Run(Compile(text, size)).Output.Trim();
  }

  // ---- the range: a contiguous run of values reaching one arm --------------

  private const string _range = """
    DIM x AS INTEGER
    READ x
    SELECT CASE x
      CASE 0 TO 9
        PRINT "in"
      CASE ELSE
        PRINT "out"
    END SELECT
    DATA {v}
    END
    """;

  [Test]
  public void Dispatch_GivenAConstantCaseRange_WhenRouted_ThenOneUnsignedCompare() {
    // The polarity is the layout's to choose, not the shape's: `Peephole.StraightenBranches` inverts
    // the branch whose taken arm is laid out next, so `cmp ax,9 / jbe in` and `cmp ax,9 / ja else`
    // are the same single unsigned test reached two ways. What the fixture pins is that there IS one.
    var img = Compile(_range.Replace("{v}", "3"));
    Assert.That(Contains(img, 0x83, 0xF8, 0x09, 0x76) || Contains(img, 0x83, 0xF8, 0x09, 0x77), Is.True,
      "CASE 0 TO 9 is one unsigned compare (cmp ax, 9 / jbe or ja), not two signed ones");
  }

  [TestCase(-1L, "out")]
  [TestCase(0L, "in")]
  [TestCase(9L, "in")]
  [TestCase(10L, "out")]
  public void Dispatch_GivenAConstantCaseRange_WhenRun_ThenTheBoundsHold(long subject, string expected)
    => Assert.That(Run(_range, subject), Is.EqualTo(expected));

  // ---- the membership mask: a scattered set reaching one arm ---------------

  private const string _mask = """
    DIM x AS INTEGER
    READ x
    SELECT CASE x
      CASE 1, 8, 15
        PRINT "member"
      CASE ELSE
        PRINT "no"
    END SELECT
    DATA {v}
    END
    """;

  [Test]
  public void Dispatch_GivenAScatteredSetToOneArm_WhenRouted_ThenAMembershipMask() {
    var image = Compile(_mask.Replace("{v}", "8"));
    Assert.Multiple(() => {
      Assert.That(Contains(image, 0xB8, 0x81, 0x40), Is.True, "the compile-time mask 4081h is loaded");
      Assert.That(Contains(image, 0xD3, 0xE8), Is.True, "and brought down by the subject (shr ax, cl)");
    });
  }

  [TestCase(0L, "no")]
  [TestCase(1L, "member")]
  [TestCase(7L, "no")]
  [TestCase(8L, "member")]
  [TestCase(15L, "member")]
  [TestCase(16L, "no")]
  [TestCase(-40L, "no")]      // below the window: the normalize wraps it, and the guard catches it
  public void Dispatch_GivenAScatteredSetToOneArm_WhenRun_ThenOnlyMembersMatch(long subject, string expected)
    => Assert.That(Run(_mask, subject), Is.EqualTo(expected));

  /// <summary>
  /// A window of 16..31 needs a 32-bit mask and therefore an 80386; without one the shape declines and
  /// the compare chain stays. This is the one dispatch decision that is about the instruction SET
  /// rather than about the case values, which is why <c>$CPU</c> has to reach the selector at all.
  /// </summary>
  [Test]
  public void Dispatch_GivenAWideWindow_WhenCpu386_ThenA32BitMaskAndOtherwiseNone() {
    const string wide = """
      DIM x AS INTEGER
      READ x
      SELECT CASE x
        CASE 0, 5, 11, 17, 20
          PRINT "member"
        CASE ELSE
          PRINT "no"
      END SELECT
      DATA 17
      END
      """;
    Assert.Multiple(() => {
      Assert.That(Contains(Compile("$CPU 80386\n" + wide), 0x66, 0xD3, 0xE8), Is.True, "shr eax, cl");
      Assert.That(Contains(Compile(wide), 0x66, 0xD3, 0xE8), Is.False, "an 8086 has no 32-bit shift to mask with");
    });
  }

  /// <summary>
  /// The 8086 build of the same program, which declines the mask and answers through the compare chain.
  /// The 386 build is checked for its BYTES above and not run, and that is a gap rather than a choice:
  /// <see cref="Cpu8086"/> has no operand-size prefix, so <c>66 D3 E8</c> stops it. The direct
  /// emitter's own 32-bit mask is unexecuted for the same reason.
  /// </summary>
  [TestCase(17L, "member")]
  [TestCase(18L, "no")]
  public void Dispatch_GivenAWideWindow_WhenNo386_ThenTheChainStillAnswers(long subject, string expected) {
    const string wide = """
      DIM x AS INTEGER
      READ x
      SELECT CASE x
        CASE 0, 5, 11, 17, 20
          PRINT "member"
        CASE ELSE
          PRINT "no"
      END SELECT
      DATA {v}
      END
      """;
    Assert.That(Run(wide, subject), Is.EqualTo(expected));
  }

  // ---- the jump table, and the byte-index table SIZE asks for --------------

  private const string _table = """
    DIM x AS INTEGER
    READ x
    SELECT CASE x
      CASE 0, 4, 8, 12
        PRINT "a"
      CASE 1, 5, 9, 13
        PRINT "b"
      CASE 2, 6, 10, 14
        PRINT "c"
      CASE ELSE
        PRINT "z"
    END SELECT
    DATA {v}
    END
    """;

  [Test]
  public void Dispatch_GivenAWideSpanWithFewArms_WhenSize_ThenAByteIndexTable() {
    Assert.Multiple(() => {
      Assert.That(Contains(Compile(_table.Replace("{v}", "5"), size: true), 0x8A, 0x9F), Is.True,
        "SIZE compresses the span to one byte per value (mov bl, [bx+table])");
      Assert.That(Contains(Compile(_table.Replace("{v}", "5")), 0x8A, 0x9F), Is.False,
        "SPEED keeps the plain word table - one load per dispatch is not worth the bytes");
      Assert.That(Contains(Compile(_table.Replace("{v}", "5")), 0xFF, 0xA7), Is.True,
        "and it is a table either way (jmp word [bx+table])");
    });
  }

  [TestCase(0L, "a")]
  [TestCase(3L, "z")]
  [TestCase(5L, "b")]
  [TestCase(14L, "c")]
  [TestCase(15L, "z")]
  [TestCase(-1L, "z")]
  public void Dispatch_GivenAJumpTable_WhenRun_ThenEveryValueReachesItsArm(long subject, string expected)
    => Assert.Multiple(() => {
      Assert.That(Run(_table, subject), Is.EqualTo(expected), "speed");
      Assert.That(Run(_table, subject, size: true), Is.EqualTo(expected), "size");
    });

  private const string _longTable = """
    DIM x AS LONG
    READ x
    SELECT CASE x
      CASE 100000
        PRINT "a"
      CASE 100001
        PRINT "b"
      CASE 100002
        PRINT "c"
      CASE 100003
        PRINT "d"
      CASE 100004
        PRINT "e"
      CASE ELSE
        PRINT "z"
    END SELECT
    DATA {v}
    END
    """;

  [Test]
  public void Dispatch_GivenADenseLongTable_WhenRouted_ThenHighWordGuardPrecedesIndexedJump() {
    var image = Compile(_longTable.Replace("{v}", "100002"));
    Assert.Multiple(() => {
      Assert.That(Contains(image, 0x85, 0xD2), Is.True, "test dx, dx rejects an index outside one word");
      Assert.That(Contains(image, 0xFF, 0xA7), Is.True, "an in-range low word indexes the address table");
    });
  }

  [TestCase(99999L, "z")]
  [TestCase(100000L, "a")]
  [TestCase(100002L, "c")]
  [TestCase(100004L, "e")]
  [TestCase(100005L, "z")]
  [TestCase(165536L, "z")]
  [TestCase(-1L, "z")]
  public void Dispatch_GivenADenseLongTable_WhenRun_ThenBothWordsBoundTheIndex(long subject, string expected)
    => Assert.That(Run(_longTable, subject), Is.EqualTo(expected));

  // ---- the perfect hash: too wide to tabulate, separable by low bits -------

  private const string _hash = """
    DIM x AS INTEGER
    READ x
    SELECT CASE x
      CASE 16
        PRINT "a"
      CASE 33
        PRINT "b"
      CASE 50
        PRINT "c"
      CASE 67
        PRINT "d"
      CASE 84
        PRINT "e"
      CASE 101
        PRINT "f"
      CASE 118
        PRINT "g"
      CASE 135
        PRINT "h"
      CASE ELSE
        PRINT "z"
    END SELECT
    DATA {v}
    END
    """;

  [Test]
  public void Dispatch_GivenASparseSeparableSet_WhenRouted_ThenAMaskedTable() {
    var image = Compile(_hash.Replace("{v}", "67"));
    Assert.Multiple(() => {
      Assert.That(Contains(image, 0x83, 0xE0, 0x07), Is.True, "the perfect hash masks the subject (and ax, 7)");
      Assert.That(Contains(image, 0xFF, 0xA7), Is.True, "and takes the indexed jump");
    });
  }

  /// <summary>
  /// The verify is what the shape rests on: the hash is injective on the case values and on nothing
  /// else, so 24 (which hashes where 16 does) and 7 (an empty slot) both have to reach CASE ELSE.
  /// </summary>
  [TestCase(16L, "a")]
  [TestCase(67L, "d")]
  [TestCase(135L, "h")]
  [TestCase(24L, "z")]
  [TestCase(7L, "z")]
  [TestCase(0L, "z")]
  public void Dispatch_GivenASparseSeparableSet_WhenRun_ThenOnlyTheKeyedValueMatches(long subject, string expected)
    => Assert.That(Run(_hash, subject), Is.EqualTo(expected));

  private const string _tree = """
    DIM x AS INTEGER
    READ x
    SELECT CASE x
      CASE 1
        PRINT "a"
      CASE 100
        PRINT "b"
      CASE 200
        PRINT "c"
      CASE 300
        PRINT "d"
      CASE 400
        PRINT "e"
      CASE 500
        PRINT "f"
      CASE 556
        PRINT "g"
      CASE 600
        PRINT "h"
      CASE ELSE
        PRINT "z"
    END SELECT
    DATA {v}
    END
    """;

  [TestCase(-1L, "z")]
  [TestCase(1L, "a")]
  [TestCase(200L, "c")]
  [TestCase(300L, "d")]
  [TestCase(400L, "e")]
  [TestCase(556L, "g")]
  [TestCase(600L, "h")]
  [TestCase(601L, "z")]
  public void Dispatch_GivenABalancedSparseTree_WhenRun_ThenEveryPartitionReachesItsArm(
      long subject, string expected)
    => Assert.That(Run(_tree, subject), Is.EqualTo(expected));

  // ---- what the dispatch declines -----------------------------------------

  [Test]
  public void Dispatch_GivenTwoCases_WhenRouted_ThenTheCompareChainStays() {
    // below three values every shape here loses to the two compares it would replace
    var image = Compile("""
      DIM x AS INTEGER
      READ x
      SELECT CASE x
        CASE 1
          PRINT "a"
        CASE 9
          PRINT "b"
      END SELECT
      DATA 9
      END
      """);
    Assert.Multiple(() => {
      Assert.That(Contains(image, 0xFF, 0xA7), Is.False, "no table");
      Assert.That(Contains(image, 0xD3, 0xE8), Is.False, "and no mask");
    });
  }

  [Test]
  public void Dispatch_GivenOptimizationOff_WhenRouted_ThenNoDispatchShapeIsChosen() {
    // every shape here is an optimization, and none may appear with the optimizer off
    var unit = Parser.Parse(Lexer.Tokenize(_table.Replace("{v}", "5"), "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var generator = new CodeGenerator(Binder.Bind(unit, Dialect.Pb36)) { Optimize = false, UseExperimentalBackend = true };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.Multiple(() => {
      Assert.That(Contains(image, 0xFF, 0xA7), Is.False);
      Assert.That(Cpu8086.Run(image).Output.Trim(), Is.EqualTo("b"), "and the chain still answers");
    });
  }
}
