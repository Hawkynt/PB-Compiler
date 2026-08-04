using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Spilling on the x86-16 back end. The allocation failure that matters on this target is not "six
/// registers ran out" - it is a <c>CALL</c>, which destroys the whole caller-saved file, so a value
/// live across one may sit in none of them. Before spilling existed those functions were selected and
/// then dropped; the census called that out by reporting routed separately from selected.
///
/// x86 is a memory-operand machine, so a spilled value needs no reload code: it simply becomes its
/// frame cell. A parameter's cell is free - the caller already pushed it, and an IR argument is an SSA
/// value nothing writes - so a spilled parameter loses its prologue copy and its uses address
/// <c>[BP+offset]</c> directly.
/// </summary>
[TestFixture]
public sealed class BackendSpillTests {

  // v% is loaded in the prologue and used AFTER the print - so it is live across a call that
  // destroys every allocatable register
  private const string _liveAcrossACall = """
    FUNCTION Twice%(BYVAL v%)
      PRINT "X"
      Twice% = v% + v%
    END FUNCTION

    PRINT Twice%(21)
    """;

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static MFunction Select(string source, string function) {
    var module = IrLowering.TryLowerModule(Bind(source));
    Assert.That(module, Is.Not.Null, "outside the IR lowering's subset");
    IrPassManager.Standard().RunOnModule(module!);
    foreach (var f in module!.Functions)
      if (!f.IsDeclaration)
        IntegerRecovery.Run(f);
    IrPassManager.Standard().RunOnModule(module);
    var fn = module.Functions.First(f => f.Name.Equals(function, StringComparison.OrdinalIgnoreCase));
    var m = InstructionSelector.TrySelect(fn, out var reason);
    Assert.That(m, Is.Not.Null, $"{function} declined: {reason}");
    return m!;
  }

  [Test]
  public void Allocate_GivenAParameterLiveAcrossACall_ThenSpillsItToTheCallersOwnWord() {
    var m = Select(_liveAcrossACall, "Twice");
    Assert.That(m.ArgumentLoads, Is.Not.Empty, "the parameter starts out in a register");

    var allocation = LinearScanAllocator.Allocate(m);

    Assert.That(allocation, Is.Not.Null, "the function should route once the parameter can move to memory");
    Assert.That(m.AllInstructions.SelectMany(i => i.Operands).OfType<MOperand.ParamCell>().ToList(),
      Is.Not.Empty, "the uses address [BP+offset] directly");
    Assert.That(m.ArgumentLoads, Is.Empty, "a spilled parameter is not copied into a register at all");
  }

  [Test]
  public void Allocate_GivenNoPressure_ThenSpillsNothing() {
    var m = Select("""
      FUNCTION Twice%(BYVAL v%)
        Twice% = v% + v%
      END FUNCTION

      PRINT Twice%(21)
      """, "Twice");

    var allocation = LinearScanAllocator.Allocate(m);

    Assert.That(allocation, Is.Not.Null);
    Assert.That(m.ArgumentLoads, Is.Not.Empty, "nothing forced the parameter out of its register");
    Assert.That(m.AllInstructions.SelectMany(i => i.Operands).OfType<MOperand.ParamCell>(), Is.Empty);
    Assert.That(m.StackSlots, Is.Empty);
  }

  [Test]
  public void Allocate_GivenASpill_ThenTheInstructionStillHasAtMostOneMemoryOperand() {
    var m = Select(_liveAcrossACall, "Twice");

    LinearScanAllocator.Allocate(m);

    foreach (var instr in m.AllInstructions) {
      var memory = instr.Operands.Count(o =>
        o is MOperand.Memory or MOperand.StackSlot or MOperand.DataCell or MOperand.ParamCell);
      Assert.That(memory, Is.LessThanOrEqualTo(1), $"{instr.Opcode} would be a memory-to-memory instruction");
    }
  }

  [Test]
  public void Emit_GivenASpilledParameter_ThenTheImageAssemblesAndTheBackEndTookTheFunction() {
    var direct = new CodeGenerator(Bind(_liveAcrossACall)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(_liveAcrossACall)) { Optimize = true, UseExperimentalBackend = true };

    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routedImage, Is.Not.Empty);
    Assert.That(routed.BackendRoutedNames, Does.Contain("Twice"), "the back end did not take the function");
    // it emits its own code for the body - a spilled parameter read straight from [BP+6] rather than
    // reloaded around the call - and the whole image still assembles and links
    Assert.That(routedImage, Is.Not.EqualTo(directImage));
  }
}
