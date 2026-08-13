using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// <c>NOINLINE</c> is a contract with the programmer - "this stays a real call" - and the IR pipeline
/// has to honour it for the same reason the direct emitter does.
///
/// The shape it guards is a procedure that exists only to be an optimization barrier: an empty
/// <c>SUB</c> taking a variable BYREF, so the optimizer cannot know what the variable holds afterwards.
/// Absorbing that body is SOUND - it writes nothing - and it is precisely what makes the barrier
/// disappear, taking with it every expectation about the code behind it. Dropping the call as a dead
/// pure call has the identical effect, so both are declined.
/// </summary>
[TestFixture]
public sealed class InlinerNoInlineTests {

  // T() is empty and takes x% BYREF: absorbing it makes x% a known 11, and then the whole program folds
  private const string _barrier = """
    DECLARE SUB T(a%)
    x% = 11
    T x%
    y% = x% * 3
    T y%
    PRINT y%
    END
    SUB T(a%)__
    END SUB
    """;

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static IrModule Lower(bool noInline) {
    var module = IrLowering.TryLowerModule(Bind(_barrier.Replace("__", noInline ? " NOINLINE" : "")), out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    return module!;
  }

  [Test]
  public void Inline_GivenANoInlineCallee_WhenTheModuleIsInlined_ThenNoCallIsSubstituted() {
    Assert.That(Inliner.Run(Lower(noInline: true)), Is.Zero, "NOINLINE pins every call site as a real call");
  }

  /// <summary>The negative twin: without the modifier the same body is exactly what the inliner takes.</summary>
  [Test]
  public void Inline_GivenAnOrdinaryEmptyCallee_WhenTheModuleIsInlined_ThenTheCallsAreSubstituted() {
    Assert.That(Inliner.Run(Lower(noInline: false)), Is.EqualTo(2), "both calls to an ordinary empty SUB are absorbed");
  }

  /// <summary>
  /// Removing the call outright is the other way to lose the barrier, and it is not inlining -
  /// an empty body writes no memory and the call has no result, so the summaries call it dead.
  /// </summary>
  [Test]
  public void RemoveDeadPureCalls_GivenANoInlineCallee_WhenTheModuleIsSwept_ThenTheCallsRemain() {
    Assert.That(FunctionSummaries.RemoveDeadPureCalls(Lower(noInline: true)), Is.Zero,
      "a call kept by NOINLINE is not a dead pure call to be swept");
    Assert.That(FunctionSummaries.RemoveDeadPureCalls(Lower(noInline: false)), Is.EqualTo(2),
      "without the modifier the same two calls are dead");
  }

  /// <summary>
  /// And the barrier does its job end to end: routed, the multiply behind a NOINLINE call is still in
  /// the image and still computes 33 - which is the property the Emit_Given* fixtures rest on.
  /// </summary>
  [Test]
  public void Emit_GivenANoInlineBarrier_WhenRoutedThroughTheBackend_ThenTheProcedureSurvivesAndTheProgramRuns() {
    var cg = new CodeGenerator(Bind(_barrier.Replace("__", " NOINLINE"))) { Optimize = true, UseExperimentalBackend = true };
    var image = cg.EmitExecutable();

    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    Assert.That(cg.DescribeImage().Procedures.Select(p => p.Name), Does.Contain("T"), "the barrier survives to the image");
    Assert.That(Cpu8086.Run(image).Output.Trim(), Is.EqualTo("33"));
  }
}
