using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>SimplifyCFG: trivial-phi elimination and single-predecessor block merging.</summary>
[TestFixture]
public sealed class SimplifyCfgTests {

  [Test]
  public void Run_MergesAChainOfSinglePredecessorBlocks() {
    // entry -> a -> b -> exit, all unconditional: collapses into one block
    var fn = new IrFunction("f", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var a = fn.CreateBlock("a");
    var b = fn.CreateBlock("b");
    var exit = fn.CreateBlock("exit");
    new IrBuilder(entry).Br(a);
    new IrBuilder(a).Br(b);
    new IrBuilder(b).Br(exit);
    new IrBuilder(exit).Ret();

    SimplifyCfg.Run(fn);

    Assert.That(fn.Blocks, Has.Count.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.Entry!.Terminator, Is.InstanceOf<IrRet>());
  }

  [Test]
  public void Run_EliminatesATrivialPhiWithIdenticalInputs() {
    var fn = new IrFunction("f", IrType.I32);
    var entry = fn.CreateBlock("entry");
    var t = fn.CreateBlock("t");
    var e = fn.CreateBlock("e");
    var merge = fn.CreateBlock("merge");
    new IrBuilder(entry).CondBr(new IrArgument(IrType.I1, 0, "c"), t, e);
    new IrBuilder(t).Br(merge);
    new IrBuilder(e).Br(merge);
    var bm = new IrBuilder(merge);
    var phi = bm.Phi(IrType.I32);
    var five = IrBuilder.ConstI32(5);
    phi.AddIncoming(five, t);
    phi.AddIncoming(five, e);                          // identical inputs -> trivial
    bm.Ret(phi);

    SimplifyCfg.Run(fn);

    Assert.That(fn.AllInstructions.OfType<IrPhi>().Count(), Is.EqualTo(0));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(IrPrinter.Print(fn), Does.Contain("ret i32 5"));
  }

  [Test]
  public void Run_PreservesARealMergePhiAndLoopShape() {
    // a diamond with two distinct inputs must keep its phi
    var fn = new IrFunction("f", IrType.I32);
    var entry = fn.CreateBlock("entry");
    var t = fn.CreateBlock("t");
    var e = fn.CreateBlock("e");
    var merge = fn.CreateBlock("merge");
    new IrBuilder(entry).CondBr(new IrArgument(IrType.I1, 0, "c"), t, e);
    new IrBuilder(t).Br(merge);
    new IrBuilder(e).Br(merge);
    var bm = new IrBuilder(merge);
    var phi = bm.Phi(IrType.I32);
    phi.AddIncoming(IrBuilder.ConstI32(1), t);
    phi.AddIncoming(IrBuilder.ConstI32(2), e);
    bm.Ret(phi);

    SimplifyCfg.Run(fn);

    Assert.That(fn.AllInstructions.OfType<IrPhi>().Count(), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void StandardPipeline_TightensALoweredIfIntoFewerBlocks() {
    var unit = Parser.Parse(Lexer.Tokenize("x% = 5\nIF x% > 1 THEN\n  y% = 1\nEND IF\nz% = y%", "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var fn = IrLowering.TryLowerMainBody(Binder.Bind(unit, Dialect.Pb35))!;
    var before = fn.Blocks.Count;

    IrPassManager.Standard().RunToFixpoint(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.Blocks.Count, Is.LessThan(before));   // dead arm + trivial blocks gone
  }
}
