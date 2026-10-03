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
    ("multiply-extend-swap-scan", """
      DIM lo&, hi&, p&, q%, z&, s&, x&, y&
      x& = -7
      ! MOV EAX, 100000
      ! MOV ECX, 300000
      ! MUL ECX
      ! MOV lo&, EAX
      ! MOV hi&, EDX
      ! MOV EAX, x&
      ! MOV ECX, 3
      ! IMUL ECX
      ! MOV p&, EDX
      ! MOV AL, 200
      ! MOV BL, 3
      ! MUL BL
      ! MOV q%, AX
      ! MOV ESI, 70000
      ! IMUL ESI, ESI, 40000
      ! JO overflowed
      ! MOV ESI, 0
      overflowed:
      ! MOV z&, ESI
      ! MOV AL, -5
      ! MOVSX EBX, AL
      ! MOV s&, EBX
      ! MOV AX, -2
      ! MOVZX EDX, AX
      ! MOV y&, EDX
      ! MOV EAX, &H11223344
      ! BSWAP EAX
      ! MOV x&, EAX
      ! MOV EDX, 1000
      ! XCHG EDX, y&
      ! MOV y&, EDX
      PRINT lo&; hi&; p&; q%; z&; s&; y&; HEX$(x&)
      """),
    ("stack", """
      DIM a&, b%, c&, f1%, f2%, v&
      v& = 99
      ! MOV EAX, &H12345678
      ! MOV BX, -3
      ! PUSH EAX
      ! PUSH BX
      ! PUSH v&
      ! POP ECX
      ! MOV c&, ECX
      ! POP DX
      ! MOV b%, DX
      ! POP a&
      ! MOV AX, 5
      ! CMP AX, 7
      ! PUSHF
      ! POP f1%
      ! MOV AX, &H08C1
      ! PUSH AX
      ! POPF
      ! PUSHF
      ! POP f2%
      PRINT HEX$(a&); b%; c&; HEX$(f1% AND &H08C1); " "; HEX$(f2% AND &H08C1)
      """),
    ("divide", """
      DIM q&, r&, q2%, r2%, q3%, n&
      n& = -1000003
      ! MOV EAX, 1000000007
      ! MOV EDX, 2
      ! MOV ECX, 65537
      ! DIV ECX
      ! MOV q&, EAX
      ! MOV r&, EDX
      ! MOV AX, -30000
      ! CWD
      ! MOV CX, 7
      ! IDIV CX
      ! MOV q2%, AX
      ! MOV r2%, DX
      ! MOV AX, 1000
      ! MOV BL, 9
      ! DIV BL
      ! MOV q3%, AX
      ! MOV EAX, n&
      ! CDQ
      ! MOV ECX, 10
      ! IDIV ECX
      ! MOV n&, EDX
      PRINT q&; r&; q2%; r2%; HEX$(q3%); n&
      """),
    ("count-and-bmi", """
      DIM p&, a&, d&, e&, z&, l&, m&, k&, s1&, s2&, s3&, ro&, hi&, lo&
      ! MOV EBX, &H0F0F1234
      ! POPCNT EAX, EBX
      ! MOV p&, EAX
      ! MOV ECX, &H00FF00F0
      ! ANDN EAX, EBX, ECX
      ! MOV a&, EAX
      ! PDEP EAX, EBX, ECX
      ! MOV d&, EAX
      ! PEXT EAX, EBX, ECX
      ! MOV e&, EAX
      ! MOV EDX, 12
      ! BZHI EAX, EBX, EDX
      ! MOV z&, EAX
      ! BLSR EAX, EBX
      ! MOV l&, EAX
      ! BLSMSK EAX, ECX
      ! MOV m&, EAX
      ! BLSI EAX, ECX
      ! MOV k&, EAX
      ! MOV EDX, 36
      ! SHLX EAX, EBX, EDX
      ! MOV s1&, EAX
      ! MOV EDX, 4
      ! SHRX EAX, EBX, EDX
      ! MOV s2&, EAX
      ! MOV ESI, -256
      ! SARX EAX, ESI, EDX
      ! MOV s3&, EAX
      ! RORX EAX, EBX, 8
      ! MOV ro&, EAX
      ! MOV EDX, -2
      ! MULX ESI, EDI, EBX
      ! MOV hi&, ESI
      ! MOV lo&, EDI
      PRINT p&; HEX$(a&); " "; HEX$(d&); " "; HEX$(e&); " "; HEX$(z&); " "; HEX$(l&); " "; HEX$(m&); " "; HEX$(k&)
      PRINT HEX$(s1&); " "; HEX$(s2&); " "; HEX$(s3&); " "; HEX$(ro&); " "; HEX$(hi&); " "; HEX$(lo&)
      """),
    ("zmm-lane-wise", """
      DIM a&, b&, r1&, r2&
      a& = &H7FF08001
      b& = &H0010FF02
      ! MOVD XMM0, a&
      ! MOVD XMM1, b&
      ! VPADDD ZMM2, ZMM0, ZMM1
      ! MOVD r1&, XMM2
      ! VPSUBW ZMM3, ZMM2, ZMM0
      ! MOVD r2&, XMM3
      PRINT HEX$(r1&); " "; HEX$(r2&)
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

  /// <summary>SETcc, BSF and BSR have no 8086 emulation to compare against, so their results are stated.</summary>
  [TestCase("x86-32")]
  [TestCase("x64")]
  [TestCase("6502")]
  public void Run_GivenSetcc_ThenEachConditionIsTheOneTheFlagsSay(string platform) {
    const string source = """
      DIM e%, b%, l%, g%, f%, r%, w%, z%
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
      ! MOV AX, &H0140
      ! BSF CX, AX
      ! MOV f%, CX
      ! BSR CX, AX
      ! MOV r%, CX
      ! MOV DX, 3
      ! MOV BX, 0
      ! BSF DX, BX
      ! SETZ CL
      ! MOV CH, 0
      ! MOV w%, DX
      ! MOV z%, CX
      PRINT e%; b%; l%; g%; f%; r%; w%; z%
      """;
    Assert.That(Vice.Normalize(FlatTargets.Run(platform, source)), Is.EqualTo(Vice.Normalize(" 1  1  0  0  6  8  3  1 \n")));
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

  /// <summary>LZCNT and TZCNT, which the 8086's emulation takes only for a 386 target, with the zero source each counts as the full width.</summary>
  [TestCase("x86-32")]
  [TestCase("x64")]
  [TestCase("6502")]
  public void Run_GivenLzcntAndTzcnt_ThenTheyCountAsTheHardwareCounts(string platform) {
    const string source = """
      DIM l&, t&, lz%, tz%, z&, c%
      ! MOV EBX, &H00012300
      ! LZCNT EAX, EBX
      ! MOV l&, EAX
      ! TZCNT EAX, EBX
      ! MOV t&, EAX
      ! MOV BX, 0
      ! LZCNT AX, BX
      ! MOV lz%, AX
      ! SETC CL
      ! MOV CH, 0
      ! MOV c%, CX
      ! MOV EBX, 0
      ! TZCNT EAX, EBX
      ! MOV z&, EAX
      ! MOV BX, &H8000
      ! TZCNT AX, BX
      ! MOV tz%, AX
      PRINT l&; t&; lz%; c%; z&; tz%
      """;
    Assert.That(Vice.Normalize(FlatTargets.Run(platform, source)), Is.EqualTo(Vice.Normalize(" 15  8  16  1  32  15 \n")));
  }

  /// <summary>A zero divisor, or a quotient too wide for its register, is the processor's divide error: error 11, as a PowerBASIC program reports it.</summary>
  [TestCase("x86-32", "! MOV ECX, 0\n! DIV ECX")]
  [TestCase("x64", "! MOV AX, 1000\n! MOV CL, 2\n! DIV CL")]
  [TestCase("6502", "! MOV AX, -32768\n! CWD\n! MOV BX, -1\n! IDIV BX")]
  public void Run_GivenADivideError_ThenItIsError11(string platform, string division) {
    var source = "DIM a&\nPRINT \"before\"\n! MOV EAX, 100\n" + division + "\n! MOV a&, EAX\nPRINT a&\n";
    var output = Vice.Normalize(FlatTargets.Run(platform, source));
    Assert.That(output, Does.StartWith("before"));
    Assert.That(output, Does.Contain("11"));
  }

  /// <summary>
  /// AVX-512 on ZMM registers: the same packed operations at 512 bits, unpacks and shuffles still
  /// within each 128-bit block, and the 128- and 256-bit inserts and extracts. Stated results, from the
  /// host compiler's AVX-512 intrinsics on the same inputs.
  /// </summary>
  [TestCase("x86-32")]
  [TestCase("x64")]
  [TestCase("6502")]
  public void Run_GivenAvx512_ThenEachPartMatchesTheHardware(string platform) {
    const string source = """
      DIM a&, b&, c&, d&, e&, f&, r0&, r13&, r15&, u12&, u15&, m12&, h7&
      a& = &H01020304
      b& = &H100
      c& = &H11111111
      d& = &H22222222
      e& = &H33333333
      f& = &H44444444
      ! VPBROADCASTD ZMM0, a&
      ! MOVD XMM1, c&
      ! MOVD XMM2, d&
      ! PUNPCKLDQ XMM1, XMM2
      ! MOVD XMM2, e&
      ! MOVD XMM3, f&
      ! PUNPCKLDQ XMM2, XMM3
      ! PUNPCKLQDQ XMM1, XMM2
      ! VINSERTI32X4 ZMM0, ZMM0, XMM1, 3
      ! VPBROADCASTD ZMM1, b&
      ! VPADDD ZMM2, ZMM0, ZMM1
      ! MOVD r0&, XMM2
      ! VEXTRACTI32X4 XMM7, ZMM2, 3
      ! PSHUFD XMM6, XMM7, 85
      ! MOVD r13&, XMM6
      ! PSHUFD XMM6, XMM7, 255
      ! MOVD r15&, XMM6
      ! VPUNPCKHDQ ZMM3, ZMM0, ZMM1
      ! VEXTRACTI32X4 XMM7, ZMM3, 3
      ! MOVD u12&, XMM7
      ! PSHUFD XMM6, XMM7, 255
      ! MOVD u15&, XMM6
      ! VPSHUFD ZMM4, ZMM0, 27
      ! VEXTRACTI32X4 XMM7, ZMM4, 3
      ! MOVD m12&, XMM7
      ! VEXTRACTI64X4 YMM5, ZMM0, 1
      ! VEXTRACTI128 XMM7, YMM5, 1
      ! PSHUFD XMM6, XMM7, 255
      ! MOVD h7&, XMM6
      PRINT HEX$(r0&); " "; HEX$(r13&); " "; HEX$(r15&); " "; HEX$(u12&); " "; HEX$(u15&); " "; HEX$(m12&); " "; HEX$(h7&)
      """;
    Assert.That(Vice.Normalize(FlatTargets.Run(platform, source)),
      Is.EqualTo(Vice.Normalize("1020404 22222322 44444544 33333333 100 44444444 44444444\n")));
  }

  [Test]
  public void Compile_GivenAnInstructionNotLiftedYet_ThenItIsDeclinedByName() {
    var work = Directory.CreateTempSubdirectory("pbc-lift-");
    try {
      var path = Path.Combine(work.FullName, "PROG.BAS");
      File.WriteAllText(path, "DIM a&\n! MOV EAX, 1\n! MOV ECX, 2\n! BTS EAX, ECX\n! MOV a&, EAX\nPRINT a&\n");
      var stderr = new StringWriter();
      var code = PowerBasic.Compiler.Cli.Driver.Run(["--dialect", "pb36", "--platform", "x64", path], TextWriter.Null, stderr);

      Assert.Multiple(() => {
        Assert.That(code, Is.Not.Zero);
        Assert.That(stderr.ToString(), Does.Contain("'BTS' has no IR lifting yet"));
      });
    } finally {
      work.Delete(recursive: true);
    }
  }
}
