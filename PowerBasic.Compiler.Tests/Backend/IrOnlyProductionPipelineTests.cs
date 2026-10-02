using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Production architecture gate: executable and unit machine code may only be emitted from the
/// multi-stage IR/x86-16 route. The retired backend selector is retained as a source-compatibility
/// shim for older tests/consumers but cannot choose the syntax emitter.
/// </summary>
[TestFixture]
public sealed class IrOnlyProductionPipelineTests {

  private static SemanticModel Bind(string source, string file = "T.BAS") {
    var model = Binder.Bind(
      Parser.Parse(Lexer.Tokenize(source, file, Dialect.Pb36), file, Dialect.Pb36),
      Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  [Test]
  public void Executable_GivenMainAndProcedure_ThenEveryEmittedBodyComesFromX8616MachineIr() {
    var generator = new CodeGenerator(Bind("""
      PRINT Twice%(21)
      END
      FUNCTION Twice%(BYVAL n%) NOINLINE
        Twice% = n% * 2
      END FUNCTION
      """)) {
      Optimize = true,
    };

    var image = generator.EmitExecutable();

    Assert.Multiple(() => {
      Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
      Assert.That(image, Is.Not.Empty);
      Assert.That(generator.BackendDeclines, Is.Empty);
      Assert.That(generator.BackendModuleForTesting?.RepresentationStage, Is.EqualTo(IrRepresentationStage.LowIr),
        "production selection must consume the verified Low IR boundary, not an implicitly compatible SSA module");
      Assert.That(generator.BackendRoutedNames, Does.Contain("main"));
      Assert.That(generator.BackendRoutedNames, Does.Contain("Twice"));
      Assert.That(generator.BackendEmittedNamesForTesting, Does.Contain("main"));
      Assert.That(generator.BackendEmittedNamesForTesting, Does.Contain("Twice"));
    });
  }

  [Test]
  public void Com_GivenOptimizedConstantOutput_ThenArtifactStillComesFromX8616MachineIr() {
    var generator = new CodeGenerator(Bind("PRINT \"routed\"\nEND")) { Optimize = true };

    var image = generator.EmitCom();

    Assert.Multiple(() => {
      Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
      Assert.That(image, Is.Not.Empty);
      Assert.That(generator.BackendDeclines, Is.Empty);
      Assert.That(generator.BackendModuleForTesting?.RepresentationStage, Is.EqualTo(IrRepresentationStage.LowIr));
      Assert.That(generator.BackendRoutedNames, Does.Contain("main"));
      Assert.That(generator.BackendEmittedNamesForTesting, Does.Contain("main"),
        "routing/allocating main is insufficient: the COM bytes must be emitted from its machine product");
    });
  }

  [Test]
  public void Unit_GivenExportedProcedure_ThenPbuCodeComesOnlyFromX8616MachineIr() {
    var generator = new CodeGenerator(Bind("""
      $COMPILE UNIT
      FUNCTION AddOne%(BYVAL n%)
        AddOne% = n% + 1
      END FUNCTION
      """, "UNIT.BAS")) {
      Optimize = true,
    };

    var unit = generator.EmitUnit("UNIT");

    Assert.Multiple(() => {
      Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
      Assert.That(generator.BackendDeclines.Where(d => !d.Name.Equals("main", StringComparison.OrdinalIgnoreCase)), Is.Empty,
        "UNIT has no module body; only real procedure declines matter");
      Assert.That(generator.BackendModuleForTesting?.RepresentationStage, Is.EqualTo(IrRepresentationStage.LowIr));
      Assert.That(generator.BackendRoutedNames, Does.Contain("AddOne"));
      Assert.That(generator.BackendEmittedNamesForTesting, Does.Contain("AddOne"));
      Assert.That(unit.Code, Is.Not.Empty);
      Assert.That(unit.Exports.Select(e => e.Name), Does.Contain("AddOne"));
    });
  }
}
