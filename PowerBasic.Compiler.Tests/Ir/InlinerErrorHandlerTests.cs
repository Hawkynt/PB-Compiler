using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// A function with an armed error handler is not duplicable, and the inliner has to know it.
///
/// <see cref="IrBlockAddress"/> is a CONSTANT, and <see cref="IrCloner"/> maps values - so a cloned
/// handler address still points at the ORIGINAL function's block. The emitter then looks that label
/// up in the function it is emitting, does not find it, and dies. Inlining INTO such a caller is no
/// better: the handler's saved frame describes a frame whose contents just changed underneath it.
///
/// This surfaced the moment inlining joined the production pipeline, as a KeyNotFoundException from
/// the machine emitter - which is the good version of this bug. The bad version is a handler address
/// that happens to resolve to a block that exists.
/// </summary>
[TestFixture]
public sealed class InlinerErrorHandlerTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private const string _handlerInAProcedure = """
    CALL Risky
    PRINT "after"
    END
    SUB Risky
      ON ERROR GOTO oops
      ERROR 5
      EXIT SUB
      oops:
      RESUME NEXT
    END SUB
    """;

  [Test]
  public void Inline_GivenACalleeWithAnArmedHandler_ThenItIsNotInlined() {
    var module = IrLowering.TryLowerModule(Bind(_handlerInAProcedure), out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    IrPassManager.Standard().RunOnModule(module!);

    Assert.That(Inliner.Run(module!), Is.Zero, "a function whose blocks a fault can jump to cannot be copied");
  }

  /// <summary>And the whole program still builds and runs - which is what the crash prevented.</summary>
  [Test]
  public void Emit_GivenACalleeWithAnArmedHandler_ThenTheProgramStillBuildsAndRuns() {
    var cg = new CodeGenerator(Bind(_handlerInAProcedure)) { Optimize = true, UseExperimentalBackend = true };
    var image = cg.EmitExecutable();

    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    Assert.That(Cpu8086.Run(image).Output.Trim(), Is.EqualTo("after"));
  }
}
