using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The three roundings PowerBASIC keeps apart on purpose, measured on the x86-16 back end by
/// RUNNING the image rather than by reading the bytes out of it.
///
/// <para>
/// <c>FIX</c> truncates toward zero, <c>INT</c> floors, and <c>CINT</c> rounds to nearest with ties
/// to even - so <c>-1.5</c> is <c>-1</c>, <c>-2</c> and <c>-2</c>, and the three answers differ on
/// every one of the values below. The x87 has no truncating store: <c>FISTP</c> rounds by the
/// control word, which is nearest-with-ties-to-even unless something changed it, and a byte
/// assertion on the round trip is exactly how routed <c>FIX(-1.5)</c> came to answer <c>-2</c>.
/// </para>
///
/// <para>
/// Every subject is a <c>NOINLINE</c> function called from SEVERAL sites with DIFFERENT values, so
/// neither the constant folder nor interprocedural constant propagation can answer the conversion
/// before selection reaches it - which is why the corpus never caught this: every <c>FIX</c> in it
/// has a constant argument.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendRoundingTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  /// <summary>
  /// The five values that separate the three roundings: two negative ties, two positive ties, and a
  /// negative non-tie (where truncation and flooring disagree but neither is a tie).
  /// </summary>
  private const string _subjects = "(-1.5#); Roll(-2.5#); Roll(1.5#); Roll(2.5#); Roll(-1.2#)";

  private static string Program(string body, string suffix) => $"""
    DECLARE FUNCTION Roll{suffix}(BYVAL x#)

    PRINT Roll{_subjects}
    END

    FUNCTION Roll{suffix}(BYVAL x#) NOINLINE
      Roll{suffix} = {body}
    END FUNCTION
    """;

  /// <summary>
  /// <c>FIX</c>, <c>INT</c> and <c>CINT</c> of a runtime argument, run on both paths and compared -
  /// plus the answer PowerBASIC gives, so a routed build that agrees with a broken direct one still
  /// fails. The expectations are the printed line, leading blank on a non-negative number included.
  /// </summary>
  [TestCase("FIX(x#)", "#", "-1 -2  1  2 -1 ", TestName = "Fix truncates toward zero")]
  [TestCase("INT(x#)", "#", "-2 -3  1  2 -2 ", TestName = "Int floors")]
  [TestCase("CINT(x#)", "%", "-2 -2  2  2 -1 ", TestName = "Cint rounds to nearest, ties to even")]
  public void Execute_GivenARoundingOfARuntimeArgument_ThenTheRoutedPathAnswersWhatTheDirectOneDoes(
      string body, string suffix, string expected) {
    var source = Program(body, suffix);
    var direct = new CodeGenerator(Bind(source)) { Optimize = false, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = false, UseExperimentalBackend = true };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("Roll"),
      "the back end must have taken the function - a fallback compares the direct path with itself");
    Assert.That(directCpu.Output.Trim('\r', '\n'), Is.EqualTo(expected),
      "the control: the direct emitter's answer is PowerBASIC's");
    Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
  }

  /// <summary>
  /// The same three under <c>--optimize</c>, where the whole IR pass pipeline runs. The five call
  /// sites carry five different values, so IPCP cannot fold the parameter into the body.
  /// </summary>
  [TestCase("FIX(x#)", "#", "-1 -2  1  2 -1 ")]
  [TestCase("INT(x#)", "#", "-2 -3  1  2 -2 ")]
  [TestCase("CINT(x#)", "%", "-2 -2  2  2 -1 ")]
  public void Execute_GivenARoundingUnderOptimization_ThenTheRoutedPathStillAnswersWhatTheDirectOneDoes(
      string body, string suffix, string expected) {
    var source = Program(body, suffix);
    var direct = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("Roll"),
      "the back end must have taken the function - a fallback compares the direct path with itself");
    Assert.That(directCpu.Output.Trim('\r', '\n'), Is.EqualTo(expected),
      "the control: the direct emitter's answer is PowerBASIC's");
    Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
  }

  /// <summary>
  /// Which x87 sequence each one selects, which is the second half of the same statement: the two
  /// truncating forms reach the direct emitter's own <c>rt_trunc</c>, and only the rounding
  /// conversion is allowed to store through <c>FISTP</c> - the instruction that rounds by the
  /// control word.
  /// </summary>
  [TestCase("FIX(x#)", "#", true)]
  [TestCase("INT(x#)", "#", true)]
  [TestCase("CINT(x#)", "%", false)]
  public void Select_GivenARounding_ThenOnlyTheNearestOneStoresThroughFistp(
      string body, string suffix, bool truncating) {
    var module = IrLowering.TryLowerModule(Bind(Program(body, suffix)));
    Assert.That(module, Is.Not.Null, "outside the IR lowering's subset");
    var fn = module!.Functions.First(f => f.Name.Equals("Roll", StringComparison.OrdinalIgnoreCase));

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, $"declined: {reason}");
    var opcodes = m!.AllInstructions.Select(i => i.Opcode).ToList();
    var calls = m.AllInstructions.Where(i => i.Opcode == MOpcode.Call)
      .Select(i => ((MOperand.LabelRef)i.Operands[0]).Name).ToList();
    Assert.That(calls.Contains("rt_trunc"), Is.EqualTo(truncating),
      "truncation toward zero is the runtime routine the direct emitter's FIX calls");
    Assert.That(opcodes.Contains(MOpcode.Fistp), Is.EqualTo(!truncating),
      "FISTP rounds by the control word, so it may only implement the nearest conversion");
  }
}
