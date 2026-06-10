using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

[TestFixture]
public sealed class AssemblerStackAndFlowTests {

  private static byte[] Assemble(Action<Assembler> emit) {
    var asm = new Assembler();
    emit(asm);
    return asm.ToArray();
  }

  #region PUSH / POP

  [TestCase(Reg.AX, new byte[] { 0x50 })]
  [TestCase(Reg.BX, new byte[] { 0x53 })]
  [TestCase(Reg.SP, new byte[] { 0x54 })]
  [TestCase(Reg.DI, new byte[] { 0x57 })]
  public void Push_GivenWordRegister_WhenAssembled_Then50PlusReg(Reg register, byte[] expected)
    => Assert.That(Assemble(a => a.Push(register)), Is.EqualTo(expected));

  [TestCase(Reg.AX, new byte[] { 0x58 })]
  [TestCase(Reg.BP, new byte[] { 0x5D })]
  public void Pop_GivenWordRegister_WhenAssembled_Then58PlusReg(Reg register, byte[] expected)
    => Assert.That(Assemble(a => a.Pop(register)), Is.EqualTo(expected));

  [TestCase(Reg.ES, new byte[] { 0x06 })]
  [TestCase(Reg.CS, new byte[] { 0x0E })]
  [TestCase(Reg.SS, new byte[] { 0x16 })]
  [TestCase(Reg.DS, new byte[] { 0x1E })]
  [TestCase(Reg.FS, new byte[] { 0x0F, 0xA0 })]
  [TestCase(Reg.GS, new byte[] { 0x0F, 0xA8 })]
  public void Push_GivenSegmentRegister_WhenAssembled_ThenDedicatedOpcode(Reg register, byte[] expected)
    => Assert.That(Assemble(a => a.Push(register)), Is.EqualTo(expected));

  [TestCase(Reg.ES, new byte[] { 0x07 })]
  [TestCase(Reg.SS, new byte[] { 0x17 })]
  [TestCase(Reg.DS, new byte[] { 0x1F })]
  [TestCase(Reg.FS, new byte[] { 0x0F, 0xA1 })]
  [TestCase(Reg.GS, new byte[] { 0x0F, 0xA9 })]
  public void Pop_GivenSegmentRegister_WhenAssembled_ThenDedicatedOpcode(Reg register, byte[] expected)
    => Assert.That(Assemble(a => a.Pop(register)), Is.EqualTo(expected));

  [Test]
  public void Pop_GivenCs_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().Pop(Reg.CS));

  [Test]
  public void Push_GivenByteRegister_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().Push(Reg.AL));

  [Test]
  public void Push_GivenDwordRegister_WhenAssembled_ThenPrefixed()
    => Assert.That(Assemble(a => a.Push(Reg.EAX)), Is.EqualTo(new byte[] { 0x66, 0x50 }));

  [Test]
  public void Push_GivenWordMemory_WhenAssembled_ThenFfDigit6()
    => Assert.That(Assemble(a => a.Push(Mem.Word(Reg.BX))), Is.EqualTo(new byte[] { 0xFF, 0x37 }));

  [Test]
  public void Pop_GivenWordMemory_WhenAssembled_Then8FDigit0()
    => Assert.That(Assemble(a => a.Pop(Mem.Word(Reg.BX))), Is.EqualTo(new byte[] { 0x8F, 0x07 }));

  [Test]
  public void Push_GivenByteMemory_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().Push(Mem.Byte(Reg.BX)));

  [TestCase(1, new byte[] { 0x6A, 0x01 })]
  [TestCase(127, new byte[] { 0x6A, 0x7F })]
  [TestCase(-128, new byte[] { 0x6A, 0x80 })]
  [TestCase(128, new byte[] { 0x68, 0x80, 0x00 })]
  [TestCase(-129, new byte[] { 0x68, 0x7F, 0xFF })]
  [TestCase(0x1234, new byte[] { 0x68, 0x34, 0x12 })]
  public void Push_GivenImmediate_WhenAssembled_ThenShortestForm(int value, byte[] expected)
    => Assert.That(Assemble(a => a.Push(value)), Is.EqualTo(expected));

  [Test]
  public void Pusha_WhenAssembled_Then60() => Assert.That(Assemble(a => a.Pusha()), Is.EqualTo(new byte[] { 0x60 }));

  [Test]
  public void Popa_WhenAssembled_Then61() => Assert.That(Assemble(a => a.Popa()), Is.EqualTo(new byte[] { 0x61 }));

  [Test]
  public void Pushf_WhenAssembled_Then9C() => Assert.That(Assemble(a => a.Pushf()), Is.EqualTo(new byte[] { 0x9C }));

  [Test]
  public void Popf_WhenAssembled_Then9D() => Assert.That(Assemble(a => a.Popf()), Is.EqualTo(new byte[] { 0x9D }));

  #endregion

  #region conditional jumps

  [TestCase(Condition.Overflow, (byte)0x70)]
  [TestCase(Condition.NotOverflow, (byte)0x71)]
  [TestCase(Condition.Below, (byte)0x72)]
  [TestCase(Condition.AboveOrEqual, (byte)0x73)]
  [TestCase(Condition.Equal, (byte)0x74)]
  [TestCase(Condition.NotEqual, (byte)0x75)]
  [TestCase(Condition.BelowOrEqual, (byte)0x76)]
  [TestCase(Condition.Above, (byte)0x77)]
  [TestCase(Condition.Sign, (byte)0x78)]
  [TestCase(Condition.NotSign, (byte)0x79)]
  [TestCase(Condition.Parity, (byte)0x7A)]
  [TestCase(Condition.NotParity, (byte)0x7B)]
  [TestCase(Condition.Less, (byte)0x7C)]
  [TestCase(Condition.GreaterOrEqual, (byte)0x7D)]
  [TestCase(Condition.LessOrEqual, (byte)0x7E)]
  [TestCase(Condition.Greater, (byte)0x7F)]
  public void J_GivenBoundTargetInShortRange_WhenAssembled_ThenShortForm(Condition condition, byte opcode) {
    var asm = new Assembler();
    var top = asm.MarkLabel("top");
    asm.J(condition, top);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { opcode, 0xFE }));
  }

  [Test]
  public void J_GivenForwardTarget_WhenAssembled_ThenNearFormUsed() {
    var asm = new Assembler();
    var done = asm.DefineLabel();
    asm.J(Condition.Equal, done);
    asm.Nop();
    asm.MarkLabel(done);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0x0F, 0x84, 0x01, 0x00, 0x90 }));
  }

  [Test]
  public void J_GivenBoundTargetAtShortRangeBoundary_WhenAssembled_ThenShortFormStillFits() {
    var asm = new Assembler();
    var top = asm.MarkLabel("top");
    for (var i = 0; i < 126; ++i)
      asm.Nop();

    asm.J(Condition.Equal, top); // rel = -(126 + 2) = -128
    Assert.That(asm.ToArray()[126..], Is.EqualTo(new byte[] { 0x74, 0x80 }));
  }

  [Test]
  public void J_GivenBoundTargetJustOutOfShortRange_WhenAssembled_ThenNearForm() {
    var asm = new Assembler();
    var top = asm.MarkLabel("top");
    for (var i = 0; i < 127; ++i)
      asm.Nop();

    asm.J(Condition.Equal, top); // rel8 would be -129 -> near form, rel16 = -131 = 0xFF7D
    Assert.That(asm.ToArray()[127..], Is.EqualTo(new byte[] { 0x0F, 0x84, 0x7D, 0xFF }));
  }

  [Test]
  public void JShort_GivenForwardTargetInRange_WhenAssembled_ThenPatched() {
    var asm = new Assembler();
    var done = asm.DefineLabel();
    asm.JShort(Condition.NotEqual, done);
    asm.Nop();
    asm.Nop();
    asm.MarkLabel(done);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0x75, 0x02, 0x90, 0x90 }));
  }

  [Test]
  public void JShort_GivenForwardTargetOutOfRange_WhenBuilt_ThenThrows() {
    var asm = new Assembler();
    var done = asm.DefineLabel();
    asm.JShort(Condition.Equal, done);
    for (var i = 0; i < 128; ++i)
      asm.Nop();

    asm.MarkLabel(done);
    Assert.Throws<InvalidOperationException>(() => asm.ToArray());
  }

  [Test]
  public void Je_GivenSugarMethod_WhenAssembled_ThenSameAsGenericForm() {
    var asm = new Assembler();
    var top = asm.MarkLabel("top");
    asm.Je(top);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0x74, 0xFE }));
  }

  [Test]
  public void Jc_GivenAlias_WhenAssembled_ThenSameOpcodeAsJb() {
    var asm = new Assembler();
    var top = asm.MarkLabel("top");
    asm.Jc(top);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0x72, 0xFE }));
  }

  #endregion

  #region JMP

  [Test]
  public void Jmp_GivenBoundTargetInRange_WhenAssembled_ThenShortForm() {
    var asm = new Assembler();
    var top = asm.MarkLabel("top");
    asm.Jmp(top);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0xEB, 0xFE }));
  }

  [Test]
  public void Jmp_GivenForwardTarget_WhenAssembled_ThenNearForm() {
    var asm = new Assembler();
    var done = asm.DefineLabel();
    asm.Jmp(done);
    asm.Nop();
    asm.MarkLabel(done);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0xE9, 0x01, 0x00, 0x90 }));
  }

  [Test]
  public void Jmp_GivenBoundTargetOutOfShortRange_WhenAssembled_ThenNearForm() {
    var asm = new Assembler();
    var top = asm.MarkLabel("top");
    for (var i = 0; i < 127; ++i)
      asm.Nop();

    asm.Jmp(top); // rel16 = -(127 + 3) = -130 = 0xFF7E
    Assert.That(asm.ToArray()[127..], Is.EqualTo(new byte[] { 0xE9, 0x7E, 0xFF }));
  }

  [Test]
  public void JmpShort_GivenForwardTarget_WhenAssembled_ThenShortFormPatched() {
    var asm = new Assembler();
    var done = asm.DefineLabel();
    asm.JmpShort(done);
    asm.Nop();
    asm.MarkLabel(done);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0xEB, 0x01, 0x90 }));
  }

  [Test]
  public void JmpNear_GivenBoundTargetInShortRange_WhenAssembled_ThenNearFormForced() {
    var asm = new Assembler();
    var top = asm.MarkLabel("top");
    asm.JmpNear(top);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0xE9, 0xFD, 0xFF }));
  }

  [Test]
  public void Jmp_GivenRegister_WhenAssembled_ThenIndirectForm()
    => Assert.That(Assemble(a => a.Jmp(Reg.BX)), Is.EqualTo(new byte[] { 0xFF, 0xE3 }));

  [Test]
  public void Jmp_GivenMemory_WhenAssembled_ThenIndirectForm()
    => Assert.That(Assemble(a => a.Jmp(Mem.At(Reg.BX))), Is.EqualTo(new byte[] { 0xFF, 0x27 }));

  [Test]
  public void JmpFar_GivenAbsoluteTarget_WhenAssembled_ThenEaForm()
    => Assert.That(Assemble(a => a.JmpFar(0x1234, 0x5678)), Is.EqualTo(new byte[] { 0xEA, 0x78, 0x56, 0x34, 0x12 }));

  [Test]
  public void JmpFar_GivenLabel_WhenAssembled_ThenSegmentRelocationRecorded() {
    var asm = new Assembler();
    var entry = asm.MarkLabel("entry");
    asm.Nop();
    asm.JmpFar(entry);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0x90, 0xEA, 0x00, 0x00, 0x00, 0x00 }));
    Assert.That(asm.SegmentRelocations, Is.EqualTo(new[] { 4 }));
  }

  [Test]
  public void JmpFar_GivenMemory_WhenAssembled_ThenIndirectFarForm()
    => Assert.That(Assemble(a => a.JmpFar(Mem.At(Reg.BX))), Is.EqualTo(new byte[] { 0xFF, 0x2F }));

  #endregion

  #region CALL / RET

  [Test]
  public void Call_GivenForwardTarget_WhenAssembled_ThenRelative16Patched() {
    var asm = new Assembler();
    var proc = asm.DefineLabel();
    asm.Call(proc);
    asm.Nop();
    asm.MarkLabel(proc);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0xE8, 0x01, 0x00, 0x90 }));
  }

  [Test]
  public void Call_GivenBackwardTarget_WhenAssembled_ThenNegativeRelative() {
    var asm = new Assembler();
    var proc = asm.MarkLabel("proc");
    asm.Call(proc);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0xE8, 0xFD, 0xFF }));
  }

  [Test]
  public void Call_GivenRegister_WhenAssembled_ThenIndirectForm()
    => Assert.That(Assemble(a => a.Call(Reg.BX)), Is.EqualTo(new byte[] { 0xFF, 0xD3 }));

  [Test]
  public void Call_GivenMemory_WhenAssembled_ThenIndirectForm()
    => Assert.That(Assemble(a => a.Call(Mem.At(Reg.BX))), Is.EqualTo(new byte[] { 0xFF, 0x17 }));

  [Test]
  public void CallFar_GivenAbsoluteTarget_WhenAssembled_Then9AForm()
    => Assert.That(Assemble(a => a.CallFar(0x1234, 0x5678)), Is.EqualTo(new byte[] { 0x9A, 0x78, 0x56, 0x34, 0x12 }));

  [Test]
  public void CallFar_GivenLabel_WhenAssembled_ThenSegmentRelocationRecorded() {
    var asm = new Assembler();
    var proc = asm.MarkLabel("proc");
    asm.CallFar(proc);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0x9A, 0x00, 0x00, 0x00, 0x00 }));
    Assert.That(asm.SegmentRelocations, Is.EqualTo(new[] { 3 }));
  }

  [Test]
  public void CallFar_GivenMemory_WhenAssembled_ThenIndirectFarForm()
    => Assert.That(Assemble(a => a.CallFar(Mem.At(Reg.BX))), Is.EqualTo(new byte[] { 0xFF, 0x1F }));

  [Test]
  public void Ret_GivenNoOperand_WhenAssembled_ThenC3()
    => Assert.That(Assemble(a => a.Ret()), Is.EqualTo(new byte[] { 0xC3 }));

  [Test]
  public void Ret_GivenPopCount_WhenAssembled_ThenC2Form()
    => Assert.That(Assemble(a => a.Ret(4)), Is.EqualTo(new byte[] { 0xC2, 0x04, 0x00 }));

  [Test]
  public void Retf_GivenNoOperand_WhenAssembled_ThenCb()
    => Assert.That(Assemble(a => a.Retf()), Is.EqualTo(new byte[] { 0xCB }));

  [Test]
  public void Retf_GivenPopCount_WhenAssembled_ThenCaForm()
    => Assert.That(Assemble(a => a.Retf(8)), Is.EqualTo(new byte[] { 0xCA, 0x08, 0x00 }));

  #endregion

  #region LOOP / JCXZ

  [TestCase("Loop", new byte[] { 0xE2, 0xFE })]
  [TestCase("Loope", new byte[] { 0xE1, 0xFE })]
  [TestCase("Loopne", new byte[] { 0xE0, 0xFE })]
  [TestCase("Jcxz", new byte[] { 0xE3, 0xFE })]
  public void LoopFamily_GivenBackwardTarget_WhenAssembled_ThenRel8Form(string mnemonic, byte[] expected) {
    var asm = new Assembler();
    var top = asm.MarkLabel("top");
    switch (mnemonic) {
      case "Loop": asm.Loop(top); break;
      case "Loope": asm.Loope(top); break;
      case "Loopne": asm.Loopne(top); break;
      case "Jcxz": asm.Jcxz(top); break;
    }

    Assert.That(asm.ToArray(), Is.EqualTo(expected));
  }

  [Test]
  public void Loop_GivenTargetOutOfShortRange_WhenBuilt_ThenThrows() {
    var asm = new Assembler();
    var top = asm.MarkLabel("top");
    for (var i = 0; i < 200; ++i)
      asm.Nop();

    asm.Loop(top);
    Assert.Throws<InvalidOperationException>(() => asm.ToArray());
  }

  #endregion

  #region INT / flags / misc

  [Test]
  public void Int_GivenVector_WhenAssembled_ThenCdForm()
    => Assert.That(Assemble(a => a.Int(0x10)), Is.EqualTo(new byte[] { 0xCD, 0x10 }));

  [Test]
  public void Int3_WhenAssembled_ThenCc() => Assert.That(Assemble(a => a.Int3()), Is.EqualTo(new byte[] { 0xCC }));

  [Test]
  public void Into_WhenAssembled_ThenCe() => Assert.That(Assemble(a => a.Into()), Is.EqualTo(new byte[] { 0xCE }));

  [Test]
  public void Iret_WhenAssembled_ThenCf() => Assert.That(Assemble(a => a.Iret()), Is.EqualTo(new byte[] { 0xCF }));

  private static IEnumerable<TestCaseData> SingleByteCases() {
    yield return new((Action<Assembler>)(a => a.Clc()), (byte)0xF8) { TestName = "Clc" };
    yield return new((Action<Assembler>)(a => a.Stc()), (byte)0xF9) { TestName = "Stc" };
    yield return new((Action<Assembler>)(a => a.Cmc()), (byte)0xF5) { TestName = "Cmc" };
    yield return new((Action<Assembler>)(a => a.Cld()), (byte)0xFC) { TestName = "Cld" };
    yield return new((Action<Assembler>)(a => a.Std()), (byte)0xFD) { TestName = "Std" };
    yield return new((Action<Assembler>)(a => a.Cli()), (byte)0xFA) { TestName = "Cli" };
    yield return new((Action<Assembler>)(a => a.Sti()), (byte)0xFB) { TestName = "Sti" };
    yield return new((Action<Assembler>)(a => a.Lahf()), (byte)0x9F) { TestName = "Lahf" };
    yield return new((Action<Assembler>)(a => a.Sahf()), (byte)0x9E) { TestName = "Sahf" };
    yield return new((Action<Assembler>)(a => a.Nop()), (byte)0x90) { TestName = "Nop" };
    yield return new((Action<Assembler>)(a => a.Hlt()), (byte)0xF4) { TestName = "Hlt" };
    yield return new((Action<Assembler>)(a => a.Xlat()), (byte)0xD7) { TestName = "Xlat" };
  }

  [TestCaseSource(nameof(SingleByteCases))]
  public void SingleByteInstruction_WhenAssembled_ThenExactOpcode(Action<Assembler> emit, byte expected)
    => Assert.That(Assemble(emit), Is.EqualTo(new[] { expected }));

  #endregion

  #region string instructions / IN / OUT

  private static IEnumerable<TestCaseData> StringCases() {
    yield return new((Action<Assembler>)(a => a.Movsb()), new byte[] { 0xA4 }) { TestName = "Movsb" };
    yield return new((Action<Assembler>)(a => a.Movsw()), new byte[] { 0xA5 }) { TestName = "Movsw" };
    yield return new((Action<Assembler>)(a => a.Movsd()), new byte[] { 0x66, 0xA5 }) { TestName = "Movsd" };
    yield return new((Action<Assembler>)(a => a.Cmpsb()), new byte[] { 0xA6 }) { TestName = "Cmpsb" };
    yield return new((Action<Assembler>)(a => a.Cmpsw()), new byte[] { 0xA7 }) { TestName = "Cmpsw" };
    yield return new((Action<Assembler>)(a => a.Cmpsd()), new byte[] { 0x66, 0xA7 }) { TestName = "Cmpsd" };
    yield return new((Action<Assembler>)(a => a.Stosb()), new byte[] { 0xAA }) { TestName = "Stosb" };
    yield return new((Action<Assembler>)(a => a.Stosw()), new byte[] { 0xAB }) { TestName = "Stosw" };
    yield return new((Action<Assembler>)(a => a.Stosd()), new byte[] { 0x66, 0xAB }) { TestName = "Stosd" };
    yield return new((Action<Assembler>)(a => a.Lodsb()), new byte[] { 0xAC }) { TestName = "Lodsb" };
    yield return new((Action<Assembler>)(a => a.Lodsw()), new byte[] { 0xAD }) { TestName = "Lodsw" };
    yield return new((Action<Assembler>)(a => a.Lodsd()), new byte[] { 0x66, 0xAD }) { TestName = "Lodsd" };
    yield return new((Action<Assembler>)(a => a.Scasb()), new byte[] { 0xAE }) { TestName = "Scasb" };
    yield return new((Action<Assembler>)(a => a.Scasw()), new byte[] { 0xAF }) { TestName = "Scasw" };
    yield return new((Action<Assembler>)(a => a.Scasd()), new byte[] { 0x66, 0xAF }) { TestName = "Scasd" };
  }

  [TestCaseSource(nameof(StringCases))]
  public void StringInstruction_WhenAssembled_ThenExactOpcode(Action<Assembler> emit, byte[] expected)
    => Assert.That(Assemble(emit), Is.EqualTo(expected));

  [Test]
  public void Rep_GivenMovsw_WhenAssembled_ThenTaskGoldenBytes()
    => Assert.That(Assemble(a => { a.Rep(); a.Movsw(); }), Is.EqualTo(new byte[] { 0xF3, 0xA5 }));

  [Test]
  public void Repne_GivenScasb_WhenAssembled_ThenF2Prefix()
    => Assert.That(Assemble(a => { a.Repne(); a.Scasb(); }), Is.EqualTo(new byte[] { 0xF2, 0xAE }));

  [Test]
  public void Repe_GivenCmpsb_WhenAssembled_ThenF3Prefix()
    => Assert.That(Assemble(a => { a.Repe(); a.Cmpsb(); }), Is.EqualTo(new byte[] { 0xF3, 0xA6 }));

  [Test]
  public void Seg_GivenEsBeforeLodsb_WhenAssembled_ThenOverridePrefix()
    => Assert.That(Assemble(a => { a.Seg(Reg.ES); a.Lodsb(); }), Is.EqualTo(new byte[] { 0x26, 0xAC }));

  [TestCase(Reg.AL, (byte)0x60, new byte[] { 0xE4, 0x60 })]
  [TestCase(Reg.AX, (byte)0x60, new byte[] { 0xE5, 0x60 })]
  public void In_GivenImmediatePort_WhenAssembled_ThenE4Form(Reg accumulator, byte port, byte[] expected)
    => Assert.That(Assemble(a => a.In(accumulator, port)), Is.EqualTo(expected));

  [TestCase(Reg.AL, new byte[] { 0xEC })]
  [TestCase(Reg.AX, new byte[] { 0xED })]
  [TestCase(Reg.EAX, new byte[] { 0x66, 0xED })]
  public void In_GivenDxPort_WhenAssembled_ThenEcForm(Reg accumulator, byte[] expected)
    => Assert.That(Assemble(a => a.In(accumulator, Reg.DX)), Is.EqualTo(expected));

  [TestCase(Reg.AL, (byte)0x20, new byte[] { 0xE6, 0x20 })]
  [TestCase(Reg.AX, (byte)0x20, new byte[] { 0xE7, 0x20 })]
  public void Out_GivenImmediatePort_WhenAssembled_ThenE6Form(Reg accumulator, byte port, byte[] expected)
    => Assert.That(Assemble(a => a.Out(port, accumulator)), Is.EqualTo(expected));

  [TestCase(Reg.AL, new byte[] { 0xEE })]
  [TestCase(Reg.AX, new byte[] { 0xEF })]
  public void Out_GivenDxPort_WhenAssembled_ThenEeForm(Reg accumulator, byte[] expected)
    => Assert.That(Assemble(a => a.Out(Reg.DX, accumulator)), Is.EqualTo(expected));

  [Test]
  public void In_GivenNonAccumulator_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().In(Reg.BL, 0x60));

  [Test]
  public void In_GivenNonDxPortRegister_WhenAssembled_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().In(Reg.AL, Reg.CX));

  #endregion
}
