using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// <c>GOTO DWORD</c> / <c>GOSUB DWORD</c>: a jump to an address computed at run time.
///
/// <para>
/// The lowering lists every label of the function as a possible target. That list does not steer the
/// branch - the selector jumps through the address register whatever is listed - it exists so the CFG
/// does not call those blocks unreachable, which is what reachability, liveness and phi placement all
/// read. Listing all of them cannot miss one, and missing one is the only error that matters.
/// </para>
/// <para>
/// A function with NO labels used to decline, on the reasoning that a computed jump had nowhere to go.
/// It has somewhere to go; it is just not in this function - <c>CODEPTR32</c> of a PROCEDURE is the
/// case that reasoning missed. An empty list says exactly that, and it is sound precisely BECAUSE
/// there are no labels: the danger an empty list would pose is a label block the jump can reach being
/// marked unreachable and deleted, and that needs a label block to exist.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendComputedJumpTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(
      Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static (string Output, IEnumerable<string> Routed) Run(string source, bool optimize, bool routed) {
    var generator = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = routed };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return (Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|"), generator.BackendRoutedNames.ToList());
  }

  /// <summary>
  /// The case the relaxation must not have broken, and the reason it is written first: a jump to a
  /// LABEL still reaches it. The label block survives only because it is still listed as a target -
  /// nothing else in the program branches to it, so an optimizer told it was unreachable would delete
  /// it and the jump would land in whatever took its place.
  /// </summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Execute_GivenAComputedJumpToALabel_WhenRouted_ThenItArrives(bool optimize) {
    const string source = """
      DIM gp AS DWORD
      gp = CODEPTR32(Landing)
      PRINT "before"
      GOTO DWORD gp
      PRINT "skipped"
      Landing:
      PRINT "arrived"
      """;

    var (output, routed) = Run(source, optimize, routed: true);
    Assert.That(routed, Does.Contain("main"));
    Assert.Multiple(() => {
      Assert.That(output, Is.EqualTo(Run(source, optimize, routed: false).Output));
      Assert.That(output, Is.EqualTo("before|arrived"),
        "the jump reached the label and the statement between them did not run");
    });
  }

  /// <summary>
  /// And the case that used to decline: the target is a PROCEDURE, so no block of this function
  /// follows the jump. It is asserted at the machine level rather than by running it, because where
  /// control goes afterwards is the program's business and not the compiler's - the procedure's own
  /// return pops whatever the jump left on the stack, which is true of the direct emitter's far jump
  /// too. What the back end owes is an indirect jump rather than a fallthrough, and that is checkable.
  /// </summary>
  [Test]
  public void Select_GivenAComputedJumpOutOfTheFunction_ThenItEmitsAnIndirectJump() {
    var model = Bind("""
      DECLARE SUB Elsewhere()
      DIM gp AS DWORD
      gp = CODEPTR32(Elsewhere)
      GOTO DWORD gp
      END
      SUB Elsewhere()
        PRINT "x"
      END SUB
      """);
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    IrMiddleEndPipeline.Standard().RunOnModule(module!);

    var main = module!.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    var jump = main.Blocks.Select(b => b.Terminator).OfType<IrIndirectBr>().Single();
    Assert.That(jump.Targets, Is.Empty, "no block of this function follows a jump that leaves it");

    var machine = InstructionSelector.TrySelect(main, out var reason);
    Assert.That(machine, Is.Not.Null, $"selection declined: {reason}");
    Assert.That(machine!.AllInstructions.Any(i => i.Opcode == MOpcode.JmpIndirect), Is.True,
      "the jump must be an indirect one, not a fallthrough");
  }
}
