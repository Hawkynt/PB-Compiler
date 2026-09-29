using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Emit;

[TestFixture]
public sealed class IrBasicWriterDeadArmTests {
  [Test]
  public void OptimizedIrWriter_OmitsDeadIfAndSelectArms() {
    const string source = "IF 1 THEN\n PRINT \"live-if\"\nELSE\n PRINT \"dead-if\"\nEND IF\nSELECT CASE 2\nCASE 1\n PRINT \"dead-case\"\nCASE 2\n PRINT \"live-case\"\nCASE ELSE\n PRINT \"dead-else\"\nEND SELECT\nEND";
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var model = Binder.Bind(unit, Dialect.Pb35);
    var module = IrLowering.TryLowerModule(model);
    Assert.That(module, Is.Not.Null);

    IrMiddleEndPipeline.RunHostedModule(module!, optimize: true, optimizeForSpeed: false,
      enableFpLookupTables: false, recoverIntegerArithmetic: true, parallelLoops: false);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);

    var text = IrBasicWriter.Write(module!);
    Assert.That(text, Does.Contain("live-if"));
    Assert.That(text, Does.Contain("live-case"));
    Assert.That(text, Does.Not.Contain("dead-if"));
    Assert.That(text, Does.Not.Contain("dead-case"));
    Assert.That(text, Does.Not.Contain("dead-else"));
  }
}
