using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// An x86 MOV of an immediate to private BASIC storage does not write any CPU
/// register or status flag. It is the first inline-ASM source pattern that can
/// safely enter the ordinary native SSA optimizer rather than remaining opaque.
/// </summary>
[TestFixture]
public sealed class InlineAsmPureStorePromotionTests {
  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36),
      "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    return model;
  }

  [Test]
  public void Optimize_GivenOnlyPureImmediateAsmStores_ThenExposesAllWritesToSsa() {
    var module = IrLowering.TryLowerModule(Bind("""
      DIM n AS INTEGER
      ! MOV n, 1
      ! MOV n, 2 ; the first store is redundant
      PRINT n
      END
      """), out var reason);
    Assert.That(module, Is.Not.Null, reason);
    var main = module!.FindFunction("main")!;
    Assert.That(main.HasInlineAsm, Is.True);

    IrMiddleEndPipeline.RunNativeModule(module, optimize: true,
      recoverIntegerArithmetic: true);

    Assert.Multiple(() => {
      Assert.That(main.HasInlineAsm, Is.False);
      Assert.That(main.AllInstructions.OfType<IrInlineAsm>(), Is.Empty);
      Assert.That(main.AllInstructions.OfType<IrStore>()
        .Any(store => store.Value is IrConstantInt { Value: 1 }), Is.False,
        "ordinary DSE can eliminate a superseded private store");
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [TestCase("! MOV AX, 7", "register state is observable")]
  [TestCase("! CMP AX, 7", "flags are observable")]
  [TestCase("! MOV n, &H0007", "unmodeled immediate form")]
  [TestCase("! NOP", "opaque instruction")]
  public void Optimize_GivenAnyUnrecognizedInlineAsm_ThenKeepsTheEntireFunctionOpaque(
      string other, string because) {
    var module = IrLowering.TryLowerModule(Bind($"""
      DIM n AS INTEGER
      ! MOV n, 1
      {other}
      ! MOV n, 2
      PRINT n
      END
      """), out var reason);
    Assert.That(module, Is.Not.Null, reason);

    IrMiddleEndPipeline.RunNativeModule(module!, optimize: true,
      recoverIntegerArithmetic: true);
    var main = module!.FindFunction("main")!;

    Assert.Multiple(() => {
      Assert.That(main.HasInlineAsm, Is.True, because);
      Assert.That(main.AllInstructions.OfType<IrInlineAsm>().Count(), Is.EqualTo(3),
        "partial rewriting would change register/flag and ordering contracts");
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void Optimize_GivenASharedVariable_ThenKeepsTheAsmStoreOpaque() {
    var module = IrLowering.TryLowerModule(Bind("""
      DIM n AS SHARED INTEGER
      ! MOV n, 7
      PRINT n
      END
      """), out var reason);
    Assert.That(module, Is.Not.Null, reason);

    IrMiddleEndPipeline.RunNativeModule(module!, optimize: true,
      recoverIntegerArithmetic: true);
    var main = module!.FindFunction("main")!;

    Assert.Multiple(() => {
      Assert.That(main.HasInlineAsm, Is.True);
      Assert.That(main.AllInstructions.OfType<IrInlineAsm>().Count(), Is.EqualTo(1));
    });
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenAsmStoreAndBasicRead_ThenTheOptimizedAndFaithfulResultsAgree(
      bool optimize) {
    var model = Bind("""
      DIM n AS INTEGER
      n = 5
      ! MOV n, -1
      ! MOV n, 2
      PRINT n
      END
      """);
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    Assert.That(Cpu8086.Run(image).Output.Trim(), Is.EqualTo("2"));
  }
}
