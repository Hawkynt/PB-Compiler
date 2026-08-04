using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// Inline assembly in the IR.
///
/// It is carried as an opaque barrier rather than modelled. A modelled node would need every operand,
/// result and clobber the text implies, and a list one entry short miscompiles silently - the same
/// failure mode as an under-declared machine effect. So the text travels intact, the function is
/// flagged, and the optimizer skips it whole: the trade the direct emitter already makes.
///
/// What this buys is that inline asm stops being a WALL. A program with one <c>!</c> line used to keep
/// every one of its procedures off the IR path; now only the procedure containing it is unoptimized.
/// </summary>
[TestFixture]
public sealed class InlineAsmLoweringTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private const string _withAsm = """
    DIM n AS INTEGER
    n = 1
    ! MOV AX, 5
    ! MOV n, AX
    PRINT n
    """;

  [Test]
  public void Lower_GivenInlineAsm_ThenTheModuleLowersInsteadOfDeclining() {
    var module = IrLowering.TryLowerModule(Bind(_withAsm), out var why);

    Assert.That(module, Is.Not.Null, $"declined: {why}");
  }

  [Test]
  public void Lower_GivenInlineAsm_ThenTheTextTravelsIntact() {
    var module = IrLowering.TryLowerModule(Bind(_withAsm));
    var main = module!.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));

    var asm = main.AllInstructions.OfType<IrInlineAsm>().ToList();
    Assert.That(asm, Has.Count.EqualTo(2));
    Assert.That(asm.Select(a => a.Text.Trim()), Is.EqualTo(new[] { "MOV AX, 5", "MOV n, AX" }));
  }

  [Test]
  public void Lower_GivenInlineAsm_ThenTheFunctionIsMarkedAndTheOptimizerSkipsIt() {
    var module = IrLowering.TryLowerModule(Bind(_withAsm));
    var main = module!.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    Assert.That(main.HasInlineAsm, Is.True);

    var before = IrPrinter.Print(main);
    IrPassManager.Standard().RunOnModule(module);

    Assert.That(IrPrinter.Print(main), Is.EqualTo(before),
      "a function whose asm the IR cannot read must not be rewritten around it");
  }

  /// <summary>
  /// The point of the flag: a procedure WITHOUT asm still optimizes, even though a sibling has it.
  /// Before this, one <c>!</c> anywhere took the whole program off the IR path.
  /// </summary>
  [Test]
  public void Lower_GivenAsmInOneProcedure_ThenTheOthersStillOptimize() {
    var module = IrLowering.TryLowerModule(Bind("""
      FUNCTION Clean%(BYVAL a%)
        Clean% = a% + a%
      END FUNCTION

      SUB Dirty
        ! MOV AX, 5
      END SUB

      PRINT Clean%(3)
      PRINT Clean%(4)
      CALL Dirty
      """));
    Assert.That(module, Is.Not.Null);

    var clean = module!.Functions.First(f => f.Name.Equals("Clean", StringComparison.OrdinalIgnoreCase));
    var dirty = module.Functions.First(f => f.Name.Equals("Dirty", StringComparison.OrdinalIgnoreCase));
    Assert.That(clean.HasInlineAsm, Is.False);
    Assert.That(dirty.HasInlineAsm, Is.True);

    IrPassManager.Standard().RunOnModule(module);
    Assert.That(clean.AllInstructions.OfType<IrAlloca>().ToList(), Is.Empty,
      "the clean procedure should still have been promoted to SSA");
  }

  [Test]
  public void Dce_GivenInlineAsm_ThenItIsNeverDeletedForLookingUnused() {
    var fn = new IrFunction("f", IrType.Void);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var asm = entry.Append(new IrInlineAsm("STI"));
    entry.Append(new IrRet());

    Dce.Run(fn);

    Assert.That(asm.Parent, Is.Not.Null, "it has no users and it still does something");
  }
}
