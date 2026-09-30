using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Inline assembly the declared CPU cannot execute must be virtualized inside the production route.
///
/// <para>
/// <c>IrInlineAsm</c> carries the text into machine IR, whose emission callback applies the target ISA
/// policy. That policy lowers the packed-integer surface onto instructions the declared CPU supports.
/// </para>
/// <para>
/// An image that faults on its declared target is a behavioural defect, not merely a byte-shape
/// difference. These checks therefore compile through the production configuration.
/// </para>
/// <para>
/// The fix is not a decline. The hosted target machine emitter takes the target's ISA policy as a
/// callback, so the routed path reaches the target emulator and keeps the body.
/// Declining would also have been correct and would have cost the routing every program with a line
/// of portable SIMD in it.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendInlineAsmVirtualizationTests {

  private const string _body = "$OPTIMIZE SPEED\nDIM a%, b%\na% = 3 : b% = 4\n! MOV AX, a%\n! PADDW MM0, MM1\n! MOV b%, AX\nPRINT b%\nEND";

  private static (byte[] Image, IReadOnlyList<string> Routed) Compile(string cpu) {
    var source = $"$CPU {cpu}\n{_body}";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = true };
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
      Assert.That(routed, Does.Contain("main"),
        "emulating it is the routed path's job now, so the body must still route - a decline here "
        + "would mean the ISA policy callback stopped reaching the hosted target machine emitter");
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
  /// Recompiling through the single production route must remain deterministic and must never emit an
  /// encoding the declared target cannot execute.
  /// </summary>
  [TestCase("8086")]
  [TestCase("80386")]
  [TestCase("SSE2")]
  public void Compile_GivenInlineAsmAboveTheDeclaredCpu_ThenRepeatedProductionBuildsStayVirtualized(string cpu) {
    Assert.Multiple(() => {
      Assert.That(ContainsPaddw(Compile(cpu).Image), Is.False, $"first build, $CPU {cpu}");
      Assert.That(ContainsPaddw(Compile(cpu).Image), Is.False, $"second build, $CPU {cpu}");
    });
  }
}
