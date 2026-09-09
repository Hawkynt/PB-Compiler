using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Regression coverage for O0307 speculative devirtualization.</summary>
[TestFixture]
public sealed class O0307MiddleEndTests {

  [Test]
  public void SpeculativeDevirtualization_GivenOneLocalCandidate_WhenRun_ThenItGuardsDirectCallAndKeepsFallback() {
    var module = new IrModule("test");
    var targetArgument = new IrArgument(IrType.I32, 0, "value");
    var target = module.AddFunction(new IrFunction("double", IrType.I32, [targetArgument]) { NoInline = true });
    var targetBuilder = new IrBuilder(target.CreateBlock("entry"));
    targetBuilder.Ret(targetBuilder.Mul(targetArgument, new IrConstantInt(IrType.I32, 2)));

    var callee = new IrArgument(IrType.Ptr, 0, "callee");
    var value = new IrArgument(IrType.I32, 1, "value");
    var caller = module.AddFunction(new IrFunction("caller", IrType.I32, [callee, value]));
    var entry = caller.CreateBlock("entry");
    var slot = entry.Append(new IrAlloca(IrType.Ptr));
    entry.Append(new IrStore(target, slot)); // static evidence: this procedure takes target's address
    var indirect = entry.Append(new IrCall(IrType.I32, callee, [value], IrCallConvention.Cdecl));
    entry.Append(new IrRet(indirect));

    Assert.That(SpeculativeDevirtualization.Run(module), Is.EqualTo(1));

    var guard = entry.Terminator as IrCondBr;
    Assert.That(guard, Is.Not.Null);
    Assert.That(guard!.Condition, Is.TypeOf<IrCmp>());
    var comparison = (IrCmp)guard.Condition;
    Assert.That(comparison.Pred, Is.EqualTo(IrCmpPred.Eq));
    Assert.That(comparison.Lhs, Is.SameAs(callee));
    Assert.That(comparison.Rhs, Is.SameAs(target));

    var direct = caller.AllInstructions.OfType<IrCall>().Single(call => ReferenceEquals(call.Callee, target));
    Assert.That(direct.Convention, Is.EqualTo(IrCallConvention.Cdecl));
    Assert.That(indirect.Callee, Is.SameAs(callee), "the mismatch path must retain the original indirect call");

    var merged = caller.AllInstructions.OfType<IrPhi>().Single();
    Assert.That(merged.Operands, Does.Contain(direct));
    Assert.That(merged.Operands, Does.Contain(indirect));
    Assert.That(caller.AllInstructions.OfType<IrRet>().Single().Value, Is.SameAs(merged));
    Assert.That(IrVerifier.Verify(caller), Is.Empty);
  }

  [Test]
  public void SpeculativeDevirtualization_GivenTwoLocalCandidates_WhenRun_ThenItDoesNotGuess() {
    var module = new IrModule("test");
    var first = module.AddFunction(new IrFunction("first", IrType.I32, [new IrArgument(IrType.I32, 0)]));
    var second = module.AddFunction(new IrFunction("second", IrType.I32, [new IrArgument(IrType.I32, 0)]));
    new IrBuilder(first.CreateBlock("entry")).Ret(first.Parameters[0]);
    new IrBuilder(second.CreateBlock("entry")).Ret(second.Parameters[0]);

    var callee = new IrArgument(IrType.Ptr, 0, "callee");
    var value = new IrArgument(IrType.I32, 1, "value");
    var caller = module.AddFunction(new IrFunction("caller", IrType.I32, [callee, value]));
    var entry = caller.CreateBlock("entry");
    var firstSlot = entry.Append(new IrAlloca(IrType.Ptr));
    var secondSlot = entry.Append(new IrAlloca(IrType.Ptr));
    entry.Append(new IrStore(first, firstSlot));
    entry.Append(new IrStore(second, secondSlot));
    var indirect = entry.Append(new IrCall(IrType.I32, callee, [value]));
    entry.Append(new IrRet(indirect));

    Assert.That(SpeculativeDevirtualization.Run(module), Is.Zero);
    Assert.That(entry.Terminator, Is.TypeOf<IrRet>());
    Assert.That(caller.AllInstructions.OfType<IrCall>().Single(), Is.SameAs(indirect));
    Assert.That(IrVerifier.Verify(caller), Is.Empty);
  }

  [Test]
  public void SpeculativeDevirtualization_GivenAnAlreadyVersionedCall_WhenRunAgain_ThenItIsIdempotent() {
    var module = new IrModule("test");
    var target = module.AddFunction(new IrFunction("target", IrType.Void, []));
    new IrBuilder(target.CreateBlock("entry")).Ret();

    var callee = new IrArgument(IrType.Ptr, 0, "callee");
    var caller = module.AddFunction(new IrFunction("caller", IrType.Void, [callee]));
    var entry = caller.CreateBlock("entry");
    var slot = entry.Append(new IrAlloca(IrType.Ptr));
    entry.Append(new IrStore(target, slot));
    entry.Append(new IrCall(IrType.Void, callee, []));
    entry.Append(new IrRet());

    Assert.That(SpeculativeDevirtualization.Run(module), Is.EqualTo(1));
    Assert.That(SpeculativeDevirtualization.Run(module), Is.Zero);
    Assert.That(caller.AllInstructions.OfType<IrPhi>(), Is.Empty);
    Assert.That(caller.AllInstructions.OfType<IrCall>().Count(), Is.EqualTo(2));
    Assert.That(IrVerifier.Verify(caller), Is.Empty);
  }
}
