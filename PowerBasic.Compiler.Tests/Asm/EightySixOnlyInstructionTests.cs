using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Asm;

/// <summary>
/// That an image built for an 8086 contains only instructions an 8086 has.
///
/// It did not. <c>Assembler.J(Condition, Label)</c> emitted <c>0F 8x</c> - the near conditional
/// jump, which is 80386 and later - for any target it could not already reach in a byte, and leaned
/// on <c>RunJumpRelaxation</c> to shrink it back to <c>7x</c>. Past 127 bytes the relaxation cannot,
/// and the 386 encoding survived into an image whose declared target is an 8086: twelve of them in
/// a PAINT program, eleven in a CIRCLE one. DOSBox emulates a 486 and runs them perfectly well,
/// which is why it went unnoticed; the in-repo 8086 interpreter throws on the opcode.
///
/// The jump is now spelled the way an 8086 spells it - <c>J!cc over; JMP target; over:</c> - and
/// folded back to a single short jump wherever the target turns out to be reachable.
///
/// The count below is a byte scan, and a byte scan cannot tell an instruction from a coincidence.
/// Two matches survive in every graphics image and neither is an instruction: <c>8B 0F</c>
/// (<c>mov cx,[bx]</c>) followed by <c>8D 77 02</c> (<c>lea si,[bx+2]</c>) straddles a modrm byte
/// and the next opcode, and <c>7F 0F</c> (<c>jg +15</c>) followed by <c>89 F0</c> (<c>mov ax,si</c>)
/// straddles a displacement and the next opcode. Two is therefore the floor here, not a remainder.
/// </summary>
[TestFixture]
public sealed class EightySixOnlyInstructionTests {

  /// <summary>Byte occurrences of the 80386 near conditional jump (0F 80..0F 8F) in an image.</summary>
  private static int Near386JumpBytes(byte[] image) {
    var count = 0;
    for (var i = 0; i < image.Length - 1; ++i)
      if (image[i] == 0x0F && image[i + 1] is >= 0x80 and <= 0x8F)
        ++count;
    return count;
  }

  private static byte[] Compile(string source, bool optimize) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return image;
  }

  [Test]
  public void Image_GivenAShortProgram_ThenNoNear386ConditionalJumpByteEvenAppears() =>
    Assert.That(Near386JumpBytes(Compile("PRINT \"x\"\nEND\n", optimize: true)), Is.Zero);

  /// <summary>
  /// The graphics runtime is where the long jumps live - its routines are long enough that some
  /// targets are simply out of a byte's reach. Twelve and eleven real ones before the fix; the two
  /// that remain are the byte collisions named above.
  /// </summary>
  [TestCase("SCREEN 13\nPAINT (10, 10), 15, 4\nEND\n", TestName = "PAINT carries no 386 jump")]
  [TestCase("SCREEN 13\nCIRCLE (40, 40), 10, 12\nEND\n", TestName = "CIRCLE carries no 386 jump")]
  [TestCase("SCREEN 13\nLINE (0, 0)-(100, 80), 4, BF\nEND\n", TestName = "LINE carries no 386 jump")]
  public void Image_GivenAGraphicsProgram_ThenOnlyTheTwoByteCollisionsMatch(string source) =>
    Assert.That(Near386JumpBytes(Compile(source, optimize: true)), Is.LessThanOrEqualTo(2));

  /// <summary>
  /// A 386 target keeps the compact encoding: the pair costs a byte, and there is no reason to pay
  /// it on a processor that has the instruction.
  /// </summary>
  [Test]
  public void Assembler_GivenA386Target_ThenTheNearFormIsStillAvailable() {
    var asm = new PowerBasic.Compiler.Asm.Assembler { Allow386Jcc = true };
    var done = asm.DefineLabel();
    asm.J(PowerBasic.Compiler.Asm.Condition.Equal, done);
    asm.Nop();
    asm.MarkLabel(done);
    Assert.That(asm.ToArray()[0], Is.EqualTo(0x0F));
  }
}
