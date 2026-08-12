using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.CodeGen;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Tail recursion through the x86-16 back end - the first of the DIRECT emitter's optimizations the
/// routed path had to earn rather than inherit.
///
/// <para>
/// It is here rather than among the optimizer tests because it is not a matter of code quality. The
/// direct emitter turns a self-call in tail position into a frame-reusing jump (pb36 O14); a routed
/// function never passed through that, so 60000 levels of recursion consumed a frame each and the
/// program died without printing. The measurement is the OUTPUT, not the instruction count: either
/// the recursion runs in constant stack or the program does not finish.
/// </para>
///
/// <para>
/// This is what made the difference between "the routed path is equivalent" and "the routed path is
/// ready": coverage said the function could be compiled, the corpus differential found no
/// disagreement on programs that recurse a handful of levels deep, and neither noticed that a
/// promise had gone missing. Depth is the only thing that asks the question.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendTailRecursionTests {

  private static string Run(string source, bool routed) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { UseExperimentalBackend = routed };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return DosBoxRunner.Normalize(DosBoxRunner.Run(image));
  }

  /// <summary>60000 self-calls, which would want about 480 KiB of stack as real frames.</summary>
  [Test]
  public void Execute_GivenDeepTailRecursion_WhenRouted_ThenConstantStack() => Assert.That(Run("""
    DECLARE SUB CountDown(BYVAL n&)
    CountDown 60000
    PRINT "DONE"
    END
    SUB CountDown(BYVAL n&)
      IF n& > 0 THEN CountDown n& - 1
    END SUB
    """, routed: true), Is.EqualTo("DONE\n"));

  /// <summary>
  /// And the mutual form, which this pass does not handle and does not need to: the inliner turns
  /// <c>Ping calls Pong calls Ping</c> into a self-call first, and the sweep that follows inlining is
  /// where the loop is formed. Two mechanisms composing is worth a test of its own, because either
  /// one alone leaves the chain a real recursion.
  /// </summary>
  [Test]
  public void Execute_GivenDeepMutualTailRecursion_WhenRouted_ThenConstantStack() => Assert.That(Run("""
    DECLARE SUB Ping(BYVAL n&)
    DECLARE SUB Pong(BYVAL n&)
    Ping 120000
    PRINT "DONE"
    END
    SUB Ping(BYVAL n&)
      IF n& > 0 THEN Pong n& - 1
    END SUB
    SUB Pong(BYVAL n&)
      IF n& > 0 THEN Ping n& - 1
    END SUB
    """, routed: true), Is.EqualTo("DONE\n"));

  /// <summary>
  /// The shape the pass has to find, read off the IR rather than inferred from the program finishing:
  /// the old entry becomes a loop header carrying the parameter as a phi, and the call is gone.
  ///
  /// The call and the <c>ret</c> are never adjacent in lowered BASIC - <c>IF n &gt; 0 THEN F n - 1</c>
  /// puts the call in a THEN block that branches to the statement after the IF - so a pass that
  /// matched only a call sitting immediately before a return fired on nothing at all. That was the
  /// first version of this, and it is why the walk through empty blocks is pinned here.
  /// </summary>
  [Test]
  public void Lower_GivenASelfTailCall_ThenTheCallBecomesABranchToALoopHeader() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize("""
      DECLARE SUB CountDown(BYVAL n&)
      CountDown 10
      END
      SUB CountDown(BYVAL n&)
        IF n& > 0 THEN CountDown n& - 1
      END SUB
      """, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");

    var countDown = module!.Functions.Single(f => f.Name.Equals("CountDown", StringComparison.OrdinalIgnoreCase));
    Assert.That(Mem2Reg.Run(countDown), Is.GreaterThanOrEqualTo(0));
    Assert.That(TailRecursion.Run(countDown), Is.EqualTo(1), "the one self tail call");

    Assert.That(countDown.AllInstructions.OfType<IrCall>().Any(c => ReferenceEquals(c.Callee, countDown)),
      Is.False, "the self-call is gone");
    Assert.That(countDown.Blocks[1].Phis.Count(), Is.EqualTo(countDown.Parameters.Count),
      "the old entry is now a loop header carrying each parameter as a phi");
  }

  /// <summary>
  /// A self-call that is NOT in tail position keeps its frame: the result is used after the call
  /// returns, so reusing the frame would overwrite what the caller still needs.
  /// </summary>
  [Test]
  public void Lower_GivenARecursionThatUsesItsResult_ThenTheCallStays() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize("""
      DECLARE FUNCTION Fact&(BYVAL n&)
      PRINT Fact&(5)
      END
      FUNCTION Fact&(BYVAL n&)
        IF n& <= 1 THEN
          Fact& = 1
        ELSE
          Fact& = n& * Fact&(n& - 1)
        END IF
      END FUNCTION
      """, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");

    var fact = module!.Functions.Single(f => f.Name.Equals("Fact", StringComparison.OrdinalIgnoreCase));
    Mem2Reg.Run(fact);
    Assert.That(TailRecursion.Run(fact), Is.Zero, "the product needs the call's result, so it is not a tail call");
  }
}
