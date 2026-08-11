using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

[TestFixture]
public sealed class TextAssemblerTests {

  private sealed class TestResolver : IAsmSymbolResolver {

    private readonly Dictionary<string, AsmSymbol> _symbols = new(StringComparer.OrdinalIgnoreCase);

    public TestResolver With(string name, AsmSymbol symbol) {
      this._symbols[name] = symbol;
      return this;
    }

    public bool TryResolve(string name, out AsmSymbol symbol) => this._symbols.TryGetValue(name, out symbol);
  }

  private static byte[] AssembleLine(string line, IAsmSymbolResolver? resolver = null) {
    var asm = new Assembler();
    var text = new TextAssembler(asm);
    Assert.That(text.TryParse(line, resolver, out var error), Is.True, error);
    return asm.ToArray();
  }

  private static string? FailLine(string line, IAsmSymbolResolver? resolver = null) {
    var asm = new Assembler();
    var text = new TextAssembler(asm);
    Assert.That(text.TryParse(line, resolver, out var error), Is.False);
    Assert.That(asm.Position, Is.EqualTo(0), "failed parse must not emit bytes");
    return error;
  }

  #region task golden lines

  [Test]
  public void TryParse_GivenMovWithHexImmediate_WhenParsed_ThenB8Form()
    => Assert.That(AssembleLine("MOV AX,&H4F05"), Is.EqualTo(new byte[] { 0xB8, 0x05, 0x4F }));

  [Test]
  public void TryParse_GivenIntWithHexVector_WhenParsed_ThenCdForm()
    => Assert.That(AssembleLine("INT &H10"), Is.EqualTo(new byte[] { 0xCD, 0x10 }));

  [Test]
  public void TryParse_GivenSegmentOverrideStoreWithComment_WhenParsed_ThenPrefixedStore()
    => Assert.That(AssembleLine("MOV ES:[BX], AL ; comment"), Is.EqualTo(new byte[] { 0x26, 0x88, 0x07 }));

  #endregion

  #region literals and casing

  [Test]
  public void TryParse_GivenLowercaseLine_WhenParsed_ThenCaseInsensitive()
    => Assert.That(AssembleLine("mov ax, bx"), Is.EqualTo(new byte[] { 0x89, 0xD8 }));

  [Test]
  public void TryParse_GivenOctalLiteral_WhenParsed_ThenValueDecoded()
    => Assert.That(AssembleLine("MOV AL, &O17"), Is.EqualTo(new byte[] { 0xB0, 0x0F }));

  [Test]
  public void TryParse_GivenBinaryLiteral_WhenParsed_ThenValueDecoded()
    => Assert.That(AssembleLine("MOV AL, &B1010"), Is.EqualTo(new byte[] { 0xB0, 0x0A }));

  [Test]
  public void TryParse_GivenDecimalLiteral_WhenParsed_ThenValueDecoded()
    => Assert.That(AssembleLine("MOV AX, 1000"), Is.EqualTo(new byte[] { 0xB8, 0xE8, 0x03 }));

  [Test]
  public void TryParse_GivenNegativeImmediate_WhenParsed_ThenTwosComplement()
    => Assert.That(AssembleLine("MOV AX, -1"), Is.EqualTo(new byte[] { 0xB8, 0xFF, 0xFF }));

  [Test]
  public void TryParse_GivenInvalidNumberPrefix_WhenParsed_ThenFails()
    => Assert.That(FailLine("MOV AX, &X12"), Is.Not.Null);

  #endregion

  #region memory operands

  [Test]
  public void TryParse_GivenBaseIndexDisplacement_WhenParsed_ThenModRmEncoded()
    => Assert.That(AssembleLine("MOV AX, [BX+SI+6]"), Is.EqualTo(new byte[] { 0x8B, 0x40, 0x06 }));

  [Test]
  public void TryParse_GivenNegativeDisplacement_WhenParsed_ThenDisp8()
    => Assert.That(AssembleLine("MOV AX, [BX-2]"), Is.EqualTo(new byte[] { 0x8B, 0x47, 0xFE }));

  [Test]
  public void TryParse_GivenIndexBeforeBase_WhenParsed_ThenNormalized()
    => Assert.That(AssembleLine("MOV AX, [SI+BX]"), Is.EqualTo(new byte[] { 0x8B, 0x00 }));

  [Test]
  public void TryParse_GivenDirectAddress_WhenParsed_ThenDisp16Form()
    => Assert.That(AssembleLine("MOV AX, [&H1234]"), Is.EqualTo(new byte[] { 0x8B, 0x06, 0x34, 0x12 }));

  [Test]
  public void TryParse_GivenBytePtrStore_WhenParsed_ThenC6Form()
    => Assert.That(AssembleLine("MOV BYTE PTR [BX], 1"), Is.EqualTo(new byte[] { 0xC6, 0x07, 0x01 }));

  [Test]
  public void TryParse_GivenWordPtrStore_WhenParsed_ThenC7Form()
    => Assert.That(AssembleLine("MOV WORD PTR [BX], &H1234"), Is.EqualTo(new byte[] { 0xC7, 0x07, 0x34, 0x12 }));

  [Test]
  public void TryParse_GivenSizeWithoutPtr_WhenParsed_ThenAccepted()
    => Assert.That(AssembleLine("MOV WORD [BX], 2"), Is.EqualTo(new byte[] { 0xC7, 0x07, 0x02, 0x00 }));

  [Test]
  public void TryParse_GivenMemorySizeFromRegisterPartner_WhenParsed_ThenInferred()
    => Assert.That(AssembleLine("ADD [BX], AL"), Is.EqualTo(new byte[] { 0x00, 0x07 }));

  [Test]
  public void TryParse_GivenUnsizedMemoryImmediate_WhenParsed_ThenFails()
    => Assert.That(FailLine("MOV [BX], 1"), Is.Not.Null);

  [Test]
  public void TryParse_GivenTwoBaseRegisters_WhenParsed_ThenFails()
    => Assert.That(FailLine("MOV AX, [BX+BP]"), Is.Not.Null);

  [Test]
  public void TryParse_GivenEmptyBrackets_WhenParsed_ThenFails()
    => Assert.That(FailLine("MOV AX, []"), Is.Not.Null);

  [Test]
  public void TryParse_GivenTrailingPlusInBrackets_WhenParsed_ThenFails()
    => Assert.That(FailLine("MOV AX, [BX+]"), Is.Not.Null);

  #endregion

  #region general instructions

  [Test]
  public void TryParse_GivenRepPrefix_WhenParsed_ThenPrefixedString()
    => Assert.That(AssembleLine("REP MOVSW"), Is.EqualTo(new byte[] { 0xF3, 0xA5 }));

  [Test]
  public void TryParse_GivenRepnePrefix_WhenParsed_ThenF2Prefix()
    => Assert.That(AssembleLine("REPNE SCASB"), Is.EqualTo(new byte[] { 0xF2, 0xAE }));

  [Test]
  public void TryParse_GivenRepOnNonStringInstruction_WhenParsed_ThenFails()
    => Assert.That(FailLine("REP NOP"), Is.Not.Null);

  [Test]
  public void TryParse_GivenAluImmediate_WhenParsed_ThenSignExtendedForm()
    => Assert.That(AssembleLine("ADD AX, 5"), Is.EqualTo(new byte[] { 0x83, 0xC0, 0x05 }));

  [Test]
  public void TryParse_GivenShiftByCl_WhenParsed_ThenD3Form()
    => Assert.That(AssembleLine("SHL AX, CL"), Is.EqualTo(new byte[] { 0xD3, 0xE0 }));

  [Test]
  public void TryParse_GivenShiftByImmediate_WhenParsed_ThenC1Form()
    => Assert.That(AssembleLine("SHR BX, 3"), Is.EqualTo(new byte[] { 0xC1, 0xEB, 0x03 }));

  [Test]
  public void TryParse_GivenPushSegment_WhenParsed_ThenDedicatedOpcode()
    => Assert.That(AssembleLine("PUSH ES"), Is.EqualTo(new byte[] { 0x06 }));

  [Test]
  public void TryParse_GivenPushImmediate_WhenParsed_Then6AForm()
    => Assert.That(AssembleLine("PUSH 5"), Is.EqualTo(new byte[] { 0x6A, 0x05 }));

  [Test]
  public void TryParse_GivenXchg_WhenParsed_ThenShortForm()
    => Assert.That(AssembleLine("XCHG AX, BX"), Is.EqualTo(new byte[] { 0x93 }));

  [Test]
  public void TryParse_GivenLea_WhenParsed_Then8DForm()
    => Assert.That(AssembleLine("LEA AX, [BX+SI+4]"), Is.EqualTo(new byte[] { 0x8D, 0x40, 0x04 }));

  [Test]
  public void TryParse_GivenMovzx_WhenParsed_Then0FB6Form()
    => Assert.That(AssembleLine("MOVZX AX, BL"), Is.EqualTo(new byte[] { 0x0F, 0xB6, 0xC3 }));

  [Test]
  public void TryParse_GivenImulThreeOperands_WhenParsed_Then6BForm()
    => Assert.That(AssembleLine("IMUL AX, BX, 5"), Is.EqualTo(new byte[] { 0x6B, 0xC3, 0x05 }));

  [Test]
  public void TryParse_GivenRetWithImmediate_WhenParsed_ThenC2Form()
    => Assert.That(AssembleLine("RET 4"), Is.EqualTo(new byte[] { 0xC2, 0x04, 0x00 }));

  [Test]
  public void TryParse_GivenRetfWithoutOperand_WhenParsed_ThenCb()
    => Assert.That(AssembleLine("RETF"), Is.EqualTo(new byte[] { 0xCB }));

  [Test]
  public void TryParse_GivenOutImmediatePort_WhenParsed_ThenE6Form()
    => Assert.That(AssembleLine("OUT &H20, AL"), Is.EqualTo(new byte[] { 0xE6, 0x20 }));

  [Test]
  public void TryParse_GivenInFromDx_WhenParsed_ThenEcForm()
    => Assert.That(AssembleLine("IN AL, DX"), Is.EqualTo(new byte[] { 0xEC }));

  [Test]
  public void TryParse_GivenIndirectJmpThroughRegister_WhenParsed_ThenFfForm()
    => Assert.That(AssembleLine("JMP BX"), Is.EqualTo(new byte[] { 0xFF, 0xE3 }));

  [Test]
  public void TryParse_GivenNoOperandInstructionWithOperand_WhenParsed_ThenFails()
    => Assert.That(FailLine("NOP AX"), Is.Not.Null);

  #endregion

  #region FPU instructions

  [Test]
  public void TryParse_GivenFldDwordPtr_WhenParsed_ThenD9Form()
    => Assert.That(AssembleLine("FLD DWORD PTR [BX]"), Is.EqualTo(new byte[] { 0xD9, 0x07 }));

  [Test]
  public void TryParse_GivenFldQword_WhenParsed_ThenDdForm()
    => Assert.That(AssembleLine("FLD QWORD [BX]"), Is.EqualTo(new byte[] { 0xDD, 0x07 }));

  [Test]
  public void TryParse_GivenFldStackRegister_WhenParsed_ThenD9C0Form()
    => Assert.That(AssembleLine("FLD ST(2)"), Is.EqualTo(new byte[] { 0xD9, 0xC2 }));

  [Test]
  public void TryParse_GivenFaddStToSt1_WhenParsed_ThenD8Form()
    => Assert.That(AssembleLine("FADD ST, ST(1)"), Is.EqualTo(new byte[] { 0xD8, 0xC1 }));

  [Test]
  public void TryParse_GivenFaddpWithExplicitOperands_WhenParsed_ThenDeForm()
    => Assert.That(AssembleLine("FADDP ST(1), ST"), Is.EqualTo(new byte[] { 0xDE, 0xC1 }));

  [Test]
  public void TryParse_GivenFsubrToSt2_WhenParsed_ThenDcSlotSwapped()
    => Assert.That(AssembleLine("FSUBR ST(2), ST"), Is.EqualTo(new byte[] { 0xDC, 0xE2 }));

  [Test]
  public void TryParse_GivenFistpWordPtr_WhenParsed_ThenDfForm()
    => Assert.That(AssembleLine("FISTP WORD PTR [BX]"), Is.EqualTo(new byte[] { 0xDF, 0x1F }));

  [Test]
  public void TryParse_GivenFstswAx_WhenParsed_ThenWaitPrefixedForm()
    => Assert.That(AssembleLine("FSTSW AX"), Is.EqualTo(new byte[] { 0x9B, 0xDF, 0xE0 }));

  [Test]
  public void TryParse_GivenFldTbytePtr_WhenParsed_ThenDbForm()
    => Assert.That(AssembleLine("FLD TBYTE PTR [BX]"), Is.EqualTo(new byte[] { 0xDB, 0x2F }));

  [Test]
  public void TryParse_GivenFsqrt_WhenParsed_ThenD9Fa()
    => Assert.That(AssembleLine("FSQRT"), Is.EqualTo(new byte[] { 0xD9, 0xFA }));

  [Test]
  public void TryParse_GivenFxchWithoutOperand_WhenParsed_ThenSt1Form()
    => Assert.That(AssembleLine("FXCH"), Is.EqualTo(new byte[] { 0xD9, 0xC9 }));

  [Test]
  public void TryParse_GivenStIndexOutOfRange_WhenParsed_ThenFails()
    => Assert.That(FailLine("FLD ST(8)"), Is.Not.Null);

  #endregion

  #region symbol resolution

  [Test]
  public void TryParse_GivenVariableSymbol_WhenResolvedToMemory_ThenBpRelativeLoad() {
    var resolver = new TestResolver().With("foo", AsmSymbol.OfMemory(Mem.Word(Reg.BP, -4)));
    Assert.That(AssembleLine("MOV AX, foo", resolver), Is.EqualTo(new byte[] { 0x8B, 0x46, 0xFC }));
  }

  [Test]
  public void TryParse_GivenConstantSymbol_WhenResolved_ThenImmediate() {
    var resolver = new TestResolver().With("count", AsmSymbol.Constant(7));
    Assert.That(AssembleLine("MOV AX, count", resolver), Is.EqualTo(new byte[] { 0xB8, 0x07, 0x00 }));
  }

  [Test]
  public void TryParse_GivenLabelSymbol_WhenJumpTargetBound_ThenShortJump() {
    var asm = new Assembler();
    var top = asm.MarkLabel("top");
    var resolver = new TestResolver().With("top", AsmSymbol.OfLabel(top));
    var text = new TextAssembler(asm);
    Assert.That(text.TryParse("JE top", resolver, out var error), Is.True, error);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0x74, 0xFE }));
  }

  [Test]
  public void TryParse_GivenLabelSymbol_WhenForwardJump_ThenNearFormEmitted() {
    var asm = new Assembler();
    var resolver = new TestResolver().With("done", AsmSymbol.OfLabel(asm.Lbl("done")));
    var text = new TextAssembler(asm);
    Assert.That(text.TryParse("JMP done", resolver, out _), Is.True);
    asm.MarkLabel("done");
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0xE9, 0x00, 0x00 }));
  }

  [Test]
  public void TryParse_GivenJmpShortKeyword_WhenParsed_ThenShortForm() {
    var asm = new Assembler();
    var top = asm.MarkLabel("top");
    var resolver = new TestResolver().With("top", AsmSymbol.OfLabel(top));
    var text = new TextAssembler(asm);
    Assert.That(text.TryParse("JMP SHORT top", resolver, out _), Is.True);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0xEB, 0xFE }));
  }

  [Test]
  public void TryParse_GivenCallToLabel_WhenParsed_ThenRelativeCall() {
    var asm = new Assembler();
    var proc = asm.MarkLabel("proc");
    var resolver = new TestResolver().With("proc", AsmSymbol.OfLabel(proc));
    var text = new TextAssembler(asm);
    Assert.That(text.TryParse("CALL proc", resolver, out _), Is.True);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0xE8, 0xFD, 0xFF }));
  }

  [Test]
  public void TryParse_GivenLoopToLabel_WhenParsed_ThenRel8Form() {
    var asm = new Assembler();
    var top = asm.MarkLabel("top");
    var resolver = new TestResolver().With("top", AsmSymbol.OfLabel(top));
    var text = new TextAssembler(asm);
    Assert.That(text.TryParse("LOOP top", resolver, out _), Is.True);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0xE2, 0xFE }));
  }

  [Test]
  public void TryParse_GivenIndexedMemorySymbol_WhenResolved_ThenCombinedAddressing() {
    var resolver = new TestResolver().With("myArr", AsmSymbol.OfMemory(Mem.At(0x1234)));
    Assert.That(AssembleLine("MOV myArr[SI], AL", resolver), Is.EqualTo(new byte[] { 0x88, 0x84, 0x34, 0x12 }));
  }

  [Test]
  public void TryParse_GivenConstantSymbolInsideBrackets_WhenResolved_ThenDisplacementAdded() {
    var resolver = new TestResolver().With("OFFS", AsmSymbol.Constant(4));
    Assert.That(AssembleLine("MOV AX, [BX+OFFS]", resolver), Is.EqualTo(new byte[] { 0x8B, 0x47, 0x04 }));
  }

  [Test]
  public void TryParse_GivenUnknownSymbol_WhenParsed_ThenFailsWithName() {
    var error = FailLine("MOV AX, nonsense");
    Assert.That(error, Does.Contain("nonsense"));
  }

  [Test]
  public void TryParse_GivenMemorySymbolWithConflictingBase_WhenResolved_ThenFails() {
    var resolver = new TestResolver().With("local", AsmSymbol.OfMemory(Mem.Word(Reg.BP, -2)));
    Assert.That(FailLine("MOV AX, local[BP]", resolver), Is.Not.Null);
  }

  #endregion

  #region error handling

  [Test]
  public void TryParse_GivenUnknownMnemonic_WhenParsed_ThenFails()
    => Assert.That(FailLine("FROBNICATE AX"), Is.Not.Null);

  [Test]
  public void TryParse_GivenEmptyLine_WhenParsed_ThenFails()
    => Assert.That(FailLine(""), Is.Not.Null);

  [Test]
  public void TryParse_GivenCommentOnlyLine_WhenParsed_ThenFails()
    => Assert.That(FailLine("; nothing here"), Is.Not.Null);

  [Test]
  public void TryParse_GivenTrailingComma_WhenParsed_ThenFails()
    => Assert.That(FailLine("MOV AX,"), Is.Not.Null);

  [Test]
  public void TryParse_GivenGarbageCharacters_WhenParsed_ThenFails()
    => Assert.That(FailLine("MOV AX, #5"), Is.Not.Null);

  [Test]
  public void TryParse_GivenExtraTokensAfterInstruction_WhenParsed_ThenFails()
    => Assert.That(FailLine("NOP NOP"), Is.Not.Null);

  [Test]
  public void TryParse_GivenSizeMismatch_WhenParsed_ThenFails()
    => Assert.That(FailLine("MOV AX, BL"), Is.Not.Null);

  [Test]
  public void TryParse_GivenFailure_WhenRepPrefixAlreadyEmitted_ThenRolledBack() {
    var asm = new Assembler();
    asm.Nop();
    var text = new TextAssembler(asm);
    Assert.That(text.TryParse("REP MOVSQ", null, out _), Is.False);
    Assert.That(asm.Position, Is.EqualTo(1), "REP prefix must be rolled back");
  }

  [Test]
  public void TryParse_GivenNullLine_WhenParsed_ThenThrows() {
    var text = new TextAssembler(new());
    Assert.Throws<ArgumentNullException>(() => text.TryParse(null!, null, out _));
  }

  #endregion

  #region MMX intrinsics

  [Test]
  public void TryParse_GivenEmms_ThenEncodes()
    => Assert.That(AssembleLine("EMMS"), Is.EqualTo(new byte[] { 0x0F, 0x77 }));

  [Test]
  public void TryParse_GivenPaddwRegisters_ThenEncodes()
    => Assert.That(AssembleLine("PADDW MM0, MM1"), Is.EqualTo(new byte[] { 0x0F, 0xFD, 0xC1 }));

  [Test]
  public void TryParse_GivenMovqRegisters_ThenEncodes()
    => Assert.That(AssembleLine("MOVQ MM2, MM3"), Is.EqualTo(new byte[] { 0x0F, 0x6F, 0xD3 }));

  [Test]
  public void TryParse_GivenPsllwImmediate_ThenEncodesGroupForm()
    => Assert.That(AssembleLine("PSLLW MM0, 3"), Is.EqualTo(new byte[] { 0x0F, 0x71, 0xF0, 0x03 }));

  [Test]
  public void TryParse_GivenMovdFromMemoryVariable_ThenEncodesLoad() {
    var resolver = new TestResolver().With("total", AsmSymbol.OfMemory(Mem.At(Reg.BX)));
    Assert.That(AssembleLine("MOVD MM0, total", resolver), Is.EqualTo(new byte[] { 0x0F, 0x6E, 0x07 }));
  }

  [Test]
  public void TryParse_GivenMovdStoreToRegister_ThenEncodesStore()
    => Assert.That(AssembleLine("MOVD EAX, MM0"), Is.EqualTo(new byte[] { 0x0F, 0x7E, 0xC0 }));

  [Test]
  public void TryParse_GivenPaddwXmm_ThenEmitsSse2PrefixedForm()
    // same mnemonic, XMM operands -> the 66-prefixed SSE2 encoding is selected by register class
    => Assert.That(AssembleLine("PADDW XMM0, XMM1"), Is.EqualTo(new byte[] { 0x66, 0x0F, 0xFD, 0xC1 }));

  [Test]
  public void TryParse_GivenMovdqaXmm_ThenEncodes()
    => Assert.That(AssembleLine("MOVDQA XMM2, XMM3"), Is.EqualTo(new byte[] { 0x66, 0x0F, 0x6F, 0xD3 }));

  [Test]
  public void TryParse_GivenPslldXmmImmediate_ThenPrefixedGroupForm()
    => Assert.That(AssembleLine("PSLLD XMM0, 5"), Is.EqualTo(new byte[] { 0x66, 0x0F, 0x72, 0xF0, 0x05 }));

  [Test]
  public void TryParse_GivenVpaddwYmm_ThenVexThreeOperandForm()
    // AVX 3-operand: dest = src1 OP src2, VEX-encoded
    => Assert.That(AssembleLine("VPADDW YMM0, YMM1, YMM2"), Is.EqualTo(new byte[] { 0xC5, 0xF5, 0xFD, 0xC2 }));

  [Test]
  public void TryParse_GivenVmovdquYmm_ThenVexLoad()
    => Assert.That(AssembleLine("VMOVDQU YMM0, [BX]"), Is.EqualTo(new byte[] { 0xC5, 0x86, 0x6F, 0x07 }));

  [Test]
  public void TryParse_GivenVpaddwZmm_ThenEvexEncoded()
    // ZMM operands route the same V-mnemonic to the 4-byte EVEX (AVX-512) form
    => Assert.That(AssembleLine("VPADDW ZMM0, ZMM1, ZMM2"), Is.EqualTo(new byte[] { 0x62, 0xF1, 0x75, 0x48, 0xFD, 0xC2 }));

  #endregion

  #region CMOVcc (686+ branchless conditional move)

  [Test]
  public void TryParse_GivenCmovl_ThenEncodes()
    // CMOVL AX, BX: 0F 4C C3 (cc Less=C, modrm reg=AX rm=BX)
    => Assert.That(AssembleLine("CMOVL AX, BX"), Is.EqualTo(new byte[] { 0x0F, 0x4C, 0xC3 }));

  [Test]
  public void TryParse_GivenCmove_ThenEncodes()
    => Assert.That(AssembleLine("CMOVE AX, BX"), Is.EqualTo(new byte[] { 0x0F, 0x44, 0xC3 }));

  [Test]
  public void TryParse_GivenCmovneDword_Then66Prefixed()
    => Assert.That(AssembleLine("CMOVNE EAX, EBX"), Is.EqualTo(new byte[] { 0x66, 0x0F, 0x45, 0xC3 }));

  [Test]
  public void TryParse_GivenCmovgMemory_ThenAddressesMemory()
    // CMOVG AX, [BX]: 0F 4F 07
    => Assert.That(AssembleLine("CMOVG AX, [BX]"), Is.EqualTo(new byte[] { 0x0F, 0x4F, 0x07 }));

  #endregion

  #region the register census

  // Which registers a statement touches - the question the back end's allocator asks before deciding
  // whether a register is its own to hand out (see Backend/InlineAsmReservation).

  private static IEnumerable<string> Used(string line)
    => TextAssembler.RegistersUsed(line).Select(r => r.ToString()).OrderBy(n => n, StringComparer.Ordinal);

  [Test]
  public void RegistersUsed_GivenNamedRegisters_ThenBothOfThem()
    => Assert.That(Used("MOV CX, BX"), Is.EqualTo(new[] { "BX", "CX" }));

  [Test]
  public void RegistersUsed_GivenAByteHalf_ThenTheWholeRegister()
    => Assert.That(Used("XOR AH, AL"), Is.EqualTo(new[] { "AX" }), "AH and AL are both AX");

  [Test]
  public void RegistersUsed_GivenA32BitName_ThenTheWordRegisterItOverlaps()
    => Assert.That(Used("MOV EAX, EBX"), Is.EqualTo(new[] { "AX", "BX" }));

  [Test]
  public void RegistersUsed_GivenAnAddressAndASegmentOverride_ThenTheAddressRegistersOnly()
    // ES is not the integer allocator's to hand out, so it is not part of the answer
    => Assert.That(Used("MOV AL, ES:[BX]"), Is.EqualTo(new[] { "AX", "BX" }));

  [Test]
  public void RegistersUsed_GivenAnOperandName_ThenItIsNotMistakenForARegister()
    => Assert.That(Used("MOV counter, AX"), Is.EqualTo(new[] { "AX" }));

  [Test]
  public void RegistersUsed_GivenACommentNamingARegister_ThenItIsIgnored()
    => Assert.That(Used("MOV AX, 1  ; and then DX"), Is.EqualTo(new[] { "AX" }));

  // the implicit-operand families: the register is architectural and the text never spells it

  [Test]
  public void RegistersUsed_GivenARepeatedStringMove_ThenTheCounterAndBothPointers()
    => Assert.That(Used("REP MOVSB"), Is.EqualTo(new[] { "CX", "DI", "SI" }));

  [Test]
  public void RegistersUsed_GivenLoop_ThenTheCounter()
    => Assert.That(Used("LOOP Again"), Is.EqualTo(new[] { "CX" }));

  [Test]
  public void RegistersUsed_GivenAMultiply_ThenItsAnswerRegistersToo()
    => Assert.That(Used("MUL BX"), Is.EqualTo(new[] { "AX", "BX", "DX" }), "DX:AX is where the product lands");

  [Test]
  public void RegistersUsed_GivenNoRegisterAtAll_ThenNothing()
    => Assert.That(Used("INT &H10"), Is.Empty);

  /// <summary>
  /// A statement nobody can tokenize could touch anything, so it is answered with the whole file. It
  /// will not assemble either - which is what really keeps it out of the back end - but the census
  /// must not report "no registers" for text it failed to read.
  /// </summary>
  [Test]
  public void RegistersUsed_GivenTextThatDoesNotTokenize_ThenEveryGeneralPurposeRegister()
    => Assert.That(TextAssembler.RegistersUsed("MOV AX, #"),
        Is.EquivalentTo(TextAssembler.AllGeneralPurposeRegisters));

  #endregion
}
