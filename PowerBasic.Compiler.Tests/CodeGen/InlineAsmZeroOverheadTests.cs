using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class InlineAsmZeroOverheadTests {
  private static CodeGenerator Compile(string source, bool speed, out byte[] image) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "zero-overhead.bas", Dialect.Pb36), "zero-overhead.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = true, OptimizeSpeed = speed };
    image = generator.EmitExecutable();
    return generator;
  }

  [Test]
  public void OptimizeSpeed_GivenSelfMove_ThenRemovesInstructionCompletely() {
    var plain = Compile("$CPU 8086\n! MOV AX, AX\nEND\n", speed: false, out var plainImage);
    var fast = Compile("$CPU 8086\n! MOV AX, AX\nEND\n", speed: true, out var fastImage);

    Assert.Multiple(() => {
      Assert.That(plain.Errors, Is.Empty, string.Join("; ", plain.Errors));
      Assert.That(fast.Errors, Is.Empty, string.Join("; ", fast.Errors));
      Assert.That(fastImage.Length, Is.LessThan(plainImage.Length));
    });
  }

  [Test]
  public void OptimizeSpeed_GivenIdentityPblendw_ThenRemovesNativeSse41Encoding() {
    const string source = "$CPU SSE41\n! PBLENDW XMM0, XMM0, 170\nEND\n";
    var plain = Compile(source, speed: false, out var plainImage);
    var fast = Compile(source, speed: true, out var fastImage);
    byte[] native = [0x66, 0x0F, 0x3A, 0x0E, 0xC0, 0xAA];

    Assert.Multiple(() => {
      Assert.That(plain.Errors, Is.Empty, string.Join("; ", plain.Errors));
      Assert.That(fast.Errors, Is.Empty, string.Join("; ", fast.Errors));
      Assert.That(Contains(plainImage, native), Is.True, "control build did not contain the PBLENDW encoding");
      Assert.That(Contains(fastImage, native), Is.False, "identity PBLENDW survived $OPTIMIZE SPEED");
      Assert.That(fastImage.Length, Is.LessThan(plainImage.Length));
    });
  }

  [Test]
  public void OptimizeSpeed_GivenUnsupportedIdentityWithErrorPolicy_ThenStillDiagnosesTargetViolation() {
    var generator = Compile("$CPU 8086\n$ISA SSE41 ERROR\n! PBLENDW XMM0, XMM0, 0\nEND\n", speed: true, out _);

    Assert.That(generator.Errors.Any(e => e.Message.Contains("forbids emulation", StringComparison.OrdinalIgnoreCase)), Is.True,
      string.Join("; ", generator.Errors));
  }

  [Test]
  public void OptimizeSpeed_GivenExplicitNativeIdentity_ThenHonorsNativePolicyAndKeepsInstruction() {
    var generator = Compile("$CPU SSE41\n$ISA SSE41 NATIVE\n! PBLENDW XMM0, XMM0, 170\nEND\n", speed: true, out var image);
    byte[] native = [0x66, 0x0F, 0x3A, 0x0E, 0xC0, 0xAA];

    Assert.Multiple(() => {
      Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
      Assert.That(Contains(image, native), Is.True, "explicit NATIVE policy must retain the hardware instruction");
    });
  }

  [Test]
  public void OptimizeSpeed_GivenEmulatedIdentityOn8086_ThenNeedsNoVirtualIsaStateOrFallback() {
    var generator = Compile("$CPU 8086\n$ISA SSE41 EMULATE\n! PBLENDW XMM0, XMM0, 170\nEND\n", speed: true, out _);

    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
  }

  private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) {
    if (needle.Length == 0)
      return true;
    for (var i = 0; i <= haystack.Length - needle.Length; ++i)
      if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
        return true;
    return false;
  }
}
