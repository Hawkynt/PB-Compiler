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

  /// <summary>
  /// Occurrences of the 80387 transcendentals FSIN (D9 FE) and FCOS (D9 FF). An 8087 has FPTAN and
  /// FPATAN and no sine or cosine of its own; those arrived with the 387.
  /// </summary>
  /// <summary>Whether <paramref name="bytes"/> appear consecutively anywhere in the image.</summary>
  private static bool HasSequence(byte[] image, params byte[] bytes) {
    for (var i = 0; i + bytes.Length <= image.Length; ++i) {
      var hit = true;
      for (var j = 0; j < bytes.Length; ++j)
        if (image[i + j] != bytes[j]) { hit = false; break; }
      if (hit)
        return true;
    }
    return false;
  }

  private static int Fsincos(byte[] image) {
    var count = 0;
    for (var i = 0; i < image.Length - 1; ++i)
      if (image[i] == 0xD9 && image[i + 1] is 0xFE or 0xFF)
        ++count;
    return count;
  }

  /// <summary>
  /// SIN and COS emit the 387's FSIN and FCOS with no CPU gate, so a program using either needs a
  /// 387 whatever its declared target. This is the same shape as the conditional-jump problem above
  /// and larger: that one only bit past 127 bytes, and this one bites every call.
  ///
  /// It is NOT caught by running the program - the interpreter implements both, so the tests pass
  /// and the image is still wrong for the processor it names. A byte scan is the only thing that
  /// sees it, which is why it is measured here rather than left to a runtime failure that will not
  /// come.
  ///
  /// FIXED for SIN and COS. Below a 386 they now call rt_trig, a shared routine built from
  /// instructions an 8087 has; a 386 keeps FSIN/FCOS, which it has and which are smaller and faster.
  /// The oracle agrees with the shape: genuine PBC 3.5 compiling all three emits zero FSIN, zero
  /// FCOS and exactly ONE FPTAN.
  ///
  /// What follows is the record of why it was not the swap it looked like.
  /// Genuine PBC 3.5 compiling <c>SIN(1.0)</c>, <c>COS(1.0)</c> and <c>TAN(1.0)</c> in one program
  /// emits ZERO FSIN and ZERO FCOS, and exactly ONE FPTAN - with FPATAN, FPREM, F2XM1, FSCALE and
  /// FYL2X alongside it. One FPTAN for all three means a single shared routine that reduces the
  /// argument and derives sine and cosine from the tangent, which is what an 8087 leaves you: its
  /// FPTAN is defined only for 0 &lt;= x &lt;= pi/4, unlike the 387's.
  ///
  /// So the fix is not "swap FSIN for FPTAN" - it is that routine. Reduce by pi/2 with FPREM (whose
  /// condition codes carry the low quotient bits, which is what they are for), fold the remainder
  /// into [0, pi/4] by taking pi/2 - r and swapping the two results, then FPTAN gives Y and X whose
  /// hypotenuse yields sin = Y/h and cos = X/h together; the quadrant picks the signs, and sine
  /// alone carries the argument's sign.
  ///
  /// TAN WAS NOT THE COUNTER-EXAMPLE IT WAS TAKEN FOR, and is now fixed alongside them. This fixture
  /// used to say TAN "uses FPTAN, which the 8087 has, and needs nothing" - true of the opcode, false
  /// of the usage. The two generations disagree about what FPTAN leaves behind:
  ///
  ///   8087/287  replaces ST with Y and pushes X - the tangent is Y/X, and the argument must
  ///             already lie in [0, pi/4]
  ///   387+      replaces ST with the tangent itself and pushes a 1.0, for any |x| &lt; 2^63
  ///
  /// This compiler emitted <c>FPTAN; FSTP ST(0)</c> - discard what was pushed, keep what is under it -
  /// which is the 387 reading. On a real 8087 that keeps Y, not the tangent. TAN therefore needed the
  /// same range reduction and the same Y/X divide as SIN and COS, and it escaped the scan above only
  /// because that scan looks for FSIN and FCOS. Below a 386 it now calls the same routine, entering
  /// at rt_tan; a 386 keeps the two-instruction form, which is correct on the processor it names.
  ///
  /// It IS verifiable, though an earlier reading here said otherwise. Cpu8086 answers FPTAN with the
  /// tangent and a pushed 1.0 - the 387 form - but that makes X equal to 1, so Y/X equals Y equals
  /// the tangent. Code written the 8087 way, dividing Y by X, is therefore correct on the hardware
  /// it targets AND on the emulator. Only the domain restriction is unmodelled, and the reduction
  /// that satisfies it is ordinary arithmetic the interpreter does model.
  ///
  /// One real gap did have to be closed first: Cpu8086's FPREM cleared C2 and never set C0/C1/C3,
  /// the quotient bits a range reduction reads the quadrant from, so the first version of the
  /// routine read whatever the previous FSTSW had left. TAN still reads FPTAN the 387 way and is
  /// pinned below as such.
  /// </summary>
  [Test]
  public void Image_GivenSinOrCos_ThenTheThreeEightySevenFormIsStillWhatIsEmitted() {
    Assert.Multiple(() => {
      Assert.That(Fsincos(Compile("PRINT SIN(1.0)\nEND\n", optimize: true)), Is.Zero,
        "an 8086 target calls the FPTAN routine instead of FSIN");
      Assert.That(Fsincos(Compile("PRINT COS(1.0)\nEND\n", optimize: true)), Is.Zero,
        "an 8086 target calls the FPTAN routine instead of FCOS");
      // A 386 keeps the single instruction: it has it, and it is smaller and faster than the routine.
      Assert.That(Fsincos(Compile("$CPU 80386\nPRINT SIN(1.0)\nEND\n", optimize: true)), Is.EqualTo(1), "SIN uses FSIN on a 386");
      Assert.That(Fsincos(Compile("$CPU 80386\nPRINT COS(1.0)\nEND\n", optimize: true)), Is.EqualTo(1), "COS uses FCOS on a 386");
      Assert.That(Fsincos(Compile("PRINT TAN(1.0)\nEND\n", optimize: true)), Is.Zero, "TAN emits no FSIN/FCOS");
      // FPTAN (D9 F2) immediately followed by FSTP ST(0) (DD D8) discards the pushed value and keeps
      // what is under it, which is the tangent only on a 387; an 8087 leaves X on top and Y beneath,
      // so the same two instructions keep Y. That pair is the 387 reading, and an 8086 target no
      // longer contains it - the routine divides Y by X instead.
      Assert.That(HasSequence(Compile("PRINT TAN(1.0)\nEND\n", optimize: true), 0xD9, 0xF2, 0xDD, 0xD8),
        Is.False, "an 8086 target reads FPTAN as an 8087 leaves it: the ratio Y/X");
      Assert.That(HasSequence(Compile("$CPU 80386\nPRINT TAN(1.0)\nEND\n", optimize: true), 0xD9, 0xF2, 0xDD, 0xD8),
        Is.True, "a 386 keeps the two-instruction form, which is what its FPTAN means");
      Assert.That(Fsincos(Compile("PRINT 1.0\nEND\n", optimize: true)), Is.Zero);
    });
  }
}
