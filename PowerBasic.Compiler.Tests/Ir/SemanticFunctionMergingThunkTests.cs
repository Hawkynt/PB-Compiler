using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class SemanticFunctionMergingThunkTests {

  [Test]
  public void EntryThunks_GivenEscapedProcedureAddress_ThenPublicAbiIsPreserved() {
    var module = new IrModule("o0284-thunks");
    var first = AddVariant(module, "First", 3);
    var second = AddVariant(module, "Second", 7);
    var sink = module.AddFunction(new IrFunction("Sink", IrType.Void,
      [new IrArgument(IrType.Ptr, 0, "p")]));
    var main = AddCaller(module, first, second);
    main.Entry!.InsertBefore(new IrCall(IrType.Void, sink, [first]), main.Entry.Terminator!);

    var result = SemanticFunctionMerging.RunWithEntryThunks(module);

    var helper = result.Helpers.Single();
    var firstThunk = first.AllInstructions.ToList();
    var secondThunk = second.AllInstructions.ToList();
    var mainCalls = main.AllInstructions.OfType<IrCall>().ToList();
    Assert.Multiple(() => {
      Assert.That(result.MergedBodies, Is.EqualTo(1));
      Assert.That(first.Parameters, Has.Count.EqualTo(1));
      Assert.That(second.Parameters, Has.Count.EqualTo(1));
      Assert.That(helper.Parameters, Has.Count.EqualTo(2));
      Assert.That(firstThunk, Has.Count.EqualTo(2));
      Assert.That(secondThunk, Has.Count.EqualTo(2));
      Assert.That(((IrCall)firstThunk[0]).Callee, Is.SameAs(helper));
      Assert.That(((IrCall)secondThunk[0]).Callee, Is.SameAs(helper));
      Assert.That(((IrConstantInt)((IrCall)firstThunk[0]).Args.Last()).Value, Is.EqualTo(3));
      Assert.That(((IrConstantInt)((IrCall)secondThunk[0]).Args.Last()).Value, Is.EqualTo(7));
      Assert.That(mainCalls.Any(call => ReferenceEquals(call.Callee, first)), Is.True,
        "visible callers still use the original entrypoint");
      Assert.That(mainCalls.Any(call => ReferenceEquals(call.Callee, second)), Is.True);
      Assert.That(mainCalls.Any(call => call.Args.Any(arg => ReferenceEquals(arg, first))), Is.True,
        "an escaped address still names the ABI-preserving entry thunk");
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void EntryThunks_GivenRecursiveVariants_ThenHelperForwardsCurrentContext() {
    var module = new IrModule("o0284-recursive-thunks");
    var first = AddRecursiveVariant(module, "First", 3);
    var second = AddRecursiveVariant(module, "Second", 7);
    AddCaller(module, first, second);

    var result = SemanticFunctionMerging.RunWithEntryThunks(module);

    var helper = result.Helpers.Single();
    var recursive = helper.AllInstructions.OfType<IrCall>()
      .Single(call => ReferenceEquals(call.Callee, helper));
    Assert.Multiple(() => {
      Assert.That(recursive.ArgCount, Is.EqualTo(helper.Parameters.Count));
      Assert.That(recursive.Args.Last(), Is.SameAs(helper.Parameters[^1]));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void EntryThunks_GivenVaryingCallTargetAndNativeFence_ThenMergeIsDeclined() {
    var module = new IrModule("o0284-native-call-target-fence");
    var plus = AddDeclaration(module, "Plus");
    var minus = AddDeclaration(module, "Minus");
    var first = AddCallVariant(module, "First", plus);
    var second = AddCallVariant(module, "Second", minus);
    AddCaller(module, first, second);

    var result = SemanticFunctionMerging.RunWithEntryThunks(module, allowCallTargetDifferences: false);

    Assert.Multiple(() => {
      Assert.That(result.MergedBodies, Is.Zero);
      Assert.That(result.Helpers, Is.Empty);
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void EntryThunks_GivenVaryingCallTargetAndTargetFilterRejectsOne_ThenMergeIsDeclined() {
    var module = new IrModule("o0284-native-call-target-filter");
    var plus = AddDeclaration(module, "Plus");
    var minus = AddDeclaration(module, "Minus");
    var first = AddCallVariant(module, "First", plus);
    var second = AddCallVariant(module, "Second", minus);
    AddCaller(module, first, second);

    var result = SemanticFunctionMerging.RunWithEntryThunks(module,
      callTargetFilter: target => ReferenceEquals(target, plus));

    Assert.Multiple(() => {
      Assert.That(result.MergedBodies, Is.Zero);
      Assert.That(result.Helpers, Is.Empty);
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void EntryThunks_GivenBackendCandidateFilter_ThenRejectedFunctionDoesNotParticipate() {
    var module = new IrModule("o0284-thunk-filter");
    var first = AddVariant(module, "First", 3);
    var second = AddVariant(module, "Second", 7);
    AddCaller(module, first, second);

    var result = SemanticFunctionMerging.RunWithEntryThunks(module,
      candidateFilter: function => !ReferenceEquals(function, second));

    Assert.That(result.MergedBodies, Is.Zero);
  }

  private static IrFunction AddVariant(IrModule module, string name, long context) {
    var x = new IrArgument(IrType.I16, 0, "x");
    var function = module.AddFunction(new IrFunction(name, IrType.I16, [x]));
    var entry = function.CreateBlock("entry");
    IrValue value = entry.Append(new IrBinary(IrBinaryOp.Add, x, new IrConstantInt(IrType.I16, context)));
    for (var i = 0; i < 7; ++i)
      value = entry.Append(new IrBinary(i % 2 == 0 ? IrBinaryOp.Xor : IrBinaryOp.Add,
        value, new IrConstantInt(IrType.I16, 0x101 + i)));
    entry.Append(new IrRet(value));
    return function;
  }

  private static IrFunction AddRecursiveVariant(IrModule module, string name, long context) {
    var x = new IrArgument(IrType.I16, 0, "x");
    var function = module.AddFunction(new IrFunction(name, IrType.I16, [x]));
    var entry = function.CreateBlock("entry");
    IrValue value = entry.Append(new IrBinary(IrBinaryOp.Add, x, new IrConstantInt(IrType.I16, context)));
    for (var i = 0; i < 6; ++i)
      value = entry.Append(new IrBinary(IrBinaryOp.Xor, value, new IrConstantInt(IrType.I16, 0x20 + i)));
    var recursive = entry.Append(new IrCall(IrType.I16, function, [value]));
    entry.Append(new IrRet(recursive));
    return function;
  }

  private static IrFunction AddDeclaration(IrModule module, string name)
    => module.AddFunction(new IrFunction(name, IrType.I16,
      [new IrArgument(IrType.I16, 0, "x")]));

  private static IrFunction AddCallVariant(IrModule module, string name, IrFunction target) {
    var x = new IrArgument(IrType.I16, 0, "x");
    var function = module.AddFunction(new IrFunction(name, IrType.I16, [x]));
    var entry = function.CreateBlock("entry");
    IrValue value = entry.Append(new IrCall(IrType.I16, target, [x]));
    for (var i = 0; i < 7; ++i)
      value = entry.Append(new IrBinary(IrBinaryOp.Xor, value, new IrConstantInt(IrType.I16, 0x40 + i)));
    entry.Append(new IrRet(value));
    return function;
  }

  private static IrFunction AddCaller(IrModule module, params IrFunction[] callees) {
    var x = new IrArgument(IrType.I16, 0, "x");
    var main = module.AddFunction(new IrFunction("main", IrType.Void, [x]));
    var entry = main.CreateBlock("entry");
    foreach (var callee in callees)
      entry.Append(new IrCall(callee.ReturnType, callee, [x]));
    entry.Append(new IrRet());
    return main;
  }
}
