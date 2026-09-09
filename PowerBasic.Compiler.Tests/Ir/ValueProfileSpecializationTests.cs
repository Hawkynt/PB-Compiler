using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0270 — guarded specialization from externally supplied argument value profiles.</summary>
[TestFixture]
public sealed class ValueProfileSpecializationTests {

  private static ValueProfileSpecialization.ArgumentProfile Profile(
      IrFunction function, int argumentIndex, long total, params (IrConstant Value, long Count)[] values)
    => new(
      function,
      argumentIndex,
      total,
      values.Select(value => new ValueProfileSpecialization.ValueCount(value.Value, value.Count)).ToArray()
    );

  [Test]
  public void Run_GivenADominantIntegerArgument_WhenRun_ThenClonesAndGuardsTheCall() {
    var module = new IrModule("test");
    var mode = new IrArgument(IrType.I16, 0, "mode");
    var data = new IrArgument(IrType.I16, 1, "data");
    var transform = module.AddFunction(new IrFunction("transform", IrType.I16, [mode, data]));
    var transformEntry = transform.CreateBlock("entry");
    transformEntry.Append(new IrRet(transformEntry.Append(new IrBinary(IrBinaryOp.Add, mode, data))));

    var runtimeMode = new IrArgument(IrType.I16, 0, "runtimeMode");
    var main = module.AddFunction(new IrFunction("main", IrType.I16, [runtimeMode]));
    var mainEntry = main.CreateBlock("entry");
    var call = mainEntry.Append(new IrCall(IrType.I16, transform, [runtimeMode, new IrConstantInt(IrType.I16, 4)]));
    mainEntry.Append(new IrRet(call));

    var changed = ValueProfileSpecialization.Run(module, [
      Profile(transform, 0, 100, (new IrConstantInt(IrType.I16, 1), 97)),
    ]);

    Assert.That(changed, Is.EqualTo(1));
    var specialized = module.Functions.Single(function => function.Name.StartsWith("transform__vp0_", StringComparison.Ordinal));
    Assert.That(mode.HasNoUsers, Is.False, "the fallback function must remain unspecialized");
    Assert.That(specialized.Parameters[0].HasNoUsers, Is.True, "the clone's hot argument must be constant-bound");

    var specializedAdd = specialized.AllInstructions.OfType<IrBinary>().Single();
    Assert.That(specializedAdd.Lhs, Is.InstanceOf<IrConstantInt>());
    Assert.That(((IrConstantInt)specializedAdd.Lhs).Value, Is.EqualTo(1));

    Assert.That(mainEntry.Terminator, Is.TypeOf<IrCondBr>());
    var calls = main.AllInstructions.OfType<IrCall>().ToList();
    Assert.That(calls.Count, Is.EqualTo(2));
    Assert.That(calls.Count(candidate => ReferenceEquals(candidate.Callee, transform)), Is.EqualTo(1));
    Assert.That(calls.Count(candidate => ReferenceEquals(candidate.Callee, specialized)), Is.EqualTo(1));

    var result = main.AllInstructions.OfType<IrPhi>().Single();
    Assert.That(result.Operands, Has.Count.EqualTo(2));
    Assert.That(IrVerifier.Verify(transform), Is.Empty);
    Assert.That(IrVerifier.Verify(specialized), Is.Empty);
    Assert.That(IrVerifier.Verify(main), Is.Empty);
  }

  [Test]
  public void Run_GivenOnlyTheHottestTrackedValue_WhenItsShareIsHigh_ThenMissingHistogramTailDoesNotBlockSpecialization() {
    var module = new IrModule("test");
    var mode = new IrArgument(IrType.I16, 0, "mode");
    var callee = module.AddFunction(new IrFunction("f", IrType.I16, [mode]));
    callee.CreateBlock("entry").Append(new IrRet(mode));

    var dynamic = new IrArgument(IrType.I16, 0, "dynamic");
    var main = module.AddFunction(new IrFunction("main", IrType.I16, [dynamic]));
    var entry = main.CreateBlock("entry");
    var call = entry.Append(new IrCall(IrType.I16, callee, [dynamic]));
    entry.Append(new IrRet(call));

    var changed = ValueProfileSpecialization.Run(module, [
      Profile(callee, 0, 1_000, (new IrConstantInt(IrType.I16, 7), 950)),
    ]);

    Assert.That(changed, Is.EqualTo(1));
    Assert.That(module.Functions.Count, Is.EqualTo(3));
    Assert.That(IrVerifier.Verify(main), Is.Empty);
  }

  [Test]
  public void Run_GivenAValueBelowTheProfitabilityThreshold_WhenRun_ThenLeavesTheCallAlone() {
    var module = new IrModule("test");
    var mode = new IrArgument(IrType.I16, 0, "mode");
    var callee = module.AddFunction(new IrFunction("f", IrType.I16, [mode]));
    callee.CreateBlock("entry").Append(new IrRet(mode));

    var dynamic = new IrArgument(IrType.I16, 0, "dynamic");
    var main = module.AddFunction(new IrFunction("main", IrType.I16, [dynamic]));
    var entry = main.CreateBlock("entry");
    var call = entry.Append(new IrCall(IrType.I16, callee, [dynamic]));
    entry.Append(new IrRet(call));

    var changed = ValueProfileSpecialization.Run(module, [
      Profile(callee, 0, 100, (new IrConstantInt(IrType.I16, 7), 89)),
    ]);

    Assert.That(changed, Is.Zero);
    Assert.That(module.Functions.Count, Is.EqualTo(2));
    Assert.That(entry.Instructions.OfType<IrCall>().Single(), Is.SameAs(call));
    Assert.That(entry.Terminator, Is.TypeOf<IrRet>());
    Assert.That(IrVerifier.Verify(main), Is.Empty);
  }

  [Test]
  public void Run_GivenNegativeZeroFloatProfile_WhenRun_ThenUsesAnExactBitGuard() {
    var module = new IrModule("test");
    var value = new IrArgument(IrType.F32, 0, "value");
    var callee = module.AddFunction(new IrFunction("f", IrType.F32, [value]));
    callee.CreateBlock("entry").Append(new IrRet(value));

    var dynamic = new IrArgument(IrType.F32, 0, "dynamic");
    var main = module.AddFunction(new IrFunction("main", IrType.F32, [dynamic]));
    var entry = main.CreateBlock("entry");
    var call = entry.Append(new IrCall(IrType.F32, callee, [dynamic]));
    entry.Append(new IrRet(call));

    var changed = ValueProfileSpecialization.Run(module, [
      Profile(callee, 0, 100, (new IrConstantFloat(IrType.F32, -0.0), 100)),
    ]);

    Assert.That(changed, Is.EqualTo(1));
    var bitcast = entry.Instructions.OfType<IrCast>().Single();
    Assert.That(bitcast.Op, Is.EqualTo(IrCastOp.BitCast));
    Assert.That(bitcast.Type, Is.EqualTo(IrType.U32));

    var comparison = entry.Instructions.OfType<IrCmp>().Single();
    Assert.That(comparison.Pred, Is.EqualTo(IrCmpPred.Eq));
    Assert.That(comparison.Rhs, Is.InstanceOf<IrConstantInt>());
    Assert.That(((IrConstantInt)comparison.Rhs).ZeroExtended, Is.EqualTo(0x8000_0000UL));
    Assert.That(IrVerifier.Verify(main), Is.Empty);
  }

  [Test]
  public void Run_GivenAnOpaqueCallee_WhenRun_ThenDeclinesSpecialization() {
    var module = new IrModule("test");
    var value = new IrArgument(IrType.I16, 0, "value");
    var callee = module.AddFunction(new IrFunction("f", IrType.I16, [value]) { HasErrorHandler = true });
    callee.CreateBlock("entry").Append(new IrRet(value));

    var main = module.AddFunction(new IrFunction("main", IrType.I16));
    var entry = main.CreateBlock("entry");
    var call = entry.Append(new IrCall(IrType.I16, callee, [new IrConstantInt(IrType.I16, 1)]));
    entry.Append(new IrRet(call));

    var changed = ValueProfileSpecialization.Run(module, [
      Profile(callee, 0, 100, (new IrConstantInt(IrType.I16, 1), 100)),
    ]);

    Assert.That(changed, Is.Zero);
    Assert.That(module.Functions.Count, Is.EqualTo(2));
    Assert.That(entry.Instructions.OfType<IrCall>().Single(), Is.SameAs(call));
  }

  [Test]
  public void Run_GivenACalleeBeyondTheCloneBudget_WhenRun_ThenDeclinesSpecialization() {
    var module = new IrModule("test");
    var value = new IrArgument(IrType.I16, 0, "value");
    var callee = module.AddFunction(new IrFunction("f", IrType.I16, [value]));
    var body = callee.CreateBlock("entry");
    var sum = body.Append(new IrBinary(IrBinaryOp.Add, value, new IrConstantInt(IrType.I16, 1)));
    body.Append(new IrRet(sum));

    var main = module.AddFunction(new IrFunction("main", IrType.I16));
    var entry = main.CreateBlock("entry");
    var call = entry.Append(new IrCall(IrType.I16, callee, [new IrConstantInt(IrType.I16, 1)]));
    entry.Append(new IrRet(call));

    var changed = ValueProfileSpecialization.Run(
      module,
      [Profile(callee, 0, 100, (new IrConstantInt(IrType.I16, 1), 100))],
      new ValueProfileSpecialization.Options { MaxCloneInstructions = 1 }
    );

    Assert.That(changed, Is.Zero);
    Assert.That(module.Functions.Count, Is.EqualTo(2));
  }
}
