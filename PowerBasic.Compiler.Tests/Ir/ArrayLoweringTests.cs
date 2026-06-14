using System.Diagnostics;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Static array lowering: DIM allocation plus indexed load/store via byte GEPs.</summary>
[TestFixture]
public sealed class ArrayLoweringTests {

  private static IrFunction Lower(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    return IrLowering.TryLowerMainBody(Binder.Bind(unit, Dialect.Pb35))!;
  }

  [Test]
  public void Lower_OneDimensionalArray_AllocatesBufferAndGepsElements() {
    var fn = Lower("DIM a%(1 TO 5)\na%(1) = 10\na%(2) = 20\nx% = a%(1) + a%(2)");

    Assert.That(fn, Is.Not.Null);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    var text = IrPrinter.Print(fn);
    Assert.That(text, Does.Contain("alloca i16, i32 5"));   // 5-element buffer
    Assert.That(text, Does.Contain("gep i8"));              // byte-offset element addressing
    Assert.That(fn.AllInstructions.OfType<IrStore>().Count(), Is.GreaterThanOrEqualTo(2));
  }

  [Test]
  public void Lower_TwoDimensionalArray_FlattensRowMajor() {
    var fn = Lower("DIM g%(1 TO 2, 1 TO 3)\ng%(1, 2) = 7\ny% = g%(1, 2)");

    Assert.That(fn, Is.Not.Null);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(IrPrinter.Print(fn), Does.Contain("alloca i16, i32 6"));  // 2*3 elements
  }

  [Test]
  public void Lower_ArrayAlloca_IsNotPromotedByMem2Reg() {
    var fn = Lower("DIM a%(1 TO 4)\nFOR i% = 1 TO 4\n  a%(i%) = i%\nNEXT i%");
    Mem2Reg.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrAlloca>().Any(a => a.Count > 1), Is.True);  // array stays in memory
  }

  [Test]
  public void Pipeline_ArrayProgram_IsAcceptedByLlvm() {
    var fn = Lower("DIM a%(0 TO 9)\nFOR i% = 0 TO 9\n  a%(i%) = i% * 2\nNEXT i%\nx% = a%(3)");
    IrPassManager.Standard().RunToFixpoint(fn);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    try {
      using var probe = Process.Start(new ProcessStartInfo("llvm-as", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
      probe!.WaitForExit();
    } catch {
      Assert.Ignore("llvm-as not available");
    }

    var module = new IrModule("T");
    module.AddFunction(fn);
    var ll = LlvmEmitter.Emit(module, "x86_64-unknown-linux-gnu");
    using var p = Process.Start(new ProcessStartInfo("llvm-as", "-o /dev/null -") { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false })!;
    p.StandardInput.Write(ll);
    p.StandardInput.Close();
    var err = p.StandardError.ReadToEnd();
    p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    Assert.That(p.ExitCode, Is.EqualTo(0), $"llvm-as rejected the array module:\n{err}\n--- IR ---\n{ll}");
  }
}
