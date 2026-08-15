using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The routed path honours <c>--no-optimize</c>: a function the x86-16 back end takes is compiled
/// through <see cref="Ir.Passes.IrPassManager.Legalize"/> rather than the full pipeline when the
/// optimizer is off.
///
/// <para>
/// The measurement needs three assertions and not one, because two of the ways it can go wrong look
/// like a pass. The subject must still be ROUTED unoptimized - gating that simply stopped the back
/// end taking the function would make the size comparison a statement about the direct emitter - and
/// the unoptimized build must still be a correct program, since running fewer passes is only worth
/// anything if what comes out still computes. Only then does "the optimized build is smaller" say
/// what it looks like it says.
/// </para>
/// <para>
/// The subject is written to defeat the ways a fixture can measure nothing: <c>NOINLINE</c> so the
/// body is not absorbed, TWO call sites with different arguments so interprocedural constant
/// propagation cannot prove the parameters, and a common subexpression as the thing being removed -
/// value numbering is the one transform here that legalization cannot supply by accident, where a
/// dead store is removed by <c>mem2reg</c> plus <c>dce</c> and would therefore go either way.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendOptimizeGatingTests {

  private const string _SOURCE = """
    SUB Cse(BYVAL y%, BYVAL x%) NOINLINE
      a% = y% * 320 + x%
      b% = y% * 320 + x%
      PRINT "gate"; a% + b%
    END SUB

    Cse 1, 2
    Cse 3, 4
    """;

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static CodeGenerator Compile(bool optimize, bool routed, out byte[] image) {
    var generator = new CodeGenerator(Bind(_SOURCE)) { Optimize = optimize, UseExperimentalBackend = routed };
    image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return generator;
  }

  /// <summary>
  /// How many 16-bit register multiplies (<c>F7 /4</c>, <c>F7 /5</c>) a procedure emits.
  ///
  /// The size of the procedure is NOT the measurement, and looked like one: the objective already
  /// reached the machine-level stages before this change - <c>Peephole</c>, the residency policy and
  /// the loop-top alignment are all gated on it - so a routed unoptimized build was already bigger
  /// than a routed optimized one while running exactly the same IR pipeline. Counting the multiply
  /// asks about the IR instead, and nothing below the IR can merge two of them.
  /// </summary>
  private static int MultipliesIn(CodeGenerator generator, byte[] image, string procedure) {
    var code = CodeOf(generator, image, procedure);
    var seen = 0;
    for (var i = 0; i + 1 < code.Length; ++i)
      if (code[i] == 0xF7 && code[i + 1] is >= 0xE0 and <= 0xEF)
        ++seen;
    return seen;
  }

  /// <summary>The emitted bytes of one procedure - it runs to whatever the codegen bound next.</summary>
  private static byte[] CodeOf(CodeGenerator generator, byte[] image, string procedure) {
    var listing = generator.DescribeImage();
    var code = image.AsSpan(BitConverter.ToUInt16(image, 8) * 16).ToArray();
    var entry = listing.Procedures.Single(p => p.Name.Equals(procedure, StringComparison.OrdinalIgnoreCase));
    Assert.That(entry.CodeOffset, Is.GreaterThanOrEqualTo(0), $"{procedure} was not emitted");
    var end = listing.Procedures.Where(p => p.CodeOffset > entry.CodeOffset).Select(p => p.CodeOffset)
      .Concat(listing.RuntimeLabels.Select(l => l.Offset).Where(o => o > entry.CodeOffset))
      .Append(Math.Min(listing.CodeLength, code.Length))
      .Min();
    return code[entry.CodeOffset..Math.Min(end, code.Length)];
  }

  [Test]
  public void Emit_GivenARoutedProcedure_WhenTheOptimizerIsOff_ThenTheBackEndStillTakesIt() {
    var optimized = Compile(optimize: true, routed: true, out _);
    var plain = Compile(optimize: false, routed: true, out _);

    Assert.Multiple(() => {
      Assert.That(optimized.BackendRoutedNames, Does.Contain("Cse"));
      Assert.That(plain.BackendRoutedNames, Does.Contain("Cse"),
        "gating the pipeline must not un-route the function - otherwise the size comparison below "
        + "measures the direct emitter");
    });
  }

  [Test]
  public void Emit_GivenARoutedCommonSubexpression_WhenTheOptimizerIsOff_ThenItIsComputedTwice() {
    var optimized = Compile(optimize: true, routed: true, out var optimizedImage);
    var plain = Compile(optimize: false, routed: true, out var plainImage);

    Assert.Multiple(() => {
      Assert.That(MultipliesIn(optimized, optimizedImage, "Cse"), Is.EqualTo(1),
        "value numbering commons the two products");
      Assert.That(MultipliesIn(plain, plainImage, "Cse"), Is.EqualTo(2),
        "with the optimizer off it must not - a routed --no-optimize build used to run the whole "
        + "pipeline, which made the battery's two builds one build");
    });
  }

  [Test]
  public void Run_GivenARoutedProcedure_WhenTheOptimizerIsOff_ThenItPrintsWhatTheDirectPathPrints() {
    Compile(optimize: false, routed: false, out var directImage);
    Compile(optimize: false, routed: true, out var routedImage);

    string Execute(byte[] image, string which) {
      try {
        return Cpu8086.Run(image).Output;
      } catch (Cpu8086Exception e) {
        Assert.Ignore($"the interpreter cannot run the {which} image: {e.Message}");
        return "";
      }
    }

    var direct = Execute(directImage, "direct");
    Assert.That(Execute(routedImage, "routed"), Is.EqualTo(direct));
    Assert.That(direct.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
      Is.EqualTo(new[] { "gate", "644", "gate", "1928" }));
  }
}
