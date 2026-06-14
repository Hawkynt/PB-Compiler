using System.Diagnostics;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Basic string-variable support: assignment, concatenation, PRINT via the runtime-handle ABI.</summary>
[TestFixture]
public sealed class StringLoweringTests {

  private static IrModule? LowerOptimized(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var module = IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35));
    if (module is not null)
      IrPassManager.Standard().RunOnModule(module);
    return module;
  }

  [Test]
  public void StringAssignAndPrint_UsesHandleRuntimeCalls() {
    var module = LowerOptimized("a$ = \"hi\"\nPRINT a$\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("call ptr @rt_str_const(ptr @.str0, i32 2)"));
    Assert.That(text, Does.Contain("call void @rt_print_strvar(ptr"));
  }

  [Test]
  public void StringConcatenation_LowersToRuntimeConcat() {
    var module = LowerOptimized("a$ = \"Hello, \"\nb$ = \"world!\"\nc$ = a$ & b$\nPRINT c$\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    Assert.That(LlvmEmitter.Emit(module!), Does.Contain("call ptr @rt_str_concat(ptr"));
  }

  [Test]
  public void StringProgram_CompilesToNativeViaLlc() {
    var module = LowerOptimized("a$ = \"Hello, \"\nb$ = \"world!\"\nPRINT a$ & b$\nEND");
    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);

    try {
      using var probe = Process.Start(new ProcessStartInfo("llc", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
      probe!.WaitForExit();
    } catch {
      Assert.Ignore("llc not available");
    }

    var ll = LlvmEmitter.Emit(module!, "x86_64-unknown-linux-gnu");
    using var p = Process.Start(new ProcessStartInfo("llc", "-filetype=obj -o /dev/null -") { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false })!;
    p.StandardInput.Write(ll);
    p.StandardInput.Close();
    var err = p.StandardError.ReadToEnd();
    p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    Assert.That(p.ExitCode, Is.EqualTo(0), $"llc rejected the string module:\n{err}\n--- IR ---\n{ll}");
  }
}
