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
      "  BEEP\n" +                  // hardware command, unsupported -> body declines, signature stays
      "  f% = n%\n" +
      "END FUNCTION");

    Assert.That(module, Is.Not.Null);
    var f = module!.Functions.First(fn => fn.Name != "main");
    Assert.That(f.IsDeclaration, Is.True);             // body could not lower, kept as a declaration
    Assert.That(IrVerifier.Verify(module), Is.Empty);  // main + the call are still valid
  }

  [Test]
  public void Module_LowersAByRefProcedureAndPassesAddresses() {
    // BYREF parameters arrive as pointers; the call passes the variable's address
    var module = LowerModule(
      "DECLARE SUB inc(x%)\n" +
      "q% = 0\n" +
      "CALL inc(q%)\n" +
      "\n" +
      "SUB inc(x%)\n" +
      "  x% = x% + 1\n" +
      "END SUB");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var sub = module!.Functions.First(f => f.Name != "main");
    Assert.That(sub.Parameters[0].Type, Is.EqualTo(IrType.Ptr));   // BYREF -> pointer parameter
    Assert.That(sub.IsDeclaration, Is.False);
    // the call passes a pointer (q's address), so q's slot is not promoted away
    Assert.That(module.FindFunction("main")!.AllInstructions.OfType<IrCall>().Single().ArgCount, Is.EqualTo(1));
  }

  [Test]
  public void Module_GivenProcedureTakingAString_ThenItIsAHandleParameter() {
    // a string is a runtime handle, so it travels as an opaque pointer: BYREF passes a pointer
    // to the caller's handle slot, which is what makes the procedure able to write it back
    var module = LowerModule(
      "DECLARE SUB greet(s$)\n" +
      "CALL greet(\"hi\")\n" +
      "\n" +
      "SUB greet(s$)\n" +
      "  DIM n%\n" +
      "  n% = LEN(s$)\n" +
      "END SUB");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var sub = module!.Functions.First(f => f.Name == "greet");
    Assert.That(sub.IsDeclaration, Is.False, "the body is in the subset now");
    Assert.That(sub.Parameters[0].Type, Is.EqualTo(IrType.Ptr));
  }

  [Test]
  public void Module_GivenStringReturningFunction_ThenTheResultIsAHandle() {
    var module = LowerModule(
      "DECLARE FUNCTION wrap$(s$)\n" +
      "DIM t AS STRING\n" +
      "t = wrap$(\"x\")\n" +
      "\n" +
      "FUNCTION wrap$(s$)\n" +
      "  wrap$ = \"[\" + s$ + \"]\"\n" +
      "END FUNCTION");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var fn = module!.Functions.First(f => f.Name == "wrap");
    Assert.That(fn.ReturnType, Is.EqualTo(IrType.Ptr));
    Assert.That(fn.IsDeclaration, Is.False);
  }

  [Test]
  public void Module_GivenSharedVariableUsedByAProcedure_ThenItBecomesOneGlobal() {
    // main and the procedure must see the SAME cell - a frame slot each would silently diverge
    var module = LowerModule(
      "DECLARE SUB bump()\n" +
      "DIM g AS SHARED INTEGER\n" +
      "g = 1\n" +
      "CALL bump()\n" +
      "\n" +
      "SUB bump()\n" +
      "  g = g + 1\n" +
      "END SUB");

    Assert.That(module, Is.Not.Null);
    Assert.That(module!.Globals.Any(g => g.Name == "g.g"), Is.True, "the shared variable needs module storage");
  }

  [Test]
  public void Module_GivenModuleVariableOnlyMainUses_ThenItStaysAFrameSlot() {
    // ... but a module variable no procedure touches stays an alloca, so mem2reg can still
    // promote it to an SSA register - correctness must not cost the optimizer its best case
    var module = LowerModule(
      "DECLARE SUB noop()\n" +
      "DIM m AS INTEGER\n" +
      "m = 1\n" +
      "CALL noop()\n" +
      "\n" +
      "SUB noop()\n" +
      "  DIM k%\n" +
      "  k% = 2\n" +
      "END SUB");

    Assert.That(module, Is.Not.Null);
    Assert.That(module!.Globals.Any(g => g.Name.StartsWith("g.")), Is.False);
  }

  [Test]
  public void Module_GivenStaticLocal_ThenItSurvivesTheCallAsAGlobal() {
    var module = LowerModule(
      "DECLARE FUNCTION ticks%()\n" +
      "r% = ticks%\n" +
      "\n" +
      "FUNCTION ticks%()\n" +
      "  STATIC seen AS INTEGER\n" +
      "  seen = seen + 1\n" +
      "  ticks% = seen\n" +
      "END FUNCTION");

    Assert.That(module, Is.Not.Null);
    Assert.That(module!.Globals.Any(g => g.Name == "static.ticks.seen"), Is.True,
      "a STATIC local must name its owning procedure and cannot live in the frame");
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
