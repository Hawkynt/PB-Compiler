using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class SemanticFunctionMergingTests {

  [Test]
  public void Constants_GivenOneVaryingLiteral_ThenBodiesMergeAndCallersPassContext() {
    var module = new IrModule("o0284");
    var first = AddArithmeticVariant(module, "First", 3);
    var second = AddArithmeticVariant(module, "Second", 7);
    var main = AddCaller(module, first, second);

    Assert.That(SemanticFunctionMerging.Run(module, minimumBodyInstructions: 1), Is.EqualTo(1));

    var calls = main.AllInstructions.OfType<IrCall>().ToList();
    var varyingAdd = first.AllInstructions.OfType<IrBinary>().First();
    Assert.Multiple(() => {
      Assert.That(module.Functions, Does.Contain(first));
      Assert.That(module.Functions, Does.Not.Contain(second));
      Assert.That(first.Parameters.Count, Is.EqualTo(2));
      Assert.That(varyingAdd.Rhs, Is.SameAs(first.Parameters[1]));
      Assert.That(calls, Has.Count.EqualTo(2));
      Assert.That(calls[0].Callee, Is.SameAs(first));
      Assert.That(calls[1].Callee, Is.SameAs(first));
      Assert.That(((IrConstantInt)calls[0].Args.Last()).Value, Is.EqualTo(3));
      Assert.That(((IrConstantInt)calls[1].Args.Last()).Value, Is.EqualTo(7));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void CallTargets_GivenOneVaryingDirectCallee_ThenMergedBodyUsesFunctionPointerParameter() {
    var module = new IrModule("o0284-targets");
    var plus = AddUnaryDeclaration(module, "Plus");
    var minus = AddUnaryDeclaration(module, "Minus");
    var first = AddCallVariant(module, "First", plus);
    var second = AddCallVariant(module, "Second", minus);
    var main = AddCaller(module, first, second);

    Assert.That(SemanticFunctionMerging.Run(module, minimumBodyInstructions: 1), Is.EqualTo(1));

    var inner = first.AllInstructions.OfType<IrCall>().Single();
    var calls = main.AllInstructions.OfType<IrCall>().ToList();
    Assert.Multiple(() => {
      Assert.That(inner.Callee, Is.SameAs(first.Parameters[^1]));
      Assert.That(inner.Callee.Type, Is.EqualTo(IrType.Ptr));
      Assert.That(calls[0].Args.Last(), Is.SameAs(plus));
      Assert.That(calls[1].Args.Last(), Is.SameAs(minus));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void Structure_GivenTwoVaryingOperands_ThenFunctionsStaySeparate() {
    var module = new IrModule("o0284-two-differences");
    var first = AddArithmeticVariant(module, "First", 3, tailConstant: 11);
    var second = AddArithmeticVariant(module, "Second", 7, tailConstant: 13);
    AddCaller(module, first, second);

    Assert.Multiple(() => {
      Assert.That(SemanticFunctionMerging.Run(module, minimumBodyInstructions: 1), Is.Zero);
      Assert.That(module.Functions, Does.Contain(first));
      Assert.That(module.Functions, Does.Contain(second));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void Visibility_GivenProcedureAddressEscapes_ThenFunctionIsNotMerged() {
    var module = new IrModule("o0284-escape");
    var first = AddArithmeticVariant(module, "First", 3);
    var second = AddArithmeticVariant(module, "Second", 7);
    var sink = module.AddFunction(new IrFunction("Sink", IrType.Void,
      [new IrArgument(IrType.Ptr, 0, "p")]));
    var main = AddCaller(module, first, second);
    main.Entry!.InsertBefore(new IrCall(IrType.Void, sink, [first]), main.Entry.Terminator!);

    Assert.Multiple(() => {
      Assert.That(SemanticFunctionMerging.Run(module, minimumBodyInstructions: 1), Is.Zero);
      Assert.That(module.Functions, Does.Contain(first));
      Assert.That(module.Functions, Does.Contain(second));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void Cost_GivenTinyBodies_ThenDefaultSizeThresholdDeclinesMerge() {
    var module = new IrModule("o0284-cost");
    var first = AddTinyVariant(module, "First", 1);
    var second = AddTinyVariant(module, "Second", 2);
    AddCaller(module, first, second);

    Assert.That(SemanticFunctionMerging.Run(module), Is.Zero);
  }

  [Test]
  public void Pipeline_GivenSizeObjective_ThenSemanticMergeRuns() {
    var module = new IrModule("o0284-size-pipeline");
    var first = AddArithmeticVariant(module, "First", 3);
    var second = AddArithmeticVariant(module, "Second", 7);
    AddCaller(module, first, second);

    IrPassManager.Standard(optimizeForSize: true).RunOnModule(module);

    Assert.Multiple(() => {
      Assert.That(module.Functions, Does.Contain(first));
      Assert.That(module.Functions, Does.Not.Contain(second));
      Assert.That(first.Parameters.Count, Is.EqualTo(2));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void Pipeline_GivenOrdinaryObjective_ThenSemanticMergeStaysOff() {
    var module = new IrModule("o0284-default-pipeline");
    var first = AddArithmeticVariant(module, "First", 3);
    var second = AddArithmeticVariant(module, "Second", 7);
    AddCaller(module, first, second);

    IrPassManager.Standard().RunOnModule(module);

    Assert.Multiple(() => {
      Assert.That(module.Functions, Does.Contain(first));
      Assert.That(module.Functions, Does.Contain(second));
      Assert.That(first.Parameters.Count, Is.EqualTo(1));
      Assert.That(second.Parameters.Count, Is.EqualTo(1));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  private static IrFunction AddArithmeticVariant(IrModule module, string name, long varyingConstant, long tailConstant = 11) {
    var opaque = module.FindFunction("Opaque") ?? module.AddFunction(new IrFunction("Opaque", IrType.I16,
      [new IrArgument(IrType.I16, 0, "x")]));
    var x = new IrArgument(IrType.I16, 0, "x");
    var function = module.AddFunction(new IrFunction(name, IrType.I16, [x]));
    var entry = function.CreateBlock("entry");
    IrValue value = entry.Append(new IrBinary(IrBinaryOp.Add, x, new IrConstantInt(IrType.I16, varyingConstant)));
    for (var i = 0; i < 6; ++i)
      value = entry.Append(new IrCall(IrType.I16, opaque, [value]));
    value = entry.Append(new IrBinary(IrBinaryOp.Add, value, new IrConstantInt(IrType.I16, tailConstant)));
    entry.Append(new IrRet(value));
    return function;
  }

  private static IrFunction AddTinyVariant(IrModule module, string name, long varyingConstant) {
    var x = new IrArgument(IrType.I16, 0, "x");
    var function = module.AddFunction(new IrFunction(name, IrType.I16, [x]));
    var entry = function.CreateBlock("entry");
    entry.Append(new IrRet(entry.Append(new IrBinary(
      IrBinaryOp.Add, x, new IrConstantInt(IrType.I16, varyingConstant)))));
    return function;
  }

  private static IrFunction AddUnaryDeclaration(IrModule module, string name)
    => module.AddFunction(new IrFunction(name, IrType.I16,
      [new IrArgument(IrType.I16, 0, "x")]));

  private static IrFunction AddCallVariant(IrModule module, string name, IrFunction target) {
    var x = new IrArgument(IrType.I16, 0, "x");
    var function = module.AddFunction(new IrFunction(name, IrType.I16, [x]));
    var entry = function.CreateBlock("entry");
    var called = entry.Append(new IrCall(IrType.I16, target, [x]));
    var mixed = entry.Append(new IrBinary(IrBinaryOp.Xor, called, new IrConstantInt(IrType.I16, 0x55)));
    var shifted = entry.Append(new IrBinary(IrBinaryOp.Add, mixed, new IrConstantInt(IrType.I16, 9)));
    entry.Append(new IrRet(shifted));
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
