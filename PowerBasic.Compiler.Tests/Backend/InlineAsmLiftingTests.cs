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
    ("mmx-compare-multiply-saturate", """
      DIM a&, b&, r1&, r2&, r3&, r4&, r5&
      a& = &H7FF08001
      b& = &H0010FF02
      ! MOVD MM0, a&
      ! MOVD MM1, b&
      ! MOVQ MM2, MM0
      ! PCMPGTW MM2, MM1
      ! MOVD r1&, MM2
      ! MOVQ MM2, MM0
      ! PMULLW MM2, MM1
      ! MOVD r2&, MM2
      ! MOVQ MM2, MM0
      ! PMULHW MM2, MM1
      ! MOVD r3&, MM2
      ! MOVQ MM2, MM0
      ! PADDSW MM2, MM1
      ! MOVD r4&, MM2
      ! MOVQ MM2, MM0
      ! PCMPEQB MM2, MM1
      ! MOVD r5&, MM2
      ! EMMS
      PRINT HEX$(r1&); " "; HEX$(r2&); " "; HEX$(r3&); " "; HEX$(r4&); " "; HEX$(r5&)
      """),
    ("mmx-shift-unpack-pack", """
      DIM a&, b&, r1&, r2&, r3&, r4&, r5&, r6&, r7&
      a& = &H8421F00F
      b& = &H12345678
      ! MOVD MM0, a&
      ! MOVD MM1, b&
      ! MOVQ MM2, MM0
      ! PSRAW MM2, 3
      ! MOVD r1&, MM2
      ! MOVQ MM2, MM0
      ! PSRLD MM2, 5
      ! MOVD r2&, MM2
      ! MOVQ MM2, MM0
      ! PSLLW MM2, 17
      ! MOVD r3&, MM2
      ! MOVQ MM2, MM0
      ! PUNPCKLBW MM2, MM1
      ! MOVD r4&, MM2
      ! PSRLQ MM2, 32
      ! MOVD r5&, MM2
      ! MOVQ MM2, MM0
      ! PACKSSWB MM2, MM1
      ! MOVD r6&, MM2
      ! MOVQ MM2, MM1
      ! PACKUSWB MM2, MM0
      ! PSRLQ MM2, 32
      ! MOVD r7&, MM2
      ! EMMS
      PRINT HEX$(r1&); " "; HEX$(r2&); " "; HEX$(r3&); " "; HEX$(r4&)
      PRINT HEX$(r5&); " "; HEX$(r6&); " "; HEX$(r7&)
      """),
    ("vex-lane-wise", """
      DIM a&, b&, r1&, r2&, r3&, r4&
      a& = &H7FF08001
      b& = &H0010FF02
      ! MOVD XMM0, a&
      ! MOVD XMM1, b&
      ! VPADDB YMM2, YMM0, YMM1
      ! MOVD r1&, XMM2
      ! VPCMPEQB YMM3, YMM2, YMM0
      ! MOVD r2&, XMM3
      ! VPMULLW XMM4, XMM0, XMM1
      ! MOVD r3&, XMM4
      ! VPXOR YMM5, YMM4, YMM1
      ! MOVD r4&, XMM5
      PRINT HEX$(r1&); " "; HEX$(r2&); " "; HEX$(r3&); " "; HEX$(r4&)
      """),
    ("flags-and-conditions", """
      DIM lo&, hi&, m&, c&, k%
      ! MOV EAX, -1
      ! MOV EDX, 7
      ! ADD EAX, 5
      ! ADC EDX, 0
      ! MOV lo&, EAX
      ! MOV hi&, EDX
      ! MOV EAX, 3
      ! SUB EAX, 4
      ! SBB EDX, 1
      ! MOV m&, EDX
      ! MOV EAX, 10
      ! MOV EBX, 20
      ! MOV ECX, -5
      ! CMP EAX, EBX
      ! CMOVL EAX, EBX
      ! CMP ECX, EAX
      ! CMOVB ECX, EAX
      ! MOV c&, ECX
      ! CMC
      ! MOV EAX, 0
      ! ADC EAX, 100
      ! MOV k%, AX
      PRINT lo&; hi&; m&; c&; k%
      """),
    ("jumps-to-labels", """
      DIM n&, s&, k%
      n& = 10
      s& = 0
      top:
      ! MOV EAX, s&
      ! ADD EAX, n&
      ! MOV s&, EAX
      ! DEC n&
      ! JNZ top
      ! MOV AX, 300
      ! CMP AX, 200
      ! JG bigger
      k% = 1
      GOTO done
      bigger:
      k% = 2
      done:
      ! STC
      ! JC carried
      k% = k% + 10
      carried:
      ! TEST AX, AX
      ! JS done2
      ! INC k%
      done2:
      PRINT s&; k%
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

  /// <summary>SETcc has no 8086 emulation to compare against, so its results are stated.</summary>
  [TestCase("x86-32")]
  [TestCase("x64")]
  [TestCase("6502")]
  public void Run_GivenSetcc_ThenEachConditionIsTheOneTheFlagsSay(string platform) {
    const string source = """
      DIM e%, b%, l%, g%
      ! MOV AX, 10
      ! CMP AX, 10
      ! SETE AL
      ! MOV AH, 0
      ! MOV e%, AX
      ! MOV BX, 1
      ! CMP BX, -1
      ! SETB BL
      ! MOV BH, 0
      ! MOV b%, BX
      ! SETL CL
      ! MOV CH, 0
      ! MOV l%, CX
      ! MOV EDX, -7
      ! CMP EDX, 2
      ! SETG DL
      ! MOV DH, 0
      ! MOV g%, DX
      PRINT e%; b%; l%; g%
      """;
    Assert.That(Vice.Normalize(FlatTargets.Run(platform, source)), Is.EqualTo(Vice.Normalize(" 1  1  0  0 \n")));
  }

  /// <summary>
  /// Packed operations the 8086's emulation does not take yet, so there is nothing to run them against
  /// there; the stated results are what the same operations print through the host compiler's SSE2
  /// intrinsics (<c>_mm_subs_epu8</c>, <c>_mm_madd_epi16</c>, <c>_mm_shuffle_epi32</c>, ...).
  /// </summary>
  [TestCase("x86-32")]
  [TestCase("x64")]
  [TestCase("6502")]
  public void Run_GivenPackedOperationsWithoutAn8086Emulation_ThenTheyMatchTheHardware(string platform) {
    const string source = """
      DIM a&, b&, r1&, r2&, r3&, r4&, r5&, r6&, r7&, r8&
      a& = &H7FF08001
      b& = &H0010FF02
      ! MOVD MM0, a&
      ! MOVD MM1, b&
      ! MOVQ MM2, MM0
      ! PSUBUSB MM2, MM1
      ! MOVD r1&, MM2
      ! MOVQ MM2, MM0
      ! PADDUSB MM2, MM1
      ! MOVD r2&, MM2
      ! MOVQ MM2, MM0
      ! PMADDWD MM2, MM1
      ! MOVD r3&, MM2
      ! EMMS
      a& = &H11223344
      b& = &H80FF0102
      ! MOVD XMM0, a&
      ! MOVD XMM1, b&
      ! PUNPCKLDQ XMM0, XMM1
      ! PUNPCKLQDQ XMM0, XMM0
      ! PSHUFD XMM2, XMM0, 27
      ! MOVD r4&, XMM2
      ! PSHUFLW XMM3, XMM0, 177
      ! MOVD r5&, XMM3
      ! MOVDQA XMM3, XMM0
      ! PMAXUB XMM3, XMM1
      ! MOVD r6&, XMM3
      ! MOVDQA XMM3, XMM0
      ! PMINSW XMM3, XMM1
      ! MOVD r7&, XMM3
      ! MOVDQA XMM3, XMM0
      ! PAVGB XMM3, XMM1
      ! MOVD r8&, XMM3
      PRINT HEX$(r1&); " "; HEX$(r2&); " "; HEX$(r3&)
      PRINT HEX$(r4&); " "; HEX$(r5&); " "; HEX$(r6&); " "; HEX$(r7&); " "; HEX$(r8&)
      """;
    Assert.That(Vice.Normalize(FlatTargets.Run(platform, source)),
      Is.EqualTo(Vice.Normalize("7FE00000 7FFFFF03 86FE02\n80FF0102 33441122 80FF3344 80FF0102 49911A23\n")));
  }

  /// <summary>
  /// AVX2 on 256-bit registers: the shuffles, unpacks and packs work within each 128-bit half, the
  /// permutes across them, and a VEX form clears what lies above the register it writes. Stated
  /// results, from the host compiler's AVX2 intrinsics on the same inputs; the 8086's emulation takes
  /// the lane-wise forms only. PB's HEX$ writes -1 as FFFF where the intrinsics' printf wrote FFFFFFFF.
  /// </summary>
  [TestCase("x86-32")]
  [TestCase("x64")]
  [TestCase("6502")]
  public void Run_GivenAvx2_ThenEachHalfAndPermuteMatchesTheHardware(string platform) {
    const string source = """
      DIM a&, b&, c&, d&, r1&, r2&, r3&, r4&, r5&, r6&, r7&, r8&, r9&, r10&, r11&, r12&, r13&, r14&, r15&, r16&, r17&, r18&, r19&
      a& = &H03020100
      b& = &H07060504
      c& = &H8B0A0988
      d& = &H0F0E0D0C
      ! MOVD XMM0, a&
      ! MOVD XMM1, b&
      ! PUNPCKLDQ XMM0, XMM1
      ! MOVD XMM2, c&
      ! MOVD XMM3, d&
      ! PUNPCKLDQ XMM2, XMM3
      ! PUNPCKLQDQ XMM0, XMM2
      ! VINSERTI128 YMM0, YMM0, XMM2, 1
      ! VPBROADCASTD YMM1, XMM2
      ! VPSHUFB YMM2, YMM0, YMM1
      ! MOVD r1&, XMM2
      ! VEXTRACTI128 XMM7, YMM2, 1
      ! MOVD r2&, XMM7
      ! VPUNPCKHBW YMM2, YMM0, YMM1
      ! MOVD r3&, XMM2
      ! VEXTRACTI128 XMM7, YMM2, 1
      ! MOVD r4&, XMM7
      ! VPACKUSWB YMM2, YMM0, YMM1
      ! MOVD r5&, XMM2
      ! VEXTRACTI128 XMM7, YMM2, 1
      ! MOVD r6&, XMM7
      ! VPSHUFD YMM2, YMM0, 27
      ! MOVD r7&, XMM2
      ! VEXTRACTI128 XMM7, YMM2, 1
      ! MOVD r8&, XMM7
      ! VPERMQ YMM2, YMM0, 78
      ! MOVD r9&, XMM2
      ! VEXTRACTI128 XMM7, YMM2, 1
      ! MOVD r10&, XMM7
      ! VPERM2I128 YMM2, YMM0, YMM1, 131
      ! MOVD r11&, XMM2
      ! VEXTRACTI128 XMM7, YMM2, 1
      ! MOVD r12&, XMM7
      ! VPSRLW YMM2, YMM0, 4
      ! MOVD r13&, XMM2
      ! VEXTRACTI128 XMM7, YMM2, 1
      ! PSHUFD XMM7, XMM7, 255
      ! MOVD r14&, XMM7
      ! VPADDB YMM2, YMM0, YMM1
      ! MOVD r15&, XMM2
      ! VEXTRACTI128 XMM7, YMM2, 1
      ! PSHUFD XMM7, XMM7, 255
      ! MOVD r16&, XMM7
      ! VEXTRACTI128 XMM7, YMM0, 1
      ! PSHUFD XMM7, XMM7, 85
      ! MOVD r17&, XMM7
      ! VPADDB XMM2, XMM0, XMM1
      ! VEXTRACTI128 XMM7, YMM2, 1
      ! MOVD r18&, XMM7
      ! VZEROUPPER
      ! VEXTRACTI128 XMM7, YMM0, 1
      ! MOVD r19&, XMM7
      PRINT HEX$(r1&); " "; HEX$(r2&); " "; HEX$(r3&); " "; HEX$(r4&); " "; HEX$(r5&); " "; HEX$(r6&)
      PRINT HEX$(r7&); " "; HEX$(r8&); " "; HEX$(r9&); " "; HEX$(r10&); " "; HEX$(r11&); " "; HEX$(r12&); " "; HEX$(r13&); " "; HEX$(r14&)
      PRINT HEX$(r15&); " "; HEX$(r16&); " "; HEX$(r17&); " "; HEX$(r18&); " "; HEX$(r19&)
      """;
    Assert.That(Vice.Normalize(FlatTargets.Run(platform, source)), Is.EqualTo(Vice.Normalize(
      "A0900 0 9098888 9008800 FFFF FFFF00FF\n"
      + "F0E0D0C 0 8B0A0988 3020100 8B0A0988 0 300010 0\n"
      + "8E0C0A88 8B0A0988 F0E0D0C 0 0\n")));
  }

  /// <summary>
  /// SSE and SSE2 floating point, packed and scalar, with the conversions and an ordered compare
  /// feeding a jump. Stated results, from the host compiler's intrinsics on the same inputs; the
  /// 8086's emulation has no floating-point SIMD. The 6502 has no FPU and computes every lane in
  /// software, so this is also what holds its rounding to IEEE's.
  /// </summary>
  [TestCase("x86-32")]
  [TestCase("x64")]
  [TestCase("6502")]
  public void Run_GivenSseFloatingPoint_ThenEachLaneMatchesTheHardware(string platform) {
    const string source = """
      DIM a&, b&, c&, d&, r1&, r2&, r3&, r4&, r5&, r6&, r7&, r8&, r9&, i1&, i2&, h1&, i3&, h2&, l2&, q0&, q1&, q3&, f1&, v0&, above%
      a& = &H3FC00000
      b& = &HC0100000
      c& = &H40400000
      d& = &H3DCCCCCD
      ! MOVD XMM0, a&
      ! MOVD XMM1, b&
      ! PUNPCKLDQ XMM0, XMM1
      ! MOVD XMM2, c&
      ! MOVD XMM3, d&
      ! PUNPCKLDQ XMM2, XMM3
      ! PUNPCKLQDQ XMM0, XMM2
      ! PSHUFD XMM1, XMM0, 27
      ! MOVAPS XMM2, XMM0
      ! ADDPS XMM2, XMM1
      ! MOVD r1&, XMM2
      ! PSHUFD XMM2, XMM2, 85
      ! MOVD r2&, XMM2
      ! MOVAPS XMM2, XMM0
      ! DIVPS XMM2, XMM1
      ! MOVD r3&, XMM2
      ! MOVAPS XMM2, XMM0
      ! MULPS XMM2, XMM1
      ! PSHUFD XMM2, XMM2, 255
      ! MOVD r4&, XMM2
      ! SQRTPS XMM2, XMM0
      ! PSHUFD XMM2, XMM2, 170
      ! MOVD r5&, XMM2
      ! MOVAPS XMM2, XMM0
      ! MINPS XMM2, XMM1
      ! PSHUFD XMM2, XMM2, 85
      ! MOVD r6&, XMM2
      ! MOVUPS XMM2, XMM0
      ! MAXPS XMM2, XMM1
      ! PSHUFD XMM2, XMM2, 85
      ! MOVD r7&, XMM2
      ! MOVAPS XMM2, XMM0
      ! ADDSS XMM2, XMM1
      ! MOVD r8&, XMM2
      ! PSHUFD XMM2, XMM2, 85
      ! MOVD r9&, XMM2
      ! PSHUFD XMM2, XMM0, 85
      ! CVTTSS2SI EAX, XMM2
      ! MOV i1&, EAX
      ! CVTSS2SI EAX, XMM0
      ! MOV i2&, EAX
      ! XORPS XMM5, XMM5
      ! CVTSS2SD XMM5, XMM0
      ! MULSD XMM5, XMM5
      ! MOV EAX, 7
      ! CVTSI2SD XMM6, EAX
      ! ADDSD XMM5, XMM6
      ! PSHUFD XMM6, XMM5, 85
      ! MOVD h1&, XMM6
      ! CVTTSD2SI EAX, XMM5
      ! MOV i3&, EAX
      ! MOV EAX, 1
      ! CVTSI2SD XMM5, EAX
      ! MOV EAX, 3
      ! CVTSI2SD XMM6, EAX
      ! DIVSD XMM5, XMM6
      ! MOVD l2&, XMM5
      ! PSHUFD XMM6, XMM5, 85
      ! MOVD h2&, XMM6
      ! CVTPS2DQ XMM3, XMM0
      ! MOVD q0&, XMM3
      ! PSHUFD XMM4, XMM3, 85
      ! MOVD q1&, XMM4
      ! PSHUFD XMM4, XMM3, 255
      ! MOVD q3&, XMM4
      ! CVTDQ2PS XMM4, XMM3
      ! PSHUFD XMM4, XMM4, 85
      ! MOVD f1&, XMM4
      ! VMULPS XMM4, XMM0, XMM1
      ! MOVD v0&, XMM4
      ! COMISS XMM0, XMM1
      ! JBE notabove
      above% = 1
      notabove:
      PRINT HEX$(r1&); " "; HEX$(r2&); " "; HEX$(r3&); " "; HEX$(r4&); " "; HEX$(r5&)
      PRINT HEX$(r6&); " "; HEX$(r7&); " "; HEX$(r8&); " "; HEX$(r9&); i1&; i2&
      PRINT HEX$(h1&); i3&; " "; HEX$(h2&); " "; HEX$(l2&)
      PRINT HEX$(q0&); " "; HEX$(q1&); " "; HEX$(q3&); " "; HEX$(f1&); " "; HEX$(v0&); above%
      """;
    Assert.That(Vice.Normalize(FlatTargets.Run(platform, source)), Is.EqualTo(Vice.Normalize(
      "3FCCCCCD 3F400000 41700000 3E19999A 3FDDB3D7\n"
      + "C0100000 40400000 3FCCCCCD C0100000-2  2 \n"
      + "40228000 9  3FD55555 55555555\n"
      + "2 FFFE 0 C0000000 3E19999A 1 \n")));
  }

  /// <summary>
  /// With exceptions masked, as they are by default, a zero divisor answers a signed infinity or
  /// the default NaN and a negative square root the default NaN - on the 6502 too, whose floats are
  /// BASIC's and would otherwise stop the program with error 11 or 5.
  /// </summary>
  [TestCase("x86-32")]
  [TestCase("x64")]
  [TestCase("6502")]
  public void Run_GivenADivisionByZeroOrANegativeRoot_ThenTheLaneIsWhatTheHardwareAnswers(string platform) {
    const string source = """
      DIM a&, b&, r0&, r1&, r2&, s1&, d0&, d1&
      a& = &H3FC00000
      b& = &HC0100000
      ! MOVD XMM0, a&
      ! MOVD XMM1, b&
      ! PUNPCKLDQ XMM0, XMM1
      ! XORPS XMM3, XMM3
      ! MOVAPS XMM2, XMM0
      ! DIVPS XMM2, XMM3
      ! MOVD r0&, XMM2
      ! PSHUFD XMM2, XMM2, 85
      ! MOVD r1&, XMM2
      ! PSHUFD XMM2, XMM3, 0
      ! DIVPS XMM2, XMM3
      ! MOVD r2&, XMM2
      ! SQRTPS XMM4, XMM0
      ! PSHUFD XMM4, XMM4, 85
      ! MOVD s1&, XMM4
      ! CVTSS2SD XMM5, XMM0
      ! XORPD XMM6, XMM6
      ! DIVSD XMM5, XMM6
      ! MOVD d0&, XMM5
      ! PSHUFD XMM5, XMM5, 85
      ! MOVD d1&, XMM5
      PRINT HEX$(r0&); " "; HEX$(r1&); " "; HEX$(r2&); " "; HEX$(s1&); " "; HEX$(d1&); " "; HEX$(d0&)
      """;
    Assert.That(Vice.Normalize(FlatTargets.Run(platform, source)),
      Is.EqualTo(Vice.Normalize("7F800000 FF800000 FFC00000 FFC00000 7FF00000 0\n")));
  }

  [Test]
  public void Compile_GivenAnInstructionNotLiftedYet_ThenItIsDeclinedByName() {
    var work = Directory.CreateTempSubdirectory("pbc-lift-");
    try {
      var path = Path.Combine(work.FullName, "PROG.BAS");
      File.WriteAllText(path, "DIM a&\n! MOV EAX, 1\n! BSWAP EAX\n! MOV a&, EAX\nPRINT a&\n");
      var stderr = new StringWriter();
      var code = PowerBasic.Compiler.Cli.Driver.Run(["--dialect", "pb36", "--platform", "x64", path], TextWriter.Null, stderr);

      Assert.Multiple(() => {
        Assert.That(code, Is.Not.Zero);
        Assert.That(stderr.ToString(), Does.Contain("'BSWAP' has no IR lifting yet"));
      });
    } finally {
      work.Delete(recursive: true);
    }
  }
}
