using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Asm;

/// <summary>
/// Whether an image built for an 8086 contains only instructions an 8086 has.
///
/// It does not. <c>Assembler.J(Condition, Label)</c> emits <c>0F 8x</c> - the near conditional jump,
/// which is 80386 and later - whenever the target is not already bound within short range, and
/// leans on <c>RunJumpRelaxation</c> to shrink it back to the 8086 <c>7x</c> form afterwards. When
/// the distance genuinely exceeds 127 bytes the relaxation cannot, and the 386 encoding survives
/// into an image whose declared target is an 8086. Nothing on that path consults the CPU setting.
///
/// It goes unnoticed because DOSBox emulates a 486 and runs the instruction perfectly well; the
/// in-repo 8086 interpreter is the stricter reader, and it throws on the opcode. The 8086 form is
/// the inverted short jump over a near JMP - <c>Jcc t</c> becomes <c>J!cc over; JMP t; over:</c> -
/// which is one byte longer, and that is the difficulty: the relaxation pass only ever shrinks
/// (<c>RemoveBytes</c>), so there is no path today that can grow one instruction into that pair.
///
/// This fixture does not fix it. It pins the count so the number cannot climb unnoticed while the
/// real fix waits, and so the next reader finds a measurement rather than a surprise.
/// </summary>
[TestFixture]
public sealed class EightySixOnlyInstructionTests {

  /// <summary>Occurrences of the 80386 near conditional jump (0F 80..0F 8F) in an image.</summary>
  private static int Near386Jumps(byte[] image) {
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

  /// <summary>
  /// A program drawing nothing carries none: the runtime sections it pulls in are all short enough
  /// that every conditional jump relaxes. This is the control - it shows the count is about jump
  /// DISTANCE and not about the runtime being 386 code throughout.
  /// </summary>
  [Test]
  public void Image_GivenAShortProgram_ThenItHasNoNear386ConditionalJump() =>
    Assert.That(Near386Jumps(Compile("PRINT \"x\"\nEND\n", optimize: true)), Is.Zero);

  /// <summary>
  /// The graphics runtime does carry them - its routines are long enough that some jumps cannot be
  /// short. Every one is an instruction an 8086 cannot execute, sitting in an image built for one.
  /// </summary>
  [TestCase("SCREEN 13\nPAINT (10, 10), 15, 4\nEND\n", 12, TestName = "PAINT pulls in twelve")]
  [TestCase("SCREEN 13\nCIRCLE (40, 40), 10, 12\nEND\n", 11, TestName = "CIRCLE pulls in eleven")]
  public void Image_GivenAGraphicsProgram_ThenTheCountIsWhatItWas(string source, int expected) =>
    // Pinned, not approved. A rise means a routine grew past what the relaxation can reach; a fall
    // means someone shortened one, or fixed this properly and should delete the case.
    Assert.That(Near386Jumps(Compile(source, optimize: true)), Is.EqualTo(expected));
}
