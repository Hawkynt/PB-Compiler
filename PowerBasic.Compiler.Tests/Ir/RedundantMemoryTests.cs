using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Intra-block load/store forwarding (RedundantMemory).</summary>
[TestFixture]
public sealed class RedundantMemoryTests {

  [Test]
  public void StoreThenLoad_SameAddress_ForwardsTheStoredValue() {
    var v = new IrArgument(IrType.I32, 0, "v");
    var fn = new IrFunction("f", IrType.I32, [v]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var slot = b.Alloca(IrType.I32);
    var p = b.Gep(slot, IrBuilder.ConstI32(0));
    b.Store(v, p);
    var l = b.Load(IrType.I32, p);
    b.Ret(l);

    RedundantMemory.Run(fn);
    Dce.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrLoad>(), Is.Empty);   // load forwarded
    Assert.That(IrPrinter.Print(fn), Does.Contain("ret i32 %v"));
  }

  [Test]
  public void RepeatedLoad_SameAddress_IsReused() {
    var fn = new IrFunction("f", IrType.I32);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var slot = b.Alloca(IrType.I32);
    var p = b.Gep(slot, IrBuilder.ConstI32(0));
    var l1 = b.Load(IrType.I32, p);
    var l2 = b.Load(IrType.I32, p);
    b.Ret(b.Add(l1, l2));

    RedundantMemory.Run(fn);

    Assert.That(fn.AllInstructions.OfType<IrLoad>().Count(), Is.EqualTo(1));   // second load reused
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void InterveningStoreToDistinctAlloca_DoesNotBlockForwarding() {
    var v = new IrArgument(IrType.I32, 0, "v");
    var w = new IrArgument(IrType.I32, 1, "w");
    var fn = new IrFunction("f", IrType.I32, [v, w]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var a1 = b.Alloca(IrType.I32);
    var a2 = b.Alloca(IrType.I32);          // a distinct stack slot - cannot alias a1
    var p1 = b.Gep(a1, IrBuilder.ConstI32(0));
    var p2 = b.Gep(a2, IrBuilder.ConstI32(0));
    b.Store(v, p1);
    b.Store(w, p2);                         // distinct alloca: must not invalidate p1
    b.Ret(b.Load(IrType.I32, p1));

    RedundantMemory.Run(fn);
    Dce.Run(fn);

    Assert.That(IrPrinter.Print(fn), Does.Contain("ret i32 %v"));
  }

  [Test]
  public void InterveningStoreToMaybeAliasingAddress_BlocksForwarding() {
    // same base, non-constant offset: the second store may alias the first address
    var v = new IrArgument(IrType.I32, 0, "v");
    var w = new IrArgument(IrType.I32, 1, "w");
    var idx = new IrArgument(IrType.I32, 2, "i");
    var fn = new IrFunction("f", IrType.I32, [v, w, idx]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var arr = b.Alloca(IrType.I32);
    var p0 = b.Gep(arr, IrBuilder.ConstI32(0));
    var pi = b.Gep(arr, idx);            // unknown offset into the same array
    b.Store(v, p0);
    b.Store(w, pi);                      // may alias p0
    b.Ret(b.Load(IrType.I32, p0));

    RedundantMemory.Run(fn);

    Assert.That(fn.AllInstructions.OfType<IrLoad>().Count(), Is.EqualTo(1));   // not forwarded
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void InterveningPartialOverlap_InvalidatesForwardedValue() {
    var original = new IrArgument(IrType.I16, 0, "original");
    var patch = new IrArgument(IrType.I8, 1, "patch");
    var fn = new IrFunction("f", IrType.I16, [original, patch]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var storage = b.Alloca(IrType.I16);
    var word = b.Gep(storage, IrBuilder.ConstI32(0));
    var highByte = b.Gep(storage, IrBuilder.ConstI32(1));
    b.Store(original, word);
    var before = b.Load(IrType.I16, word);
    b.Store(patch, highByte);             // overlaps the second byte of the cached word
    var after = b.Load(IrType.I16, word);
    b.Ret(b.Add(before, after));

    var removed = RedundantMemory.Run(fn);

    Assert.That(removed, Is.EqualTo(1));  // the first load forwards, the post-patch load must remain
    Assert.That(fn.AllInstructions.OfType<IrLoad>().Count(), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Pipeline_ArrayStoreThenRead_FoldsThroughMemory() {
    var unit = Parser.Parse(Lexer.Tokenize("DIM a%(0 TO 3)\na%(1) = 5\nx% = a%(1)\nEND", "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var fn = IrLowering.TryLowerMainBody(Binder.Bind(unit, Dialect.Pb35))!;

    IrPassManager.Standard().RunToFixpoint(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    // a%(1) read back the just-stored constant 5; the reload is gone
    Assert.That(fn.AllInstructions.OfType<IrLoad>(), Is.Empty);
  }
}
