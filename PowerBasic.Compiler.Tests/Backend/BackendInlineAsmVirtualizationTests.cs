using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Inline assembly the declared CPU cannot execute must not route.
///
/// <para>
/// The direct emitter does not pass such an instruction through - it EMULATES it, lowering the
/// packed-integer surface onto plain 8086 instructions. The routed path has no such lowering:
/// <c>IrInlineAsm</c> carries the text and the machine emitter assembles it verbatim. Routing a body
/// that needs emulating therefore produces an image containing an instruction the machine the source
/// NAMED cannot execute, and nothing reports it.
/// </para>
/// <para>
/// This is the regression that came in with default-on routing and was not caught, because the
/// fixtures that would have caught it were pinned to the direct emitter as "assertions about emitted
/// code". They were not: an image that faults on its target is a behaviour, not a shape. The check
/// here is deliberately on the PRODUCTION configuration - no <c>UseExperimentalBackend</c> in sight -
/// so it cannot be hidden the same way twice.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendInlineAsmVirtualizationTests {

  private const string _body = "$OPTIMIZE SPEED\nDIM a%, b%\na% = 3 : b% = 4\n! MOV AX, a%\n! PADDW MM0, MM1\n! MOV b%, AX\nPRINT b%\nEND";

  private static (byte[] Image, IReadOnlyList<string> Routed) Compile(string cpu, bool? routed = null) {
    var source = $"$CPU {cpu}\n{_body}";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = true };
    if (routed is { } force)
      generator.UseExperimentalBackend = force;
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return (image, generator.BackendRoutedNames.ToList());
  }

  /// <summary>PADDW is 0F FD - the encoding an 8086 or a bare 386 has no way to execute.</summary>
  private static bool ContainsPaddw(byte[] image) {
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x0F && image[i + 1] == 0xFD)
        return true;
    return false;
  }

  /// <summary>
  /// The values are the point. A target without MMX must not receive the MMX encoding, whichever
  /// emitter produced the image - and <c>$CPU SSE2</c> is in the list on purpose: SSE2 does not bring
  /// the MMX register file with it here, so it emulates too. A guard keyed on "is this 8086" rather
  /// than on the instruction's actual feature requirement would pass the first two and miss this one.
  /// </summary>
  [TestCase("8086")]
  [TestCase("80386")]
  [TestCase("SSE2")]
  public void Compile_GivenInlineAsmAboveTheDeclaredCpu_ThenTheImageHasNoInstructionThatTargetCannotRun(string cpu) {
    var (image, routed) = Compile(cpu);

    Assert.Multiple(() => {
      Assert.That(ContainsPaddw(image), Is.False,
        $"$CPU {cpu} got a raw PADDW; the declared target cannot execute it");
      Assert.That(routed, Does.Not.Contain("main"),
        "the body needs ISA emulation, which only the direct emitter does - it must decline rather than pass the instruction through");
    });
  }

  /// <summary>
  /// The other half, without which the guard could be refusing everything: where the target DOES have
  /// the instruction there is nothing to emulate, so the body routes and the encoding is emitted.
  /// </summary>
  [Test]
  public void Compile_GivenInlineAsmTheDeclaredCpuSupports_ThenItRoutesAndEmitsTheInstruction() {
    var (image, routed) = Compile("MMX");

    Assert.Multiple(() => {
      Assert.That(ContainsPaddw(image), Is.True, "$CPU MMX can execute PADDW; it should be emitted");
      Assert.That(routed, Does.Contain("main"), "nothing needs emulating here, so the body must route");
    });
  }

  /// <summary>
  /// And the equivalence that matters to a user: below the tier, the production build must produce
  /// exactly what the direct emitter produces, because it IS the direct emitter picking the body up.
  /// </summary>
  [TestCase("8086")]
  [TestCase("80386")]
  [TestCase("SSE2")]
  public void Compile_GivenInlineAsmAboveTheDeclaredCpu_ThenProductionMatchesTheDirectBuild(string cpu) {
    var production = Compile(cpu).Image;
    var direct = Compile(cpu, routed: false).Image;

    Assert.That(production, Is.EqualTo(direct),
      $"$CPU {cpu} declined to the direct emitter, so the images must be identical");
  }
}
