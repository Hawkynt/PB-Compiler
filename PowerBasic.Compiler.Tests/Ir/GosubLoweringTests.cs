using System.Diagnostics;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// GOSUB / RETURN lowering: a fixed-depth return-id stack records the call site, and a
/// single shared dispatch block pops the top id and switches back to the matching
/// continuation - so nested GOSUBs return in LIFO order.
/// </summary>
[TestFixture]
public sealed class GosubLoweringTests {

  private static IrFunction? Lower(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    return IrLowering.TryLowerMainBody(Binder.Bind(unit, Dialect.Pb35));
  }

  private static IrModule? LowerModule(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var module = IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35));
    if (module is not null)
      IrPassManager.Standard().RunOnModule(module);
    return module;
  }

  [Test]
  public void SingleGosub_PushesIdAndDispatchesBack() {
    var fn = Lower("x% = 1\nGOSUB add_ten\ny% = x%\nEND\nadd_ten:\nx% = x% + 10\nRETURN");

    Assert.That(fn, Is.Not.Null);
    Assert.That(IrVerifier.Verify(fn!), Is.Empty);
    Assert.That(fn!.AllInstructions.OfType<IrSwitch>().Any(), Is.True);                 // the dispatch switch
    Assert.That(fn.Blocks.Any(b => b.Label == "gosub.dispatch"), Is.True);
    Assert.That(fn.AllInstructions.OfType<IrAlloca>().Any(a => a.Count > 1), Is.True);  // the return-id stack
  }

  [Test]
  public void SingleGosub_VerifiesAfterOptimization() {
    var fn = Lower("x% = 1\nGOSUB add_ten\ny% = x%\nEND\nadd_ten:\nx% = x% + 10\nRETURN")!;

    IrPassManager.Standard().RunToFixpoint(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void NestedGosub_ReturnsInLifoOrder() {
    var fn = Lower("GOSUB outer\nz% = a%\nEND\nouter:\na% = 1\nGOSUB inner\na% = a% + 1\nRETURN\ninner:\na% = a% + 100\nRETURN")!;

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    var dispatch = fn.Blocks.First(b => b.Label == "gosub.dispatch");
    var sw = dispatch.Instructions.OfType<IrSwitch>().Single();
    Assert.That(sw.Cases.Count, Is.EqualTo(2));   // two distinct GOSUB sites share the one dispatch

    IrPassManager.Standard().RunToFixpoint(fn);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void ReturnToExplicitLabel_PopsThenJumps() {
    var fn = Lower("x% = 0\nGOSUB body\nfin:\ny% = x%\nEND\nbody:\nx% = 5\nRETURN fin")!;

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    IrPassManager.Standard().RunToFixpoint(fn);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Pipeline_GosubProgram_IsAcceptedByLlvm() {
    var module = LowerModule("x% = 0\nGOSUB bump\nPRINT x%\nEND\nbump:\nx% = x% + 7\nRETURN");
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
    Assert.That(p.ExitCode, Is.EqualTo(0), $"llvm-as rejected the GOSUB module:\n{err}\n--- IR ---\n{ll}");
  }
}
