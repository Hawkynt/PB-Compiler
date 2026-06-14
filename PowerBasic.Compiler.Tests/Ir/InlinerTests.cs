using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Function inlining of single-block callees.</summary>
[TestFixture]
public sealed class InlinerTests {

  [Test]
  public void Run_InlinesASingleBlockCalleeAndRemovesTheCall() {
    // i32 @sq(i32 n) { ret n*n }  ;  i32 @main() { ret sq(x) }
    var module = new IrModule("T");
    var n = new IrArgument(IrType.I32, 0, "n");
    var sq = new IrFunction("sq", IrType.I32, [n]);
    var sb = new IrBuilder(sq.CreateBlock("entry"));
    sb.Ret(sb.Mul(n, n));
    module.AddFunction(sq);

    var x = new IrArgument(IrType.I32, 0, "x");
    var main = new IrFunction("main", IrType.I32, [x]);
    var mb = new IrBuilder(main.CreateBlock("entry"));
    mb.Ret(mb.Call(IrType.I32, sq, x));
    module.AddFunction(main);

    var inlined = Inliner.Run(module);

    Assert.That(inlined, Is.EqualTo(1));
    Assert.That(main.AllInstructions.OfType<IrCall>(), Is.Empty);
    Assert.That(main.AllInstructions.OfType<IrBinary>().Count(), Is.EqualTo(1));   // the mul, now in main
    Assert.That(IrVerifier.Verify(main), Is.Empty);
    Assert.That(IrPrinter.Print(main), Does.Contain("mul i32 %x, %x"));            // args remapped
  }

  [Test]
  public void Run_InlinesAMultiBlockCalleeWithAReturnPhi() {
    // i32 @f(i32 n) { return n>0 ? 1 : 0 } with two returns -> inlined with a merge phi
    var module = new IrModule("T");
    var n = new IrArgument(IrType.I32, 0, "n");
    var f = new IrFunction("f", IrType.I32, [n]);
    var entry = f.CreateBlock("entry");
    var t = f.CreateBlock("t");
    var e = f.CreateBlock("e");
    new IrBuilder(entry).CondBr(new IrCmp(IrCmpPred.Sgt, n, IrBuilder.ConstI32(0)).Also(entry), t, e);
    new IrBuilder(t).Ret(IrBuilder.ConstI32(1));
    new IrBuilder(e).Ret(IrBuilder.ConstI32(0));
    module.AddFunction(f);
    var main = new IrFunction("main", IrType.I32, [new IrArgument(IrType.I32, 0, "x")]);
    var mb = new IrBuilder(main.CreateBlock("entry"));
    mb.Ret(mb.Call(IrType.I32, f, main.Parameters[0]));
    module.AddFunction(main);

    Assert.That(Inliner.Run(module), Is.EqualTo(1));
    Assert.That(main.AllInstructions.OfType<IrCall>(), Is.Empty);            // call gone
    Assert.That(main.AllInstructions.OfType<IrPhi>().Count(), Is.EqualTo(1)); // returns merged
    Assert.That(IrVerifier.Verify(main), Is.Empty);
  }

  [Test]
  public void Run_DoesNotInlineDirectRecursion() {
    var module = new IrModule("T");
    var n = new IrArgument(IrType.I32, 0, "n");
    var f = new IrFunction("f", IrType.I32, [n]);
    var fb = new IrBuilder(f.CreateBlock("entry"));
    fb.Ret(fb.Call(IrType.I32, f, n));   // f calls itself
    module.AddFunction(f);

    Assert.That(Inliner.Run(module), Is.EqualTo(0));
    Assert.That(f.AllInstructions.OfType<IrCall>().Count(), Is.EqualTo(1));
  }

  [Test]
  public void Pipeline_InlinesAndThenFoldsAcrossTheCall() {
    // sq(5) should fold all the way to a constant once inlined and re-optimized
    var unit = Parser.Parse(Lexer.Tokenize(
      "DECLARE FUNCTION sq%(BYVAL n%)\nr% = sq%(5)\n\nFUNCTION sq%(BYVAL n%)\n  sq% = n% OR n%\nEND FUNCTION", "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var module = IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35))!;
    var pm = IrPassManager.Standard();

    pm.RunOnModule(module);                 // make the callee a clean single block
    Inliner.Run(module);
    pm.RunOnModule(module);                 // fold the exposed body in main

    var main = module.FindFunction("main")!;
    Assert.That(main.AllInstructions.OfType<IrCall>(), Is.Empty);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }
}

internal static class TestExtensions {
  /// <summary>Appends an instruction to a block and returns it (test helper for inline construction).</summary>
  public static T Also<T>(this T inst, IrBasicBlock block) where T : IrInstruction {
    block.Append(inst);
    return inst;
  }
}
