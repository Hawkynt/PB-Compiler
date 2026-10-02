using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// x86 inline assembly on the machines that are not an 8086 (<see cref="Compiler.Ir.Passes.InlineAsmLifting"/>):
/// each program runs on x86-32, x64 and the 6502 and must print what the same program prints on the
/// 8086 with <c>$CPU 8086</c>, where the existing ISA emulation runs it on <see cref="Cpu8086"/> - the
/// oracle the 8086's own emulation is already held to.
/// </summary>
[TestFixture]
public sealed class InlineAsmLiftingTests {

  private static readonly (string Name, string Source)[] _programs = [
    ("general-purpose", """
      DIM r&, s&, t%, u%
      ! MOV EAX, 305419896
      ! MOV EBX, EAX
      ! ADD EBX, 1000
      ! SUB EBX, EAX
      ! MOV r&, EBX
      ! MOV AX, 4660
      ! MOV AH, 18
      ! MOV AL, 52
      ! NOT AL
      ! XOR AH, 255
      ! MOV t%, AX
      ! MOV ECX, 3
      ! MOV EDX, -64
      ! SAR EDX, CL
      ! SHL EDX, 1
      ! NEG EDX
      ! INC EDX
      ! DEC EDX
      ! DEC EDX
      ! MOV s&, EDX
      ! MOV DX, 255
      ! AND DX, 15
      ! OR DX, 256
      ! SHR DX, 2
      ! MOV u%, DX
      PRINT r&; s&; HEX$(t%); u%
      """),
    ("variables-in-and-out", """
      DIM a&, b&, w%
      a& = 123456
      w% = -2
      ! MOV EAX, a&
      ! ADD EAX, a&
      ! MOV b&, EAX
      ! ADD b&, 7
      ! MOV BX, w%
      ! SHL BX, 3
      ! MOV w%, BX
      PRINT b&; w%
      """),
    ("mmx-packed", """
      DIM a&, b&, c&, d&
      a& = &H01FF7F80
      ! MOVD MM0, a&
      ! MOVQ MM1, MM0
      ! PADDB MM0, MM1
      ! MOVD b&, MM0
      ! MOVQ MM2, MM1
      ! PSUBW MM2, MM0
      ! MOVD c&, MM2
      ! MOV EAX, 252645135
      ! MOVD MM3, EAX
      ! PANDN MM3, MM1
      ! MOVD d&, MM3
      ! EMMS
      PRINT HEX$(b&); " "; HEX$(c&); " "; HEX$(d&)
      """),
    ("pshufb-mmx-and-xmm", """
      DIM lo&, hi&, x&
      ! MOV EAX, &H04030201
      ! MOVD MM0, EAX
      ! MOV EAX, &H80000102
      ! MOVD MM1, EAX
      ! PSHUFB MM0, MM1
      ! MOVD lo&, MM0
      ! MOV EAX, &H44332211
      ! MOVD XMM0, EAX
      ! MOV EAX, &H00030203
      ! MOVD XMM1, EAX
      ! PSHUFB XMM0, XMM1
      ! MOVD x&, XMM0
      ! EMMS
      PRINT HEX$(lo&); " "; HEX$(x&)
      """),
  ];

  private static IEnumerable<TestCaseData> Cases()
    => from platform in (string[])["x86-32", "x64", "6502"]
       from program in _programs
       select new TestCaseData(platform, program.Name).SetName($"Run_GivenInlineAssembly_ThenItPrintsWhatThe8086Prints({platform}, {program.Name})");

  private static string OnThe8086(string body) {
    var source = "$CPU 8086\n" + body;
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = false };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return Cpu8086.Run(image).Output;
  }

  [TestCaseSource(nameof(Cases))]
  public void Run_GivenInlineAssembly_ThenItPrintsWhatThe8086Prints(string platform, string name) {
    var source = _programs.Single(program => program.Name == name).Source;
    var expected = Vice.Normalize(OnThe8086(source));
    Assume.That(expected, Is.Not.Empty, "the 8086 oracle printed nothing");

    Assert.That(Vice.Normalize(FlatTargets.Run(platform, source)), Is.EqualTo(expected));
  }

  [Test]
  public void Compile_GivenAnInstructionNotLiftedYet_ThenItIsDeclinedByName() {
    var work = Directory.CreateTempSubdirectory("pbc-lift-");
    try {
      var path = Path.Combine(work.FullName, "PROG.BAS");
      File.WriteAllText(path, "DIM a&\n! MOV EAX, 1\n! ADC EAX, 2\n! MOV a&, EAX\nPRINT a&\n");
      var stderr = new StringWriter();
      var code = PowerBasic.Compiler.Cli.Driver.Run(["--dialect", "pb36", "--platform", "x64", path], TextWriter.Null, stderr);

      Assert.Multiple(() => {
        Assert.That(code, Is.Not.Zero);
        Assert.That(stderr.ToString(), Does.Contain("'ADC' has no IR lifting yet"));
      });
    } finally {
      work.Delete(recursive: true);
    }
  }
}
