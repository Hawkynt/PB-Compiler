using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Global value numbering: redundant pure computations are replaced by a dominating equal.</summary>
[TestFixture]
public sealed class GvnTests {

  private static int Count<T>(IrFunction fn) => fn.AllInstructions.OfType<T>().Count();

  [Test]
  public void Run_EliminatesRedundantComputationWithinABlock() {
    // %a = mul x,x ; %b = mul x,x ; ret a + b  ->  b folds into a
    var x = new IrArgument(IrType.I32, 0, "x");
    var fn = new IrFunction("f", IrType.I32, [x]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var a = b.Mul(x, x);
    var dup = b.Mul(x, x);
    b.Ret(b.Add(a, dup));

    var removed = Gvn.Run(fn);

    Assert.That(removed, Is.EqualTo(1));
    Assert.That(Count<IrBinary>(fn), Is.EqualTo(2));       // one mul + the add
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Run_RecognisesCommutativeOperands() {
    // add x,y and add y,x are congruent
    var x = new IrArgument(IrType.I32, 0, "x");
    var y = new IrArgument(IrType.I32, 1, "y");
    var fn = new IrFunction("f", IrType.I32, [x, y]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var s1 = b.Add(x, y);
    var s2 = b.Add(y, x);
    b.Ret(b.Add(s1, s2));

    Gvn.Run(fn);

    Assert.That(Count<IrBinary>(fn), Is.EqualTo(2));       // one add(x,y) + the outer add
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Run_EliminatesRedundancyAcrossDominatingBlocks() {
    // %a = add x,1 in entry; %b = add x,1 in a block entry dominates -> b folds into a
    var x = new IrArgument(IrType.I32, 0, "x");
    var c = new IrArgument(IrType.I1, 1, "c");
    var fn = new IrFunction("f", IrType.I32, [x, c]);
    var entry = fn.CreateBlock("entry");
    var t = fn.CreateBlock("t");
    var e = fn.CreateBlock("e");
    var be = new IrBuilder(entry);
    var a = be.Add(x, IrBuilder.ConstI32(1));
    be.CondBr(c, t, e);
    var bt = new IrBuilder(t);
    var dup = bt.Add(x, IrBuilder.ConstI32(1));            // congruent with a, dominated by entry
    bt.Ret(dup);
    new IrBuilder(e).Ret(a);

    var removed = Gvn.Run(fn);

    Assert.That(removed, Is.EqualTo(1));
    Assert.That(Count<IrBinary>(fn), Is.EqualTo(1));       // only entry's add remains
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Run_DoesNotMergeAcrossNonDominatingBlocks() {
    // identical adds on two sibling arms: neither dominates the other -> both survive
    var x = new IrArgument(IrType.I32, 0, "x");
    var c = new IrArgument(IrType.I1, 1, "c");
    var fn = new IrFunction("f", IrType.I32, [x, c]);
    var entry = fn.CreateBlock("entry");
    var t = fn.CreateBlock("t");
    var e = fn.CreateBlock("e");
    new IrBuilder(entry).CondBr(c, t, e);
    var bt = new IrBuilder(t);
    bt.Ret(bt.Add(x, IrBuilder.ConstI32(1)));
    var bel = new IrBuilder(e);
    bel.Ret(bel.Add(x, IrBuilder.ConstI32(1)));

    var removed = Gvn.Run(fn);

    Assert.That(removed, Is.EqualTo(0));
    Assert.That(Count<IrBinary>(fn), Is.EqualTo(2));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }
}
