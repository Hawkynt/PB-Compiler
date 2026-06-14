using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Numeric PRINT lowering via a runtime-call ABI (the computation is optimized; output is a runtime call).</summary>
[TestFixture]
public sealed class PrintLoweringTests {

  private static IrModule? LowerModule(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    return IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35));
  }

  [Test]
  public void Print_OfNumbers_EmitsTypedRuntimeCallsAndDeclarations() {
    var module = LowerModule("x% = 21 * 2\nPRINT x%\ny& = 100000\nPRINT y&\nPRINT\nEND");

    Assert.That(module, Is.Not.Null);
    IrPassManager.Standard().RunOnModule(module!);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("call void @rt_print_i16(i16 42)"));    // 21*2 folded, printed as INTEGER
    Assert.That(text, Does.Contain("call void @rt_print_i32(i32 100000)"));
    Assert.That(text, Does.Contain("call void @rt_print_nl()"));
    Assert.That(text, Does.Contain("declare void @rt_print_i16"));
  }

  [Test]
  public void Print_KeepsTheComputationAlive() {
    // a PRINT is a side effect, so the loop feeding it is not eliminated as dead
    var module = LowerModule("s% = 0\nFOR i% = 1 TO 10\n s% = s% + i%\nNEXT i%\nPRINT s%\nEND");

    Assert.That(module, Is.Not.Null);
    IrPassManager.Standard().RunOnModule(module!);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var main = module!.FindFunction("main")!;
    Assert.That(main.AllInstructions.OfType<IrCall>().Any(), Is.True);   // the print survives
  }

  [Test]
  public void Print_TrailingSemicolon_SuppressesTheNewline() {
    var module = LowerModule("PRINT 5;\nEND");

    Assert.That(module, Is.Not.Null);
    var text = LlvmEmitter.Emit(module!);
    // the value is printed but the trailing ';' suppresses the newline, so rt_print_nl is never referenced
    Assert.That(text, Does.Contain("@rt_print_i16"));
    Assert.That(text, Does.Not.Contain("rt_print_nl"));
  }

  [Test]
  public void Print_OfAStringLiteral_EmitsAConstantAndRuntimeCall() {
    var module = LowerModule("PRINT \"Hello, world!\"\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("private constant [13 x i8] c\"Hello, world!\""));
    Assert.That(text, Does.Contain("call void @rt_print_str(ptr @.str0, i32 13)"));
  }

  [Test]
  public void Print_OfAStringVariable_StillDeclines() {
    Assert.That(LowerModule("PRINT s$\nEND"), Is.Null);   // string variables are not supported yet
  }
}
