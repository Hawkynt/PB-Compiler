using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

/// <summary>Golden-byte tests for every 16-bit ModRM addressing form (using MOV AX, mem = 8B /r).</summary>
[TestFixture]
public sealed class AssemblerModRmTests {

  private static byte[] Assemble(Action<Assembler> emit) {
    var asm = new Assembler();
    emit(asm);
    return asm.ToArray();
  }

  [Test]
  public void Mov_GivenAccumulatorToDirectAddress_ThenUsesShortForm() {
    // MOV [addr], AX/AL -> A3/A2 moffs (one byte shorter than the 89/88 mod=00 rm=110 form)
    Assert.That(Assemble(a => a.Mov(Mem.At(0x1234), Reg.AX)), Is.EqualTo(new byte[] { 0xA3, 0x34, 0x12 }));
    Assert.That(Assemble(a => a.Mov(Mem.At(0x1234), Reg.AL)), Is.EqualTo(new byte[] { 0xA2, 0x34, 0x12 }));
    // a non-accumulator register (or a based/indexed address) keeps the modrm direct form
    Assert.That(Assemble(a => a.Mov(Mem.At(0x1234), Reg.BX)), Is.EqualTo(new byte[] { 0x89, 0x1E, 0x34, 0x12 }));
  }

  private static IEnumerable<TestCaseData> AddressingModeCases() {
    // mod=00 register forms
    yield return new(Mem.At(Reg.BX, Reg.SI), new byte[] { 0x8B, 0x00 }) { TestName = "BxSi" };
    yield return new(Mem.At(Reg.BX, Reg.DI), new byte[] { 0x8B, 0x01 }) { TestName = "BxDi" };
    yield return new(Mem.At(Reg.BP, Reg.SI), new byte[] { 0x8B, 0x02 }) { TestName = "BpSi" };
    yield return new(Mem.At(Reg.BP, Reg.DI), new byte[] { 0x8B, 0x03 }) { TestName = "BpDi" };
    yield return new(Mem.At(Reg.SI), new byte[] { 0x8B, 0x04 }) { TestName = "Si" };
    yield return new(Mem.At(Reg.DI), new byte[] { 0x8B, 0x05 }) { TestName = "Di" };
    yield return new(Mem.At(Reg.BX), new byte[] { 0x8B, 0x07 }) { TestName = "Bx" };
    // [BP] has no mod=00 encoding -> disp8 of zero
    yield return new(Mem.At(Reg.BP), new byte[] { 0x8B, 0x46, 0x00 }) { TestName = "BpNeedsDisp8" };
    // direct address: mod=00 rm=110
    yield return new(Mem.At(0x1234), new byte[] { 0x8B, 0x06, 0x34, 0x12 }) { TestName = "Direct" };
    yield return new(Mem.At(0), new byte[] { 0x8B, 0x06, 0x00, 0x00 }) { TestName = "DirectZero" };
    // disp8 boundaries
    yield return new(Mem.At(Reg.BX, Reg.SI, 6), new byte[] { 0x8B, 0x40, 0x06 }) { TestName = "Disp8" };
    yield return new(Mem.At(Reg.BX, 127), new byte[] { 0x8B, 0x47, 0x7F }) { TestName = "Disp8Max" };
    yield return new(Mem.At(Reg.BX, -128), new byte[] { 0x8B, 0x47, 0x80 }) { TestName = "Disp8Min" };
    yield return new(Mem.At(Reg.BX, -1), new byte[] { 0x8B, 0x47, 0xFF }) { TestName = "Disp8Negative" };
    // disp16 boundaries
    yield return new(Mem.At(Reg.BX, 128), new byte[] { 0x8B, 0x87, 0x80, 0x00 }) { TestName = "Disp16AboveSByte" };
    yield return new(Mem.At(Reg.BX, -129), new byte[] { 0x8B, 0x87, 0x7F, 0xFF }) { TestName = "Disp16BelowSByte" };
    yield return new(Mem.At(Reg.BP, Reg.DI, 0x1234), new byte[] { 0x8B, 0x83, 0x34, 0x12 }) { TestName = "Disp16BaseIndex" };
    // mod=00 with disp 0 stays short even for BP+index
    yield return new(Mem.At(Reg.BP, Reg.SI, 0), new byte[] { 0x8B, 0x02 }) { TestName = "BpSiNoDisp" };
  }

  [TestCaseSource(nameof(AddressingModeCases))]
  public void Mov_GivenAddressingMode_WhenAssembled_ThenMatchesIntelEncoding(Mem memory, byte[] expected)
    => Assert.That(Assemble(a => a.Mov(Reg.AX, memory)), Is.EqualTo(expected));

  private static IEnumerable<TestCaseData> SegmentOverrideCases() {
    yield return new(Reg.ES, (byte)0x26);
    yield return new(Reg.CS, (byte)0x2E);
    yield return new(Reg.SS, (byte)0x36);
    yield return new(Reg.DS, (byte)0x3E);
    yield return new(Reg.FS, (byte)0x64);
    yield return new(Reg.GS, (byte)0x65);
  }

  [TestCaseSource(nameof(SegmentOverrideCases))]
  public void Mov_GivenSegmentOverride_WhenAssembled_ThenPrefixPrecedesOpcode(Reg segment, byte prefix)
    => Assert.That(Assemble(a => a.Mov(Reg.AX, Mem.At(Reg.BX).Seg(segment))), Is.EqualTo(new byte[] { prefix, 0x8B, 0x07 }));

  [Test]
  public void Mov_GivenEsSegmentViaFluentHelper_WhenAssembled_ThenSamePrefix()
    => Assert.That(Assemble(a => a.Mov(Reg.AX, Mem.At(Reg.BX).Es())), Is.EqualTo(new byte[] { 0x26, 0x8B, 0x07 }));

  [Test]
  public void Mem_GivenInvalidBaseRegister_WhenConstructed_ThenThrows()
    => Assert.Throws<ArgumentException>(() => Mem.At(Reg.AX));

  [Test]
  public void Mem_GivenInvalidIndexRegister_WhenConstructed_ThenThrows()
    => Assert.Throws<ArgumentException>(() => Mem.At(Reg.BX, Reg.BX));

  [Test]
  public void Mem_GivenSiPlusDi_WhenConstructed_ThenThrows()
    => Assert.Throws<ArgumentException>(() => Mem.At(Reg.SI, Reg.DI));

  [Test]
  public void Mem_GivenNonSegmentOverride_WhenApplied_ThenThrows()
    => Assert.Throws<ArgumentException>(() => Mem.At(Reg.BX).Seg(Reg.AX));

  [Test]
  public void Mov_GivenLabelDisplacement_WhenBoundLater_ThenDirectAddressPatched() {
    var asm = new Assembler();
    var data = asm.DefineLabel("data");
    asm.Mov(Reg.AX, Mem.Word(data));
    asm.MarkLabel(data);
    asm.Dw(0xBEEF);
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0x8B, 0x06, 0x04, 0x00, 0xEF, 0xBE }));
  }

  [Test]
  public void Mov_GivenLabelWithBaseRegister_WhenBound_ThenDisp16FormUsed() {
    var asm = new Assembler();
    var data = asm.DefineLabel("data");
    asm.MarkLabel(data);
    asm.Mov(Reg.AX, Mem.Word(Reg.BX, data));
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0x8B, 0x87, 0x00, 0x00 }));
  }

  [Test]
  public void Mov_GivenLabelWithAddend_WhenBound_ThenDisplacementAdded() {
    var asm = new Assembler();
    var data = asm.MarkLabel("data");
    asm.Dw(0x1111, 0x2222);
    asm.Mov(Reg.AX, Mem.Word(data, 2));
    Assert.That(asm.ToArray(), Is.EqualTo(new byte[] { 0x11, 0x11, 0x22, 0x22, 0x8B, 0x06, 0x02, 0x00 }));
  }
}
