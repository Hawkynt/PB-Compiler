using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// A back-end-compiled function reading a module-level variable. The back end lays out no data of its
/// own - the whole-program codegen does - so a global access resolves at emission to exactly the
/// <c>Mem</c> the direct emitter uses for that symbol. Both paths then address the same storage,
/// which is what lets a routed function and directly-emitted code share state at all.
///
/// The soundness question this had to answer first is whether that cell can be <b>stale</b>. It
/// cannot, for two independent reasons: a global a procedure can see is <c>SHARED</c>, and
/// <c>SsaForm.IsTrackableShape</c> excludes SHARED variables from SSA tracking - so no store to one is
/// ever elided and no read of one is ever folded to a constant; and register residency, which could
/// otherwise hold a value in SI/DI while the cell went stale, requires an SI/DI-clean region, which a
/// call is not.
/// </summary>
[TestFixture]
public sealed class BackendGlobalAccessTests {

  private const string _sharedGlobalProgram = """
    DIM g AS SHARED INTEGER

    FUNCTION AddG%(BYVAL v%)
      AddG% = v% + g
    END FUNCTION

    g = 40
    PRINT AddG%(2)
    """;

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static IrModule Optimized(SemanticModel model) {
    var module = IrLowering.TryLowerModule(model);
    Assert.That(module, Is.Not.Null, "outside the IR lowering's subset");
    IrPassManager.Standard().RunOnModule(module!);
    foreach (var f in module!.Functions)
      if (!f.IsDeclaration)
        IntegerRecovery.Run(f);
    IrPassManager.Standard().RunOnModule(module);
    return module;
  }

  [Test]
  public void Select_GivenFunctionReadingASharedGlobal_ThenAddressesItAsANamedDataCell() {
    var module = Optimized(Bind(_sharedGlobalProgram));
    var fn = module.Functions.First(f => f.Name.Equals("AddG", StringComparison.OrdinalIgnoreCase));

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, $"AddG declined: {reason}");
    var cells = m!.AllInstructions
      .SelectMany(i => i.Operands)
      .OfType<MOperand.DataCell>()
      .ToList();
    Assert.That(cells, Is.Not.Empty, "the global is read through a data cell, not a register-held address");
    Assert.That(cells[0].Name, Does.StartWith("g."), "a module variable keeps the IR's g.<name> spelling");
  }

  [Test]
  public void Select_GivenSynthesizedIrGlobal_ThenDeclinesBecauseItHasNoSymbol() {
    // DATA/READ introduces .data_cursor - an IR global with no PowerBASIC symbol behind it, so there
    // is no cell of the direct emitter's to borrow
    var module = Optimized(Bind("""
      DIM n AS INTEGER
      DATA 7
      READ n
      PRINT n
      """));
    var main = module.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));

    InstructionSelector.TrySelect(main, out var reason);

    Assert.That(reason, Is.Not.Null);
  }

  [Test]
  public void Emit_GivenRoutedGlobalAccess_ThenTheImageAssemblesAndTheBackEndTookTheFunction() {
    var direct = new CodeGenerator(Bind(_sharedGlobalProgram)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(_sharedGlobalProgram)) { Optimize = true, UseExperimentalBackend = true };

    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routedImage, Is.Not.Empty);
    // an unresolved data reference would have thrown while the fixups resolved; a differing image
    // proves the back end really compiled the function rather than falling back
    Assert.That(routedImage, Is.Not.EqualTo(directImage),
      "the back end did not take the global-reading function");
  }
}
