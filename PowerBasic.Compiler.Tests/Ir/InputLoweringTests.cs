using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Console INPUT lowering via the runtime-call ABI.</summary>
[TestFixture]
public sealed class InputLoweringTests {

  private static IrModule? LowerOptimized(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var module = IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35));
    if (module is not null)
      IrPassManager.Standard().RunOnModule(module);
    return module;
  }

  [Test]
  public void NumericInput_WithPrompt_EmitsPromptAndRuntimeRead() {
    var module = LowerOptimized("INPUT \"Enter a number\"; n%\nm% = n% * 2\nPRINT m%\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("Enter a number"));        // the prompt literal
    Assert.That(text, Does.Contain("call i16 @rt_input_i16()"));
  }

  [Test]
  public void FixedStringInput_PadsIntoTheBuffer() {
    var module = LowerOptimized("DIM s AS STRING * 12\nINPUT s\nPRINT s\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("call ptr @rt_input_str()"));
    Assert.That(text, Does.Contain("@rt_str_to_fixed(ptr"));   // the input is padded/truncated into the fixed buffer
  }

  [Test]
  public void StringInput_ReadsAHandle() {
    var module = LowerOptimized("INPUT a$\nPRINT a$\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    Assert.That(LlvmEmitter.Emit(module!), Does.Contain("call ptr @rt_input_str()"));
  }

  [Test]
  public void LineInput_ReadsAWholeLine() {
    var module = LowerOptimized("LINE INPUT a$\nPRINT a$\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(LlvmEmitter.Emit(module!), Does.Contain("call ptr @rt_input_line()"));
  }

  [Test]
  public void FileInput_LowersToAFileRuntimeRead() {
    var module = LowerOptimized("OPEN \"x\" FOR INPUT AS #1\nINPUT #1, n%\nCLOSE #1\nPRINT n%\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    Assert.That(LlvmEmitter.Emit(module!), Does.Contain("@rt_finput_i16(i32"));
  }
}
