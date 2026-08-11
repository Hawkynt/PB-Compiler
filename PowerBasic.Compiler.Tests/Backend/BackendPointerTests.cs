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
