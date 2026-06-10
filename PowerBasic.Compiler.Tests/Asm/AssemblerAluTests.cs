using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

[TestFixture]
public sealed class AssemblerAluTests {

  private static byte[] Assemble(Action<Assembler> emit) {
    var asm = new Assembler();
    emit(asm);
    return asm.ToArray();
  }

  #region opcode bases per operation

  private static IEnumerable<TestCaseData> RegisterRegisterCases() {
    yield return new((Action<Assembler>)(a => a.Add(Reg.AX, Reg.BX)), new byte[] { 0x01, 0xD8 }) { TestName = "Add" };
    yield return new((Action<Assembler>)(a => a.Or(Reg.AX, Reg.BX)), new byte[] { 0x09, 0xD8 }) { TestName = "Or" };
    yield return new((Action<Assembler>)(a => a.Adc(Reg.AX, Reg.BX)), new byte[] { 0x11, 0xD8 }) { TestName = "Adc" };
    yield return new((Action<Assembler>)(a => a.Sbb(Reg.AX, Reg.BX)), new byte[] { 0x19, 0xD8 }) { TestName = "Sbb" };
    yield return new((Action<Assembler>)(a => a.And(Reg.AX, Reg.BX)), new byte[] { 0x21, 0xD8 }) { TestName = "And" };
    yield return new((Action<Assembler>)(a => a.Sub(Reg.AX, Reg.BX)), new byte[] { 0x29, 0xD8 }) { TestName = "Sub" };
    yield return new((Action<Assembler>)(a => a.Xor(Reg.AX, Reg.BX)), new byte[] { 0x31, 0xD8 }) { TestName = "Xor" };
    yield return new((Action<Assembler>)(a => a.Cmp(Reg.AX, Reg.BX)), new byte[] { 0x39, 0xD8 }) { TestName = "Cmp" };
    yield return new((Action<Assembler>)(a => a.Add(Reg.AL, Reg.BL)), new byte[] { 0x00, 0xD8 }) { TestName = "AddByte" };
    yield return new((Action<Assembler>)(a => a.Xor(Reg.AL, Reg.BL)), new byte[] { 0x30, 0xD8 }) { TestName = "XorByte" };
    yield return new((Action<Assembler>)(a => a.Add(Reg.EAX, Reg.EBX)), new byte[] { 0x66, 0x01, 0xD8 }) { TestName = "AddDword" };
  }

  [TestCaseSource(nameof(RegisterRegisterCases))]
  public void Alu_GivenRegisterOperands_WhenAssembled_ThenOpcodeBaseMatchesOperation(Action<Assembler> emit, byte[] expected)
    => Assert.That(Assemble(emit), Is.EqualTo(expected));

  private static IEnumerable<TestCaseData> ImmediateGroupCases() {
    // 83 /digit selects the operation
    yield return new((Action<Assembler>)(a => a.Add(Reg.BX, 5)), new byte[] { 0x83, 0xC3, 0x05 }) { TestName = "AddDigit0" };
    yield return new((Action<Assembler>)(a => a.Or(Reg.BX, 5)), new byte[] { 0x83, 0xCB, 0x05 }) { TestName = "OrDigit1" };
    yield return new((Action<Assembler>)(a => a.Adc(Reg.BX, 5)), new byte[] { 0x83, 0xD3, 0x05 }) { TestName = "AdcDigit2" };
    yield return new((Action<Assembler>)(a => a.Sbb(Reg.BX, 5)), new byte[] { 0x83, 0xDB, 0x05 }) { TestName = "SbbDigit3" };
    yield return new((Action<Assembler>)(a => a.And(Reg.BX, 5)), new byte[] { 0x83, 0xE3, 0x05 }) { TestName = "AndDigit4" };
    yield return new((Action<Assembler>)(a => a.Sub(Reg.BX, 5)), new byte[] { 0x83, 0xEB, 0x05 }) { TestName = "SubDigit5" };
    yield return new((Action<Assembler>)(a => a.Xor(Reg.BX, 5)), new byte[] { 0x83, 0xF3, 0x05 }) { TestName = "XorDigit6" };
    yield return new((Action<Assembler>)(a => a.Cmp(Reg.BX, 5)), new byte[] { 0x83, 0xFB, 0x05 }) { TestName = "CmpDigit7" };
  }

  [TestCaseSource(nameof(ImmediateGroupCases))]
  public void Alu_GivenSmallImmediate_WhenAssembled_ThenSignExtended83Form(Action<Assembler> emit, byte[] expected)
    => Assert.That(Assemble(emit), Is.EqualTo(expected));

  #endregion

  #region immediate form selection boundaries

  [TestCase(127, new byte[] { 0x83, 0xC3, 0x7F })]
  [TestCase(-128, new byte[] { 0x83, 0xC3, 0x80 })]
  [TestCase(128, new byte[] { 0x81, 0xC3, 0x80, 0x00 })]
  [TestCase(-129, new byte[] { 0x81, 0xC3, 0x7F, 0xFF })]
  public void Add_GivenImmediateAtSByteBoundary_WhenAssembled_ThenFormSwitches(int value, byte[] expected)
    => Assert.That(Assemble(a => a.Add(Reg.BX, value)), Is.EqualTo(expected));

  [Test]
  public void Add_GivenAlImmediate_WhenAssembled_ThenAccumulatorShortForm()
    => Assert.That(Assemble(a => a.Add(Reg.AL, 5)), Is.EqualTo(new byte[] { 0x04, 0x05 }));

  [Test]
  public void Add_GivenAxSmallImmediate_WhenAssembled_ThenSignExtendedFormPreferred()
    => Assert.That(Assemble(a => a.Add(Reg.AX, 5)), Is.EqualTo(new byte[] { 0x83, 0xC0, 0x05 }));

  [Test]
  public void Add_GivenAxLargeImmediate_WhenAssembled_ThenAccumulatorForm()
    => Assert.That(Assemble(a => a.Add(Reg.AX, 300)), Is.EqualTo(new byte[] { 0x05, 0x2C, 0x01 }));

  [Test]
  public void Add_GivenByteRegisterImmediate_WhenAssembled_Then80Form()
    => Assert.That(Assemble(a => a.Add(Reg.BL, 5)), Is.EqualTo(new byte[] { 0x80, 0xC3, 0x05 }));

  [Test]
  public void Add_GivenNonAccumulatorLargeImmediate_WhenAssembled_Then81Form()
    => Assert.That(Assemble(a => a.Add(Reg.BX, 300)), Is.EqualTo(new byte[] { 0x81, 0xC3, 0x2C, 0x01 }));

  [Test]
  public void Add_GivenDwordLargeImmediate_WhenAssembled_ThenPrefixedAccumulatorForm()
    => Assert.That(Assemble(a => a.Add(Reg.EAX, 0x12345)), Is.EqualTo(new byte[] { 0x66, 0x05, 0x45, 0x23, 0x01, 0x00 }));

  [Test]
  public void Add_GivenDwordSmallImmediate_WhenAssembled_ThenPrefixed83Form()
    => Assert.That(Assemble(a => a.Add(Reg.EAX, 5)), Is.EqualTo(new byte[] { 0x66, 0x83, 0xC0, 0x05 }));

  #endregion

  #region memory operands

  [Test]
  public void Add_GivenMemoryDestinationByteRegister_WhenAssembled_ThenTaskGoldenBytes()
    => Assert.That(Assemble(a => a.Add(Mem.At(Reg.BX, Reg.SI, 6), Reg.AL)), Is.EqualTo(new byte[] { 0x00, 0x40, 0x06 }));

  [Test]
  public void Add_GivenMemorySource_WhenAssembled_Then03Form()
    => Assert.That(Assemble(a => a.Add(Reg.AX, Mem.At(Reg.BX))), Is.EqualTo(new byte[] { 0x03, 0x07 }));

  [Test]
  public void Add_GivenByteMemorySource_WhenAssembled_Then02Form()
    => Assert.That(Assemble(a => a.Add(Reg.AL, Mem.At(Reg.BX))), Is.EqualTo(new byte[] { 0x02, 0x07 }));

  [Test]
  public void Add_GivenByteMemoryImmediate_WhenAssembled_Then80Form()
    => Assert.That(Assemble(a => a.Add(Mem.Byte(Reg.BX), 5)), Is.EqualTo(new byte[] { 0x80, 0x07, 0x05 }));

  [Test]
  public void Add_GivenWordMemorySmallImmediate_WhenAssembled_Then83Form()
    => Assert.That(Assemble(a => a.Add(Mem.Word(Reg.BX), 5)), Is.EqualTo(new byte[] { 0x83, 0x07, 0x05 }));

  [Test]
  public void Add_GivenWordMemoryLargeImmediate_WhenAssembled_Then81Form()
    => Assert.That(Assemble(a => a.Add(Mem.Word(Reg.BX), 300)), Is.EqualTo(new byte[] { 0x81, 0x07, 0x2C, 0x01 }));

  [Test]
  public void Add_GivenUnsizedMemoryImmediate_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().Add(Mem.At(Reg.BX), 5));

  [Test]
  public void Cmp_GivenSegmentPrefixedMemory_WhenAssembled_ThenPrefixFirst()
    => Assert.That(Assemble(a => a.Cmp(Reg.AX, Mem.At(Reg.BX).Es())), Is.EqualTo(new byte[] { 0x26, 0x3B, 0x07 }));

  #endregion

  #region TEST / NOT / NEG

  [TestCase(Reg.AL, Reg.BL, new byte[] { 0x84, 0xD8 })]
  [TestCase(Reg.AX, Reg.BX, new byte[] { 0x85, 0xD8 })]
  public void Test_GivenRegisters_WhenAssembled_Then84Or85Form(Reg first, Reg second, byte[] expected)
    => Assert.That(Assemble(a => a.Test(first, second)), Is.EqualTo(expected));

  [Test]
  public void Test_GivenAlImmediate_WhenAssembled_ThenA8Form()
    => Assert.That(Assemble(a => a.Test(Reg.AL, 5)), Is.EqualTo(new byte[] { 0xA8, 0x05 }));

  [Test]
  public void Test_GivenAxImmediate_WhenAssembled_ThenA9Form()
    => Assert.That(Assemble(a => a.Test(Reg.AX, 5)), Is.EqualTo(new byte[] { 0xA9, 0x05, 0x00 }));

  [Test]
  public void Test_GivenByteRegisterImmediate_WhenAssembled_ThenF6Form()
    => Assert.That(Assemble(a => a.Test(Reg.BL, 5)), Is.EqualTo(new byte[] { 0xF6, 0xC3, 0x05 }));

  [Test]
  public void Test_GivenWordRegisterImmediate_WhenAssembled_ThenF7Form()
    => Assert.That(Assemble(a => a.Test(Reg.BX, 5)), Is.EqualTo(new byte[] { 0xF7, 0xC3, 0x05, 0x00 }));

  [Test]
  public void Test_GivenMemoryAndRegister_WhenAssembled_Then84Form()
    => Assert.That(Assemble(a => a.Test(Mem.At(Reg.BX), Reg.AL)), Is.EqualTo(new byte[] { 0x84, 0x07 }));

  [Test]
  public void Test_GivenByteMemoryImmediate_WhenAssembled_ThenF6Form()
    => Assert.That(Assemble(a => a.Test(Mem.Byte(Reg.BX), 1)), Is.EqualTo(new byte[] { 0xF6, 0x07, 0x01 }));

  [TestCase(Reg.AL, new byte[] { 0xF6, 0xD0 })]
  [TestCase(Reg.AX, new byte[] { 0xF7, 0xD0 })]
  public void Not_GivenRegister_WhenAssembled_ThenDigit2Form(Reg register, byte[] expected)
    => Assert.That(Assemble(a => a.Not(register)), Is.EqualTo(expected));

  [TestCase(Reg.AL, new byte[] { 0xF6, 0xD8 })]
  [TestCase(Reg.AX, new byte[] { 0xF7, 0xD8 })]
  public void Neg_GivenRegister_WhenAssembled_ThenDigit3Form(Reg register, byte[] expected)
    => Assert.That(Assemble(a => a.Neg(register)), Is.EqualTo(expected));

  [Test]
  public void Neg_GivenByteMemory_WhenAssembled_ThenF6Form()
    => Assert.That(Assemble(a => a.Neg(Mem.Byte(Reg.BX))), Is.EqualTo(new byte[] { 0xF6, 0x1F }));

  [Test]
  public void Not_GivenDwordRegister_WhenAssembled_ThenPrefixedF7Form()
    => Assert.That(Assemble(a => a.Not(Reg.EAX)), Is.EqualTo(new byte[] { 0x66, 0xF7, 0xD0 }));

  #endregion

  #region INC / DEC

  [TestCase(Reg.AX, new byte[] { 0x40 })]
  [TestCase(Reg.DI, new byte[] { 0x47 })]
  public void Inc_GivenWordRegister_WhenAssembled_ThenShortForm(Reg register, byte[] expected)
    => Assert.That(Assemble(a => a.Inc(register)), Is.EqualTo(expected));

  [TestCase(Reg.AX, new byte[] { 0x48 })]
  [TestCase(Reg.BP, new byte[] { 0x4D })]
  public void Dec_GivenWordRegister_WhenAssembled_ThenShortForm(Reg register, byte[] expected)
    => Assert.That(Assemble(a => a.Dec(register)), Is.EqualTo(expected));

  [Test]
  public void Inc_GivenByteRegister_WhenAssembled_ThenFeForm()
    => Assert.That(Assemble(a => a.Inc(Reg.AL)), Is.EqualTo(new byte[] { 0xFE, 0xC0 }));

  [Test]
  public void Dec_GivenByteRegister_WhenAssembled_ThenFeForm()
    => Assert.That(Assemble(a => a.Dec(Reg.BL)), Is.EqualTo(new byte[] { 0xFE, 0xCB }));

  [Test]
  public void Inc_GivenDwordRegister_WhenAssembled_ThenPrefixedShortForm()
    => Assert.That(Assemble(a => a.Inc(Reg.EAX)), Is.EqualTo(new byte[] { 0x66, 0x40 }));

  [Test]
  public void Inc_GivenWordMemory_WhenAssembled_ThenFfForm()
    => Assert.That(Assemble(a => a.Inc(Mem.Word(Reg.BX))), Is.EqualTo(new byte[] { 0xFF, 0x07 }));

  [Test]
  public void Inc_GivenByteMemory_WhenAssembled_ThenFeForm()
    => Assert.That(Assemble(a => a.Inc(Mem.Byte(Reg.BX))), Is.EqualTo(new byte[] { 0xFE, 0x07 }));

  [Test]
  public void Dec_GivenWordMemoryAtBp_WhenAssembled_ThenDisp8Form()
    => Assert.That(Assemble(a => a.Dec(Mem.Word(Reg.BP))), Is.EqualTo(new byte[] { 0xFF, 0x4E, 0x00 }));

  #endregion

  #region MUL / IMUL / DIV / IDIV / sign extension

  [TestCase(Reg.BL, new byte[] { 0xF6, 0xE3 })]
  [TestCase(Reg.BX, new byte[] { 0xF7, 0xE3 })]
  public void Mul_GivenRegister_WhenAssembled_ThenDigit4Form(Reg register, byte[] expected)
    => Assert.That(Assemble(a => a.Mul(register)), Is.EqualTo(expected));

  [Test]
  public void Imul_GivenRegister_WhenAssembled_ThenDigit5Form()
    => Assert.That(Assemble(a => a.Imul(Reg.BX)), Is.EqualTo(new byte[] { 0xF7, 0xEB }));

  [Test]
  public void Div_GivenRegister_WhenAssembled_ThenDigit6Form()
    => Assert.That(Assemble(a => a.Div(Reg.BX)), Is.EqualTo(new byte[] { 0xF7, 0xF3 }));

  [Test]
  public void Idiv_GivenRegister_WhenAssembled_ThenDigit7Form()
    => Assert.That(Assemble(a => a.Idiv(Reg.BX)), Is.EqualTo(new byte[] { 0xF7, 0xFB }));

  [Test]
  public void Mul_GivenWordMemory_WhenAssembled_ThenF7Form()
    => Assert.That(Assemble(a => a.Mul(Mem.Word(Reg.BX))), Is.EqualTo(new byte[] { 0xF7, 0x27 }));

  [Test]
  public void Imul_GivenTwoRegisters_WhenAssembled_Then0FAfForm()
    => Assert.That(Assemble(a => a.Imul(Reg.AX, Reg.BX)), Is.EqualTo(new byte[] { 0x0F, 0xAF, 0xC3 }));

  [Test]
  public void Imul_GivenRegisterAndMemory_WhenAssembled_Then0FAfForm()
    => Assert.That(Assemble(a => a.Imul(Reg.CX, Mem.At(Reg.BX))), Is.EqualTo(new byte[] { 0x0F, 0xAF, 0x0F }));

  [Test]
  public void Imul_GivenSmallImmediate_WhenAssembled_Then6BForm()
    => Assert.That(Assemble(a => a.Imul(Reg.AX, Reg.BX, 5)), Is.EqualTo(new byte[] { 0x6B, 0xC3, 0x05 }));

  [Test]
  public void Imul_GivenLargeImmediate_WhenAssembled_Then69Form()
    => Assert.That(Assemble(a => a.Imul(Reg.AX, Reg.BX, 300)), Is.EqualTo(new byte[] { 0x69, 0xC3, 0x2C, 0x01 }));

  [Test]
  public void Imul_GivenMemoryAndImmediate_WhenAssembled_Then6BForm()
    => Assert.That(Assemble(a => a.Imul(Reg.CX, Mem.Word(Reg.BX), 10)), Is.EqualTo(new byte[] { 0x6B, 0x0F, 0x0A }));

  [Test]
  public void Imul_GivenTwoOperandImmediate_WhenAssembled_ThenDestinationDoubled()
    => Assert.That(Assemble(a => a.Imul(Reg.DX, 100)), Is.EqualTo(new byte[] { 0x6B, 0xD2, 0x64 }));

  [Test]
  public void Cbw_WhenAssembled_Then98() => Assert.That(Assemble(a => a.Cbw()), Is.EqualTo(new byte[] { 0x98 }));

  [Test]
  public void Cwd_WhenAssembled_Then99() => Assert.That(Assemble(a => a.Cwd()), Is.EqualTo(new byte[] { 0x99 }));

  [Test]
  public void Cwde_WhenAssembled_ThenPrefixed98() => Assert.That(Assemble(a => a.Cwde()), Is.EqualTo(new byte[] { 0x66, 0x98 }));

  [Test]
  public void Cdq_WhenAssembled_ThenPrefixed99() => Assert.That(Assemble(a => a.Cdq()), Is.EqualTo(new byte[] { 0x66, 0x99 }));

  #endregion
}
