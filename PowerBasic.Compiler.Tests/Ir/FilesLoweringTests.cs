using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>The FILES statement is one consuming runtime call, with <c>*.*</c> as its omitted mask.</summary>
[TestFixture]
public sealed class FilesLoweringTests {

  private static IrModule Lower(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model);
    Assert.That(module, Is.Not.Null, "FILES must lower through the mandatory IR path");
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    return module!;
  }

  [Test]
  public void Lower_GivenBareFiles_WhenMaskIsOmitted_ThenCallsTheRuntimeWithAllFilesMask() {
    var text = LlvmEmitter.Emit(Lower("FILES\nEND\n"));

    Assert.That(text, Does.Contain("c\"*.*\""));
    Assert.That(text, Does.Contain("call void @rt_files(ptr"));
  }

  [Test]
  public void Lower_GivenFilesWithAComputedMask_WhenLowered_ThenTheRuntimeConsumesTheOwnedString() {
    var module = Lower("mask$ = \"*.\" + \"BAS\"\nFILES mask$\nEND\n");
    var calls = module.FindFunction("main")!.AllInstructions.OfType<IrCall>().ToList();
    var files = calls.Single(call => call.Callee.Name == "rt_files");
    var filesIndex = calls.IndexOf(files);

    Assert.That(calls[filesIndex - 1].Callee.Name, Is.EqualTo("rt_str_dup"),
      "a variable's borrowed cell cannot be consumed directly");
    Assert.That(files.ArgCount, Is.EqualTo(1), "rt_files receives and consumes that owned duplicate");
  }
}
