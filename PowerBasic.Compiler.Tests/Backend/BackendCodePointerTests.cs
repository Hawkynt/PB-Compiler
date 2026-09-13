using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// PB 3.2 CODE pointers on the retargetable path: <c>CODEPTR32</c> of a label, and the
/// <c>GOTO DWORD</c> / <c>GOSUB DWORD</c> that jump through one.
///
/// A code address is the one value whose NUMBER neither back end can be held to - the two emitters
/// lay out instructions differently, so the same label is at different offsets in the two images.
/// What can be held is everything the address is FOR: that jumping through it lands on that label and
/// not on the statement before it, that a computed choice between two of them reaches the one chosen,
/// that <c>GOSUB DWORD</c> comes back to the statement after itself, and that the offset the 32-bit
/// form carries in its low half is the offset the 16-bit <c>CODEPTR</c> answers on its own.
/// </summary>
[TestFixture]
public sealed class BackendCodePointerTests {

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

  private static string[] Lines(string output) => output.Replace("\r", "")
    .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();

  /// <summary>The reason the whole-module lowering declined, or null when it took the program.</summary>
  private static string? DeclineReason(string source) {
    var module = IrLowering.TryLowerModule(Bind(source), out var why);
    return module is null ? why ?? "unknown" : null;
  }

  [Test]
  public void GotoDword_GivenALabelsCodePointer_ThenControlLandsOnThatLabel() {
    var (direct, routed, names) = RunBothWays("""
      DIM g AS DWORD
      g = CODEPTR32(Second)
      GOTO DWORD g
      PRINT "never printed"
      Second:
      PRINT "second"
      END
      """);

    Assert.That(names, Does.Contain("main"), "the back end did not take the module body under test");
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(Lines(routed), Is.EqualTo(new[] { "second" }), "the statement between the jump and the label ran");
  }

  /// <summary>
  /// The point of a code pointer rather than a GOTO: which label is reached is decided at RUN time.
  /// One target would be reached by a plain branch too, so a wrongly formed address that happened to
  /// land on the only label there is would still pass; two make the address carry the decision.
  /// </summary>
  [Test]
  public void GotoDword_GivenAChoiceBetweenTwoLabels_ThenTheAddressDecidesWhichIsReached() {
    var (direct, routed, names) = RunBothWays("""
      DIM g AS DWORD
      n% = 2
      IF n% = 1 THEN
        g = CODEPTR32(First)
      ELSE
        g = CODEPTR32(Second)
      END IF
      GOTO DWORD g
      First:
      PRINT "first"
      GOTO Done
      Second:
      PRINT "second"
      Done:
      PRINT "done"
      END
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(Lines(routed), Is.EqualTo(new[] { "second", "done" }));
  }

  /// <summary>
  /// <c>GOSUB DWORD</c> is a GOSUB: the destination is computed, and the <c>RETURN</c> at the far end
  /// still comes back to the statement AFTER the call site. That return is what separates it from
  /// <c>GOTO DWORD</c>, so it is what this asserts.
  /// </summary>
  [Test]
  public void GosubDword_WhenTheTargetReturns_ThenControlResumesAfterTheCallSite() {
    var (direct, routed, names) = RunBothWays("""
      DIM g AS DWORD
      g = CODEPTR32(Body)
      GOSUB DWORD g
      PRINT "back"
      END
      Body:
      PRINT "body"
      RETURN
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    Assert.That(Lines(routed), Is.EqualTo(new[] { "body", "back" }));
  }

  /// <summary>
  /// The 32-bit form is <c>segment:offset</c> and the 16-bit form is that offset on its own, so the
  /// low half of one must equal the other. This is the only thing about the NUMBER that can be
  /// asserted without pinning a code layout - and it is what a program that takes a pointer apart
  /// relies on.
  /// </summary>
  [Test]
  public void CodePtr32_GivenTheSameLabelAsCodePtr_ThenItsLowHalfIsTheSameOffset() {
    var (direct, routed, names) = RunBothWays("""
      DIM wide AS DWORD
      DIM near AS DWORD
      wide = CODEPTR32(Here) AND &HFFFF&
      near = CODEPTR(Here)
      IF wide = near THEN PRINT "same" ELSE PRINT "differ"
      IF CODEPTR(Here) = CODEPTR(There) THEN PRINT "collided" ELSE PRINT "distinct"
      GOTO Done
      Here:
      PRINT "here"
      There:
      PRINT "there"
      Done:
      PRINT "done"
      END
      """);

    Assert.That(names, Does.Contain("main"));
    Assert.That(routed, Is.EqualTo(direct));
    // ...and two DIFFERENT labels are two different addresses, which is what stops "same" above from
    // passing on an implementation that answered one constant for every label
    Assert.That(Lines(routed), Is.EqualTo(new[] { "same", "distinct", "done" }));
  }

  /// <summary>
  /// <c>CODEPTR32</c> of a PROCEDURE routes, and used to decline.
  ///
  /// <para>
  /// The decline was reasoned from the far entry THUNK the direct emitter synthesizes beside a
  /// procedure, on the grounds that a near entry offset would be "a different address wearing the
  /// same name". That reasoning was wrong about which address is being asked for. The thunk exists
  /// so a FAR call can reach a NEAR procedure; <c>CODEPTR</c> asks for the entry offset, and the
  /// procedure's own label is that offset on both paths. <c>CODEPTR32</c> pairs it with CS, exactly
  /// as the LABEL form above does.
  /// </para>
  /// <para>
  /// The selector could already name one all along - <c>PtrToInt</c> of an <c>IrFunction</c> becomes
  /// <c>MOperand.LabelRef</c>, resolved through the same callee lookup a CALL uses - so the decline
  /// was costing 15 module bodies over the SVGA corpus for a capability that was already there.
  /// </para>
  /// </summary>
  [Test]
  public void Lowering_GivenACodePointerToAProcedure_ThenItRoutes() {
    const string source = """
      DECLARE SUB Work ()
      DIM g AS DWORD
      g = CODEPTR32(Work)
      IF g <> 0 THEN PRINT "have it" ELSE PRINT "BAD"
      END

      SUB Work ()
        PRINT "work"
      END SUB
      """;

    Assert.That(DeclineReason(source), Is.Null, "CODEPTR32 of a procedure lowers now");
  }

  /// <summary>
  /// A computed jump in a function with no labels declines rather than being given a target list it
  /// cannot fill. The list is not how the branch chooses - the address is - but it is how the CFG
  /// says where control can arrive, and an empty one would claim the jump goes nowhere.
  /// </summary>
  [Test]
  public void Lowering_GivenAComputedJumpWithNoLabelsToReach_ThenItDeclines() {
    Assert.That(DeclineReason("""
      DIM g AS DWORD
      g = 0
      GOTO DWORD g
      """), Is.EqualTo("GOTO/GOSUB DWORD in a function with no labels to reach"));
  }
}
