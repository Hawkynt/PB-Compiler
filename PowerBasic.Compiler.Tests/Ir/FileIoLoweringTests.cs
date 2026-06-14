using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Sequential file I/O lowering (OPEN/CLOSE/PRINT#/INPUT#) via the runtime-call ABI.</summary>
[TestFixture]
public sealed class FileIoLoweringTests {

  private static IrModule? LowerOptimized(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var module = IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35));
    if (module is not null)
      IrPassManager.Standard().RunOnModule(module);
    return module;
  }

  [Test]
  public void SequentialFileWriteAndRead_LowerToRuntimeCalls() {
    var module = LowerOptimized(
      "OPEN \"out.txt\" FOR OUTPUT AS #1\nFOR i% = 1 TO 5\n PRINT #1, i% * i%\nNEXT i%\nCLOSE #1\n" +
      "OPEN \"out.txt\" FOR INPUT AS #2\nINPUT #2, n%\nPRINT n%\nCLOSE #2\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    var text = LlvmEmitter.Emit(module!);
    Assert.That(text, Does.Contain("@rt_file_open(i32"));
    Assert.That(text, Does.Contain("@rt_fprint_single(i32"));   // i%*i% promotes to SINGLE
    Assert.That(text, Does.Contain("@rt_finput_i16(i32"));
    Assert.That(text, Does.Contain("@rt_file_close(i32"));
  }

  [Test]
  public void Close_WithNoArguments_ClosesAll() {
    var module = LowerOptimized("OPEN \"x\" FOR OUTPUT AS #1\nCLOSE\nEND");

    Assert.That(module, Is.Not.Null);
    Assert.That(LlvmEmitter.Emit(module!), Does.Contain("@rt_file_close_all()"));
  }
}
