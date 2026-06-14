using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>GEP simplification (zero-offset elimination).</summary>
[TestFixture]
public sealed class GepSimplifyTests {

  [Test]
  public void GepZeroOffset_FoldsToTheBasePointer() {
    var v = new IrArgument(IrType.I32, 0, "v");
    var fn = new IrFunction("f", IrType.I32, [v]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var slot = b.Alloca(IrType.I32);
    var p = b.Gep(slot, IrBuilder.ConstI32(0));   // zero offset -> the slot itself
    b.Store(v, p);
    b.Ret(b.Load(IrType.I32, p));

    InstCombine.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrGep>(), Is.Empty);   // gep p, 0 removed
  }

  [Test]
  public void Pipeline_BaseIndexArrayElement_DropsTheZeroGep() {
    // a(0) with OPTION BASE 0 has offset (0-0)*size = 0, so the GEP folds to the array base
    var unit = Parser.Parse(Lexer.Tokenize("DIM a%(0 TO 3)\na%(0) = 7\nx% = a%(0)\nEND", "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var fn = IrLowering.TryLowerMainBody(Binder.Bind(unit, Dialect.Pb35))!;

    IrPassManager.Standard().RunToFixpoint(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);   // still well-formed after folding the zero gep
  }
}
