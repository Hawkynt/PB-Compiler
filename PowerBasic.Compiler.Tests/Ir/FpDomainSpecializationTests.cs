using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0343 — range-driven transcendental specialization.</summary>
[TestFixture]
public sealed class FpDomainSpecializationTests {

  [Test]
  public void Lookup_GivenWideSignedDomainWithAtMost256Values_ThenUsesTypedTable() {
    var module = new IrModule("test");
    var n = new IrArgument(IrType.I32, 0, "n");
    var function = module.AddFunction(new IrFunction("f", IrType.F64, [n]));
    var sin = module.AddFunction(new IrFunction("llvm.sin.f64", IrType.F64,
      [new IrArgument(IrType.F64, 0)]));
    var block = function.CreateBlock("entry");
    var masked = block.Append(new IrBinary(IrBinaryOp.And, n, new IrConstantInt(IrType.I32, 63)));
    var shifted = block.Append(new IrBinary(IrBinaryOp.Sub, masked, new IrConstantInt(IrType.I32, 32)));
    var x = block.Append(new IrCast(IrCastOp.SIToFP, shifted, IrType.F64));
    var call = block.Append(new IrCall(IrType.F64, sin, [x]));
    block.Append(new IrRet(call));

    Assert.That(FpDomainSpecialization.Run(module, allowLookupTables: true), Is.EqualTo(1));

    var table = module.Globals.Single(global => global.Name.StartsWith(".fplut.sin.", StringComparison.Ordinal));
    var gep = function.AllInstructions.OfType<IrGep>().Single();
    var normalizedIndex = function.AllInstructions.OfType<IrCast>()
      .Single(cast => cast.Op == IrCastOp.Trunc && cast.Type.Equals(IrType.U16));
    Assert.Multiple(() => {
      Assert.That(table.Count, Is.EqualTo(64));
      Assert.That(table.FloatingValues, Has.Length.EqualTo(64));
      Assert.That(function.AllInstructions.OfType<IrCall>(), Is.Empty);
      Assert.That(normalizedIndex.Value.Type, Is.EqualTo(IrType.I32));
      Assert.That(gep.ByteOffset, Is.SameAs(normalizedIndex));
      Assert.That(LlvmEmitter.Emit(module), Does.Contain("getelementptr double, ptr @.fplut.sin.0, i16"));
    });
  }

  [Test]
  public void Lookup_GivenWideDomainAboveEntryLimit_ThenLeavesGeneralCall() {
    var module = new IrModule("test");
    var n = new IrArgument(IrType.U32, 0, "n");
    var function = module.AddFunction(new IrFunction("f", IrType.F64, [n]));
    var sin = module.AddFunction(new IrFunction("llvm.sin.f64", IrType.F64,
      [new IrArgument(IrType.F64, 0)]));
    var block = function.CreateBlock("entry");
    var masked = block.Append(new IrBinary(IrBinaryOp.And, n, new IrConstantInt(IrType.U32, 511)));
    var x = block.Append(new IrCast(IrCastOp.UIToFP, masked, IrType.F64));
    var call = block.Append(new IrCall(IrType.F64, sin, [x]));
    block.Append(new IrRet(call));

    Assert.That(FpDomainSpecialization.Run(module, allowLookupTables: true), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(module.Globals, Is.Empty);
      Assert.That(call.Parent, Is.SameAs(block));
    });
  }
}
