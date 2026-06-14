using System.Diagnostics;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Floating-point math intrinsics lowered to LLVM intrinsics (llc-optimizable, not opaque).</summary>
[TestFixture]
public sealed class MathIntrinsicTests {

  private static IrModule LowerOptimized(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var module = IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35))!;
    IrPassManager.Standard().RunOnModule(module);
    return module;
  }

  [Test]
  public void MathFunctions_LowerToLlvmIntrinsics() {
    var module = LowerOptimized("x# = 2.0\ny# = SQR(x#)\nz# = SIN(x#) + COS(x#) + EXP(x#) + LOG(x#)\nPRINT y#\nPRINT z#\nEND");

    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var text = LlvmEmitter.Emit(module);
    Assert.That(text, Does.Contain("@llvm.sqrt.f"));
    Assert.That(text, Does.Contain("@llvm.sin.f"));
    Assert.That(text, Does.Contain("@llvm.cos.f"));
    Assert.That(text, Does.Contain("@llvm.exp.f"));
    Assert.That(text, Does.Contain("@llvm.log.f"));
  }

  [Test]
  public void TanAndAtan_LowerToLlvmIntrinsics() {
    var module = LowerOptimized("x# = 1.0\ny# = TAN(x#) + ATN(x#)\nPRINT y#\nEND");

    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var text = LlvmEmitter.Emit(module);
    Assert.That(text, Does.Contain("@llvm.tan.f"));
    Assert.That(text, Does.Contain("@llvm.atan.f"));
  }

  [Test]
  public void PowerOperator_LowersToLlvmPow() {
    var module = LowerOptimized("b# = 2.0\ne# = 10.0\nr# = b# ^ e#\nPRINT r#\nEND");

    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(LlvmEmitter.Emit(module), Does.Contain("@llvm.pow.f"));
  }

  [Test]
  public void Fp80Constant_IsEmittedInLlvmExtendedForm() {
    // SQR's EXT result surfaces an x86_fp80 constant; it must use the 0xK 20-hex form
    var module = LowerOptimized("y# = SQR(2.0)\nPRINT y#\nEND");
    var text = LlvmEmitter.Emit(module);

    Assert.That(text, Does.Match("x86_fp80 0xK[0-9A-F]{20}"));
  }

  [Test]
  public void MathModule_CompilesToNativeViaLlc() {
    var module = LowerOptimized("x# = 9.0\nPRINT SQR(x#)\nEND");

    try {
      using var probe = Process.Start(new ProcessStartInfo("llc", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
      probe!.WaitForExit();
    } catch {
      Assert.Ignore("llc not available");
    }

    var ll = LlvmEmitter.Emit(module, "x86_64-unknown-linux-gnu");
    using var p = Process.Start(new ProcessStartInfo("llc", "-filetype=obj -o /dev/null -") { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false })!;
    p.StandardInput.Write(ll);
    p.StandardInput.Close();
    var err = p.StandardError.ReadToEnd();
    p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    Assert.That(p.ExitCode, Is.EqualTo(0), $"llc rejected the math module:\n{err}\n--- IR ---\n{ll}");
  }
}
