using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Emit;

[TestFixture]
public sealed class IrBasicWriterOptimizationTests {
  [Test]
  public void OptimizedIrWriter_DesugarsIntegerIdentityOperators() {
    const string source = "x% = 7\na% = x% * 1\nb% = x% XOR x%\nc% = x% AND x%\nd% = x% OR x%\nPRINT a%, b%, c%, d%\nEND";
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var model = Binder.Bind(unit, Dialect.Pb35);
    var module = IrLowering.TryLowerModule(model);
    Assert.That(module, Is.Not.Null);

    IrMiddleEndPipeline.RunHostedModule(module!, optimize: true, optimizeForSpeed: false,
      enableFpLookupTables: false, recoverIntegerArithmetic: true, parallelLoops: false);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);

    var text = IrBasicWriter.Write(module!);
    Assert.That(text, Does.Not.Contain(" * 1"));
    Assert.That(text, Does.Not.Contain(" XOR "));
    Assert.That(text, Does.Not.Contain(" AND "));
    Assert.That(text, Does.Not.Contain(" OR "));
  }

  [Test]
  public void HostedPipeline_RemovesUnusedPureCallsAndUncalledFunctions() {
    const string source = "discard% = Pure%(4)\nPRINT 1\nEND\nFUNCTION Pure%(BYVAL n%)\n Pure% = n% * 2\nEND FUNCTION\nFUNCTION Never%()\n Never% = 99\nEND FUNCTION";
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var model = Binder.Bind(unit, Dialect.Pb35);
    var module = IrLowering.TryLowerModule(model);
    Assert.That(module, Is.Not.Null);

    IrMiddleEndPipeline.RunHostedModule(module!, optimize: true, recoverIntegerArithmetic: true);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    Assert.That(module!.Functions.Any(f => f.Name.Contains("Never", StringComparison.OrdinalIgnoreCase)), Is.False);
    Assert.That(module.Functions.SelectMany(f => f.AllInstructions).OfType<IrCall>()
      .Any(c => c.Callee is IrFunction f && f.Name.Contains("Pure", StringComparison.OrdinalIgnoreCase)), Is.False);
  }
}
