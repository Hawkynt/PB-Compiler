using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>SimplifyCFG: trivial-phi elimination and single-predecessor block merging.</summary>
[TestFixture]
public sealed class SimplifyCfgTests {

  /// <summary>
  /// A loop guarded by a flag phi that one latch edge carries unchanged (<c>[%flag, keep]</c>) and
  /// the other clears. Threading the entry edge - whose flag is the constant 1 - straight into the
  /// body removed that 1 from the header phi, which then looked trivial and folded to 0: the loop's
  /// back edge went to the exit and the counter phi was merged into its own increment. The portable
  /// runtime's INPUT field reader has exactly this shape (<c>WHILE reading</c>).
  /// </summary>
  [Test]
  public void Run_GivenALoopFlagCarriedUnchangedOnALatchEdge_ThenTheLoopSurvivesThreading() {
    var fn = new IrFunction("f", IrType.I32);
    var stopHere = new IrFunction("stop_here", IrType.I1);
    var entry = fn.CreateBlock("entry");
    var header = fn.CreateBlock("header");
    var body = fn.CreateBlock("body");
    var stop = fn.CreateBlock("stop");
    var keep = fn.CreateBlock("keep");
    var latch = fn.CreateBlock("latch");
    var exit = fn.CreateBlock("exit");
    new IrBuilder(entry).Br(header);
    var h = new IrBuilder(header);
    var flag = h.Phi(IrType.I1);
    var count = h.Phi(IrType.I32);
    h.CondBr(flag, body, exit);
    var b = new IrBuilder(body);
    b.CondBr(b.Call(IrType.I1, stopHere), stop, keep);
    new IrBuilder(stop).Br(latch);
    var k = new IrBuilder(keep);
    var incremented = k.Add(count, IrBuilder.ConstI32(1));
    k.Br(latch);
    var l = new IrBuilder(latch);
    var nextFlag = l.Phi(IrType.I1);
    nextFlag.AddIncoming(IrBuilder.ConstBool(false), stop);
    nextFlag.AddIncoming(flag, keep);
    var nextCount = l.Phi(IrType.I32);
    nextCount.AddIncoming(count, stop);
    nextCount.AddIncoming(incremented, keep);
    l.Br(header);
    flag.AddIncoming(IrBuilder.ConstBool(true), entry);
    flag.AddIncoming(nextFlag, latch);
    count.AddIncoming(IrBuilder.ConstI32(0), entry);
    count.AddIncoming(nextCount, latch);
    new IrBuilder(exit).Ret(count);
    Assert.That(IrVerifier.Verify(fn), Is.Empty, "the input is valid SSA");

    SimplifyCfg.Run(fn);

    Assert.Multiple(() => {
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
      var call = fn.AllInstructions.OfType<IrCall>().Single();
      Assert.That(Reaches(call.Parent!, call.Parent!), Is.True, "the call is still inside a loop");
      Assert.That(fn.AllInstructions.OfType<IrBinary>().Single().Lhs, Is.InstanceOf<IrPhi>(),
        "the count is still a loop-carried phi, not its own increment");
    });
  }

  private static bool Reaches(IrBasicBlock from, IrBasicBlock to) {
    var seen = new HashSet<IrBasicBlock>();
    var work = new Stack<IrBasicBlock>(from.Successors);
    while (work.TryPop(out var block)) {
      if (ReferenceEquals(block, to))
        return true;
      if (seen.Add(block))
        foreach (var successor in block.Successors)
          work.Push(successor);
    }
    return false;
  }

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

    IrMiddleEndPipeline.Standard().RunToFixpoint(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.Blocks.Count, Is.LessThan(before));   // dead arm + trivial blocks gone
  }
}
