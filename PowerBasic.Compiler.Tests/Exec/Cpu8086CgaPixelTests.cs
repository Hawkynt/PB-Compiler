using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Tests.CodeGen;

namespace PowerBasic.Compiler.Tests.Exec;

[TestFixture]
public sealed class Cpu8086CgaPixelTests {
  private static byte[] CreateProgram(int x, int y, params byte[] colors) {
    var asm = new Assembler();
    asm.Mov(Reg.AX, 4);
    asm.Int(0x10);
    asm.Xor(Reg.BX, Reg.BX);
    asm.Mov(Reg.CX, x);
    asm.Mov(Reg.DX, y);
    foreach (var color in colors) {
      asm.Mov(Reg.AX, 0x0C00 | color);
      asm.Int(0x10);
    }
    asm.Mov(Reg.AX, 0x0D00);
    asm.Int(0x10);
    asm.Mov(Reg.DL, Reg.AL);
    asm.Add(Reg.DL, (Imm)'0');
    asm.Mov(Reg.AH, (Imm)2);
    asm.Int(0x21);
    asm.Mov(Reg.AX, 0x4C00);
    asm.Int(0x21);
    return asm.ToArray();
  }

  [TestCase(0, 0, 0, 0, "0")]
  [TestCase(319, 199, 0, 3, "3")]
  [TestCase(10, 20, 3, 129, "2")]
  [TestCase(10, 20, 0, 7, "3")]
  public void Execute_GivenCgaBiosPixels_ThenTheModelMatchesDosBox(
      int x, int y, byte initial, byte color, string expected) {
    var image = CreateProgram(x, y, initial, color);
    Assert.That(Cpu8086.Run(image).Output, Is.EqualTo(expected));
    Assert.That(DosBoxRunner.Run(image), Is.EqualTo(expected));
  }

  [TestCase(320, 199)]
  [TestCase(319, 200)]
  public void Execute_GivenAnUnmodelledOutOfBoundsPixel_ThenFailsLoudly(int x, int y) {
    Assert.That(() => Cpu8086.Run(CreateProgram(x, y, 1)),
      Throws.TypeOf<Cpu8086Exception>().With.Message.Contains("out-of-range BIOS pixel access"));
  }
}
