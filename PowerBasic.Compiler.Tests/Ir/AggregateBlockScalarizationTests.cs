using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class AggregateBlockScalarizationTests {

  private static IrModule Optimize(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var model = Binder.Bind(unit, Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");

    var pipeline = IrPassManager.Standard();
    pipeline.VerifyEachPass = true;
    pipeline.RunOnModule(module!);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    return module!;
  }

  [Test]
  public void Pipeline_GivenByValRecordAliasedByAnotherParameter_WhenCopyScalarizes_ThenSnapshotLoadsStayAtEntry() {
    // Given a BYVAL record whose incoming pointer aliases a separate BYREF parameter at the call site.
    // The callee mutates the BYREF object before reading the BYVAL fields, so replacing the entry copy
    // with loads at the later field-use sites would be observably wrong.
    var module = Optimize("""
      TYPE Point
        X AS INTEGER
        Y AS INTEGER
      END TYPE
      SUB Change(q AS Point)
        q.X = 99
      END SUB
      DIM r AS Point
      r.X = 3
      r.Y = 4
      PRINT Snapshot%(r, r)
      END
      FUNCTION Snapshot%(BYVAL p AS Point, q AS Point)
        CALL Change(q)
        Snapshot% = p.X + p.Y
      END FUNCTION
      """);
    var snapshot = module.Functions.Single(f => f.Name.Equals("Snapshot", StringComparison.OrdinalIgnoreCase));
    var incoming = snapshot.Parameters[0];
    var instructions = snapshot.AllInstructions.ToList();
    var mutation = instructions.OfType<IrCall>().Single(c => c.Callee is IrFunction { Name: "Change" });
    var mutationIndex = instructions.IndexOf(mutation);
    var snapshotLoads = instructions.OfType<IrLoad>()
      .Where(load => ReferencesBase(load.Pointer, incoming))
      .ToList();

    // Then the block copy itself is gone, but its semantics are not: all four bytes are captured from
    // the incoming record before Change can mutate the aliased caller object, and the local packed
    // temporary is eliminated after those entry-point loads become SSA values.
    Assert.Multiple(() => {
      Assert.That(snapshot.AllInstructions.OfType<IrCall>().Any(c => c.Callee is IrFunction { Name: "llvm.memcpy.p0.p0.i32" }), Is.False);
      Assert.That(snapshot.AllInstructions.OfType<IrAlloca>().Any(a => a.Allocated == IrType.I8 && a.Count == 4), Is.False);
      Assert.That(snapshotLoads, Has.Count.EqualTo(2));
      Assert.That(snapshotLoads.All(load => instructions.IndexOf(load) < mutationIndex), Is.True);
    });
  }

  private static bool ReferencesBase(IrValue pointer, IrValue expected)
    => ReferenceEquals(pointer, expected)
       || pointer is IrGep gep && ReferenceEquals(gep.BasePtr, expected);
}
