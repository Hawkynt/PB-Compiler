using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0283 — caller-identity cloning with bounded code growth.</summary>
[TestFixture]
public sealed class ContextSensitiveCloningTests {

  private static IrConstantInt Const(long value) => new(IrType.I16, value);

  private static (IrFunction Function, IrArgument Parameter) Identity(IrModule module, string name, bool noInline = false) {
    var parameter = new IrArgument(IrType.I16, 0, "n");
    var function = module.AddFunction(new IrFunction(name, IrType.I16, [parameter]) { NoInline = noInline });
    function.CreateBlock("entry").Append(new IrRet(parameter));
    return (function, parameter);
  }

  private static (IrFunction Function, IrCall Call) Caller(IrModule module, string name, IrFunction callee, IrValue argument) {
    var function = module.AddFunction(new IrFunction(name, IrType.Void));
    var entry = function.CreateBlock("entry");
    var call = entry.Append(new IrCall(IrType.I16, callee, [argument]));
    entry.Append(new IrRet());
    return (function, call);
  }

  [Test]
  public void Run_GivenOneCallerHasAStableConstantAndAnotherIsGeneral_ThenOnlyTheStableCallerUsesAClone() {
    var module = new IrModule("t");
    var (callee, parameter) = Identity(module, "f");
    var (_, specializedCall) = Caller(module, "hot", callee, Const(7));
    var outside = new IrArgument(IrType.I16, 0, "outside");
    var generalCaller = module.AddFunction(new IrFunction("cold", IrType.Void, [outside]));
    var generalEntry = generalCaller.CreateBlock("entry");
    var generalCall = generalEntry.Append(new IrCall(IrType.I16, callee, [outside]));
    generalEntry.Append(new IrRet());

    Assert.That(ContextSensitiveCloning.Run(module), Is.EqualTo(1));

    var clone = specializedCall.Callee as IrFunction;
    Assert.Multiple(() => {
      Assert.That(clone, Is.Not.Null.And.Not.SameAs(callee));
      Assert.That(generalCall.Callee, Is.SameAs(callee));
      Assert.That(parameter.HasNoUsers, Is.False, "the general body must retain its parameter use");
      Assert.That(clone!.Parameters[0].HasNoUsers, Is.True, "the caller fact should be substituted in the clone");
      Assert.That(((IrRet)clone.Entry!.Terminator!).Value, Is.TypeOf<IrConstantInt>());
      Assert.That(((IrConstantInt)((IrRet)clone.Entry!.Terminator!).Value!).Value, Is.EqualTo(7));
      Assert.That(IrVerifier.Verify(callee), Is.Empty);
      Assert.That(IrVerifier.Verify(clone), Is.Empty);
    });
  }

  [Test]
  public void Run_GivenEquivalentIntegerBitPatternsWithinOneCaller_ThenTheyFormOneContext() {
    var module = new IrModule("t");
    var (callee, _) = Identity(module, "f");
    var firstCaller = module.AddFunction(new IrFunction("a", IrType.Void));
    var firstEntry = firstCaller.CreateBlock("entry");
    var first = firstEntry.Append(new IrCall(IrType.I16, callee, [Const(-1)]));
    var second = firstEntry.Append(new IrCall(IrType.I16, callee, [Const(65535)]));
    firstEntry.Append(new IrRet());
    var outside = new IrArgument(IrType.I16, 0, "outside");
    var secondCaller = module.AddFunction(new IrFunction("b", IrType.Void, [outside]));
    var secondEntry = secondCaller.CreateBlock("entry");
    secondEntry.Append(new IrCall(IrType.I16, callee, [outside]));
    secondEntry.Append(new IrRet());

    Assert.That(ContextSensitiveCloning.Run(module), Is.EqualTo(1));
    Assert.That(first.Callee, Is.Not.SameAs(callee));
    Assert.That(second.Callee, Is.SameAs(first.Callee), "i16 -1 and i16 65535 are the same bit pattern");
  }

  [Test]
  public void Run_GivenTheSameFactFromEveryCaller_ThenItLeavesTheFactToInterproceduralPropagation() {
    var module = new IrModule("t");
    var (callee, _) = Identity(module, "f");
    var (_, first) = Caller(module, "a", callee, Const(7));
    var (_, second) = Caller(module, "b", callee, Const(7));

    Assert.That(ContextSensitiveCloning.Run(module), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(first.Callee, Is.SameAs(callee));
      Assert.That(second.Callee, Is.SameAs(callee));
      Assert.That(module.Functions, Has.Count.EqualTo(3));
    });
  }

  [Test]
  public void Run_GivenACallerDisagreesWithItself_ThenThatCallerIsNotSpecialized() {
    var module = new IrModule("t");
    var (callee, _) = Identity(module, "f");
    var firstCaller = module.AddFunction(new IrFunction("a", IrType.Void));
    var firstEntry = firstCaller.CreateBlock("entry");
    var first = firstEntry.Append(new IrCall(IrType.I16, callee, [Const(7)]));
    var second = firstEntry.Append(new IrCall(IrType.I16, callee, [Const(8)]));
    firstEntry.Append(new IrRet());
    Caller(module, "b", callee, Const(9));

    Assert.That(ContextSensitiveCloning.Run(module), Is.EqualTo(1),
      "the other stable caller may still be specialized");
    Assert.Multiple(() => {
      Assert.That(first.Callee, Is.SameAs(callee));
      Assert.That(second.Callee, Is.SameAs(callee));
    });
  }

  [Test]
  public void Run_GivenNoInlineCallee_ThenItDoesNotCloneTheProgrammersInspectionBarrier() {
    var module = new IrModule("t");
    var (callee, _) = Identity(module, "f", noInline: true);
    Caller(module, "a", callee, Const(7));
    Caller(module, "b", callee, Const(8));

    Assert.That(ContextSensitiveCloning.Run(module), Is.Zero);
    Assert.That(module.Functions, Has.Count.EqualTo(3));
  }

  [Test]
  public void Run_GivenCdeclDefinition_WhenCloned_ThenClonePreservesDefinitionAbi() {
    var module = new IrModule("t");
    var parameter = new IrArgument(IrType.I16, 0, "n");
    var callee = module.AddFunction(new IrFunction("f", IrType.I16, [parameter]) {
      Convention = IrCallConvention.Cdecl,
    });
    callee.CreateBlock("entry").Append(new IrRet(parameter));

    var hot = module.AddFunction(new IrFunction("hot", IrType.Void));
    var hotEntry = hot.CreateBlock("entry");
    var specialized = hotEntry.Append(new IrCall(IrType.I16, callee, [Const(7)], IrCallConvention.Cdecl));
    hotEntry.Append(new IrRet());

    var outside = new IrArgument(IrType.I16, 0, "outside");
    var cold = module.AddFunction(new IrFunction("cold", IrType.Void, [outside]));
    var coldEntry = cold.CreateBlock("entry");
    coldEntry.Append(new IrCall(IrType.I16, callee, [outside], IrCallConvention.Cdecl));
    coldEntry.Append(new IrRet());

    Assert.That(ContextSensitiveCloning.Run(module), Is.EqualTo(1));
    var clone = specialized.Callee as IrFunction;
    Assert.Multiple(() => {
      Assert.That(clone, Is.Not.Null);
      Assert.That(clone!.Convention, Is.EqualTo(IrCallConvention.Cdecl));
      Assert.That(IrVerifier.Verify(clone), Is.Empty);
      Assert.That(IrVerifier.Verify(hot), Is.Empty);
    });
  }

  [Test]
  public void Run_GivenARecursiveCallInAClone_WhenRunAgain_ThenGeneratedCallerIdentityDoesNotRespecializeIt() {
    var module = new IrModule("t");
    var parameter = new IrArgument(IrType.I16, 0, "n");
    var callee = module.AddFunction(new IrFunction("f", IrType.I16, [parameter]));
    var body = callee.CreateBlock("entry");
    body.Append(new IrCall(IrType.I16, callee, [Const(1)]));
    body.Append(new IrRet(parameter));
    var (_, specializedCall) = Caller(module, "hot", callee, Const(7));
    var outside = new IrArgument(IrType.I16, 0, "outside");
    var generalCaller = module.AddFunction(new IrFunction("cold", IrType.Void, [outside]));
    var generalEntry = generalCaller.CreateBlock("entry");
    generalEntry.Append(new IrCall(IrType.I16, callee, [outside]));
    generalEntry.Append(new IrRet());

    Assert.That(ContextSensitiveCloning.Run(module), Is.EqualTo(1));
    var clone = (IrFunction)specializedCall.Callee;
    var recursiveCall = clone.AllInstructions.OfType<IrCall>().Single();
    Assert.That(recursiveCall.Callee, Is.SameAs(callee));

    Assert.That(ContextSensitiveCloning.Run(module), Is.Zero);
    Assert.That(recursiveCall.Callee, Is.SameAs(callee),
      "a generated clone is not a fresh source-level caller context on later pipeline sweeps");
  }

  [Test]
  public void Run_GivenMoreContextsThanThePerFunctionBudget_ThenItCreatesAtMostThreeClonesAcrossRuns() {
    var module = new IrModule("t");
    var (callee, _) = Identity(module, "f");
    var calls = Enumerable.Range(0, 5)
      .Select(index => Caller(module, $"c{index}", callee, Const(index)).Call)
      .ToList();
    var outside = new IrArgument(IrType.I16, 0, "outside");
    var general = module.AddFunction(new IrFunction("general", IrType.Void, [outside]));
    var entry = general.CreateBlock("entry");
    entry.Append(new IrCall(IrType.I16, callee, [outside]));
    entry.Append(new IrRet());

    Assert.That(ContextSensitiveCloning.Run(module), Is.EqualTo(3));
    Assert.Multiple(() => {
      Assert.That(calls.Count(call => !ReferenceEquals(call.Callee, callee)), Is.EqualTo(3));
      Assert.That(module.Functions.Count(function => function.Name.StartsWith("f__o0283_ctx", StringComparison.Ordinal)),
        Is.EqualTo(3));
    });

    Assert.That(ContextSensitiveCloning.Run(module), Is.Zero, "a second pass must not evade the clone cap");
  }
}
