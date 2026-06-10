using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

[TestFixture]
public sealed class AssemblerShiftTests {

  private static byte[] Assemble(Action<Assembler> emit) {
    var asm = new Assembler();
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
    => Assert.That(Assemble(a => a.Shl(Reg.AX, 5)), Is.EqualTo(new byte[] { 0xC1, 0xE0, 0x05 }));

  [Test]
  public void Shl_GivenByteRegisterImmediateCount_WhenAssembled_ThenC0Form()
    => Assert.That(Assemble(a => a.Shl(Reg.AL, 3)), Is.EqualTo(new byte[] { 0xC0, 0xE0, 0x03 }));

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
    => Assert.That(Assemble(a => a.Shr(Mem.Word(Reg.BX), 4)), Is.EqualTo(new byte[] { 0xC1, 0x2F, 0x04 }));

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
