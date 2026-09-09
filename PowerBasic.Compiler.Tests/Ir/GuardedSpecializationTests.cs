using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// O0304 — guarded specialization. The primitive must keep the general region correct while making a
/// second copy reachable only through predicates that can be treated as facts in that copy.
/// </summary>
[TestFixture]
public sealed class GuardedSpecializationTests {

  [Test]
  public void TryVersion_GivenCombinedGuards_EmitsOneGuardAndKeepsTheGeneralFallback() {
    var x = new IrArgument(IrType.I32, 0, "x");
    var enabled = new IrArgument(IrType.I1, 1, "enabled");
    var fn = new IrFunction("f", IrType.I32, [x, enabled]);
    var entry = fn.CreateBlock("entry");
    var work = fn.CreateBlock("work");
    var exit = fn.CreateBlock("exit");

    var before = new IrBuilder(entry);
    var equalsSeven = before.Cmp(IrCmpPred.Eq, x, IrBuilder.ConstI32(7));
    before.Br(work);
    var body = new IrBuilder(work);
    var selected = body.Select(enabled, x, IrBuilder.ConstI32(0));
    var computed = body.Add(selected, IrBuilder.ConstI32(1));
    body.Br(exit);
    new IrBuilder(exit).Ret(computed);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    var assumptions = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance) {
      [x] = IrBuilder.ConstI32(7),
    };
    Assert.That(
      GuardedSpecialization.TryVersion(fn, [work], [equalsSeven, enabled], assumptions, out var result),
      Is.True);
    Assert.That(result, Is.Not.Null);

    Assert.That(result!.FallbackEntry, Is.SameAs(work));
    Assert.That(work.Instructions, Does.Contain(selected), "the fallback body itself must not be replaced");
    Assert.That(selected.Condition, Is.SameAs(enabled));
    Assert.That(selected.IfTrue, Is.SameAs(x));

    var guardAnds = result.GuardBlock.Instructions
      .OfType<IrBinary>()
      .Where(binary => binary.Op == IrBinaryOp.And)
      .ToList();
    Assert.That(guardAnds, Has.Count.EqualTo(1), "two predicates are combined in one guard block");
    Assert.That(result.GuardBlock.Terminator, Is.TypeOf<IrCondBr>());
    var branch = (IrCondBr)result.GuardBlock.Terminator!;
    Assert.That(branch.IfTrue, Is.SameAs(result.FastEntry));
    Assert.That(branch.IfFalse, Is.SameAs(work));

    var fastSelected = (IrSelect)result.ValueMap[selected];
    Assert.That(fastSelected.Condition, Is.TypeOf<IrConstantInt>());
    Assert.That(((IrConstantInt)fastSelected.Condition).Value, Is.EqualTo(1));
    Assert.That(fastSelected.IfTrue, Is.TypeOf<IrConstantInt>());
    Assert.That(((IrConstantInt)fastSelected.IfTrue).Value, Is.EqualTo(7));

    var returned = ((IrRet)exit.Terminator!).Value;
    Assert.That(
      returned,
      Is.TypeOf<IrPhi>(),
      "the value escaping either version must be joined at the exit");
    var joined = (IrPhi)returned!;
    Assert.That(joined.IncomingBlocks, Has.Count.EqualTo(2));
    Assert.That(joined.IncomingFrom(work), Is.SameAs(computed));
    Assert.That(joined.IncomingFrom(result.BlockMap[work]), Is.SameAs(result.ValueMap[computed]));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void TryVersion_GivenAnExistingExitPhi_ExtendsItForTheFastClone() {
    var guard = new IrArgument(IrType.I1, 0, "guard");
    var choose = new IrArgument(IrType.I1, 1, "choose");
    var x = new IrArgument(IrType.I32, 2, "x");
    var fn = new IrFunction("f", IrType.I32, [guard, choose, x]);
    var entry = fn.CreateBlock("entry");
    var dispatch = fn.CreateBlock("dispatch");
    var left = fn.CreateBlock("left");
    var right = fn.CreateBlock("right");
    var exit = fn.CreateBlock("exit");

    new IrBuilder(entry).Br(dispatch);
    new IrBuilder(dispatch).CondBr(choose, left, right);
    var leftBuilder = new IrBuilder(left);
    var leftValue = leftBuilder.Add(x, IrBuilder.ConstI32(1));
    leftBuilder.Br(exit);
    var rightBuilder = new IrBuilder(right);
    var rightValue = rightBuilder.Add(x, IrBuilder.ConstI32(2));
    rightBuilder.Br(exit);
    var exitBuilder = new IrBuilder(exit);
    var phi = exitBuilder.Phi(IrType.I32);
    phi.AddIncoming(leftValue, left);
    phi.AddIncoming(rightValue, right);
    exitBuilder.Ret(phi);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    Assert.That(
      GuardedSpecialization.TryVersion(fn, [dispatch, left, right], [guard], out var result),
      Is.True);
    Assert.That(result, Is.Not.Null);

    Assert.That(
      exit.Phis.Single(),
      Is.SameAs(phi),
      "the existing merge remains the value identity seen by its users");
    Assert.That(phi.IncomingBlocks, Has.Count.EqualTo(4));
    Assert.That(phi.IncomingFrom(result!.BlockMap[left]), Is.SameAs(result.ValueMap[leftValue]));
    Assert.That(phi.IncomingFrom(result.BlockMap[right]), Is.SameAs(result.ValueMap[rightValue]));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [TestCase(1, false)]
  [TestCase(2, true)]
  public void TryVersion_GivenACodeSizeBudget_RespectsTheExactBoundary(int budget, bool expected) {
    var guard = new IrArgument(IrType.I1, 0, "guard");
    var x = new IrArgument(IrType.I32, 1, "x");
    var fn = new IrFunction("f", IrType.I32, [guard, x]);
    var entry = fn.CreateBlock("entry");
    var work = fn.CreateBlock("work");
    var exit = fn.CreateBlock("exit");
    new IrBuilder(entry).Br(work);
    var body = new IrBuilder(work);
    var value = body.Add(x, IrBuilder.ConstI32(1));
    body.Br(exit); // region cost is exactly two IR instructions: the add and its branch
    new IrBuilder(exit).Ret(value);

    var original = IrPrinter.Print(fn);
    var originalBlockCount = fn.Blocks.Count;
    var changed = GuardedSpecialization.TryVersion(
      fn,
      [work],
      [guard],
      new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance),
      budget,
      out _);

    Assert.That(changed, Is.EqualTo(expected));
    if (!expected) {
      Assert.That(fn.Blocks, Has.Count.EqualTo(originalBlockCount));
      Assert.That(IrPrinter.Print(fn), Is.EqualTo(original), "a declined candidate must be mutation-free");
    }
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void TryVersion_GivenExceptionalControlFlow_DeclinesWithoutMutation() {
    var guard = new IrArgument(IrType.I1, 0, "guard");
    var fn = new IrFunction("f", IrType.Void, [guard]) { HasErrorHandler = true };
    var entry = fn.CreateBlock("entry");
    var work = fn.CreateBlock("work");
    var exit = fn.CreateBlock("exit");
    new IrBuilder(entry).Br(work);
    new IrBuilder(work).Br(exit);
    new IrBuilder(exit).Ret();
    var original = IrPrinter.Print(fn);

    Assert.That(GuardedSpecialization.TryVersion(fn, [work], [guard], out _), Is.False);
    Assert.That(IrPrinter.Print(fn), Is.EqualTo(original));
  }
}
