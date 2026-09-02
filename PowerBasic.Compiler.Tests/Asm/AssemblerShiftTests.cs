using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

[TestFixture]
public sealed class AssemblerShiftTests {

  private static byte[] Assemble(Action<Assembler> emit) {
    var asm = new Assembler();
    emit(asm);
    return asm.ToArray();
  }

  /// <summary>
  /// The same for a target that HAS the 80186 immediate-count shift and rotate. A bare assembler
  /// builds for an 8086, whose group 2 is only the count-one D0/D1 and the CL-count D2/D3, so a test
  /// of the C0/C1 form has to declare the target that has it - the way the near-conditional-jump
  /// tests declare <c>Allow386Jcc</c>.
  /// </summary>
  private static byte[] Assemble186(Action<Assembler> emit) {
    var asm = new Assembler { Allow186ImmediateShifts = true };
    emit(asm);
    return asm.ToArray();
  }

  #region operation digits

  private static IEnumerable<TestCaseData> ShiftByOneCases() {
    yield return new((Action<Assembler>)(a => a.Rol(Reg.AX, 1)), new byte[] { 0xD1, 0xC0 }) { TestName = "RolDigit0" };
    yield return new((Action<Assembler>)(a => a.Ror(Reg.AX, 1)), new byte[] { 0xD1, 0xC8 }) { TestName = "RorDigit1" };
    yield return new((Action<Assembler>)(a => a.Rcl(Reg.AX, 1)), new byte[] { 0xD1, 0xD0 }) { TestName = "RclDigit2" };
    yield return new((Action<Assembler>)(a => a.Rcr(Reg.AX, 1)), new byte[] { 0xD1, 0xD8 }) { TestName = "RcrDigit3" };
    yield return new((Action<Assembler>)(a => a.Shl(Reg.AX, 1)), new byte[] { 0xD1, 0xE0 }) { TestName = "ShlDigit4" };
    yield return new((Action<Assembler>)(a => a.Shr(Reg.AX, 1)), new byte[] { 0xD1, 0xE8 }) { TestName = "ShrDigit5" };
    yield return new((Action<Assembler>)(a => a.Sar(Reg.AX, 1)), new byte[] { 0xD1, 0xF8 }) { TestName = "SarDigit7" };
  }

  [TestCaseSource(nameof(ShiftByOneCases))]
  public void Shift_GivenCountOne_WhenAssembled_ThenD1Form(Action<Assembler> emit, byte[] expected)
    => Assert.That(Assemble(emit), Is.EqualTo(expected));

  #endregion

  #region count forms

  [Test]
  public void Shl_GivenByteRegisterCountOne_WhenAssembled_ThenD0Form()
    => Assert.That(Assemble(a => a.Shl(Reg.AL, 1)), Is.EqualTo(new byte[] { 0xD0, 0xE0 }));

  [Test]
  public void Shl_GivenImmediateCount_WhenAssembled_ThenC1Form()
    => Assert.That(Assemble186(a => a.Shl(Reg.AX, 5)), Is.EqualTo(new byte[] { 0xC1, 0xE0, 0x05 }));

  [Test]
  public void Shl_GivenByteRegisterImmediateCount_WhenAssembled_ThenC0Form()
    => Assert.That(Assemble186(a => a.Shl(Reg.AL, 3)), Is.EqualTo(new byte[] { 0xC0, 0xE0, 0x03 }));

  /// <summary>
  /// The default target has no C0/C1, so a multi-bit immediate count becomes that many count-one
  /// instructions rather than a later-generation opcode in an image that says it is an 8086.
  /// </summary>
  [Test]
  public void Shr_GivenImmediateCountOnAn8086_WhenAssembled_ThenRepeatedD1Form()
    => Assert.That(Assemble(a => a.Shr(Reg.AX, 3)),
      Is.EqualTo(new byte[] { 0xD1, 0xE8, 0xD1, 0xE8, 0xD1, 0xE8 }));

  [Test]
  public void Shl_GivenByteRegisterImmediateCountOnAn8086_WhenAssembled_ThenRepeatedD0Form()
    => Assert.That(Assemble(a => a.Shl(Reg.AL, 2)), Is.EqualTo(new byte[] { 0xD0, 0xE0, 0xD0, 0xE0 }));

  /// <summary>
  /// RCR expands too. Rotating through the carry n times IS n single-bit rotates through the carry,
  /// so the expansion carries the same bit chain the one-instruction form would have.
  /// </summary>
  [Test]
  public void Rcr_GivenImmediateCountOnAn8086_WhenAssembled_ThenRepeatedD1Form()
    => Assert.That(Assemble(a => a.Rcr(Reg.BX, 2)), Is.EqualTo(new byte[] { 0xD1, 0xDB, 0xD1, 0xDB }));

  [Test]
  public void Shr_GivenWordMemoryImmediateCountOnAn8086_WhenAssembled_ThenRepeatedD1Form()
    => Assert.That(Assemble(a => a.Shr(Mem.Word(Reg.BX), 2)),
      Is.EqualTo(new byte[] { 0xD1, 0x2F, 0xD1, 0x2F }));

  [Test]
  public void Shld_GivenDwordRegisters_WhenAssembled_Then66_0F_A4()
    // SHLD EDX, EAX, 4: 66 prefix, 0F A4, ModRM mod=11 reg=EAX(0) rm=EDX(2)=C2, imm 04
    => Assert.That(Assemble(a => a.Shld(Reg.EDX, Reg.EAX, 4)), Is.EqualTo(new byte[] { 0x66, 0x0F, 0xA4, 0xC2, 0x04 }));

  [Test]
  public void Shrd_GivenDwordRegisters_WhenAssembled_Then66_0F_AC()
    // SHRD EAX, EDX, 8: 66 prefix, 0F AC, ModRM mod=11 reg=EDX(2) rm=EAX(0)=D0, imm 08
    => Assert.That(Assemble(a => a.Shrd(Reg.EAX, Reg.EDX, 8)), Is.EqualTo(new byte[] { 0x66, 0x0F, 0xAC, 0xD0, 0x08 }));

  [Test]
  public void Shld_GivenWordRegisters_WhenAssembled_ThenNo66Prefix()
    => Assert.That(Assemble(a => a.Shld(Reg.DX, Reg.AX, 1)), Is.EqualTo(new byte[] { 0x0F, 0xA4, 0xC2, 0x01 }));

  [Test]
  public void Shl_GivenClCount_WhenAssembled_ThenD3Form()
    => Assert.That(Assemble(a => a.Shl(Reg.AX, Reg.CL)), Is.EqualTo(new byte[] { 0xD3, 0xE0 }));

  [Test]
  public void Shr_GivenByteRegisterClCount_WhenAssembled_ThenD2Form()
    => Assert.That(Assemble(a => a.Shr(Reg.AL, Reg.CL)), Is.EqualTo(new byte[] { 0xD2, 0xE8 }));

  [Test]
  public void Shl_GivenDwordRegister_WhenAssembled_ThenPrefixedD1Form()
    => Assert.That(Assemble(a => a.Shl(Reg.EAX, 1)), Is.EqualTo(new byte[] { 0x66, 0xD1, 0xE0 }));

  [Test]
  public void Shl_GivenNonClCountRegister_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().Shl(Reg.AX, Reg.BL));

  [TestCase(0)]
  [TestCase(32)]
  public void Shl_GivenCountOutOfRange_WhenAssembled_ThenThrows(int count)
    => Assert.Throws<ArgumentOutOfRangeException>(() => new Assembler().Shl(Reg.AX, count));

  #endregion

  #region memory destinations

  [Test]
  public void Shl_GivenByteMemoryCountOne_WhenAssembled_ThenD0Form()
    => Assert.That(Assemble(a => a.Shl(Mem.Byte(Reg.BX), 1)), Is.EqualTo(new byte[] { 0xD0, 0x27 }));

  [Test]
  public void Shl_GivenWordMemoryClCount_WhenAssembled_ThenD3Form()
    => Assert.That(Assemble(a => a.Shl(Mem.Word(Reg.BX), Reg.CL)), Is.EqualTo(new byte[] { 0xD3, 0x27 }));

  [Test]
  public void Shr_GivenWordMemoryImmediateCount_WhenAssembled_ThenC1Form()
    => Assert.That(Assemble186(a => a.Shr(Mem.Word(Reg.BX), 4)), Is.EqualTo(new byte[] { 0xC1, 0x2F, 0x04 }));

  [Test]
  public void Shl_GivenUnsizedMemory_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().Shl(Mem.At(Reg.BX), 1));

  #endregion

  #region MOVZX / MOVSX

  [Test]
  public void Movzx_GivenWordFromByteRegister_WhenAssembled_Then0FB6Form()
    => Assert.That(Assemble(a => a.Movzx(Reg.AX, Reg.BL)), Is.EqualTo(new byte[] { 0x0F, 0xB6, 0xC3 }));

  [Test]
  public void Movzx_GivenDwordFromByteRegister_WhenAssembled_ThenPrefixed0FB6Form()
    => Assert.That(Assemble(a => a.Movzx(Reg.EAX, Reg.BL)), Is.EqualTo(new byte[] { 0x66, 0x0F, 0xB6, 0xC3 }));

  [Test]
  public void Movzx_GivenDwordFromWordRegister_WhenAssembled_ThenPrefixed0FB7Form()
    => Assert.That(Assemble(a => a.Movzx(Reg.EAX, Reg.BX)), Is.EqualTo(new byte[] { 0x66, 0x0F, 0xB7, 0xC3 }));

  [Test]
  public void Movsx_GivenWordFromByteRegister_WhenAssembled_Then0FBeForm()
    => Assert.That(Assemble(a => a.Movsx(Reg.AX, Reg.BL)), Is.EqualTo(new byte[] { 0x0F, 0xBE, 0xC3 }));

  [Test]
  public void Movsx_GivenDwordFromWordRegister_WhenAssembled_ThenPrefixed0FBfForm()
    => Assert.That(Assemble(a => a.Movsx(Reg.EAX, Reg.BX)), Is.EqualTo(new byte[] { 0x66, 0x0F, 0xBF, 0xC3 }));

  [Test]
  public void Movzx_GivenByteMemorySource_WhenAssembled_Then0FB6Form()
    => Assert.That(Assemble(a => a.Movzx(Reg.AX, Mem.Byte(Reg.BX))), Is.EqualTo(new byte[] { 0x0F, 0xB6, 0x07 }));

  [Test]
  public void Movzx_GivenWordMemorySource_WhenAssembled_ThenPrefixed0FB7Form()
    => Assert.That(Assemble(a => a.Movzx(Reg.EAX, Mem.Word(Reg.BX))), Is.EqualTo(new byte[] { 0x66, 0x0F, 0xB7, 0x07 }));

  [Test]
  public void Movsx_GivenByteMemorySource_WhenAssembled_Then0FBeForm()
    => Assert.That(Assemble(a => a.Movsx(Reg.CX, Mem.Byte(Reg.SI))), Is.EqualTo(new byte[] { 0x0F, 0xBE, 0x0C }));

  [Test]
  public void Movzx_GivenEqualSizes_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().Movzx(Reg.AX, Reg.BX));

  [Test]
  public void Movzx_GivenUnsizedMemory_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().Movzx(Reg.AX, Mem.At(Reg.BX)));

  #endregion
}
