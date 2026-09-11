using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class AggregateDeadFieldScalarizationTests {

  private static IrModule Lower(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var model = Binder.Bind(unit, Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    return module!;
  }

  private static IrModule Optimize(string source) {
    var module = Lower(source);
    var pipeline = IrPassManager.Standard();
    pipeline.VerifyEachPass = true;
    pipeline.RunOnModule(module);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    return module;
  }

  [Test]
  public void Pipeline_GivenWholeRecordCopyWithUnobservedField_WhenOptimized_ThenDeadFieldDoesNotKeepCopyMaterialized() {
    var module = Optimize("""
      TYPE Trio
        A AS INTEGER
        B AS INTEGER
        C AS INTEGER
      END TYPE
      PRINT CopyLive%(3, 4)
      END
      FUNCTION CopyLive%(BYVAL x%, BYVAL z%)
        DIM source AS Trio
        DIM destination AS Trio
        source.A = x%
        source.C = z%
        destination = source
        CopyLive% = destination.A + destination.C
      END FUNCTION
      """);
    var copyLive = module.Functions.Single(f => f.Name.Equals("CopyLive", StringComparison.OrdinalIgnoreCase));

    Assert.Multiple(() => {
      Assert.That(copyLive.AllInstructions.OfType<IrCall>()
        .Any(c => c.Callee is IrFunction { Name: "llvm.memcpy.p0.p0.i32" }), Is.False);
      Assert.That(copyLive.AllInstructions.OfType<IrAlloca>()
        .Any(a => a.Allocated == IrType.I8 && a.Count == 6), Is.False);
      Assert.That(copyLive.AllInstructions.OfType<IrGep>(), Is.Empty);
      Assert.That(IrVerifier.Verify(copyLive), Is.Empty);
    });
  }

  [Test]
  public void BlockScalarization_GivenIncompleteLayoutAndEscapingDestination_WhenRun_ThenCopyRemainsWhole() {
    var module = Lower("""
      TYPE Trio
        A AS INTEGER
        B AS INTEGER
        C AS INTEGER
      END TYPE
      PRINT KeepCopy%(3, 4)
      END
      SUB Observe(r AS Trio)
        PRINT r.B
      END SUB
      FUNCTION KeepCopy%(BYVAL x%, BYVAL z%)
        DIM source AS Trio
        DIM destination AS Trio
        source.A = x%
        source.C = z%
        destination = source
        CALL Observe(destination)
        KeepCopy% = destination.A + destination.C
      END FUNCTION
      """);
    var keepCopy = module.Functions.Single(f => f.Name.Equals("KeepCopy", StringComparison.OrdinalIgnoreCase));

    var changes = AggregateBlockScalarization.Run(keepCopy);

    Assert.Multiple(() => {
      Assert.That(changes, Is.Zero);
      Assert.That(keepCopy.AllInstructions.OfType<IrCall>()
        .Any(c => c.Callee is IrFunction { Name: "llvm.memcpy.p0.p0.i32" }), Is.True);
      Assert.That(keepCopy.AllInstructions.OfType<IrAlloca>()
        .Count(a => a.Allocated == IrType.I8 && a.Count == 6), Is.EqualTo(2));
      Assert.That(IrVerifier.Verify(keepCopy), Is.Empty);
    });
  }
}
