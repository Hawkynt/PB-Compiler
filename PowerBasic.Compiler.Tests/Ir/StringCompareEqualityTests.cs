using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0298: route string compares to the length-guarded equality-only runtime entry when ordering is unobservable.</summary>
[TestFixture]
public sealed class StringCompareEqualityTests {

  [Test]
  public void Run_GivenDiscardedCompareResult_ThenUsesEqualityOnlyEntry() {
    var (module, function, general) = ModuleWithCompareFunction(IrType.Void);
    var builder = new IrBuilder(function.CreateBlock("entry"));
    var call = builder.Call(IrType.I32, general, function.Parameters[0], function.Parameters[1]);
    builder.Ret();

    var changes = StringCompareEquality.Run(module);

    Assert.That(changes, Is.EqualTo(1));
    Assert.That(call.Callee, Is.SameAs(module.FindFunction("rt_str_compare_eq")));
    Assert.That(call.Users, Is.Empty);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  [TestCase(IrCmpPred.Eq)]
  [TestCase(IrCmpPred.Ne)]
  public void Run_GivenZeroEqualityUser_ThenUsesEqualityOnlyEntry(IrCmpPred predicate) {
    var (module, function, general) = ModuleWithCompareFunction(IrType.I1);
    var builder = new IrBuilder(function.CreateBlock("entry"));
    var call = builder.Call(IrType.I32, general, function.Parameters[0], function.Parameters[1]);
    builder.Ret(builder.Cmp(predicate, call, IrBuilder.ConstI32(0)));

    var changes = StringCompareEquality.Run(module);

    Assert.That(changes, Is.EqualTo(1));
    Assert.That(call.Callee, Is.SameAs(module.FindFunction("rt_str_compare_eq")));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  [Test]
  public void Run_GivenOrderingUser_ThenKeepsThreeWayCompare() {
    var (module, function, general) = ModuleWithCompareFunction(IrType.I1);
    var builder = new IrBuilder(function.CreateBlock("entry"));
    var call = builder.Call(IrType.I32, general, function.Parameters[0], function.Parameters[1]);
    builder.Ret(builder.Cmp(IrCmpPred.Slt, call, IrBuilder.ConstI32(0)));

    var changes = StringCompareEquality.Run(module);

    Assert.That(changes, Is.Zero);
    Assert.That(call.Callee, Is.SameAs(general));
    Assert.That(module.FindFunction("rt_str_compare_eq"), Is.Null);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  private static (IrModule Module, IrFunction Function, IrFunction General) ModuleWithCompareFunction(IrType returnType) {
    var module = new IrModule("O0298");
    var general = module.AddFunction(new IrFunction("rt_str_compare", IrType.I32,
      [new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.Ptr, 1)]));
    var function = module.AddFunction(new IrFunction("test", returnType,
      [new IrArgument(IrType.Ptr, 0, "left"), new IrArgument(IrType.Ptr, 1, "right")]));
    return (module, function, general);
  }
}
