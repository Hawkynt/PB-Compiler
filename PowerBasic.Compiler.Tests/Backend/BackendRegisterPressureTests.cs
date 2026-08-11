using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Register PRESSURE on the x86-16 back end, as distinct from the CALL-driven spilling next door in
/// <see cref="BackendSpillTests"/>: too many values wanted at once, with no call anywhere near them.
///
/// A 32-bit accumulation over an array is the shape that finds it. The selector writes it serially -
/// load an element, sign-extend it into a register PAIR, add the pair, move to the next - which needs
/// four registers at a time however long the array is. With constant bounds the loop is unrolled, so
/// every one of those loads is independent and ready at the top of the block, and a list scheduler
/// maximizing independence will issue them all there: ten live values on a six-register machine.
///
/// Nothing downstream can undo that. A loaded element's defining instruction already carries a memory
/// operand, so the value cannot itself become one; and with the loop unrolled there is no call between
/// the load and its use for live-range splitting to split around. The pressure has to not be created,
/// which is why <see cref="MachineScheduler"/> now weighs a reordering against the register file.
/// </summary>
[TestFixture]
public sealed class BackendRegisterPressureTests {

  /// <summary>
  /// The accumulation that kept DIFF56 off the back end. Elements of 3000 each keep every element
  /// inside an INTEGER while the sum - 3000 * (1+2+...+10) = 165000 - does not fit in one, which is
  /// the whole point of accumulating into a LONG rather than an INTEGER.
  /// </summary>
  private const string _longAccumulationOverAStaticArray = """
    DIM a%(1 TO 10)
    FOR i% = 1 TO 10
      a%(i%) = i% * 3000
    NEXT i%
    s& = 0
    FOR i% = 1 TO 10
      s& = s& + a%(i%)
    NEXT i%
    PRINT "sum"; s&
    """;

  /// <summary>
  /// Four LONG accumulators wanted at the same moment - eight words on a six-register machine, so
  /// something must reach the frame - and all four still live across the runtime call that prints the
  /// first of them. Written as a procedure called twice with different seeds so interprocedural
  /// constant propagation cannot fold the array away and leave a test of nothing.
  /// </summary>
  private const string _fourSimultaneousLongAccumulators = """
    SUB Accumulate(BYVAL seed%)
      DIM a%(1 TO 8)
      FOR i% = 1 TO 8
        a%(i%) = i% * seed%
      NEXT i%
      p& = a%(1) + a%(2)
      q& = a%(3) + a%(4)
      r& = a%(5) + a%(6)
      t& = a%(7) + a%(8)
      PRINT p&; q&; r&; t&; p& + q& + r& + t&
    END SUB

    Accumulate 1000
    Accumulate 2000
    """;

  /// <summary>
  /// A LONG that outlives a CALL: both of its words are live across a runtime print, so both must
  /// survive a caller-saved file the call destroys entirely. Two call sites again, so the argument
  /// stays an argument.
  /// </summary>
  private const string _longLiveAcrossACall = """
    FUNCTION Scaled&(BYVAL base%)
      value& = base%
      value& = value& * 1000
      PRINT "step"
      Scaled& = value& + 7
    END FUNCTION

    PRINT Scaled&(70)
    PRINT Scaled&(80)
    """;

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36),
      Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static MFunction Select(string source, string function) {
    var module = IrLowering.TryLowerModule(Bind(source), out var why);
    Assert.That(module, Is.Not.Null, "outside the IR lowering's subset: " + why);
    IrPassManager.Standard().RunOnModule(module!);
    foreach (var f in module!.Functions)
      if (!f.IsDeclaration)
        IntegerRecovery.Run(f);
    IrPassManager.Standard().RunOnModule(module);
    var fn = module.Functions.First(f => f.Name.Equals(function, StringComparison.OrdinalIgnoreCase));
    var machine = InstructionSelector.TrySelect(fn, out var reason);
    Assert.That(machine, Is.Not.Null, $"{function} declined at selection: {reason}");
    return machine!;
  }

  private static (Cpu8086 Direct, Cpu8086 Routed, CodeGenerator Generator) RunBothWays(string source) {
    var direct = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };
    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());
    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    return (directCpu, routedCpu, routed);
  }

  [Test]
  public void Allocate_GivenAnUnrolled32BitAccumulation_ThenTheFunctionRoutes() {
    var machine = Select(_longAccumulationOverAStaticArray, "main");
    MachineScheduler.Schedule(machine);

    var allocation = LinearScanAllocator.Allocate(machine, out var reason);

    Assert.That(allocation, Is.Not.Null,
      $"allocation declined: {reason}\n{string.Join(Environment.NewLine, machine.AllInstructions)}");
  }

  [Test]
  public void Run_GivenAnUnrolled32BitAccumulation_ThenTheSumExceedsSixteenBitsOnBothBackEnds() {
    var (direct, routed, generator) = RunBothWays(_longAccumulationOverAStaticArray);

    Assert.That(generator.BackendRoutedNames, Does.Contain("main"), "the back end did not take the accumulation");
    // the value, not just the agreement: 3000 * 55 is past 65535, so a sum carried in one 16-bit
    // register would print 33928 and a truncated low word 34464
    Assert.That(routed.Output.Trim(), Is.EqualTo("sum 165000"));
    Assert.That(routed.Output, Is.EqualTo(direct.Output));
  }

  [Test]
  public void Run_GivenFourSimultaneousLongAccumulators_ThenEachKeepsItsOwnValue() {
    var (direct, routed, generator) = RunBothWays(_fourSimultaneousLongAccumulators);

    Assert.That(generator.BackendRoutedNames, Does.Contain("Accumulate"),
      "the back end did not take the four-accumulator procedure");
    // seed 1000 then the same shape doubled, so a value left behind in a register from the first call
    // would show up as the first call's answer. Both totals are past what an INTEGER holds.
    Assert.That(routed.Output.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0),
      Is.EqualTo(new[] {
        "3000  7000  11000  15000  36000",
        "6000  14000  22000  30000  72000",
      }));
    Assert.That(routed.Output, Is.EqualTo(direct.Output));
  }

  [Test]
  public void Run_GivenALongLiveAcrossACall_ThenBothOfItsWordsSurvive() {
    var (direct, routed, generator) = RunBothWays(_longLiveAcrossACall);

    Assert.That(generator.BackendRoutedNames, Does.Contain("Scaled"),
      "the back end did not take the function whose LONG spans a call");
    // 70 * 1000 + 7 and 80 * 1000 + 7: both past 65535, so a lost high word prints 4471 and 14471
    Assert.That(routed.Output.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0),
      Is.EqualTo(new[] { "step", "70007", "step", "80007" }));
    Assert.That(routed.Output, Is.EqualTo(direct.Output));
  }
}
