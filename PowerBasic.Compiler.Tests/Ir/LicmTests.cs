using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>LICM: hoisting loop-invariant computations into the loop preheader.</summary>
[TestFixture]
public sealed class LicmTests {

  /// <summary>
  /// Builds: entry -> header; header: i = phi(0, inext); c = i &lt; n; condbr body/exit;
  /// body: inv = k*k; inext = i + inv; br header; exit: ret.
  /// </summary>
  private static (IrFunction Fn, IrBinary Inv, IrBinary Inext, IrBasicBlock Entry, IrBasicBlock Body) BuildLoop() {
    var n = new IrArgument(IrType.I32, 0, "n");
    var k = new IrArgument(IrType.I32, 1, "k");
    var fn = new IrFunction("f", IrType.Void, [n, k]);
    var entry = fn.CreateBlock("entry");
    var header = fn.CreateBlock("header");
    var body = fn.CreateBlock("body");
    var exit = fn.CreateBlock("exit");

    new IrBuilder(entry).Br(header);

    var bh = new IrBuilder(header);
    var i = bh.Phi(IrType.I32);
    var c = bh.Cmp(IrCmpPred.Slt, i, n);
    bh.CondBr(c, body, exit);

    var bb = new IrBuilder(body);
    var inv = bb.Mul(k, k);                          // loop-invariant
    var inext = bb.Add(i, inv);                      // loop-variant (uses the phi)
    bb.Br(header);

    i.AddIncoming(IrBuilder.ConstI32(0), entry);
    i.AddIncoming(inext, body);

    new IrBuilder(exit).Ret();
    return (fn, inv, inext, entry, body);
  }

  [Test]
  public void Run_HoistsInvariantComputationIntoPreheader() {
    var (fn, inv, inext, entry, body) = BuildLoop();

    var hoisted = Licm.Run(fn);

    Assert.That(hoisted, Is.EqualTo(1));
    Assert.That(inv.Parent, Is.SameAs(entry));       // k*k moved to the preheader
    Assert.That(inext.Parent, Is.SameAs(body));      // i+inv stays (loop-variant)
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Run_LeavesNothingToHoistWhenAllComputationIsVariant() {
    var fn = new IrFunction("f", IrType.Void, [new IrArgument(IrType.I32, 0, "n")]);
    var entry = fn.CreateBlock("entry");
    var header = fn.CreateBlock("header");
    var body = fn.CreateBlock("body");
    var exit = fn.CreateBlock("exit");
    new IrBuilder(entry).Br(header);
    var bh = new IrBuilder(header);
    var i = bh.Phi(IrType.I32);
    bh.CondBr(bh.Cmp(IrCmpPred.Slt, i, fn.Parameters[0]), body, exit);
    var bb = new IrBuilder(body);
    var inext = bb.Add(i, IrBuilder.ConstI32(1));     // depends on the phi -> variant
    bb.Br(header);
    i.AddIncoming(IrBuilder.ConstI32(0), entry);
    i.AddIncoming(inext, body);
    new IrBuilder(exit).Ret();

    Assert.That(Licm.Run(fn), Is.EqualTo(0));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Run_DoesNotHoistTrappingDivision() {
    // inv would be invariant but sdiv can trap, so it must stay in the loop
    var k = new IrArgument(IrType.I32, 0, "k");
    var m = new IrArgument(IrType.I32, 1, "m");
    var fn = new IrFunction("f", IrType.Void, [k, m]);
    var entry = fn.CreateBlock("entry");
    var header = fn.CreateBlock("header");
    var body = fn.CreateBlock("body");
    var exit = fn.CreateBlock("exit");
    new IrBuilder(entry).Br(header);
    var bh = new IrBuilder(header);
    var i = bh.Phi(IrType.I32);
    bh.CondBr(bh.Cmp(IrCmpPred.Slt, i, k), body, exit);
    var bb = new IrBuilder(body);
    var div = bb.SDiv(k, m);                          // invariant but trapping
    var inext = bb.Add(i, div);
    bb.Br(header);
    i.AddIncoming(IrBuilder.ConstI32(0), entry);
    i.AddIncoming(inext, body);
    new IrBuilder(exit).Ret();

    Assert.That(Licm.Run(fn), Is.EqualTo(0));
    Assert.That(div.Parent, Is.SameAs(body));         // division stays inside the loop
  }
}
