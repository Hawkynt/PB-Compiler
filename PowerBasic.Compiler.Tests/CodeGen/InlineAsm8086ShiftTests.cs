using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class InlineAsm8086ShiftTests {
  private static (byte[] Image, CodeGenerator Generator) Compile(string cpu, string body) {
    var source = $"$CPU {cpu}\n" + body;
    var tree = Parser.Parse(Lexer.Tokenize(source, "shift.bas", Dialect.Pb36), "shift.bas", Dialect.Pb36);
    var model = Binder.Bind(tree, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = false };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return (image, generator);
  }

  private static string Run(string body) => Cpu8086.Run(Compile("8086", body).Image).Output;

  private static bool ContainsSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) {
    if (needle.Length == 0)
      return true;
    for (var i = 0; i <= haystack.Length - needle.Length; ++i)
      if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
        return true;
    return false;
  }

  [Test]
  public void ShrWordByCl_Assembler_UsesOriginal8086D3Encoding() {
    var asm = new Assembler();
    asm.Shr(Reg.AX, Reg.CL);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0xD3, 0xE8 }));
  }

  [Test]
  public void ShrWordByCl_Given8086Target_ExecutesNativeVariableCountForm() {
    var output = Run("""
      DIM result%
      ! MOV AX, -32768
      ! MOV CL, 3
      ! SHR AX, CL
      ! MOV result%, AX
      IF result% = 4096 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void ShrWordByCl_Given8086CountAbove31_ThenDoesNotApply286CountMask() {
    var output = Run("""
      DIM result%
      ! MOV AX, -32768
      ! MOV CL, 33
      ! SHR AX, CL
      ! MOV result%, AX
      IF result% = 0 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [Test]
  public void ShrWordByImmediateThree_Given8086Target_ThenUsesThreeD1InstructionsNot186C1() {
    var (image, _) = Compile("8086", "! SHR AX, 3\nEND\n");

    Assert.That(ContainsSequence(image, [0xD1, 0xE8, 0xD1, 0xE8, 0xD1, 0xE8]), Is.True,
      "8086 immediate shift should lower to repeated count-one instructions");
    Assert.That(ContainsSequence(image, [0xC1, 0xE8, 0x03]), Is.False,
      "C1 /5 ib is an 80186+ encoding and must not leak into an 8086 target");
  }

  [Test]
  public void ShrWordByImmediateThree_Given186Target_ThenKeepsCompactC1Encoding() {
    var (image, _) = Compile("186", "! SHR AX, 3\nEND\n");
    Assert.That(ContainsSequence(image, [0xC1, 0xE8, 0x03]), Is.True);
  }

  [Test]
  public void ShrWordMemoryByCl_Given8086Target_ThenUsesVariableCountSemantics() {
    var output = Run("""
      DIM value%
      value% = -32768
      ! MOV CL, 3
      ! SHR value%, CL
      IF value% = 4096 THEN PRINT "OK" ELSE PRINT "BAD"
      """);

    Assert.That(output, Does.Contain("OK"));
    Assert.That(output, Does.Not.Contain("BAD"));
  }

  [TestCase("ROL", 0xC0)]
  [TestCase("ROR", 0xC8)]
  [TestCase("RCL", 0xD0)]
  [TestCase("RCR", 0xD8)]
  [TestCase("SHL", 0xE0)]
  [TestCase("SAL", 0xE0)]
  [TestCase("SHR", 0xE8)]
  [TestCase("SAR", 0xF8)]
  public void LegacyWordShiftRotateByCl_Assembler_Uses8086D3Group(string mnemonic, byte modRm) {
    var asm = new Assembler();
    switch (mnemonic) {
      case "ROL": asm.Rol(Reg.AX, Reg.CL); break;
      case "ROR": asm.Ror(Reg.AX, Reg.CL); break;
      case "RCL": asm.Rcl(Reg.AX, Reg.CL); break;
      case "RCR": asm.Rcr(Reg.AX, Reg.CL); break;
      case "SHL" or "SAL": asm.Shl(Reg.AX, Reg.CL); break;
      case "SHR": asm.Shr(Reg.AX, Reg.CL); break;
      case "SAR": asm.Sar(Reg.AX, Reg.CL); break;
      default: throw new AssertionException($"unhandled mnemonic {mnemonic}");
    }

    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0xD3, modRm }));
  }
}
