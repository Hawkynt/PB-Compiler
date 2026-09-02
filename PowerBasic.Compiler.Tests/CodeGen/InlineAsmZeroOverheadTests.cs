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
  public void OptimizeSpeed_GivenNativeSse2SelfMove_ThenRemovesWholeEncoding() {
    const string source = "$CPU 80586 SSE2\n! MOVDQA XMM0, XMM0\nEND\n";
    var plain = Compile(source, speed: false, out var plainImage);
    var fast = Compile(source, speed: true, out var fastImage);
    byte[] native = [0x66, 0x0F, 0x6F, 0xC0];

    Assert.Multiple(() => {
      Assert.That(plain.Errors, Is.Empty, string.Join("; ", plain.Errors));
      Assert.That(fast.Errors, Is.Empty, string.Join("; ", fast.Errors));
      Assert.That(Contains(plainImage, native), Is.True, "control build did not contain MOVDQA XMM0,XMM0");
      Assert.That(Contains(fastImage, native), Is.False, "identity MOVDQA survived $OPTIMIZE SPEED");
      Assert.That(fastImage.Length, Is.LessThan(plainImage.Length));
    });
  }

  [Test]
  public void OptimizeSpeed_GivenSse41IdentityOn8086_ThenCollapsesBeforeVirtualIsaAllocation() {
    const string source = "$CPU 8086\n! PBLENDW XMM0, XMM0, 170\nEND\n";
    var plain = Compile(source, speed: false, out var plainImage);
    var fast = Compile(source, speed: true, out var fastImage);

    Assert.Multiple(() => {
      Assert.That(plain.Errors, Is.Empty, string.Join("; ", plain.Errors));
      Assert.That(fast.Errors, Is.Empty, string.Join("; ", fast.Errors));
      Assert.That(fastImage.Length, Is.LessThan(plainImage.Length),
        "identity should disappear before allocating/emitting the virtual SIMD bank");
    });
  }

  [Test]
  public void OptimizeSpeed_GivenWrongRegisterClass_ThenDoesNotHideOperandDiagnostic() {
    var generator = Compile("$CPU 8086\n! PBLENDW AX, AX, 0\nEND\n", speed: true, out _);

    Assert.That(generator.Errors, Is.Not.Empty,
      "typed no-op recognition must not turn malformed inline assembly into an accepted program");
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
