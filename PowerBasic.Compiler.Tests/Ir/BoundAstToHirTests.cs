using PowerBasic.Compiler.Hir;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class BoundAstToHirTests {

  [Test]
  public void Lower_GivenBoundAssignment_ThenHirCarriesAnExplicitAssignmentOperation() {
    const string source = "x% = 1";
    var unit = Parser.Parse(Lexer.Tokenize(source, "HIR.BAS", Dialect.Pb35), "HIR.BAS", Dialect.Pb35);
    var model = Binder.Bind(unit, Dialect.Pb35);

    var hir = BoundAstToHir.Lower(model);

    Assert.Multiple(() => {
      Assert.That(hir.EntryPoint.Body, Has.Count.EqualTo(1));
      Assert.That(hir.EntryPoint.Body[0], Is.TypeOf<HirAssignmentStatement>());
      Assert.That(HirVerifier.Verify(hir), Is.Empty);
      var mir = HirToMir.Lower(hir);
      Assert.That(mir, Is.Not.Null);
      Assert.That(mir!.RepresentationStage, Is.EqualTo(IrRepresentationStage.Mir));
      Assert.That(MirVerifier.Verify(mir), Is.Empty);
      Assert.That(IrVerifier.Verify(mir), Is.Empty);
    });
  }

  [Test]
  public void Lower_GivenGroupedBoundStatements_ThenItProducesCanonicalHirAndIrLoweringConsumesIt() {
    var position = new SourcePosition("HIR.BAS", 1, 1);
    var model = new SemanticModel { FileName = "HIR.BAS" };
    model.MainBody.Add(new GroupStmt(position, [new EndStmt(position, null), new EndStmt(position, null)]));

    var hir = BoundAstToHir.Lower(model);
    var lowered = HirToMir.Lower(hir, out var declinedBecause);

    Assert.Multiple(() => {
      Assert.That(hir.Functions, Has.Count.EqualTo(1));
      Assert.That(hir.EntryPoint.IsEntryPoint, Is.True);
      Assert.That(hir.EntryPoint.Body, Has.Count.EqualTo(2), "statement groups are flattened at the HIR boundary");
      Assert.That(hir.EntryPoint.Body[0], Is.TypeOf<HirEndStatement>());
      Assert.That(hir.EntryPoint.Body[1], Is.TypeOf<HirEndStatement>());
      Assert.That(HirVerifier.Verify(hir), Is.Empty);
      Assert.That(lowered, Is.Not.Null, declinedBecause);
      Assert.That(IrVerifier.Verify(lowered!), Is.Empty);
    });
  }

  [Test]
  public void Pipeline_GivenMirModule_ThenLegalizationRunsAfterTheExplicitSsaFormationBoundary() {
    const string source = "x% = 1";
    var unit = Parser.Parse(Lexer.Tokenize(source, "HIR.BAS", Dialect.Pb35), "HIR.BAS", Dialect.Pb35);
    var module = IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35))!;

    IrMiddleEndPipeline.RunHostedModule(module, optimize: false);

    Assert.Multiple(() => {
      Assert.That(module.RepresentationStage, Is.EqualTo(IrRepresentationStage.Ssa));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void Lower_GivenProceduresPresentInSeveralBinderCollections_ThenEachAppearsOnceInHir() {
    var procedure = new ProcedureSymbol("Worker", isFunction: false) { Body = [] };
    var model = new SemanticModel { FileName = "HIR.BAS" };
    model.Procedures.Add(procedure.Name, procedure);
    model.ProcedureList.Add(procedure);

    var hir = BoundAstToHir.Lower(model);

    Assert.Multiple(() => {
      Assert.That(hir.Functions, Has.Count.EqualTo(2));
      Assert.That(hir.Functions[1].Procedure, Is.SameAs(procedure));
      Assert.That(HirVerifier.Verify(hir), Is.Empty);
    });
  }
}
