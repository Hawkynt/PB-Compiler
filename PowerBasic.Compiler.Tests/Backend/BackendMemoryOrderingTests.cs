using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The one aliasing rule <see cref="MachineScheduler"/> has - order any pair where at least one side
/// writes memory - and the effect descriptors it reads that rule out of.
///
/// <para>
/// The rule is only as good as the descriptors: an instruction that touches memory and says it does
/// not is invisible to it. A 32-bit load said exactly that. <see cref="MOperand.DataCell"/>,
/// <see cref="MOperand.StackSlot"/> and <see cref="MOperand.ParamCell"/> are memory accesses that
/// name their address instead of holding it in a register, and the effect builders tested for
/// <see cref="MOperand.Memory"/> alone - the register-addressed form. The 16-bit load path was
/// unaffected because it hard-codes <c>ReadsMemory: true</c> and never asks, which is why an INTEGER
/// accumulator was right and a LONG one was not.
/// </para>
/// <para>
/// What that cost: <c>acc = acc + n : PRINT acc</c> over a LONG <c>STATIC</c> or <c>SHARED</c> read
/// the cell BEFORE the store that had just been written to it, so a routed procedure printed the
/// value from the previous call. It is a scheduling fault and therefore the x86-16 back end's alone -
/// <c>--emit-c</c> and <c>--emit-llvm</c> consume the IR, which was correct throughout.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendMemoryOrderingTests {

  /// <summary>
  /// A LONG accumulator a routed SUB advances and then prints. The optimizer is off in one of the two
  /// runs on purpose: with it on the inliner and the folding can remove the second read altogether,
  /// which is what kept the whole class out of sight.
  /// </summary>
  private const string _staticLongProgram = """
    DECLARE SUB Bump()
    Bump
    Bump
    PRINT
    END

    SUB Bump() NOINLINE
      STATIC acc AS LONG
      acc = acc + 10
      PRINT acc;
    END SUB
    """;

  private const string _sharedLongProgram = """
    DECLARE FUNCTION Given%(BYVAL v%)
    DECLARE SUB Bump(BYVAL by%)
    DIM acc AS SHARED LONG
    Bump Given%(10)
    Bump Given%(1)
    PRINT
    END

    FUNCTION Given%(BYVAL v%) NOINLINE
      Given% = v%
    END FUNCTION
    SUB Bump(BYVAL by%) NOINLINE
      acc = acc + by%
      PRINT acc;
    END SUB
    """;

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static (string Direct, string Routed, IEnumerable<string> Names) RunBothWays(string source, bool optimize) {
    var direct = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = true };
    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();
    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    return (Cpu8086.Run(directImage).Output, Cpu8086.Run(routedImage).Output, routed.BackendRoutedNames.ToList());
  }

  [TestCase(true)]
  [TestCase(false)]
  public void Run_GivenAStaticLongAdvancedThenPrinted_WhenRouted_ThenItPrintsTheNewValue(bool optimize) {
    var (direct, routed, names) = RunBothWays(_staticLongProgram, optimize);

    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("Bump"), "the back end did not take the procedure under test");
      Assert.That(routed, Is.EqualTo(direct));
      Assert.That(direct.Trim(), Is.EqualTo("10  20"), "each call prints the total it has just added to");
    });
  }

  [TestCase(true)]
  [TestCase(false)]
  public void Run_GivenASharedLongAdvancedThenPrinted_WhenRouted_ThenItPrintsTheNewValue(bool optimize) {
    var (direct, routed, names) = RunBothWays(_sharedLongProgram, optimize);

    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("Bump"));
      Assert.That(routed, Is.EqualTo(direct));
      Assert.That(direct.Trim(), Is.EqualTo("10  11"));
    });
  }

  /// <summary>
  /// The invariant underneath both, asserted where it lives: every instruction carrying a memory
  /// operand claims to touch memory. Stated over the whole selected function rather than over the one
  /// load that was wrong, because the fault was a builder's test and there are a dozen builders.
  /// </summary>
  [Test]
  public void Select_GivenAWideGlobalAccess_ThenEveryMemoryOperandIsDeclaredAsOne() {
    var module = IrLowering.TryLowerModule(Bind(_staticLongProgram));
    Assert.That(module, Is.Not.Null, "outside the IR lowering's subset");
    IrPassManager.Legalize().RunOnModule(module!);
    var fn = module!.Functions.First(f => f.Name.Equals("Bump", StringComparison.OrdinalIgnoreCase));

    var machine = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(machine, Is.Not.Null, $"Bump declined: {reason}");
    var silent = machine!.AllInstructions
      .Where(i => i.Operands.Any(o => o.IsMemoryAccess()))
      .Where(i => !i.Effect.ReadsMemory && !i.Effect.WritesMemory)
      .ToList();
    Assert.That(silent, Is.Empty,
      "an instruction touching memory that says it does not is invisible to the scheduler's only "
      + "aliasing rule: " + string.Join(" | ", silent));
  }
}
