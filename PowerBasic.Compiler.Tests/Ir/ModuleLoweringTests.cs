using System.Diagnostics;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Whole-module lowering: main body plus user SUB/FUNCTION (BYVAL scalar params) and calls.</summary>
[TestFixture]
public sealed class ModuleLoweringTests {

  private static IrModule? LowerModule(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    return IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35));
  }

  [Test]
  public void Module_LowersMainAndAByValFunction() {
    var module = LowerModule(
      "DECLARE FUNCTION sq%(BYVAL n%)\n" +
      "x% = sq%(5)\n" +
      "\n" +
      "FUNCTION sq%(BYVAL n%)\n" +
      "  sq% = n% * n%\n" +
      "END FUNCTION");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var fn = module!.Functions.First(f => f.Name != "main");
    Assert.That(fn.Parameters, Has.Count.EqualTo(1));
    Assert.That(fn.Parameters[0].Type, Is.EqualTo(IrType.I16));
    Assert.That(fn.ReturnType, Is.EqualTo(IrType.I16));
    Assert.That(fn.IsDeclaration, Is.False);
    // main contains a call to it
    Assert.That(module.FindFunction("main")!.AllInstructions.OfType<IrCall>().Count(), Is.EqualTo(1));
  }

  [Test]
  public void Module_LowersASubCallAsVoid() {
    var module = LowerModule(
      "DECLARE SUB show(BYVAL n%)\n" +
      "CALL show(42)\n" +
      "\n" +
      "SUB show(BYVAL n%)\n" +
      "  DIM t%\n" +
      "  t% = n%\n" +
      "END SUB");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var sub = module!.Functions.First(f => f.Name != "main");
    Assert.That(sub.ReturnType, Is.EqualTo(IrType.Void));
    Assert.That(module.FindFunction("main")!.AllInstructions.OfType<IrCall>().Single().Type, Is.EqualTo(IrType.Void));
  }

  [Test]
  public void Module_FunctionWithUnsupportedBodyBecomesADeclaration() {
    var module = LowerModule(
      "DECLARE FUNCTION f%(BYVAL n%)\n" +
      "y% = f%(3)\n" +
      "\n" +
      "FUNCTION f%(BYVAL n%)\n" +
      "  PRINT n%\n" +              // I/O is unsupported -> body declines, signature stays
      "  f% = n%\n" +
      "END FUNCTION");

    Assert.That(module, Is.Not.Null);
    var f = module!.Functions.First(fn => fn.Name != "main");
    Assert.That(f.IsDeclaration, Is.True);             // body could not lower, kept as a declaration
    Assert.That(IrVerifier.Verify(module), Is.Empty);  // main + the call are still valid
  }

  [Test]
  public void Module_DeclinesWhenMainCallsAByRefProcedure() {
    // BYREF parameters are not modelled yet, so a program that calls one cannot lower
    var module = LowerModule(
      "DECLARE SUB inc(x%)\n" +
      "q% = 0\n" +
      "CALL inc(q%)\n" +
      "\n" +
      "SUB inc(x%)\n" +
      "  x% = x% + 1\n" +
      "END SUB");

    Assert.That(module, Is.Null);
  }

  [Test]
  public void Module_OptimizedWholeModuleIsAcceptedByLlvm() {
    var module = LowerModule(
      "DECLARE FUNCTION poly%(BYVAL n%)\n" +
      "r% = poly%(7)\n" +
      "\n" +
      "FUNCTION poly%(BYVAL n%)\n" +
      "  poly% = n% * n% OR 1\n" +
      "END FUNCTION");
    Assert.That(module, Is.Not.Null);
    IrPassManager.Standard().RunOnModule(module!);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);

    RequireTool("llvm-as");
    var ll = LlvmEmitter.Emit(module!, "x86_64-unknown-linux-gnu");
    var (code, err) = Run("llvm-as", "-o /dev/null -", ll);
    Assert.That(code, Is.EqualTo(0), $"llvm-as rejected the module:\n{err}\n--- IR ---\n{ll}");
  }

  private static void RequireTool(string tool) {
    try {
      using var p = Process.Start(new ProcessStartInfo(tool, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
      p!.WaitForExit();
    } catch {
      Assert.Ignore($"{tool} not available");
    }
  }

  private static (int Code, string Err) Run(string tool, string args, string stdin) {
    using var p = Process.Start(new ProcessStartInfo(tool, args) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
    p.StandardInput.Write(stdin);
    p.StandardInput.Close();
    var err = p.StandardError.ReadToEnd();
    p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return (p.ExitCode, err);
  }
}
