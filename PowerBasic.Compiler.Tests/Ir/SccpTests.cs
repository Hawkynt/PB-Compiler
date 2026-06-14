using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>SCCP: conditional constant propagation with dead-branch elimination over the IR.</summary>
[TestFixture]
public sealed class SccpTests {

  [Test]
  public void Run_GivenConstantBranch_DeletesTheUntakenArm() {
    var fn = new IrFunction("f", IrType.I32);
    var entry = fn.CreateBlock("entry");
    var then = fn.CreateBlock("then");
    var els = fn.CreateBlock("els");
    new IrBuilder(entry).CondBr(IrBuilder.ConstBool(true), then, els);
    new IrBuilder(then).Ret(IrBuilder.ConstI32(1));
    new IrBuilder(els).Ret(IrBuilder.ConstI32(2));

    Sccp.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.Blocks, Has.Count.EqualTo(2));               // els is gone
    var text = IrPrinter.Print(fn);
    Assert.That(text, Does.Contain("br label %then"));          // entry is now unconditional
    Assert.That(text, Does.Contain("ret i32 1"));
    Assert.That(text, Does.Not.Contain("ret i32 2"));
  }

  [Test]
  public void Run_GivenPhiFedOnlyByReachableEdge_ProvesItConstant() {
    var fn = new IrFunction("f", IrType.I32);
    var entry = fn.CreateBlock("entry");
    var a = fn.CreateBlock("a");
    var b = fn.CreateBlock("b");
    var merge = fn.CreateBlock("merge");
    new IrBuilder(entry).CondBr(IrBuilder.ConstBool(true), a, b);  // only 'a' is reachable
    new IrBuilder(a).Br(merge);
    new IrBuilder(b).Br(merge);
    var bm = new IrBuilder(merge);
    var phi = bm.Phi(IrType.I32);
    phi.AddIncoming(IrBuilder.ConstI32(5), a);
    phi.AddIncoming(IrBuilder.ConstI32(7), b);
    bm.Ret(phi);

    Sccp.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(IrPrinter.Print(fn), Does.Contain("ret i32 5"));  // 7 came only via the dead edge
    Assert.That(fn.Blocks, Has.None.Matches<IrBasicBlock>(blk => blk.Label == "b"));
  }

  [Test]
  public void Run_GivenConstantChainThroughPhi_FoldsToASingleConstant() {
    // %p = phi [10, a], [10, b] ; %r = add %p, 5  ->  %r is 15
    var fn = new IrFunction("f", IrType.I32);
    var entry = fn.CreateBlock("entry");
    var a = fn.CreateBlock("a");
    var b = fn.CreateBlock("b");
    var merge = fn.CreateBlock("merge");
    new IrBuilder(entry).CondBr(new IrArgument(IrType.I1, 0, "c"), a, b);  // both edges live
    new IrBuilder(a).Br(merge);
    new IrBuilder(b).Br(merge);
    var bm = new IrBuilder(merge);
    var phi = bm.Phi(IrType.I32);
    phi.AddIncoming(IrBuilder.ConstI32(10), a);
    phi.AddIncoming(IrBuilder.ConstI32(10), b);
    bm.Ret(bm.Add(phi, IrBuilder.ConstI32(5)));

    Sccp.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(IrPrinter.Print(fn), Does.Contain("ret i32 15"));
  }

  [Test]
  public void Run_OverLoweredProgram_FoldsAConstantConditionAndDropsTheComparison() {
    var unit = Parser.Parse(Lexer.Tokenize("x% = 5\nIF x% > 3 THEN\n  y% = 1\nELSE\n  y% = 2\nEND IF\nz% = y%", "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var model = Binder.Bind(unit, Dialect.Pb35);
    var fn = IrLowering.TryLowerMainBody(model)!;
    Mem2Reg.Run(fn);
    Sccp.Run(fn);
    Dce.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(IrPrinter.Print(fn), Does.Not.Contain("icmp"));   // 5 > 3 proven, branch folded away
  }
}
