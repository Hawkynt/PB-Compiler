using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Runtime;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Runtime;

[TestFixture]
public sealed class RuntimeTargetPolicyTests {
  [Test]
  public void Target_GivenFeatureOnlySse2_ThenInfersPrerequisiteCoreCapabilities() {
    var target = RuntimeTarget.For("SSE2");
    Assert.Multiple(() => {
      Assert.That(target.CpuLevel, Is.GreaterThanOrEqualTo(686));
      Assert.That(target.Has32BitGeneralPurpose, Is.True);
      Assert.That(target.HasP6, Is.True);
      Assert.That(target.HasSse, Is.True);
      Assert.That(target.HasSse2, Is.True);
    });
  }

  [Test]
  public void Target_GivenFeatureOnlyX87_ThenDoesNotInventA386IntegerCore() {
    var target = RuntimeTarget.For("X87");
    Assert.Multiple(() => {
      Assert.That(target.CpuLevel, Is.EqualTo(86));
      Assert.That(target.HasX87, Is.True);
      Assert.That(target.Has32BitGeneralPurpose, Is.False);
    });
  }

  [Test]
  public void Target_GivenAvx512Requirement_ThenNormalizesAllVectorPrerequisites() {
    var target = RuntimeTarget.For("AVX512");
    Assert.Multiple(() => {
      Assert.That(target.HasAvx512, Is.True);
      Assert.That(target.HasAvx2, Is.True);
      Assert.That(target.HasAvx, Is.True);
      Assert.That(target.HasSse2, Is.True);
      Assert.That(target.Has32BitGeneralPurpose, Is.True);
    });
  }

  [Test]
  public void InlinePolicy_ErrorOnSupportedSse2Target_DoesNotRejectNativeInstruction() {
    var generator = Compile("$CPU SSE2\n$ISA SSE2 ERROR\n! PXOR XMM0, XMM0\nEND\n");
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
  }

  [Test]
  public void InlinePolicy_ErrorOnUnsupportedSse2Target_RejectsInsteadOfEmulating() {
    var generator = Compile("$CPU 8086\n$ISA SSE2 ERROR\n! PXOR XMM0, XMM0\nEND\n");
    Assert.That(generator.Errors.Any(e => e.Message.Contains("forbids emulation", StringComparison.OrdinalIgnoreCase)), Is.True,
      string.Join("; ", generator.Errors));
  }

  [Test]
  public void InlinePolicy_OptimizeSpeedDoesNotHideUnsupportedIdentityInstruction() {
    var generator = Compile("$CPU 8086\n$OPTIMIZE SPEED\n$ISA SSE4.1 ERROR\n! PMINUD XMM0, XMM0\nEND\n");
    Assert.That(generator.Errors.Any(e => e.Message.Contains("forbids emulation", StringComparison.OrdinalIgnoreCase)), Is.True,
      string.Join("; ", generator.Errors));
  }

  [Test]
  public void InlinePolicy_EmulateSse2On8086_DoesNotEmitNativePxorEncoding() {
    var generator = Compile("$CPU 8086\n$ISA SSE2 EMULATE\n! PXOR XMM0, XMM0\nEND\n", out var exe);
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(Contains(exe, [0x66, 0x0F, 0xEF, 0xC0]), Is.False, "native PXOR XMM0,XMM0 leaked into an 8086 emulation build");
  }

  [Test]
  public void InlinePolicy_EmulateAvx512On8086_AcceptsVirtualZmmRegisters() {
    var generator = Compile("$CPU 8086\n$ISA AVX512 EMULATE\n! VPADDW ZMM0, ZMM1, ZMM2\nEND\n");
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
  }

  [Test]
  public void FloatNpx_On8086_DeclaresNativeX87AndAllowsErrorPolicy() {
    var generator = Compile("$CPU 8086\n$FLOAT NPX\n$ISA X87 ERROR\n! FLD1\nEND\n");
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
  }

  private static CodeGenerator Compile(string source) => Compile(source, out _);

  private static CodeGenerator Compile(string source, out byte[] executable) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "isa-policy.bas", Dialect.Pb36), "isa-policy.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    executable = generator.EmitExecutable();
    return generator;
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
