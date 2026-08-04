using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// A float that flows out of a loop or a branch is a PHI, and a phi on this target cannot be a
/// register: floats live in frame cells. So the edge copies that take SSA apart are FLD/FSTP through
/// the phi's cell rather than register moves.
///
/// There is no copy CYCLE to guard against the way there is for registers - each edge copy is a
/// complete load-and-store, so nothing is half-overwritten in between.
/// </summary>
[TestFixture]
public sealed class BackendFloatPhiTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  // the DOUBLE is carried across the back edge, so mem2reg makes it a phi
  private const string _carriesAFloat = """
    DIM t AS DOUBLE
    DIM i AS INTEGER
    t = 1
    FOR i = 1 TO 40
      t = t + 0.5
    NEXT i
    PRINT t
    """;

  [Test]
  public void Phi_GivenAFloatCarriedRoundALoop_ThenItSelectsThroughAFrameCell() {
    var module = IrLowering.TryLowerModule(Bind(_carriesAFloat), out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    IrPassManager.Standard().RunOnModule(module!);

    var main = module!.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    Assert.That(main.AllInstructions.OfType<IrPhi>().Any(p => p.Type.IsFloat), Is.True,
      "the program should have produced a float phi to begin with");

    var m = InstructionSelector.TrySelect(main, out var reason);
    Assert.That(m, Is.Not.Null, $"selection declined: {reason}");
    Assert.That(m!.AllInstructions.Any(i => i.Opcode == MOpcode.Fstp), "the edge copy stores through the cell");
  }

  /// <summary>And it prints the same number either way, which is the only claim that matters.</summary>
  [Test]
  public void Phi_GivenAFloatCarriedRoundALoop_ThenBothPathsPrintTheSame() {
    string Run(bool routed) {
      var cg = new CodeGenerator(Bind(_carriesAFloat)) { Optimize = true, UseExperimentalBackend = routed };
      var image = cg.EmitExecutable();
      Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
      return Cpu8086.Run(image).Output.Trim();
    }

    Assert.That(Run(routed: true), Is.EqualTo(Run(routed: false)));
  }
}
