using System.Diagnostics;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// String arrays: a DIM of string elements allocates a buffer of target-sized pointer
/// handles, and indexed read/write go through a typed (element-indexed) GEP so LLVM scales
/// the index by the target pointer size rather than the DOS 2-byte handle width.
/// </summary>
[TestFixture]
public sealed class StringArrayLoweringTests {

  private static IrModule? LowerOptimized(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var module = IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35));
    if (module is not null)
      IrPassManager.Standard().RunOnModule(module);
    return module;
  }

  private static IrModule? Lower(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    return IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35));
  }

  [Test]
  public void Lower_StringArray_AllocatesPointerHandleBuffer() {
    var module = Lower("DIM s$(1 TO 3)\ns$(1) = \"a\"\ns$(2) = \"b\"\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("alloca ptr, i32 3"));                   // 3 string handles
    Assert.That(text, Does.Contain("getelementptr ptr, ptr"));             // typed (element-indexed) GEP
  }

  [Test]
  public void Lower_StringArrayElementStore_StoresTheHandle() {
    var module = Lower("DIM s$(1 TO 2)\ns$(1) = \"hi\"\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("call ptr @rt_str_const(ptr"));          // the literal becomes a handle
    Assert.That(text, Does.Contain("store ptr"));                           // the handle is stored into the cell
  }

  [Test]
  public void Lower_StringArrayElementRead_LoadsHandleAndPrints() {
    var module = Lower("DIM s$(1 TO 2)\ns$(1) = \"hi\"\nPRINT s$(1)\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("load ptr, ptr"));                       // read the handle back
    Assert.That(text, Does.Contain("call void @rt_print_strvar(ptr"));      // print it
  }

  [Test]
  public void Lower_StringArrayElementConcat_RoundTripsThroughHandles() {
    var module = Lower("DIM s$(1 TO 2)\ns$(1) = \"Hello, \"\ns$(2) = \"world!\"\nPRINT s$(1) & s$(2)\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    Assert.That(LlvmEmitter.Emit(module!), Does.Contain("call ptr @rt_str_concat(ptr"));
  }

  [Test]
  public void Lower_StringArrayInLoop_StaysInMemory() {
    var module = LowerOptimized("DIM s$(1 TO 4)\nFOR i% = 1 TO 4\n  s$(i%) = \"x\"\nNEXT i%\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var main = module!.Functions.First(f => !f.IsDeclaration);
    Assert.That(main.AllInstructions.OfType<IrAlloca>().Any(a => a is { Count: 4 } && a.Allocated.Kind == IrTypeKind.Ptr), Is.True);
  }

  [Test]
  public void Pipeline_StringArrayProgram_IsAcceptedByLlvm() {
    var module = LowerOptimized("DIM s$(0 TO 2)\ns$(0) = \"a\"\ns$(1) = \"b\"\ns$(2) = s$(0) & s$(1)\nPRINT s$(2)\nEND");
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
    Assert.That(p.ExitCode, Is.EqualTo(0), $"llvm-as rejected the string-array module:\n{err}\n--- IR ---\n{ll}");
  }
}
