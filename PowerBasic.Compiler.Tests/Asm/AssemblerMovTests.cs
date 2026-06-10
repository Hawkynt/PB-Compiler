using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

[TestFixture]
public sealed class AssemblerMovTests {

  private static byte[] Assemble(Action<Assembler> emit) {
    var asm = new Assembler();
    emit(asm);
    return asm.ToArray();
  }

  #region register-immediate

  [Test]
  public void Mov_GivenAxImmediate_WhenAssembled_ThenB8Form()
    => Assert.That(Assemble(a => a.Mov(Reg.AX, 1)), Is.EqualTo(new byte[] { 0xB8, 0x01, 0x00 }));

  [TestCase(Reg.AL, 0x12, new byte[] { 0xB0, 0x12 })]
  [TestCase(Reg.CL, 0xFF, new byte[] { 0xB1, 0xFF })]
  [TestCase(Reg.BH, 0x00, new byte[] { 0xB7, 0x00 })]
  public void Mov_GivenByteRegisterImmediate_WhenAssembled_ThenB0PlusRegForm(Reg register, int value, byte[] expected)
    => Assert.That(Assemble(a => a.Mov(register, value)), Is.EqualTo(expected));

  [TestCase(Reg.CX, 0x1234, new byte[] { 0xB9, 0x34, 0x12 })]
  [TestCase(Reg.SP, 0xFFFF, new byte[] { 0xBC, 0xFF, 0xFF })]
  [TestCase(Reg.DI, 0, new byte[] { 0xBF, 0x00, 0x00 })]
  public void Mov_GivenWordRegisterImmediate_WhenAssembled_ThenB8PlusRegForm(Reg register, int value, byte[] expected)
    => Assert.That(Assemble(a => a.Mov(register, value)), Is.EqualTo(expected));

  [Test]
  public void Mov_GivenDwordRegisterImmediate_WhenAssembled_ThenOperandSizePrefixed()
    => Assert.That(Assemble(a => a.Mov(Reg.EAX, 0x12345678)), Is.EqualTo(new byte[] { 0x66, 0xB8, 0x78, 0x56, 0x34, 0x12 }));

  [Test]
  public void Mov_GivenLabelOffsetImmediate_WhenBound_ThenOffsetPatched() {
    var asm = new Assembler();
    var data = asm.DefineLabel();
    asm.Mov(Reg.AX, Imm.OffsetOf(data));
    asm.MarkLabel(data);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0xB8, 0x03, 0x00 }));
  }

  [Test]
  public void Mov_GivenSegmentImmediate_WhenAssembled_ThenRelocationRecorded() {
    var asm = new Assembler();
    asm.Mov(Reg.AX, Imm.Segment());
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0xB8, 0x00, 0x00 }));
    Assert.That(asm.SegmentRelocations, Is.EqualTo(new[] { 1 }));
  }

  #endregion

  #region register-register

  [TestCase(Reg.BX, Reg.CX, new byte[] { 0x89, 0xCB })]
  [TestCase(Reg.AX, Reg.AX, new byte[] { 0x89, 0xC0 })]
  [TestCase(Reg.SP, Reg.BP, new byte[] { 0x89, 0xEC })]
  public void Mov_GivenWordRegisters_WhenAssembled_Then89Form(Reg destination, Reg source, byte[] expected)
    => Assert.That(Assemble(a => a.Mov(destination, source)), Is.EqualTo(expected));

  [TestCase(Reg.BL, Reg.CL, new byte[] { 0x88, 0xCB })]
  [TestCase(Reg.AH, Reg.DH, new byte[] { 0x88, 0xF4 })]
  public void Mov_GivenByteRegisters_WhenAssembled_Then88Form(Reg destination, Reg source, byte[] expected)
    => Assert.That(Assemble(a => a.Mov(destination, source)), Is.EqualTo(expected));

  [Test]
  public void Mov_GivenDwordRegisters_WhenAssembled_ThenPrefixed89Form()
    => Assert.That(Assemble(a => a.Mov(Reg.EBX, Reg.ECX)), Is.EqualTo(new byte[] { 0x66, 0x89, 0xCB }));

  [Test]
  public void Mov_GivenMismatchedRegisterSizes_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().Mov(Reg.AX, Reg.BL));

  #endregion

  #region segment register forms

  [TestCase(Reg.DS, Reg.AX, new byte[] { 0x8E, 0xD8 })]
  [TestCase(Reg.ES, Reg.BX, new byte[] { 0x8E, 0xC3 })]
  [TestCase(Reg.SS, Reg.DX, new byte[] { 0x8E, 0xD2 })]
  public void Mov_GivenSegmentDestination_WhenAssembled_Then8EForm(Reg segment, Reg source, byte[] expected)
    => Assert.That(Assemble(a => a.Mov(segment, source)), Is.EqualTo(expected));

  [TestCase(Reg.AX, Reg.DS, new byte[] { 0x8C, 0xD8 })]
  [TestCase(Reg.BX, Reg.ES, new byte[] { 0x8C, 0xC3 })]
  public void Mov_GivenSegmentSource_WhenAssembled_Then8CForm(Reg destination, Reg segment, byte[] expected)
    => Assert.That(Assemble(a => a.Mov(destination, segment)), Is.EqualTo(expected));

  [Test]
  public void Mov_GivenSegmentFromMemory_WhenAssembled_Then8EForm()
    => Assert.That(Assemble(a => a.Mov(Reg.ES, Mem.At(Reg.BX))), Is.EqualTo(new byte[] { 0x8E, 0x07 }));

  [Test]
  public void Mov_GivenSegmentToMemory_WhenAssembled_Then8CForm()
    => Assert.That(Assemble(a => a.Mov(Mem.At(Reg.BX), Reg.ES)), Is.EqualTo(new byte[] { 0x8C, 0x07 }));

  [Test]
  public void Mov_GivenCsDestination_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().Mov(Reg.CS, Reg.AX));

  #endregion

  #region register-memory

  [Test]
  public void Mov_GivenAxFromEsBx_WhenAssembled_ThenSegmentPrefixedLoad()
    => Assert.That(Assemble(a => a.Mov(Reg.AX, Mem.At(Reg.BX).Es())), Is.EqualTo(new byte[] { 0x26, 0x8B, 0x07 }));

  [Test]
  public void Mov_GivenByteLoad_WhenAssembled_Then8AForm()
    => Assert.That(Assemble(a => a.Mov(Reg.AL, Mem.At(Reg.SI))), Is.EqualTo(new byte[] { 0x8A, 0x04 }));

  [Test]
  public void Mov_GivenByteStore_WhenAssembled_Then88Form()
    => Assert.That(Assemble(a => a.Mov(Mem.At(Reg.DI), Reg.AL)), Is.EqualTo(new byte[] { 0x88, 0x05 }));

  [Test]
  public void Mov_GivenWordStore_WhenAssembled_Then89Form()
    => Assert.That(Assemble(a => a.Mov(Mem.At(Reg.BX), Reg.AX)), Is.EqualTo(new byte[] { 0x89, 0x07 }));

  [Test]
  public void Mov_GivenDwordLoad_WhenAssembled_ThenPrefixed8BForm()
    => Assert.That(Assemble(a => a.Mov(Reg.EAX, Mem.At(Reg.BX))), Is.EqualTo(new byte[] { 0x66, 0x8B, 0x07 }));

  [Test]
  public void Mov_GivenSizedMemoryMismatchingRegister_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().Mov(Reg.AX, Mem.Byte(Reg.BX)));

  #endregion

  #region memory-immediate

  [Test]
  public void Mov_GivenByteMemoryImmediate_WhenAssembled_ThenC6Form()
    => Assert.That(Assemble(a => a.Mov(Mem.Byte(Reg.BX), 0x12)), Is.EqualTo(new byte[] { 0xC6, 0x07, 0x12 }));

  [Test]
  public void Mov_GivenWordMemoryImmediate_WhenAssembled_ThenC7Form()
    => Assert.That(Assemble(a => a.Mov(Mem.Word(Reg.BX, 2), 0x1234)), Is.EqualTo(new byte[] { 0xC7, 0x47, 0x02, 0x34, 0x12 }));

  [Test]
  public void Mov_GivenDwordMemoryImmediate_WhenAssembled_ThenPrefixedC7Form()
    => Assert.That(Assemble(a => a.Mov(Mem.Dword(Reg.BX), 0x11223344)), Is.EqualTo(new byte[] { 0x66, 0xC7, 0x07, 0x44, 0x33, 0x22, 0x11 }));

  [Test]
  public void Mov_GivenUnsizedMemoryImmediate_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().Mov(Mem.At(Reg.BX), 1));

  [Test]
  public void Mov_GivenSegmentPrefixedMemoryImmediate_WhenAssembled_ThenPrefixFirst()
    => Assert.That(Assemble(a => a.Mov(Mem.Byte(Reg.BX).Es(), 1)), Is.EqualTo(new byte[] { 0x26, 0xC6, 0x07, 0x01 }));

  #endregion

  #region XCHG / LEA / LDS / LES

  [TestCase(Reg.AX, Reg.BX, new byte[] { 0x93 })]
  [TestCase(Reg.BX, Reg.AX, new byte[] { 0x93 })]
  [TestCase(Reg.AX, Reg.AX, new byte[] { 0x90 })]
  [TestCase(Reg.CX, Reg.DX, new byte[] { 0x87, 0xD1 })]
  [TestCase(Reg.BL, Reg.CL, new byte[] { 0x86, 0xCB })]
  public void Xchg_GivenRegisters_WhenAssembled_ThenShortestForm(Reg first, Reg second, byte[] expected)
    => Assert.That(Assemble(a => a.Xchg(first, second)), Is.EqualTo(expected));

  [Test]
  public void Xchg_GivenDwordAccumulator_WhenAssembled_ThenPrefixed90Form()
    => Assert.That(Assemble(a => a.Xchg(Reg.EAX, Reg.EBX)), Is.EqualTo(new byte[] { 0x66, 0x93 }));

  [Test]
  public void Xchg_GivenRegisterAndMemory_WhenAssembled_Then87Form()
    => Assert.That(Assemble(a => a.Xchg(Reg.CX, Mem.At(Reg.BX))), Is.EqualTo(new byte[] { 0x87, 0x0F }));

  [Test]
  public void Xchg_GivenByteRegisterAndMemory_WhenAssembled_Then86Form()
    => Assert.That(Assemble(a => a.Xchg(Reg.AL, Mem.At(Reg.BX))), Is.EqualTo(new byte[] { 0x86, 0x07 }));

  [Test]
  public void Lea_GivenBaseIndexDisp_WhenAssembled_Then8DForm()
    => Assert.That(Assemble(a => a.Lea(Reg.AX, Mem.At(Reg.BX, Reg.SI, 4))), Is.EqualTo(new byte[] { 0x8D, 0x40, 0x04 }));

  [Test]
  public void Lea_GivenByteRegister_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().Lea(Reg.AL, Mem.At(Reg.BX)));

  [Test]
  public void Lds_GivenPointerLoad_WhenAssembled_ThenC5Form()
    => Assert.That(Assemble(a => a.Lds(Reg.SI, Mem.At(Reg.BX))), Is.EqualTo(new byte[] { 0xC5, 0x37 }));

  [Test]
  public void Les_GivenPointerLoad_WhenAssembled_ThenC4Form()
    => Assert.That(Assemble(a => a.Les(Reg.DI, Mem.At(Reg.BX))), Is.EqualTo(new byte[] { 0xC4, 0x3F }));

  #endregion
}
