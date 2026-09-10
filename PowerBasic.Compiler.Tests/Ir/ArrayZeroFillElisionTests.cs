using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class ArrayZeroFillElisionTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36),
      Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static IrModule Lower(string body) {
    var module = IrLowering.TryLowerModule(Bind(body));
    Assert.That(module, Is.Not.Null);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    return module!;
  }

  private static string RuntimeName(IrCall call) => (call.Callee as IrFunction)?.Name ?? string.Empty;

  [Test]
  public void Run_GivenImmediateCompleteNumericFill_ThenUsesNoZeroAllocator() {
    var module = Lower("""
      DIM n AS INTEGER
      DIM i AS INTEGER
      INPUT "", n
      DIM a%(1 TO n)
      FOR i = 1 TO n
        a%(i) = i * 3
      NEXT i
      PRINT a%(1); a%(n)
      END
      """);

    Assert.That(ArrayZeroFillElision.Run(module), Is.EqualTo(1));

    var calls = module.FindFunction("main")!.AllInstructions.OfType<IrCall>().Select(RuntimeName).ToList();
    Assert.Multiple(() => {
      Assert.That(calls, Does.Contain("rt_arr_alloc_nz"));
      Assert.That(calls, Does.Not.Contain("rt_arr_alloc"));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void Standard_GivenImmediateCompleteNumericFill_ThenRunsO0068BeforeMem2Reg() {
    var module = Lower("""
      DIM n AS INTEGER
      DIM i AS INTEGER
      INPUT "", n
      DIM a%(1 TO n)
      FOR i = 1 TO n
        a%(i) = i
      NEXT i
      PRINT a%(n)
      END
      """);

    IrPassManager.Standard().RunOnModule(module);

    var calls = module.FindFunction("main")!.AllInstructions.OfType<IrCall>().Select(RuntimeName).ToList();
    Assert.That(calls, Does.Contain("rt_arr_alloc_nz"));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  [TestCase("FOR i = 2 TO n\n  a%(i) = i\nNEXT i", TestName = "different-lower-bound")]
  [TestCase("FOR i = 1 TO n - 1\n  a%(i) = i\nNEXT i", TestName = "different-upper-bound")]
  [TestCase("FOR i = 1 TO n STEP 2\n  a%(i) = i\nNEXT i", TestName = "non-unit-step")]
  [TestCase("FOR i = 1 TO n\n  a%(i) = a%(i) + 1\nNEXT i", TestName = "reads-array")]
  [TestCase("FOR i = 1 TO n\n  a%(i) = i\n  a%(i) = i + 1\nNEXT i", TestName = "writes-twice")]
  [TestCase("FOR i = 1 TO n\n  IF i > 2 THEN a%(i) = i\nNEXT i", TestName = "conditional-write")]
  public void Run_GivenIncompleteOrObservableFill_ThenKeepsZeroingAllocator(string fill) {
    var module = Lower($"""
      DIM n AS INTEGER
      DIM i AS INTEGER
      INPUT "", n
      DIM a%(1 TO n)
      {fill}
      PRINT a%(1)
      END
      """);

    Assert.That(ArrayZeroFillElision.Run(module), Is.Zero);

    var calls = module.FindFunction("main")!.AllInstructions.OfType<IrCall>().Select(RuntimeName).ToList();
    Assert.Multiple(() => {
      Assert.That(calls, Does.Contain("rt_arr_alloc"));
      Assert.That(calls, Does.Not.Contain("rt_arr_alloc_nz"));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void Run_GivenStringArray_ThenKeepsPointerAllocator() {
    var module = Lower("""
      DIM n AS INTEGER
      DIM i AS INTEGER
      INPUT "", n
      DIM a$(1 TO n)
      FOR i = 1 TO n
        a$(i) = "x"
      NEXT i
      PRINT a$(1)
      END
      """);

    Assert.That(ArrayZeroFillElision.Run(module), Is.Zero);

    var calls = module.FindFunction("main")!.AllInstructions.OfType<IrCall>().Select(RuntimeName).ToList();
    Assert.Multiple(() => {
      Assert.That(calls, Does.Contain("rt_arr_alloc_ptr"));
      Assert.That(calls, Does.Not.Contain("rt_arr_alloc_nz"));
    });
  }
}
