using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

[TestFixture]
public sealed class AssemblerFpuTests {

  private static byte[] Assemble(Action<Assembler> emit) {
    var asm = new Assembler();
    emit(asm);
    return asm.ToArray();
  }

  #region load / store

  [Test]
  public void Fld_GivenDwordMemory_WhenAssembled_ThenD9Digit0()
    => Assert.That(Assemble(a => a.Fld(Mem.Dword(Reg.BX))), Is.EqualTo(new byte[] { 0xD9, 0x07 }));

  [Test]
  public void Fld_GivenQwordMemory_WhenAssembled_ThenTaskGoldenBytes()
    => Assert.That(Assemble(a => a.Fld(Mem.Qword(Reg.BX))), Is.EqualTo(new byte[] { 0xDD, 0x07 }));

  [Test]
  public void Fld_GivenTbyteMemory_WhenAssembled_ThenDbDigit5()
    => Assert.That(Assemble(a => a.Fld(Mem.Tbyte(Reg.BX))), Is.EqualTo(new byte[] { 0xDB, 0x2F }));

  [Test]
  public void Fld_GivenStackRegister_WhenAssembled_ThenD9C0Form()
    => Assert.That(Assemble(a => a.Fld(St.St2)), Is.EqualTo(new byte[] { 0xD9, 0xC2 }));

  [Test]
  public void Fld_GivenWordMemory_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().Fld(Mem.Word(Reg.BX)));

  [Test]
  public void Fld_GivenUnsizedMemory_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().Fld(Mem.At(Reg.BX)));

  [Test]
  public void Fst_GivenDwordMemory_WhenAssembled_ThenD9Digit2()
    => Assert.That(Assemble(a => a.Fst(Mem.Dword(Reg.BX))), Is.EqualTo(new byte[] { 0xD9, 0x17 }));

  [Test]
  public void Fst_GivenQwordMemory_WhenAssembled_ThenDdDigit2()
    => Assert.That(Assemble(a => a.Fst(Mem.Qword(Reg.BX))), Is.EqualTo(new byte[] { 0xDD, 0x17 }));

  [Test]
  public void Fst_GivenTbyteMemory_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().Fst(Mem.Tbyte(Reg.BX)));

  [Test]
  public void Fst_GivenStackRegister_WhenAssembled_ThenDdD0Form()
    => Assert.That(Assemble(a => a.Fst(St.St3)), Is.EqualTo(new byte[] { 0xDD, 0xD3 }));

  [Test]
  public void Fstp_GivenDwordMemory_WhenAssembled_ThenD9Digit3()
    => Assert.That(Assemble(a => a.Fstp(Mem.Dword(Reg.BX))), Is.EqualTo(new byte[] { 0xD9, 0x1F }));

  [Test]
  public void Fstp_GivenQwordMemory_WhenAssembled_ThenDdDigit3()
    => Assert.That(Assemble(a => a.Fstp(Mem.Qword(Reg.BX))), Is.EqualTo(new byte[] { 0xDD, 0x1F }));

  [Test]
  public void Fstp_GivenTbyteMemory_WhenAssembled_ThenDbDigit7()
    => Assert.That(Assemble(a => a.Fstp(Mem.Tbyte(Reg.BX))), Is.EqualTo(new byte[] { 0xDB, 0x3F }));

  [Test]
  public void Fstp_GivenStackRegister_WhenAssembled_ThenDdD8Form()
    => Assert.That(Assemble(a => a.Fstp(St.St3)), Is.EqualTo(new byte[] { 0xDD, 0xDB }));

  [Test]
  public void Fld_GivenSegmentPrefixedMemory_WhenAssembled_ThenPrefixFirst()
    => Assert.That(Assemble(a => a.Fld(Mem.Dword(Reg.BX).Es())), Is.EqualTo(new byte[] { 0x26, 0xD9, 0x07 }));

  #endregion

  #region integer load / store

  [TestCase(OperandSize.Word, new byte[] { 0xDF, 0x07 })]
  [TestCase(OperandSize.Dword, new byte[] { 0xDB, 0x07 })]
  [TestCase(OperandSize.Qword, new byte[] { 0xDF, 0x2F })]
  public void Fild_GivenIntegerMemory_WhenAssembled_ThenSizeSelectsOpcode(OperandSize size, byte[] expected)
    => Assert.That(Assemble(a => a.Fild(Mem.At(Reg.BX).WithSize(size))), Is.EqualTo(expected));

  [TestCase(OperandSize.Word, new byte[] { 0xDF, 0x17 })]
  [TestCase(OperandSize.Dword, new byte[] { 0xDB, 0x17 })]
  public void Fist_GivenIntegerMemory_WhenAssembled_ThenSizeSelectsOpcode(OperandSize size, byte[] expected)
    => Assert.That(Assemble(a => a.Fist(Mem.At(Reg.BX).WithSize(size))), Is.EqualTo(expected));

  [TestCase(OperandSize.Word, new byte[] { 0xDF, 0x1F })]
  [TestCase(OperandSize.Dword, new byte[] { 0xDB, 0x1F })]
  [TestCase(OperandSize.Qword, new byte[] { 0xDF, 0x3F })]
  public void Fistp_GivenIntegerMemory_WhenAssembled_ThenSizeSelectsOpcode(OperandSize size, byte[] expected)
    => Assert.That(Assemble(a => a.Fistp(Mem.At(Reg.BX).WithSize(size))), Is.EqualTo(expected));

  [Test]
  public void Fist_GivenQwordMemory_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().Fist(Mem.Qword(Reg.BX)));

  [Test]
  public void Fild_GivenByteMemory_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().Fild(Mem.Byte(Reg.BX)));

  #endregion

  #region arithmetic

  private static IEnumerable<TestCaseData> ArithmeticMemoryCases() {
    yield return new((Action<Assembler>)(a => a.Fadd(Mem.Dword(Reg.BX))), new byte[] { 0xD8, 0x07 }) { TestName = "FaddSingle" };
    yield return new((Action<Assembler>)(a => a.Fadd(Mem.Qword(Reg.BX))), new byte[] { 0xDC, 0x07 }) { TestName = "FaddDouble" };
    yield return new((Action<Assembler>)(a => a.Fmul(Mem.Dword(Reg.BX))), new byte[] { 0xD8, 0x0F }) { TestName = "FmulSingle" };
    yield return new((Action<Assembler>)(a => a.Fsub(Mem.Dword(Reg.BX))), new byte[] { 0xD8, 0x27 }) { TestName = "FsubSingle" };
    yield return new((Action<Assembler>)(a => a.Fsubr(Mem.Dword(Reg.BX))), new byte[] { 0xD8, 0x2F }) { TestName = "FsubrSingle" };
    yield return new((Action<Assembler>)(a => a.Fdiv(Mem.Dword(Reg.BX))), new byte[] { 0xD8, 0x37 }) { TestName = "FdivSingle" };
    yield return new((Action<Assembler>)(a => a.Fdivr(Mem.Dword(Reg.BX))), new byte[] { 0xD8, 0x3F }) { TestName = "FdivrSingle" };
    yield return new((Action<Assembler>)(a => a.Fdiv(Mem.Qword(Reg.BP))), new byte[] { 0xDC, 0x76, 0x00 }) { TestName = "FdivDoubleAtBp" };
  }

  [TestCaseSource(nameof(ArithmeticMemoryCases))]
  public void FpuArithmetic_GivenMemoryOperand_WhenAssembled_ThenSizeSelectsEscape(Action<Assembler> emit, byte[] expected)
    => Assert.That(Assemble(emit), Is.EqualTo(expected));

  private static IEnumerable<TestCaseData> ArithmeticStackCases() {
    yield return new((Action<Assembler>)(a => a.Fadd(St.St0, St.St3)), new byte[] { 0xD8, 0xC3 }) { TestName = "FaddToSt0" };
    yield return new((Action<Assembler>)(a => a.Fadd(St.St3, St.St0)), new byte[] { 0xDC, 0xC3 }) { TestName = "FaddToSt3" };
    yield return new((Action<Assembler>)(a => a.Fmul(St.St0, St.St2)), new byte[] { 0xD8, 0xCA }) { TestName = "FmulToSt0" };
    yield return new((Action<Assembler>)(a => a.Fmul(St.St2, St.St0)), new byte[] { 0xDC, 0xCA }) { TestName = "FmulToSt2" };
    // SUB/DIV swap their opcode slots in the DC encoding
    yield return new((Action<Assembler>)(a => a.Fsub(St.St0, St.St2)), new byte[] { 0xD8, 0xE2 }) { TestName = "FsubToSt0" };
    yield return new((Action<Assembler>)(a => a.Fsub(St.St2, St.St0)), new byte[] { 0xDC, 0xEA }) { TestName = "FsubToSt2" };
    yield return new((Action<Assembler>)(a => a.Fsubr(St.St0, St.St2)), new byte[] { 0xD8, 0xEA }) { TestName = "FsubrToSt0" };
    yield return new((Action<Assembler>)(a => a.Fsubr(St.St2, St.St0)), new byte[] { 0xDC, 0xE2 }) { TestName = "FsubrToSt2" };
    yield return new((Action<Assembler>)(a => a.Fdiv(St.St0, St.St2)), new byte[] { 0xD8, 0xF2 }) { TestName = "FdivToSt0" };
    yield return new((Action<Assembler>)(a => a.Fdiv(St.St2, St.St0)), new byte[] { 0xDC, 0xFA }) { TestName = "FdivToSt2" };
    yield return new((Action<Assembler>)(a => a.Fdivr(St.St0, St.St2)), new byte[] { 0xD8, 0xFA }) { TestName = "FdivrToSt0" };
    yield return new((Action<Assembler>)(a => a.Fdivr(St.St2, St.St0)), new byte[] { 0xDC, 0xF2 }) { TestName = "FdivrToSt2" };
  }

  [TestCaseSource(nameof(ArithmeticStackCases))]
  public void FpuArithmetic_GivenStackOperands_WhenAssembled_ThenDirectionSelectsEscape(Action<Assembler> emit, byte[] expected)
    => Assert.That(Assemble(emit), Is.EqualTo(expected));

  [Test]
  public void Fadd_GivenNeitherOperandSt0_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().Fadd(St.St2, St.St3));

  private static IEnumerable<TestCaseData> PopArithmeticCases() {
    yield return new((Action<Assembler>)(a => a.Faddp()), new byte[] { 0xDE, 0xC1 }) { TestName = "Faddp" };
    yield return new((Action<Assembler>)(a => a.Faddp(St.St2)), new byte[] { 0xDE, 0xC2 }) { TestName = "FaddpSt2" };
    yield return new((Action<Assembler>)(a => a.Fmulp()), new byte[] { 0xDE, 0xC9 }) { TestName = "Fmulp" };
    yield return new((Action<Assembler>)(a => a.Fsubp()), new byte[] { 0xDE, 0xE9 }) { TestName = "Fsubp" };
    yield return new((Action<Assembler>)(a => a.Fsubrp()), new byte[] { 0xDE, 0xE1 }) { TestName = "Fsubrp" };
    yield return new((Action<Assembler>)(a => a.Fdivp()), new byte[] { 0xDE, 0xF9 }) { TestName = "Fdivp" };
    yield return new((Action<Assembler>)(a => a.Fdivrp()), new byte[] { 0xDE, 0xF1 }) { TestName = "Fdivrp" };
  }

  [TestCaseSource(nameof(PopArithmeticCases))]
  public void FpuPopArithmetic_WhenAssembled_ThenDeEscape(Action<Assembler> emit, byte[] expected)
    => Assert.That(Assemble(emit), Is.EqualTo(expected));

  private static IEnumerable<TestCaseData> IntegerArithmeticCases() {
    yield return new((Action<Assembler>)(a => a.Fiadd(Mem.Word(Reg.BX))), new byte[] { 0xDE, 0x07 }) { TestName = "FiaddWord" };
    yield return new((Action<Assembler>)(a => a.Fiadd(Mem.Dword(Reg.BX))), new byte[] { 0xDA, 0x07 }) { TestName = "FiaddDword" };
    yield return new((Action<Assembler>)(a => a.Fimul(Mem.Word(Reg.BX))), new byte[] { 0xDE, 0x0F }) { TestName = "FimulWord" };
    yield return new((Action<Assembler>)(a => a.Fisub(Mem.Word(Reg.BX))), new byte[] { 0xDE, 0x27 }) { TestName = "FisubWord" };
    yield return new((Action<Assembler>)(a => a.Fisubr(Mem.Word(Reg.BX))), new byte[] { 0xDE, 0x2F }) { TestName = "FisubrWord" };
    yield return new((Action<Assembler>)(a => a.Fidiv(Mem.Word(Reg.BX))), new byte[] { 0xDE, 0x37 }) { TestName = "FidivWord" };
    yield return new((Action<Assembler>)(a => a.Fidivr(Mem.Dword(Reg.BX))), new byte[] { 0xDA, 0x3F }) { TestName = "FidivrDword" };
  }

  [TestCaseSource(nameof(IntegerArithmeticCases))]
  public void FpuIntegerArithmetic_GivenMemory_WhenAssembled_ThenSizeSelectsEscape(Action<Assembler> emit, byte[] expected)
    => Assert.That(Assemble(emit), Is.EqualTo(expected));

  [Test]
  public void Fiadd_GivenQwordMemory_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().Fiadd(Mem.Qword(Reg.BX)));

  #endregion

  #region comparisons

  [Test]
  public void Fcom_GivenDwordMemory_WhenAssembled_ThenD8Digit2()
    => Assert.That(Assemble(a => a.Fcom(Mem.Dword(Reg.BX))), Is.EqualTo(new byte[] { 0xD8, 0x17 }));

  [Test]
  public void Fcom_GivenQwordMemory_WhenAssembled_ThenDcDigit2()
    => Assert.That(Assemble(a => a.Fcom(Mem.Qword(Reg.BX))), Is.EqualTo(new byte[] { 0xDC, 0x17 }));

  [Test]
  public void Fcom_GivenDefaultOperand_WhenAssembled_ThenSt1Form()
    => Assert.That(Assemble(a => a.Fcom()), Is.EqualTo(new byte[] { 0xD8, 0xD1 }));

  [Test]
  public void Fcom_GivenStackRegister_WhenAssembled_ThenD8D0Form()
    => Assert.That(Assemble(a => a.Fcom(St.St2)), Is.EqualTo(new byte[] { 0xD8, 0xD2 }));

  [Test]
  public void Fcomp_GivenStackRegister_WhenAssembled_ThenD8D8Form()
    => Assert.That(Assemble(a => a.Fcomp(St.St2)), Is.EqualTo(new byte[] { 0xD8, 0xDA }));

  [Test]
  public void Fcomp_GivenDwordMemory_WhenAssembled_ThenD8Digit3()
    => Assert.That(Assemble(a => a.Fcomp(Mem.Dword(Reg.BX))), Is.EqualTo(new byte[] { 0xD8, 0x1F }));

  [Test]
  public void Fcompp_WhenAssembled_ThenDeD9()
    => Assert.That(Assemble(a => a.Fcompp()), Is.EqualTo(new byte[] { 0xDE, 0xD9 }));

  [Test]
  public void Ficom_GivenWordMemory_WhenAssembled_ThenDeDigit2()
    => Assert.That(Assemble(a => a.Ficom(Mem.Word(Reg.BX))), Is.EqualTo(new byte[] { 0xDE, 0x17 }));

  [Test]
  public void Ficomp_GivenDwordMemory_WhenAssembled_ThenDaDigit3()
    => Assert.That(Assemble(a => a.Ficomp(Mem.Dword(Reg.BX))), Is.EqualTo(new byte[] { 0xDA, 0x1F }));

  [Test]
  public void Fucom_GivenDefaultOperand_WhenAssembled_ThenDdE1()
    => Assert.That(Assemble(a => a.Fucom()), Is.EqualTo(new byte[] { 0xDD, 0xE1 }));

  [Test]
  public void Fucom_GivenStackRegister_WhenAssembled_ThenDdE0Form()
    => Assert.That(Assemble(a => a.Fucom(St.St2)), Is.EqualTo(new byte[] { 0xDD, 0xE2 }));

  [Test]
  public void Fucomp_GivenStackRegister_WhenAssembled_ThenDdE8Form()
    => Assert.That(Assemble(a => a.Fucomp(St.St2)), Is.EqualTo(new byte[] { 0xDD, 0xEA }));

  [Test]
  public void Fucompp_WhenAssembled_ThenDaE9()
    => Assert.That(Assemble(a => a.Fucompp()), Is.EqualTo(new byte[] { 0xDA, 0xE9 }));

  [Test]
  public void Ftst_WhenAssembled_ThenD9E4()
    => Assert.That(Assemble(a => a.Ftst()), Is.EqualTo(new byte[] { 0xD9, 0xE4 }));

  #endregion

  #region unary / transcendental / constants

  private static IEnumerable<TestCaseData> NoOperandCases() {
    yield return new((Action<Assembler>)(a => a.Fchs()), new byte[] { 0xD9, 0xE0 }) { TestName = "Fchs" };
    yield return new((Action<Assembler>)(a => a.Fabs()), new byte[] { 0xD9, 0xE1 }) { TestName = "Fabs" };
    yield return new((Action<Assembler>)(a => a.Fsqrt()), new byte[] { 0xD9, 0xFA }) { TestName = "Fsqrt" };
    yield return new((Action<Assembler>)(a => a.Frndint()), new byte[] { 0xD9, 0xFC }) { TestName = "Frndint" };
    yield return new((Action<Assembler>)(a => a.Fscale()), new byte[] { 0xD9, 0xFD }) { TestName = "Fscale" };
    yield return new((Action<Assembler>)(a => a.Fprem()), new byte[] { 0xD9, 0xF8 }) { TestName = "Fprem" };
    yield return new((Action<Assembler>)(a => a.Fprem1()), new byte[] { 0xD9, 0xF5 }) { TestName = "Fprem1" };
    yield return new((Action<Assembler>)(a => a.Fptan()), new byte[] { 0xD9, 0xF2 }) { TestName = "Fptan" };
    yield return new((Action<Assembler>)(a => a.Fpatan()), new byte[] { 0xD9, 0xF3 }) { TestName = "Fpatan" };
    yield return new((Action<Assembler>)(a => a.F2xm1()), new byte[] { 0xD9, 0xF0 }) { TestName = "F2xm1" };
    yield return new((Action<Assembler>)(a => a.Fyl2x()), new byte[] { 0xD9, 0xF1 }) { TestName = "Fyl2x" };
    yield return new((Action<Assembler>)(a => a.Fyl2xp1()), new byte[] { 0xD9, 0xF9 }) { TestName = "Fyl2xp1" };
    yield return new((Action<Assembler>)(a => a.Fsin()), new byte[] { 0xD9, 0xFE }) { TestName = "Fsin" };
    yield return new((Action<Assembler>)(a => a.Fcos()), new byte[] { 0xD9, 0xFF }) { TestName = "Fcos" };
    yield return new((Action<Assembler>)(a => a.Fsincos()), new byte[] { 0xD9, 0xFB }) { TestName = "Fsincos" };
    yield return new((Action<Assembler>)(a => a.Fldz()), new byte[] { 0xD9, 0xEE }) { TestName = "Fldz" };
    yield return new((Action<Assembler>)(a => a.Fld1()), new byte[] { 0xD9, 0xE8 }) { TestName = "Fld1" };
    yield return new((Action<Assembler>)(a => a.Fldpi()), new byte[] { 0xD9, 0xEB }) { TestName = "Fldpi" };
    yield return new((Action<Assembler>)(a => a.Fldl2e()), new byte[] { 0xD9, 0xEA }) { TestName = "Fldl2e" };
    yield return new((Action<Assembler>)(a => a.Fldl2t()), new byte[] { 0xD9, 0xE9 }) { TestName = "Fldl2t" };
    yield return new((Action<Assembler>)(a => a.Fldlg2()), new byte[] { 0xD9, 0xEC }) { TestName = "Fldlg2" };
    yield return new((Action<Assembler>)(a => a.Fldln2()), new byte[] { 0xD9, 0xED }) { TestName = "Fldln2" };
    yield return new((Action<Assembler>)(a => a.Fincstp()), new byte[] { 0xD9, 0xF7 }) { TestName = "Fincstp" };
    yield return new((Action<Assembler>)(a => a.Fdecstp()), new byte[] { 0xD9, 0xF6 }) { TestName = "Fdecstp" };
    yield return new((Action<Assembler>)(a => a.Fninit()), new byte[] { 0xDB, 0xE3 }) { TestName = "Fninit" };
    yield return new((Action<Assembler>)(a => a.Finit()), new byte[] { 0x9B, 0xDB, 0xE3 }) { TestName = "Finit" };
    yield return new((Action<Assembler>)(a => a.Fnclex()), new byte[] { 0xDB, 0xE2 }) { TestName = "Fnclex" };
    yield return new((Action<Assembler>)(a => a.Fclex()), new byte[] { 0x9B, 0xDB, 0xE2 }) { TestName = "Fclex" };
    yield return new((Action<Assembler>)(a => a.Fwait()), new byte[] { 0x9B }) { TestName = "Fwait" };
  }

  [TestCaseSource(nameof(NoOperandCases))]
  public void FpuNoOperandInstruction_WhenAssembled_ThenExactBytes(Action<Assembler> emit, byte[] expected)
    => Assert.That(Assemble(emit), Is.EqualTo(expected));

  #endregion

  #region exchange / free / control words

  [Test]
  public void Fxch_GivenDefaultOperand_WhenAssembled_ThenSt1Form()
    => Assert.That(Assemble(a => a.Fxch()), Is.EqualTo(new byte[] { 0xD9, 0xC9 }));

  [Test]
  public void Fxch_GivenStackRegister_WhenAssembled_ThenD9C8Form()
    => Assert.That(Assemble(a => a.Fxch(St.St3)), Is.EqualTo(new byte[] { 0xD9, 0xCB }));

  [Test]
  public void Ffree_GivenStackRegister_WhenAssembled_ThenDdC0Form()
    => Assert.That(Assemble(a => a.Ffree(St.St3)), Is.EqualTo(new byte[] { 0xDD, 0xC3 }));

  [Test]
  public void FstswAx_WhenAssembled_ThenWaitPrefixedForm()
    => Assert.That(Assemble(a => a.FstswAx()), Is.EqualTo(new byte[] { 0x9B, 0xDF, 0xE0 }));

  [Test]
  public void FnstswAx_WhenAssembled_ThenDfE0()
    => Assert.That(Assemble(a => a.FnstswAx()), Is.EqualTo(new byte[] { 0xDF, 0xE0 }));

  [Test]
  public void Fstsw_GivenMemory_WhenAssembled_ThenWaitPrefixedDdDigit7()
    => Assert.That(Assemble(a => a.Fstsw(Mem.Word(Reg.BX))), Is.EqualTo(new byte[] { 0x9B, 0xDD, 0x3F }));

  [Test]
  public void Fnstsw_GivenMemory_WhenAssembled_ThenDdDigit7()
    => Assert.That(Assemble(a => a.Fnstsw(Mem.At(Reg.BX))), Is.EqualTo(new byte[] { 0xDD, 0x3F }));

  [Test]
  public void Fstcw_GivenMemory_WhenAssembled_ThenWaitPrefixedD9Digit7()
    => Assert.That(Assemble(a => a.Fstcw(Mem.Word(Reg.BX))), Is.EqualTo(new byte[] { 0x9B, 0xD9, 0x3F }));

  [Test]
  public void Fnstcw_GivenMemory_WhenAssembled_ThenD9Digit7()
    => Assert.That(Assemble(a => a.Fnstcw(Mem.At(Reg.BX))), Is.EqualTo(new byte[] { 0xD9, 0x3F }));

  [Test]
  public void Fldcw_GivenMemory_WhenAssembled_ThenD9Digit5()
    => Assert.That(Assemble(a => a.Fldcw(Mem.Word(Reg.BX))), Is.EqualTo(new byte[] { 0xD9, 0x2F }));

  [Test]
  public void Fldcw_GivenDwordMemory_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().Fldcw(Mem.Dword(Reg.BX)));

  [Test]
  public void Fstsw_GivenSegmentPrefixedMemory_WhenAssembled_ThenWaitThenPrefix()
    => Assert.That(Assemble(a => a.Fstsw(Mem.Word(Reg.BX).Es())), Is.EqualTo(new byte[] { 0x9B, 0x26, 0xDD, 0x3F }));

  #endregion

  #region St model

  [Test]
  public void St_GivenIndexOutOfRange_WhenConstructed_ThenThrows()
    => Assert.Throws<ArgumentOutOfRangeException>(() => _ = new St(8));

  [Test]
  public void St_GivenNegativeIndex_WhenConstructed_ThenThrows()
    => Assert.Throws<ArgumentOutOfRangeException>(() => _ = new St(-1));

  #endregion
}
