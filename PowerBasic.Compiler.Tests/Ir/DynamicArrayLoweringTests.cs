using System.Diagnostics;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// Dynamic (REDIM'd) 1-D arrays: the array is a runtime-allocated buffer addressed
/// through a (data pointer, lower bound) descriptor. REDIM allocates via the array
/// runtime, element access loads the data pointer and indexes relative to the bound,
/// and ERASE frees the buffer.
/// </summary>
[TestFixture]
public sealed class DynamicArrayLoweringTests {

  private static IrModule? LowerModule(string source, bool optimize = true) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var module = IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35));
    if (module is not null && optimize)
      IrPassManager.Standard().RunOnModule(module);
    return module;
  }

  [Test]
  public void NumericRedim_AllocatesBufferAndIndexesRelativeToBound() {
    var module = LowerModule("REDIM a%(1 TO 5)\na%(1) = 10\na%(2) = 20\nx% = a%(1) + a%(2)\nEND", optimize: false);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("@rt_arr_alloc(i32"));     // count + element size
    Assert.That(text, Does.Contain("alloca ptr"));            // the data-pointer descriptor slot
  }

  [Test]
  public void StringRedim_AllocatesPointerBuffer() {
    var module = LowerModule("REDIM s$(1 TO 3)\ns$(1) = \"hi\"\nPRINT s$(1)\nEND", optimize: false);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("@rt_arr_alloc_ptr(i32"));        // a buffer of target pointers
    Assert.That(text, Does.Contain("getelementptr ptr, ptr"));      // typed element-indexed GEP
  }

  [Test]
  public void Erase_FreesTheBuffer() {
    var module = LowerModule("REDIM a%(1 TO 4)\na%(1) = 1\nERASE a%\nEND", optimize: false);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    Assert.That(LlvmEmitter.Emit(module!), Does.Contain("@rt_arr_free(ptr"));
  }

  [Test]
  public void Reredim_AllocatesTwice() {
    var module = LowerModule("REDIM a%(1 TO 3)\nREDIM a%(1 TO 9)\na%(5) = 1\nEND", optimize: false);

    Assert.That(module, Is.Not.Null);
    var main = module!.Functions.First(f => f.Name == "main");
    var allocs = main.AllInstructions.OfType<IrCall>().Count(c => c.Callee is IrFunction { Name: "rt_arr_alloc" });
    Assert.That(allocs, Is.EqualTo(2));
  }

  [Test]
  public void RedimPreserve_ReallocatesKeepingContents() {
    var module = LowerModule("REDIM a%(1 TO 3)\na%(1) = 7\nREDIM PRESERVE a%(1 TO 9)\nx% = a%(1)\nEND", optimize: false);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    Assert.That(LlvmEmitter.Emit(module!), Does.Contain("@rt_arr_realloc(ptr"));   // grow in place, keeping the prefix
  }

  [Test]
  public void MultiDimRedim_FlattensRowMajor() {
    var module = LowerModule("REDIM g%(1 TO 2, 1 TO 3)\ng%(1, 2) = 7\ny% = g%(1, 2)\nEND", optimize: false);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var main = module!.Functions.First(f => f.Name == "main");
    // allocation count is the product of both dimension sizes (2 * 3)
    Assert.That(main.AllInstructions.OfType<IrCall>().Any(c => c.Callee is IrFunction { Name: "rt_arr_alloc" }), Is.True);
  }

  [Test]
  public void MultiDimRedim_VerifiesAfterOptimization() {
    var module = LowerModule("REDIM g%(1 TO 3, 1 TO 4)\nFOR i% = 1 TO 3\n  FOR j% = 1 TO 4\n    g%(i%, j%) = i% * j%\n  NEXT j%\nNEXT i%\nx% = g%(2, 3)\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
  }

  [Test]
  public void NumericRedim_VerifiesAfterOptimization() {
    var module = LowerModule("REDIM a%(0 TO 9)\nFOR i% = 0 TO 9\n  a%(i%) = i% * 2\nNEXT i%\nx% = a%(3)\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
  }

  [Test]
  public void Pipeline_DynamicArrayProgram_IsAcceptedByLlvm() {
    var module = LowerModule("REDIM a%(1 TO 4)\nFOR i% = 1 TO 4\n  a%(i%) = i% * i%\nNEXT i%\nPRINT a%(4)\nEND");
    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);

    try {
      using var probe = Process.Start(new ProcessStartInfo("llvm-as", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
      probe!.WaitForExit();
    } catch {
      Assert.Ignore("llvm-as not available");
    }

    var ll = LlvmEmitter.Emit(module!, "x86_64-unknown-linux-gnu");
    using var p = Process.Start(new ProcessStartInfo("llvm-as", "-o /dev/null -") { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false })!;
    p.StandardInput.Write(ll);
    p.StandardInput.Close();
    var err = p.StandardError.ReadToEnd();
    p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    Assert.That(p.ExitCode, Is.EqualTo(0), $"llvm-as rejected the dynamic-array module:\n{err}\n--- IR ---\n{ll}");
  }
}
