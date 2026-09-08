using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0280 — fully visible read-only aggregate parameters reduced to scalar field arguments.</summary>
[TestFixture]
public sealed class ArgumentStructureReductionTests {

  [Test]
  public void AggregateParameter_GivenTwoReadOnlyFields_WhenRun_ThenSignatureAndCallUseScalars() {
    var module = new IrModule("test");
    var recordParameter = new IrArgument(IrType.Ptr, 0, "r");
    var scaleParameter = new IrArgument(IrType.I16, 1, "scale");
    var area = module.AddFunction(new IrFunction("Area", IrType.I16, [recordParameter, scaleParameter]));
    var areaEntry = area.CreateBlock("entry");
    var width = areaEntry.Append(new IrLoad(IrType.I16,
      areaEntry.Append(new IrGep(recordParameter, new IrConstantInt(IrType.I32, 4)))));
    var height = areaEntry.Append(new IrLoad(IrType.I16,
      areaEntry.Append(new IrGep(recordParameter, new IrConstantInt(IrType.I32, 6)))));
    var product = areaEntry.Append(new IrBinary(IrBinaryOp.Mul, width, height));
    var scaled = areaEntry.Append(new IrBinary(IrBinaryOp.Mul, product, scaleParameter));
    areaEntry.Append(new IrRet(scaled));

    var caller = module.AddFunction(new IrFunction("main", IrType.I16));
    var callerEntry = caller.CreateBlock("entry");
    var record = callerEntry.Append(new IrAlloca(IrType.I8) { Count = 8, Name = "rect" });
    callerEntry.Append(new IrStore(new IrConstantInt(IrType.I16, 3),
      callerEntry.Append(new IrGep(record, new IrConstantInt(IrType.I32, 4)))));
    callerEntry.Append(new IrStore(new IrConstantInt(IrType.I16, 5),
      callerEntry.Append(new IrGep(record, new IrConstantInt(IrType.I32, 6)))));
    var originalCall = callerEntry.Append(new IrCall(IrType.I16, area,
      [record, new IrConstantInt(IrType.I16, 2)], IrCallConvention.Cdecl) { Name = "area" });
    callerEntry.Append(new IrRet(originalCall));
    Assert.That(IrVerifier.Verify(module), Is.Empty, "test setup must start from valid IR");

    Assert.That(ArgumentStructureReduction.Run(module), Is.EqualTo(1));

    var call = caller.AllInstructions.OfType<IrCall>().Single();
    var arguments = call.Args.ToArray();
    Assert.Multiple(() => {
      Assert.That(area.Parameters.Select(parameter => parameter.Type),
        Is.EqualTo(new[] { IrType.I16, IrType.I16, IrType.I16 }));
      Assert.That(area.Parameters.Select(parameter => parameter.Index), Is.EqualTo(new[] { 0, 1, 2 }));
      Assert.That(area.Parameters.Select(parameter => parameter.Name),
        Is.EqualTo(new[] { "r.4", "r.6", "scale" }));
      Assert.That(arguments, Has.Length.EqualTo(3));
      Assert.That(arguments[0], Is.TypeOf<IrLoad>());
      Assert.That(arguments[1], Is.TypeOf<IrLoad>());
      Assert.That(arguments[2], Is.TypeOf<IrConstantInt>());
      Assert.That(call.Convention, Is.EqualTo(IrCallConvention.Cdecl));
      Assert.That(call.Name, Is.EqualTo("area"));
      Assert.That(recordParameter.Parent, Is.Null);
      Assert.That(scaleParameter.Parent, Is.Null);
      Assert.That(originalCall.Parent, Is.Null);
      Assert.That(area.AllInstructions.OfType<IrLoad>(), Is.Empty);
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
    Assert.That(ArgumentStructureReduction.Run(module), Is.Zero, "the signature rewrite must be idempotent");
  }

  [Test]
  public void AggregateParameter_GivenRepeatedLoadsOfOneField_WhenRun_ThenOneScalarServesEveryUse() {
    var module = new IrModule("test");
    var recordParameter = new IrArgument(IrType.Ptr, 0, "r");
    var function = module.AddFunction(new IrFunction("Twice", IrType.I16, [recordParameter]));
    var entry = function.CreateBlock("entry");
    var fieldAddress = entry.Append(new IrGep(recordParameter, new IrConstantInt(IrType.I32, 0)));
    var first = entry.Append(new IrLoad(IrType.I16, fieldAddress));
    var second = entry.Append(new IrLoad(IrType.I16, fieldAddress));
    var sum = entry.Append(new IrBinary(IrBinaryOp.Add, first, second));
    entry.Append(new IrRet(sum));

    var caller = module.AddFunction(new IrFunction("main", IrType.I16));
    var callerEntry = caller.CreateBlock("entry");
    var record = callerEntry.Append(new IrAlloca(IrType.I8) { Count = 2 });
    callerEntry.Append(new IrStore(new IrConstantInt(IrType.I16, 7), record));
    var call = callerEntry.Append(new IrCall(IrType.I16, function, [record]));
    callerEntry.Append(new IrRet(call));

    Assert.That(ArgumentStructureReduction.Run(module), Is.EqualTo(1));

    Assert.Multiple(() => {
      Assert.That(function.Parameters, Has.Count.EqualTo(1));
      Assert.That(function.Parameters[0].Type, Is.EqualTo(IrType.I16));
      Assert.That(sum.Lhs, Is.SameAs(function.Parameters[0]));
      Assert.That(sum.Rhs, Is.SameAs(function.Parameters[0]));
      Assert.That(function.AllInstructions.OfType<IrLoad>(), Is.Empty);
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void AggregateParameter_GivenTheFunctionAddressEscapes_WhenRun_ThenSignatureStaysUnchanged() {
    var module = new IrModule("test");
    var recordParameter = new IrArgument(IrType.Ptr, 0, "r");
    var function = module.AddFunction(new IrFunction("Read", IrType.I16, [recordParameter]));
    var entry = function.CreateBlock("entry");
    var value = entry.Append(new IrLoad(IrType.I16, recordParameter));
    entry.Append(new IrRet(value));

    var caller = module.AddFunction(new IrFunction("main", IrType.I16));
    var callerEntry = caller.CreateBlock("entry");
    var record = callerEntry.Append(new IrAlloca(IrType.I8) { Count = 2 });
    var functionCell = callerEntry.Append(new IrAlloca(IrType.Ptr));
    callerEntry.Append(new IrStore(function, functionCell));
    var call = callerEntry.Append(new IrCall(IrType.I16, function, [record]));
    callerEntry.Append(new IrRet(call));

    Assert.That(ArgumentStructureReduction.Run(module), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(function.Parameters, Has.Count.EqualTo(1));
      Assert.That(function.Parameters[0], Is.SameAs(recordParameter));
      Assert.That(call.Parent, Is.SameAs(callerEntry));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void AggregateParameter_GivenDisjointWriteThroughAnotherPointer_WhenRun_ThenModRefProvesItSafe() {
    var module = new IrModule("test");
    var recordParameter = new IrArgument(IrType.Ptr, 0, "r");
    var otherParameter = new IrArgument(IrType.Ptr, 1, "other");
    var function = module.AddFunction(new IrFunction("ReadAfterWrite", IrType.I16,
      [recordParameter, otherParameter]));
    var entry = function.CreateBlock("entry");
    entry.Append(new IrStore(new IrConstantInt(IrType.I16, 9), otherParameter));
    var value = entry.Append(new IrLoad(IrType.I16, recordParameter));
    entry.Append(new IrRet(value));

    var caller = module.AddFunction(new IrFunction("main", IrType.I16));
    var callerEntry = caller.CreateBlock("entry");
    var record = callerEntry.Append(new IrAlloca(IrType.I8) { Count = 2 });
    var other = callerEntry.Append(new IrAlloca(IrType.I16));
    var call = callerEntry.Append(new IrCall(IrType.I16, function, [record, other]));
    callerEntry.Append(new IrRet(call));

    Assert.That(ArgumentStructureReduction.Run(module), Is.EqualTo(1));
    Assert.Multiple(() => {
      Assert.That(function.Parameters[0].Type, Is.EqualTo(IrType.I16));
      Assert.That(function.Parameters[1].Type, Is.EqualTo(IrType.Ptr));
      Assert.That(function.AllInstructions.OfType<IrLoad>(), Is.Empty);
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void AggregateParameter_GivenAliasingWriteThroughAnotherPointer_WhenRun_ThenItDeclines() {
    var module = new IrModule("test");
    var recordParameter = new IrArgument(IrType.Ptr, 0, "r");
    var otherParameter = new IrArgument(IrType.Ptr, 1, "other");
    var function = module.AddFunction(new IrFunction("ReadAfterWrite", IrType.I16,
      [recordParameter, otherParameter]));
    var entry = function.CreateBlock("entry");
    entry.Append(new IrStore(new IrConstantInt(IrType.I16, 9), otherParameter));
    var value = entry.Append(new IrLoad(IrType.I16, recordParameter));
    entry.Append(new IrRet(value));

    var caller = module.AddFunction(new IrFunction("main", IrType.I16));
    var callerEntry = caller.CreateBlock("entry");
    var record = callerEntry.Append(new IrAlloca(IrType.I8) { Count = 2 });
    var call = callerEntry.Append(new IrCall(IrType.I16, function, [record, record]));
    callerEntry.Append(new IrRet(call));

    Assert.That(ArgumentStructureReduction.Run(module), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(function.Parameters[0], Is.SameAs(recordParameter));
      Assert.That(call.Parent, Is.SameAs(callerEntry));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void AggregateParameter_GivenAReadOnlyNestedCall_WhenRun_ThenTheNonLeafCalleeScalarizes() {
    var module = new IrModule("test");
    var helperArg = new IrArgument(IrType.I16, 0, "v");
    var helper = module.AddFunction(new IrFunction("Helper", IrType.I16, [helperArg]));
    var helperEntry = helper.CreateBlock("entry");
    helperEntry.Append(new IrRet(helperEntry.Append(new IrBinary(
      IrBinaryOp.Add, helperArg, new IrConstantInt(IrType.I16, 1)))));

    var recordParameter = new IrArgument(IrType.Ptr, 0, "r");
    var function = module.AddFunction(new IrFunction("Read", IrType.I16, [recordParameter]));
    var entry = function.CreateBlock("entry");
    var helperCall = entry.Append(new IrCall(IrType.I16, helper, [new IrConstantInt(IrType.I16, 3)]));
    var value = entry.Append(new IrLoad(IrType.I16, recordParameter));
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.Add, helperCall, value))));

    var caller = module.AddFunction(new IrFunction("main", IrType.I16));
    var callerEntry = caller.CreateBlock("entry");
    var record = callerEntry.Append(new IrAlloca(IrType.I8) { Count = 2 });
    var call = callerEntry.Append(new IrCall(IrType.I16, function, [record]));
    callerEntry.Append(new IrRet(call));

    Assert.That(ArgumentStructureReduction.Run(module), Is.EqualTo(1));
    Assert.Multiple(() => {
      Assert.That(function.Parameters.Single().Type, Is.EqualTo(IrType.I16));
      Assert.That(helperCall.Parent, Is.SameAs(entry));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void AggregateParameter_GivenNestedWriterToDisjointLocal_WhenRun_ThenRecursiveModRefProvesItSafe() {
    var module = new IrModule("test");
    var destination = new IrArgument(IrType.Ptr, 0, "p");
    var writer = module.AddFunction(new IrFunction("Writer", IrType.Void, [destination]));
    var writerEntry = writer.CreateBlock("entry");
    writerEntry.Append(new IrStore(new IrConstantInt(IrType.I16, 11), destination));
    writerEntry.Append(new IrRet());

    var recordParameter = new IrArgument(IrType.Ptr, 0, "r");
    var function = module.AddFunction(new IrFunction("Read", IrType.I16, [recordParameter]));
    var entry = function.CreateBlock("entry");
    var scratch = entry.Append(new IrAlloca(IrType.I16));
    entry.Append(new IrCall(IrType.Void, writer, [scratch]));
    var value = entry.Append(new IrLoad(IrType.I16, recordParameter));
    entry.Append(new IrRet(value));

    var caller = module.AddFunction(new IrFunction("main", IrType.I16));
    var callerEntry = caller.CreateBlock("entry");
    var record = callerEntry.Append(new IrAlloca(IrType.I8) { Count = 2 });
    var call = callerEntry.Append(new IrCall(IrType.I16, function, [record]));
    callerEntry.Append(new IrRet(call));

    Assert.That(ArgumentStructureReduction.Run(module), Is.EqualTo(1));
    Assert.Multiple(() => {
      Assert.That(function.Parameters.Single().Type, Is.EqualTo(IrType.I16));
      Assert.That(function.AllInstructions.OfType<IrCall>(), Has.Count.EqualTo(1));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void AggregateParameter_GivenNestedWriterToTheAggregate_WhenRun_ThenItDeclines() {
    var module = new IrModule("test");
    var destination = new IrArgument(IrType.Ptr, 0, "p");
    var writer = module.AddFunction(new IrFunction("Writer", IrType.Void, [destination]));
    var writerEntry = writer.CreateBlock("entry");
    writerEntry.Append(new IrStore(new IrConstantInt(IrType.I16, 11), destination));
    writerEntry.Append(new IrRet());

    var recordParameter = new IrArgument(IrType.Ptr, 0, "r");
    var function = module.AddFunction(new IrFunction("Read", IrType.I16, [recordParameter]));
    var entry = function.CreateBlock("entry");
    entry.Append(new IrCall(IrType.Void, writer, [recordParameter]));
    var value = entry.Append(new IrLoad(IrType.I16, recordParameter));
    entry.Append(new IrRet(value));

    var caller = module.AddFunction(new IrFunction("main", IrType.I16));
    var callerEntry = caller.CreateBlock("entry");
    var record = callerEntry.Append(new IrAlloca(IrType.I8) { Count = 2 });
    var call = callerEntry.Append(new IrCall(IrType.I16, function, [record]));
    callerEntry.Append(new IrRet(call));

    Assert.That(ArgumentStructureReduction.Run(module), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(function.Parameters[0], Is.SameAs(recordParameter));
      Assert.That(call.Parent, Is.SameAs(callerEntry));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void AggregateParameter_GivenForwardedAggregateWithVisibleRoot_WhenRun_ThenTheChainScalarizes() {
    var module = new IrModule("test");
    var leafParameter = new IrArgument(IrType.Ptr, 0, "r");
    var leaf = module.AddFunction(new IrFunction("Leaf", IrType.I16, [leafParameter]));
    var leafEntry = leaf.CreateBlock("entry");
    leafEntry.Append(new IrRet(leafEntry.Append(new IrLoad(IrType.I16, leafParameter))));

    var forwarded = new IrArgument(IrType.Ptr, 0, "p");
    var wrapper = module.AddFunction(new IrFunction("Wrapper", IrType.I16, [forwarded]));
    var wrapperEntry = wrapper.CreateBlock("entry");
    var leafCall = wrapperEntry.Append(new IrCall(IrType.I16, leaf, [forwarded]));
    wrapperEntry.Append(new IrRet(leafCall));

    var main = module.AddFunction(new IrFunction("main", IrType.I16));
    var mainEntry = main.CreateBlock("entry");
    var record = mainEntry.Append(new IrAlloca(IrType.I8) { Count = 2 });
    var wrapperCall = mainEntry.Append(new IrCall(IrType.I16, wrapper, [record]));
    mainEntry.Append(new IrRet(wrapperCall));

    Assert.That(ArgumentStructureReduction.Run(module), Is.EqualTo(2));

    var rewrittenLeafCall = wrapper.AllInstructions.OfType<IrCall>().Single();
    Assert.Multiple(() => {
      Assert.That(leaf.Parameters.Single().Type, Is.EqualTo(IrType.I16));
      Assert.That(wrapper.Parameters.Single().Type, Is.EqualTo(IrType.I16));
      Assert.That(rewrittenLeafCall.Args.Single(), Is.SameAs(wrapper.Parameters[0]));
      Assert.That(main.AllInstructions.OfType<IrCall>().Single().Args.Single(), Is.TypeOf<IrLoad>());
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void AggregateParameter_GivenGlobalAggregate_WhenRun_ThenGlobalProvenanceIsAccepted() {
    var module = new IrModule("test");
    var global = module.AddGlobal(new IrGlobalVariable("rect", IrType.I8) { Count = 4 });
    var recordParameter = new IrArgument(IrType.Ptr, 0, "r");
    var function = module.AddFunction(new IrFunction("Read", IrType.I16, [recordParameter]));
    var entry = function.CreateBlock("entry");
    var field = entry.Append(new IrGep(recordParameter, new IrConstantInt(IrType.I32, 2)));
    entry.Append(new IrRet(entry.Append(new IrLoad(IrType.I16, field))));

    var caller = module.AddFunction(new IrFunction("main", IrType.I16));
    var callerEntry = caller.CreateBlock("entry");
    var call = callerEntry.Append(new IrCall(IrType.I16, function, [global]));
    callerEntry.Append(new IrRet(call));

    Assert.That(ArgumentStructureReduction.Run(module), Is.EqualTo(1));
    var rewritten = caller.AllInstructions.OfType<IrCall>().Single();
    Assert.Multiple(() => {
      Assert.That(function.Parameters.Single().Type, Is.EqualTo(IrType.I16));
      Assert.That(rewritten.Args.Single(), Is.TypeOf<IrLoad>());
      Assert.That(((IrLoad)rewritten.Args.Single()).Pointer, Is.TypeOf<IrGep>());
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void AggregateParameter_GivenByValEntryCopy_WhenRun_ThenCopyInIsReplacedByScalarSnapshot() {
    var module = new IrModule("test");
    var memcpy = module.AddFunction(new IrFunction("llvm.memcpy.p0.p0.i32", IrType.Void, [
      new IrArgument(IrType.Ptr, 0, "dst"),
      new IrArgument(IrType.Ptr, 1, "src"),
      new IrArgument(IrType.I32, 2, "n"),
      new IrArgument(IrType.I1, 3, "volatile"),
    ]));

    var recordParameter = new IrArgument(IrType.Ptr, 0, "r");
    var function = module.AddFunction(new IrFunction("ReadByVal", IrType.I16, [recordParameter]));
    var entry = function.CreateBlock("entry");
    var copyStorage = entry.Append(new IrAlloca(IrType.I8) { Count = 4, Name = "r" });
    var copy = entry.Append(new IrCall(IrType.Void, memcpy, [
      copyStorage, recordParameter, new IrConstantInt(IrType.I32, 4), new IrConstantInt(IrType.I1, 0),
    ]));
    var field = entry.Append(new IrGep(copyStorage, new IrConstantInt(IrType.I32, 2)));
    entry.Append(new IrRet(entry.Append(new IrLoad(IrType.I16, field))));

    var caller = module.AddFunction(new IrFunction("main", IrType.I16));
    var callerEntry = caller.CreateBlock("entry");
    var record = callerEntry.Append(new IrAlloca(IrType.I8) { Count = 4 });
    var call = callerEntry.Append(new IrCall(IrType.I16, function, [record]));
    callerEntry.Append(new IrRet(call));
    Assert.That(IrVerifier.Verify(module), Is.Empty, "test setup must start from valid IR");

    Assert.That(ArgumentStructureReduction.Run(module), Is.EqualTo(1));

    var rewritten = caller.AllInstructions.OfType<IrCall>().Single();
    Assert.Multiple(() => {
      Assert.That(function.Parameters.Single().Type, Is.EqualTo(IrType.I16));
      Assert.That(copy.Parent, Is.Null);
      Assert.That(copyStorage.Parent, Is.Null);
      Assert.That(function.AllInstructions.OfType<IrLoad>(), Is.Empty);
      Assert.That(rewritten.Args.Single(), Is.TypeOf<IrLoad>());
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void AggregateParameter_GivenAnUnprovenForwardedPointerActual_WhenRun_ThenItIsNotAssumedToBeARecord() {
    var module = new IrModule("test");
    var recordParameter = new IrArgument(IrType.Ptr, 0, "r");
    var function = module.AddFunction(new IrFunction("Read", IrType.I16, [recordParameter]));
    var entry = function.CreateBlock("entry");
    var value = entry.Append(new IrLoad(IrType.I16, recordParameter));
    entry.Append(new IrRet(value));

    var forwarded = new IrArgument(IrType.Ptr, 0, "p");
    var caller = module.AddFunction(new IrFunction("caller", IrType.I16, [forwarded]));
    var callerEntry = caller.CreateBlock("entry");
    var call = callerEntry.Append(new IrCall(IrType.I16, function, [forwarded]));
    callerEntry.Append(new IrRet(call));

    Assert.That(ArgumentStructureReduction.Run(module), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(function.Parameters[0], Is.SameAs(recordParameter));
      Assert.That(call.Args.Single(), Is.SameAs(forwarded));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void AggregateParameter_GivenMoreThanThreeScalarPieces_WhenRun_ThenProfitabilityLimitDeclines() {
    var module = new IrModule("test");
    var recordParameter = new IrArgument(IrType.Ptr, 0, "r");
    var function = module.AddFunction(new IrFunction("Sum", IrType.I16, [recordParameter]));
    var entry = function.CreateBlock("entry");
    var values = new IrValue[4];
    for (var i = 0; i < values.Length; ++i)
      values[i] = entry.Append(new IrLoad(IrType.I16,
        entry.Append(new IrGep(recordParameter, new IrConstantInt(IrType.I32, i * 2)))));
    var firstPair = entry.Append(new IrBinary(IrBinaryOp.Add, values[0], values[1]));
    var secondPair = entry.Append(new IrBinary(IrBinaryOp.Add, values[2], values[3]));
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.Add, firstPair, secondPair))));

    var caller = module.AddFunction(new IrFunction("main", IrType.I16));
    var callerEntry = caller.CreateBlock("entry");
    var record = callerEntry.Append(new IrAlloca(IrType.I8) { Count = 8 });
    var call = callerEntry.Append(new IrCall(IrType.I16, function, [record]));
    callerEntry.Append(new IrRet(call));

    Assert.That(ArgumentStructureReduction.Run(module), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(function.Parameters[0], Is.SameAs(recordParameter));
      Assert.That(function.AllInstructions.OfType<IrLoad>(), Has.Count.EqualTo(4));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }
}
