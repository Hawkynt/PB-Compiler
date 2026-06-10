using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

[TestFixture]
public sealed class AssemblerLabelAndDataTests {

  private static byte[] Assemble(Action<Assembler> emit) {
    var asm = new Assembler();
    emit(asm);
    return asm.ToArray();
  }

  #region labels

  [Test]
  public void Lbl_GivenSameNameTwice_WhenRequested_ThenSameInstanceCaseInsensitive() {
    var asm = new Assembler();
    Assert.That(asm.Lbl("foo"), Is.SameAs(asm.Lbl("FOO")));
  }

  [Test]
  public void DefineLabel_GivenTwoCalls_WhenRequested_ThenDistinctInstances() {
    var asm = new Assembler();
    Assert.That(asm.DefineLabel("x"), Is.Not.SameAs(asm.DefineLabel("x")));
  }

  [Test]
  public void MarkLabel_GivenAlreadyBoundLabel_WhenMarkedAgain_ThenThrows() {
    var asm = new Assembler();
    var label = asm.MarkLabel("here");
    Assert.Throws<InvalidOperationException>(() => asm.MarkLabel(label));
  }

  [Test]
  public void ToArray_GivenUnboundReferencedLabel_WhenBuilt_ThenThrows() {
    var asm = new Assembler();
    asm.Jmp(asm.DefineLabel("nowhere"));
    Assert.Throws<InvalidOperationException>(() => asm.ToArray());
  }

  [Test]
  public void MarkLabel_GivenName_WhenBound_ThenPositionIsCurrent() {
    var asm = new Assembler();
    asm.Nop();
    asm.Nop();
    var label = asm.MarkLabel("here");
    Assert.That(label.Position, Is.EqualTo(2));
  }

  [Test]
  public void Jmp_GivenLabelViaName_WhenReferencedBeforeAndAfterBinding_ThenSameTarget() {
    var asm = new Assembler();
    asm.Jmp(asm.Lbl("loop"));   // forward -> near: E9 00 00
    asm.MarkLabel("loop");
    asm.Jmp(asm.Lbl("loop"));   // backward in range -> short: EB FE
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0xE9, 0x00, 0x00, 0xEB, 0xFE }));
  }

  #endregion

  #region data emission

  [Test]
  public void Db_GivenBytes_WhenBuilt_ThenVerbatim()
    => Assert.That(Assemble(a => a.Db(0x01, 0x02, 0xFF)), Is.EqualTo(new byte[] { 0x01, 0x02, 0xFF }));

  [Test]
  public void Db_GivenAsciiText_WhenBuilt_ThenAsciiBytes()
    => Assert.That(Assemble(a => a.Db("AB$")), Is.EqualTo(new byte[] { 0x41, 0x42, 0x24 }));

  [Test]
  public void Db_GivenNonAsciiText_WhenEmitted_ThenThrows()
    => Assert.Throws<ArgumentException>(() => new Assembler().Db("€"));

  [Test]
  public void Dw_GivenWords_WhenBuilt_ThenLittleEndian()
    => Assert.That(Assemble(a => a.Dw(0x1234, 0xFFFF)), Is.EqualTo(new byte[] { 0x34, 0x12, 0xFF, 0xFF }));

  [Test]
  public void Dd_GivenDword_WhenBuilt_ThenLittleEndian()
    => Assert.That(Assemble(a => a.Dd(0x12345678u)), Is.EqualTo(new byte[] { 0x78, 0x56, 0x34, 0x12 }));

  [Test]
  public void Dd_GivenSingleFloat_WhenBuilt_ThenIeeeBits()
    => Assert.That(Assemble(a => a.Dd(1.0f)), Is.EqualTo(new byte[] { 0x00, 0x00, 0x80, 0x3F }));

  [Test]
  public void Dq_GivenQword_WhenBuilt_ThenLittleEndian()
    => Assert.That(Assemble(a => a.Dq(0x1122334455667788ul)), Is.EqualTo(new byte[] { 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11 }));

  [Test]
  public void Dq_GivenDouble_WhenBuilt_ThenIeeeBits()
    => Assert.That(Assemble(a => a.Dq(1.0)), Is.EqualTo(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F }));

  [Test]
  public void Dw_GivenForwardLabel_WhenBound_ThenOffsetPatched() {
    var asm = new Assembler();
    var data = asm.DefineLabel();
    asm.Dw(data);
    asm.Nop();
    asm.MarkLabel(data);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0x03, 0x00, 0x90 }));
  }

  [Test]
  public void DwSegment_WhenEmitted_ThenRelocationRecorded() {
    var asm = new Assembler();
    asm.Nop();
    asm.DwSegment(0x1234);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0x90, 0x34, 0x12 }));
    Assert.That(asm.SegmentRelocations, Is.EqualTo(new[] { 1 }));
  }

  #endregion

  #region alignment

  [Test]
  public void Align_GivenUnalignedPosition_WhenAligned_ThenPaddedWithFill() {
    var asm = new Assembler();
    asm.Db(0x01, 0x02, 0x03);
    asm.Align(4, 0x90);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0x01, 0x02, 0x03, 0x90 }));
  }

  [Test]
  public void Align_GivenAlignedPosition_WhenAligned_ThenNothingEmitted() {
    var asm = new Assembler();
    asm.Dd(0u);
    asm.Align(4);
    Assert.That(asm.Position, Is.EqualTo(4));
  }

  [Test]
  public void Align_GivenDefaultFill_WhenPadded_ThenZeroBytes() {
    var asm = new Assembler();
    asm.Db(0xFF);
    asm.Align(2);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0xFF, 0x00 }));
  }

  [TestCase(0)]
  [TestCase(-4)]
  [TestCase(3)]
  public void Align_GivenInvalidAlignment_WhenCalled_ThenThrows(int alignment)
    => Assert.Throws<ArgumentOutOfRangeException>(() => new Assembler().Align(alignment));

  #endregion

  #region 80-bit extended reals

  [Test]
  public void Dt_GivenOneDouble_WhenBuilt_ThenExtendedOne()
    => Assert.That(Assemble(a => a.Dt(1.0)), Is.EqualTo(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0xFF, 0x3F }));

  [Test]
  public void Dt_GivenMinusOneDouble_WhenBuilt_ThenSignBitSet()
    => Assert.That(Assemble(a => a.Dt(-1.0)), Is.EqualTo(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0xFF, 0xBF }));

  [Test]
  public void Dt_GivenZeroDouble_WhenBuilt_ThenAllZero()
    => Assert.That(Assemble(a => a.Dt(0.0)), Is.EqualTo(new byte[10]));

  [Test]
  public void Dt_GivenNegativeZeroDouble_WhenBuilt_ThenOnlySignBit()
    => Assert.That(Assemble(a => a.Dt(-0.0)), Is.EqualTo(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0x00, 0x80 }));

  [Test]
  public void Dt_GivenHalfDouble_WhenBuilt_ThenExponentDecremented()
    => Assert.That(Assemble(a => a.Dt(0.5)), Is.EqualTo(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0xFE, 0x3F }));

  [Test]
  public void Dt_GivenTenthDouble_WhenBuilt_ThenExactWideningOfDoubleValue()
    => Assert.That(Assemble(a => a.Dt(0.1)), Is.EqualTo(new byte[] { 0x00, 0xD0, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xFB, 0x3F }));

  [Test]
  public void Dt_GivenPositiveInfinity_WhenBuilt_ThenMaxExponentAndIntegerBit()
    => Assert.That(Assemble(a => a.Dt(double.PositiveInfinity)), Is.EqualTo(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0x80, 0xFF, 0x7F }));

  [Test]
  public void Dt_GivenSmallestSubnormalDouble_WhenBuilt_ThenNormalizedExtended()
    => Assert.That(Assemble(a => a.Dt(double.Epsilon)), Is.EqualTo(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0x80, 0xCD, 0x3B }));

  [Test]
  public void Dt_GivenOneDecimal_WhenBuilt_ThenSameAsDouble()
    => Assert.That(Assemble(a => a.Dt(1m)), Is.EqualTo(Assemble(a => a.Dt(1.0))));

  [Test]
  public void Dt_GivenZeroDecimal_WhenBuilt_ThenAllZero()
    => Assert.That(Assemble(a => a.Dt(0m)), Is.EqualTo(new byte[10]));

  [Test]
  public void Dt_GivenTenthDecimal_WhenBuilt_ThenCorrectlyRoundedExtended()
    => Assert.That(Assemble(a => a.Dt(0.1m)), Is.EqualTo(new byte[] { 0xCD, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xFB, 0x3F }));

  [Test]
  public void Dt_GivenMinusTwoPointFiveDecimal_WhenBuilt_ThenExactExtended()
    => Assert.That(Assemble(a => a.Dt(-2.5m)), Is.EqualTo(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xA0, 0x00, 0xC0 }));

  [Test]
  public void Dt_GivenLargeDecimal_WhenBuilt_ThenMatchesDoubleWidening()
    // 2^60 is exactly representable in both decimal and double
    => Assert.That(Assemble(a => a.Dt(1152921504606846976m)), Is.EqualTo(Assemble(a => a.Dt(1152921504606846976.0))));

  [Test]
  public void Dt_GivenDecimalWithManyDigits_WhenBuilt_ThenRoundsToNearestEven()
    // 1/3 to 28 digits: mantissa 1010...10 rounds up to 0xAAAAAAAAAAAAAAAB at 2^-2
    => Assert.That(Assemble(a => a.Dt(0.3333333333333333333333333333m)),
      Is.EqualTo(new byte[] { 0xAB, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xFD, 0x3F }));

  #endregion

  #region truncate (rollback support)

  [Test]
  public void Position_GivenEmissions_WhenQueried_ThenTracksByteCount() {
    var asm = new Assembler();
    Assert.That(asm.Position, Is.EqualTo(0));
    asm.Mov(Reg.AX, 1);
    Assert.That(asm.Position, Is.EqualTo(3));
  }

  #endregion
}
