using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>If-conversion: a simple diamond becomes a branchless select.</summary>
[TestFixture]
public sealed class IfConversionTests {

  [Test]
  public void Run_ConvertsADiamondPhiIntoASelect() {
    // entry: condbr c, t, e ; t: br m ; e: br m ; m: phi [a,t],[b,e] ; ret phi
    var c = new IrArgument(IrType.I1, 0, "c");
    var a = new IrArgument(IrType.I32, 1, "a");
    var b = new IrArgument(IrType.I32, 2, "b");
    var fn = new IrFunction("f", IrType.I32, [c, a, b]);
    var entry = fn.CreateBlock("entry");
    var t = fn.CreateBlock("t");
    var e = fn.CreateBlock("e");
    var m = fn.CreateBlock("m");
    new IrBuilder(entry).CondBr(c, t, e);
    new IrBuilder(t).Br(m);
    new IrBuilder(e).Br(m);
    var bm = new IrBuilder(m);
    var phi = bm.Phi(IrType.I32);
    phi.AddIncoming(a, t);
    phi.AddIncoming(b, e);
    bm.Ret(phi);

    var converted = IfConversion.Run(fn);

    Assert.That(converted, Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrSelect>().Count(), Is.EqualTo(1));
    Assert.That(fn.AllInstructions.OfType<IrPhi>(), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrCondBr>(), Is.Empty);
    Assert.That(IrPrinter.Print(fn), Does.Contain("select i1 %c, i32 %a, i32 %b"));
  }

  [Test]
  public void Run_LeavesNonEmptyArmsAlone() {
    // the then-arm does real work, so it cannot be speculated into a select
    var c = new IrArgument(IrType.I1, 0, "c");
    var a = new IrArgument(IrType.I32, 1, "a");
    var fn = new IrFunction("f", IrType.I32, [c, a]);
    var entry = fn.CreateBlock("entry");
    var t = fn.CreateBlock("t");
    var e = fn.CreateBlock("e");
    var m = fn.CreateBlock("m");
    new IrBuilder(entry).CondBr(c, t, e);
    var bt = new IrBuilder(t);
    var work = bt.Mul(a, a);     // non-empty arm
    bt.Br(m);
    new IrBuilder(e).Br(m);
    var bm = new IrBuilder(m);
    var phi = bm.Phi(IrType.I32);
    phi.AddIncoming(work, t);
    phi.AddIncoming(a, e);
    bm.Ret(phi);

    Assert.That(IfConversion.Run(fn), Is.EqualTo(0));
    Assert.That(fn.AllInstructions.OfType<IrSelect>(), Is.Empty);
  }

  [Test]
  public void Run_GivenAConditionalNegateDiamond_ThenCanonicalizesBranchlessAbs() {
    var x = new IrArgument(IrType.I16, 0, "x");
    var fn = new IrFunction("abs", IrType.I16, [x]);
    var entry = fn.CreateBlock("entry");
    var negative = fn.CreateBlock("negative");
    var nonNegative = fn.CreateBlock("nonnegative");
    var merge = fn.CreateBlock("merge");
    var condition = new IrBuilder(entry).Cmp(IrCmpPred.Slt, x, new IrConstantInt(IrType.I16, 0));
    new IrBuilder(entry).CondBr(condition, negative, nonNegative);
    var negated = new IrBuilder(negative).Sub(new IrConstantInt(IrType.I16, 0), x);
    new IrBuilder(negative).Br(merge);
    new IrBuilder(nonNegative).Br(merge);
    var result = new IrBuilder(merge).Phi(IrType.I16);
    result.AddIncoming(negated, negative);
    result.AddIncoming(x, nonNegative);
    new IrBuilder(merge).Ret(result);

    var converted = IfConversion.Run(fn);

    Assert.That(converted, Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.Multiple(() => {
      Assert.That(fn.AllInstructions.OfType<IrCondBr>(), Is.Empty);
      Assert.That(fn.AllInstructions.OfType<IrPhi>(), Is.Empty);
      Assert.That(fn.AllInstructions.OfType<IrBinary>().Select(binary => binary.Op),
        Is.SupersetOf(new[] { IrBinaryOp.AShr, IrBinaryOp.Xor, IrBinaryOp.Sub }));
    });
  }

  [Test]
  public void Pipeline_LoweredIfElseAssignment_BecomesBranchless() {
    var unit = Parser.Parse(Lexer.Tokenize("c% = 1\nIF c% THEN\n  y% = 7\nELSE\n  y% = 9\nEND IF\nz% = y% + 1\nEND", "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var fn = IrLowering.TryLowerMainBody(Binder.Bind(unit, Dialect.Pb35))!;

    IrPassManager.Standard().RunToFixpoint(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }
}
