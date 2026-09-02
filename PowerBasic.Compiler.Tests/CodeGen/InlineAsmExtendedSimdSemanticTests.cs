using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class InlineAsmExtendedSimdSemanticTests {
  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "simd-emu.bas", Dialect.Pb36), "simd-emu.bas", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static string Run(string body) {
    var generator = new CodeGenerator(Bind("$CPU 8086\n" + body)) { Optimize = false };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return Cpu8086.Run(image, maxSteps: 4_000_000).Output;
  }

  private static void AssertResult(string setupAndInstruction, int expected) {
    var output = Run($$"""
      DIM result&
      {{setupAndInstruction}}
      ! MOVD EAX, XMM0
      ! MOV result&, EAX
      IF result& = {{expected}} THEN PRINT "OK" ELSE PRINT "BAD"
      """);
    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void Pabsb_On8086_ThenUsesSignedByteLanesAndPreservesMinValueModuloLaneWidth() => AssertResult("""
    ! MOV EAX, 2139160321
    ! MOVD XMM1, EAX
    ! PABSB XMM0, XMM1
    """, 2139095297);

  [Test]
  public void Pabsw_On8086_ThenUsesSignedWordLanesAndPreservesMinValueModuloLaneWidth() => AssertResult("""
    ! MOV EAX, -32768
    ! MOVD XMM1, EAX
    ! PABSW XMM0, XMM1
    """, 98304);

  [Test]
  public void Pabsd_On8086_ThenNegatesWholeDwordInsteadOfEachWord() => AssertResult("""
    ! MOV EAX, -5
    ! MOVD XMM1, EAX
    ! PABSD XMM0, XMM1
    """, 5);

  [Test]
  public void Pshufb_On8086_ThenUsesOriginalDestinationSnapshotForEverySelectedByte() => AssertResult("""
    ! MOV EAX, 67305985
    ! MOVD XMM0, EAX
    ! MOV EAX, 66051
    ! MOVD XMM1, EAX
    ! PSHUFB XMM0, XMM1
    """, 16909060);

  [Test]
  public void Palignr_On8086_ThenConcatenatesSourceBelowOriginalDestination() => AssertResult("""
    ! MOV EAX, 67305985
    ! MOVD XMM0, EAX
    ! MOV EAX, 134678021
    ! MOVD XMM1, EAX
    ! PALIGNR XMM0, XMM1, 2
    """, 2055);

  [Test]
  public void Pblendw_On8086_ThenSelectsWordsByImmediateMask() => AssertResult("""
    ! MOV EAX, 131073
    ! MOVD XMM0, EAX
    ! MOV EAX, 262147
    ! MOVD XMM1, EAX
    ! PBLENDW XMM0, XMM1, 1
    """, 131075);

  [Test]
  public void Pmulld_On8086_ThenKeepsLowDwordOfEachFullProduct() => AssertResult("""
    ! MOV EAX, 70000
    ! MOVD XMM0, EAX
    ! MOV EAX, 70000
    ! MOVD XMM1, EAX
    ! PMULLD XMM0, XMM1
    """, 605032704);

  [Test]
  public void Pminuw_On8086_ThenComparesWordsUnsigned() => AssertResult("""
    ! MOV EAX, 196607
    ! MOVD XMM0, EAX
    ! MOV EAX, 196609
    ! MOVD XMM1, EAX
    ! PMINUW XMM0, XMM1
    """, 131073);

  [Test]
  public void Pmaxuw_On8086_ThenComparesWordsUnsigned() => AssertResult("""
    ! MOV EAX, 196607
    ! MOVD XMM0, EAX
    ! MOV EAX, 196609
    ! MOVD XMM1, EAX
    ! PMAXUW XMM0, XMM1
    """, 262143);

  [Test]
  public void Pcmpeqq_On8086_ThenProducesAllOnesForEqualQword() => AssertResult("""
    ! MOV EAX, 123456789
    ! MOVD XMM0, EAX
    ! MOV EAX, 123456789
    ! MOVD XMM1, EAX
    ! PCMPEQQ XMM0, XMM1
    """, -1);

  [Test]
  public void Pcmpgtq_On8086_ThenUsesSignedQwordComparison() => AssertResult("""
    ! MOV EAX, 5
    ! MOVD XMM0, EAX
    ! MOV EAX, 3
    ! MOVD XMM1, EAX
    ! PCMPGTQ XMM0, XMM1
    """, -1);

  [Test]
  public void Packusdw_On8086_ThenSaturatesPositiveDwordAboveUnsignedWordRange() => AssertResult("""
    ! MOV EAX, 70000
    ! MOVD XMM0, EAX
    ! MOV EAX, 0
    ! MOVD XMM1, EAX
    ! PACKUSDW XMM0, XMM1
    """, 65535);

  [Test]
  public void Phminposuw_On8086_ThenReturnsMinimumAndFirstIndex() => AssertResult("""
    ! MOV EAX, 196613
    ! MOVD XMM1, EAX
    ! PHMINPOSUW XMM0, XMM1
    """, 131072);
}
