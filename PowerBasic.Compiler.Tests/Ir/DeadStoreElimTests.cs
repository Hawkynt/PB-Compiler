using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Intra-block dead-store elimination for memory (DeadStoreElim).</summary>
[TestFixture]
public sealed class DeadStoreElimTests {

  [Test]
  public void OverwrittenStore_WithNoInterveningLoad_IsRemoved() {
    var x = new IrArgument(IrType.I32, 0, "x");
    var y = new IrArgument(IrType.I32, 1, "y");
    var fn = new IrFunction("f", IrType.Void, [x, y]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var p = b.Gep(b.Alloca(IrType.I32), IrBuilder.ConstI32(0));
    b.Store(x, p);      // dead: overwritten below before any read
    b.Store(y, p);
    b.Ret();

    var removed = DeadStoreElim.Run(fn);

    Assert.That(removed, Is.EqualTo(1));
    Assert.That(fn.AllInstructions.OfType<IrStore>().Count(), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Store_ObservedByALoad_IsKept() {
    var x = new IrArgument(IrType.I32, 0, "x");
    var y = new IrArgument(IrType.I32, 1, "y");
    var fn = new IrFunction("f", IrType.I32, [x, y]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var p = b.Gep(b.Alloca(IrType.I32), IrBuilder.ConstI32(0));
    b.Store(x, p);
    var l = b.Load(IrType.I32, p);   // observes the first store
    b.Store(y, p);
    b.Ret(l);

    Assert.That(DeadStoreElim.Run(fn), Is.EqualTo(0));
    Assert.That(fn.AllInstructions.OfType<IrStore>().Count(), Is.EqualTo(2));
  }

  [Test]
  public void Store_AcrossACall_IsKept() {
    var x = new IrArgument(IrType.I32, 0, "x");
    var y = new IrArgument(IrType.I32, 1, "y");
    var callee = new IrFunction("g", IrType.Void);
    var fn = new IrFunction("f", IrType.Void, [x, y]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var p = b.Gep(b.Alloca(IrType.I32), IrBuilder.ConstI32(0));
    b.Store(x, p);
    b.Call(IrType.Void, callee);     // may read memory
    b.Store(y, p);
    b.Ret();

    Assert.That(DeadStoreElim.Run(fn), Is.EqualTo(0));
  }

  [Test]
  public void PartialOverwriteAtSameAddress_DoesNotKillWiderStore() {
    var word = new IrArgument(IrType.I16, 0, "word");
    var lowByte = new IrArgument(IrType.I8, 1, "lowByte");
    var fn = new IrFunction("f", IrType.Void, [word, lowByte]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var p = b.Alloca(IrType.I16);
    b.Store(word, p);
    b.Store(lowByte, p);             // same start, but one byte of the original word remains live
    b.Ret();

    var removed = DeadStoreElim.Run(fn);

    Assert.That(removed, Is.EqualTo(0));
    Assert.That(fn.AllInstructions.OfType<IrStore>().Count(), Is.EqualTo(2));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Pipeline_DoubleAssignedArrayElement_DropsTheDeadStore() {
    var unit = Parser.Parse(Lexer.Tokenize("DIM a%(0 TO 3)\na%(1) = 1\na%(1) = 2\nx% = a%(1)\nEND", "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var fn = IrLowering.TryLowerMainBody(Binder.Bind(unit, Dialect.Pb35))!;

    IrPassManager.Standard().RunToFixpoint(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrStore>().Count(), Is.LessThanOrEqualTo(1));  // the a%(1)=1 store is dead
  }
}
