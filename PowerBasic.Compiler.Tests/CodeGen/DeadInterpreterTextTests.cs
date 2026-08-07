using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// BASICA and GW-BASIC store a line without validating every statement on it, so unparseable text
/// behind a branch that is never taken is legal source. The parser preserves it as a
/// <c>DeferredSourceStmt</c> instead of inventing a SUB call, and code generation may discard it
/// only once it can prove control never arrives - otherwise it must refuse, because silently
/// dropping a statement that CAN run would invent semantics.
///
/// The fold that shipped with this feature only reached text INSIDE an <c>IF</c>. The commoner
/// interpreter shape puts it on the line AFTER an always-taken jump, which is what the two DEADTEXT
/// battery fixtures use and what neither could compile.
///
/// Two conditions decide it and both are necessary: nothing falls through to the line, AND nothing
/// branches to its number. The tests below remove each in turn.
/// </summary>
[TestFixture]
public sealed class DeadInterpreterTextTests {

  private static IReadOnlyList<string> Errors(string source, Dialect dialect) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect), dialect);
    var generator = new CodeGenerator(model);
    generator.EmitExecutable();
    return [.. model.Errors.Select(e => e.Message).Concat(generator.Errors.Select(e => e.Message))];
  }

  /// <summary>`IF -1 GOTO 50` always jumps, and nothing branches to 40, so line 40 is dead.</summary>
  [TestCase(Dialect.Basica)]
  [TestCase(Dialect.Gw)]
  public void Emit_GivenTextAfterAnAlwaysTakenJump_ThenItIsDiscarded(Dialect dialect) =>
    Assert.That(Errors("""
      10 OPEN "RESULT.TXT" FOR OUTPUT AS #1
      20 IF -1 GOTO 40
      30 THIS IS ARBITRARY TEXT
      40 PRINT #1, "ok"
      50 CLOSE #1
      60 SYSTEM
      """, dialect), Is.Empty);

  /// <summary>
  /// The same program with the condition no longer constant: control may fall through to line 30, so
  /// the text must be refused rather than dropped.
  /// </summary>
  [TestCase(Dialect.Basica)]
  [TestCase(Dialect.Gw)]
  public void Emit_GivenTextAfterAConditionalJump_ThenItIsRefused(Dialect dialect) =>
    Assert.That(Errors("""
      10 OPEN "RESULT.TXT" FOR OUTPUT AS #1
      15 INPUT X
      20 IF X GOTO 40
      30 THIS IS ARBITRARY TEXT
      40 PRINT #1, "ok"
      50 CLOSE #1
      60 SYSTEM
      """, dialect), Has.Some.Contains("not provably unreachable"));

  /// <summary>
  /// Nothing falls through to line 30, but something BRANCHES to it - so it is live and must be
  /// refused. This is the half a fall-through-only analysis would get wrong.
  /// </summary>
  [TestCase(Dialect.Basica)]
  [TestCase(Dialect.Gw)]
  public void Emit_GivenUnreachableTextThatIsAlsoAJumpTarget_ThenItIsRefused(Dialect dialect) =>
    Assert.That(Errors("""
      10 OPEN "RESULT.TXT" FOR OUTPUT AS #1
      15 IF -1 GOTO 30
      20 IF -1 GOTO 40
      30 THIS IS ARBITRARY TEXT
      40 PRINT #1, "ok"
      50 CLOSE #1
      60 SYSTEM
      """, dialect), Has.Some.Contains("not provably unreachable"));

  /// <summary>A bare GOTO makes the following line dead just as the folded IF does.</summary>
  [Test]
  public void Emit_GivenTextAfterABareGoto_ThenItIsDiscarded() =>
    Assert.That(Errors("""
      10 OPEN "RESULT.TXT" FOR OUTPUT AS #1
      20 GOTO 40
      30 THIS IS ARBITRARY TEXT
      40 PRINT #1, "ok"
      50 CLOSE #1
      60 SYSTEM
      """, Dialect.Gw), Is.Empty);
}
