using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>ON ... GOTO lowering (computed jump via a switch) and constant-selector folding.</summary>
[TestFixture]
public sealed class OnGotoLoweringTests {

  private static IrModule? LowerModule(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    return IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35));
  }

  private const string Program =
    "ON n% GOTO one, two, three\nx% = 0\nGOTO done\n" +
    "one:\nx% = 11\nGOTO done\n" +
    "two:\nx% = 22\nGOTO done\n" +
    "three:\nx% = 33\n" +
    "done:\nPRINT x%\nEND";

  [Test]
  public void OnGoto_WithVariableSelector_EmitsASwitch() {
    var module = LowerModule("n% = 2\n" + Program);

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    // before optimization the switch dispatch is present (1-based cases to the labels)
    Assert.That(LlvmEmitter.Emit(module!), Does.Contain("switch i16"));
  }

  [Test]
  public void OnGoto_WithConstantSelector_FoldsToTheChosenArm() {
    var module = LowerModule("n% = 2\n" + Program);
    IrPassManager.Standard().RunOnModule(module!);

    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Not.Contain("switch"));               // selector known: switch folded away
    Assert.That(text, Does.Contain("@rt_print_i16(i16 22)"));    // n=2 -> the 'two' arm -> 22
  }

  [Test]
  public void OnGoto_OutOfRangeSelector_FallsThrough() {
    var module = LowerModule("n% = 9\n" + Program);
    IrPassManager.Standard().RunOnModule(module!);

    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    // selector 9 has no case -> falls through, x stays 0
    Assert.That(LlvmEmitter.Emit(module!), Does.Contain("@rt_print_i16(i16 0)"));
  }

  [Test]
  public void Switch_ConstantSelector_FoldsInSimplifyCfg() {
    var fn = new IrFunction("f", IrType.I32);
    var entry = fn.CreateBlock("entry");
    var a = fn.CreateBlock("a");
    var b = fn.CreateBlock("b");
    var def = fn.CreateBlock("def");
    var sw = new IrSwitch(IrBuilder.ConstI32(2), def);
    entry.Append(sw);
    sw.AddCase(1, a);
    sw.AddCase(2, b);
    new IrBuilder(a).Ret(IrBuilder.ConstI32(10));
    new IrBuilder(b).Ret(IrBuilder.ConstI32(20));
    new IrBuilder(def).Ret(IrBuilder.ConstI32(0));

    SimplifyCfg.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrSwitch>(), Is.Empty);   // folded to br b
    Assert.That(IrPrinter.Print(fn), Does.Contain("ret i32 20"));
    Assert.That(IrPrinter.Print(fn), Does.Not.Contain("ret i32 10"));
  }
}
