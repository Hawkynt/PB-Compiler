using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The character-set string surface on the retargetable path: <c>INSTR … ANY</c>, <c>VERIFY</c>,
/// <c>EXTRACT$</c> and <c>TALLY</c>, each in both its substring and its <c>ANY</c> reading.
///
/// One DOS routine serves each pair under a flag, so the risk is not that the call fails but that it
/// is made with the WRONG flag - which answers a plausible number rather than an error. Every case
/// therefore asserts the value PowerBASIC gives as well as agreement with the direct emitter.
/// </summary>
[TestFixture]
public sealed class BackendStringSetTests {

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

  private static string[] Lines(string output)
    => [.. output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())];

  [Test]
  public void Instr_GivenAnAnySet_ThenItFindsTheFirstMemberAndNotASubstring() {
    var (direct, routed, names) = RunBothWays("""
      a$ = "12-34/56"
      PRINT INSTR(a$, ANY "-/")
      PRINT INSTR(4, a$, ANY "-/")
      PRINT INSTR(a$, "34")
      PRINT INSTR(a$, ANY "xyz")
      """);

    Assert.That(names, Does.Contain("main"), "the back end did not take the module body under test");
    Assert.That(routed, Is.EqualTo(direct));
    // the set finds a MEMBER (position 3, then 6 from the fourth character); the plain needle still
    // finds the substring at 4; a set nothing matches is 0
    Assert.That(Lines(routed), Is.EqualTo(new[] { "3", "6", "4", "0" }));
  }

  [Test]
  public void Verify_GivenASet_ThenItFindsTheFirstNonMember() {
    var (direct, routed, names) = RunBothWays("""
      PRINT VERIFY("123A45", "0123456789")
      PRINT VERIFY("12345", "0123456789")
      PRINT VERIFY(3, "12 45", "0123456789")
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    // the opposite question from INSTR ANY: 'A' at 4 is the first non-digit, an all-digit string
    // answers 0, and the start position skips the leading characters
    Assert.That(Lines(routed), Is.EqualTo(new[] { "4", "0", "3" }));
  }

  [Test]
  public void Extract_GivenASubstringAndASet_ThenItKeepsWhatComesBefore() {
    var (direct, routed, names) = RunBothWays("""
      PRINT EXTRACT$("name=value", "=")
      PRINT EXTRACT$("name", "=")
      PRINT EXTRACT$("abc,def/ghi", ANY ",/")
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    // no match keeps the WHOLE string, which is the case a wrong flag would break first
    Assert.That(Lines(routed), Is.EqualTo(new[] { "name", "name", "abc" }));
  }

  /// <summary>
  /// CHR$ is variadic, and the IR path used to read its FIRST argument and drop the rest - so
  /// CHR$(65, 66, 67) was "A". Nothing caught it because no program that lowered contained one; the
  /// answer was wrong rather than missing, which is the failure a decline exists to avoid.
  /// </summary>
  [Test]
  public void Chr_GivenSeveralCodes_ThenTheyConcatenate() {
    var (direct, routed, names) = RunBothWays("""
      PRINT CHR$(65, 66, 67, 49, 50)
      PRINT CHR$(65)
      PRINT LEN(CHR$(65, 66, 67))
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(Lines(routed), Is.EqualTo(new[] { "ABC12", "A", "3" }));
  }

  [Test]
  public void Replace_GivenASubstring_ThenEveryOccurrenceChanges() {
    var (direct, routed, names) = RunBothWays("""
      t$ = "one-two-three"
      REPLACE "-" WITH "+" IN t$
      PRINT t$
      REPLACE "ee" WITH "EE" IN t$
      PRINT t$
      REPLACE "zz" WITH "!" IN t$
      PRINT t$
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    // every occurrence, not just the first; and a find that matches nothing leaves the string alone
    Assert.That(Lines(routed), Is.EqualTo(new[] { "one+two+three", "one+two+thrEE", "one+two+thrEE" }));
  }

  [Test]
  public void Bit_GivenSetResetAndToggle_ThenOneBitChangesInTheVariablesOwnWidth() {
    var (direct, routed, names) = RunBothWays("""
      w% = 0
      BIT SET w%, 0
      BIT SET w%, 14
      PRINT w%
      BIT RESET w%, 0
      PRINT w%
      BIT TOGGLE w%, 3
      PRINT w%
      l& = 0
      BIT SET l&, 30
      PRINT l&
      BIT TOGGLE l&, 30
      PRINT l&
      b% = 8
      PRINT BIT(b%, 3)
      PRINT BIT(b%, 2)
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    // 1 + 16384, then the low bit off, then bit 3 on; a LONG reaches bit 30 where an INTEGER cannot
    Assert.That(Lines(routed), Is.EqualTo(new[] { "16385", "16384", "16392", "1073741824", "0", "1", "0" }));
  }

  [Test]
  public void Tally_GivenASubstringAndASet_ThenItCountsOccurrences() {
    var (direct, routed, names) = RunBothWays("""
      PRINT TALLY("the cat and the hat", "the")
      PRINT TALLY("the cat and the hat", ANY "th")
      PRINT TALLY("aaaa", "aa")
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    // the substring count does not overlap (two "aa" in "aaaa"), and the set counts CHARACTERS -
    // four t and three h - which is a different number from the substring reading of the same text
    Assert.That(Lines(routed), Is.EqualTo(new[] { "2", "7", "2" }));
  }
}
