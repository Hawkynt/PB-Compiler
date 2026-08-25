using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>mem2reg: promotes alloca/load/store slots to SSA registers + phis.</summary>
[TestFixture]
public sealed class Mem2RegTests {

  private static IrFunction LowerAndPromote(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb35), "TEST.BAS", Dialect.Pb35);
    var model = Binder.Bind(unit, Dialect.Pb35);
    var fn = IrLowering.TryLowerMainBody(model)!;
    Mem2Reg.Run(fn);
    return fn;
  }

  private static int Count<T>(IrFunction fn) => fn.AllInstructions.OfType<T>().Count();

  [Test]
  public void Run_RemovesAllPromotableAllocasAndKeepsVerifiable() {
    var fn = LowerAndPromote("s% = 0\nFOR i% = 1 TO 10\n  s% = s% + i%\nNEXT i%");

    Assert.That(Count<IrAlloca>(fn), Is.EqualTo(0));
    Assert.That(Count<IrLoad>(fn), Is.EqualTo(0));
    Assert.That(Count<IrStore>(fn), Is.EqualTo(0));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Run_OverLoop_IntroducesPhisForCounterAndAccumulator() {
    var fn = LowerAndPromote("s% = 0\nFOR i% = 1 TO 10\n  s% = s% + i%\nNEXT i%");

    var text = IrPrinter.Print(fn);
    Assert.That(Count<IrPhi>(fn), Is.GreaterThanOrEqualTo(2));   // counter + accumulator
    Assert.That(text, Does.Contain("phi i16"));
    Assert.That(text, Does.Not.Contain("alloca"));
  }

  [Test]
  public void Run_OverStraightLine_FullyPromotesAndPropagatesStoredValue() {
    // x is read once then both are dead: promotion removes all memory traffic
    var fn = LowerAndPromote("x% = 5\ny% = x%");

    Assert.That(IrPrinter.Print(fn), Is.EqualTo(
      "define void @main() {\n" +
      "entry:\n" +
      "  ret void\n" +
      "}\n"));
  }

  [Test]
  public void Run_OverIfElse_MergesWithAPhiAtTheJoin() {
    // r gets one value on each arm; mem2reg merges them with a phi at if.end
    var fn = LowerAndPromote(
      "r% = 0\nIF r% = 0 THEN\n  r% = 1\nELSE\n  r% = 2\nEND IF\nq% = r%");

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(Count<IrAlloca>(fn), Is.EqualTo(0));
    Assert.That(Count<IrPhi>(fn), Is.GreaterThanOrEqualTo(1));
  }

  [Test]
  public void RunForFaithfulSelection_GivenWriteOnlySourceAndTemporarySlots_ThenRetainsOnlySourceStorage() {
    var fn = new IrFunction("main", IrType.Void);
    var builder = new IrBuilder(fn.CreateBlock("entry"));
    var source = builder.Alloca(IrType.I16);
    source.IsSourceVariable = true;
    builder.Store(new IrConstantInt(IrType.I16, 42), source);
    var temporary = builder.Alloca(IrType.I16);
    builder.Store(new IrConstantInt(IrType.I16, 7), temporary);
    builder.Ret();

    Mem2Reg.RunForFaithfulSelection(fn);

    Assert.Multiple(() => {
      Assert.That(fn.AllInstructions.OfType<IrAlloca>(), Is.EquivalentTo(new[] { source }));
      Assert.That(fn.AllInstructions.OfType<IrStore>().Single().Pointer, Is.SameAs(source));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }
}
