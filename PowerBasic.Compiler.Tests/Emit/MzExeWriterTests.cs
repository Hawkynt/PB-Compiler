using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Emit;

namespace PowerBasic.Compiler.Tests.Emit;

[TestFixture]
public sealed class MzExeWriterTests {

  private static ushort ReadWord(byte[] buffer, int offset) => (ushort)(buffer[offset] | buffer[offset + 1] << 8);

  #region golden header

  [Test]
  public void ToArray_GivenTinyImageWithoutRelocations_WhenBuilt_ThenExactFileBytes() {
    // MOV AH,4Ch / INT 21h
    var writer = new MzExeWriter([0xB4, 0x4C, 0xCD, 0x21]);
    var expected = new byte[] {
      0x4D, 0x5A,             // "MZ"
      0x24, 0x00,             // 36 bytes in last page
      0x01, 0x00,             // 1 page
      0x00, 0x00,             // 0 relocations
      0x02, 0x00,             // header is 2 paragraphs (28 -> padded to 32)
      0x00, 0x00,             // min alloc
      0xFF, 0xFF,             // max alloc
      0x00, 0x00,             // SS
      0x00, 0x00,             // SP
      0x00, 0x00,             // checksum
      0x00, 0x00,             // IP
      0x00, 0x00,             // CS
      0x1C, 0x00,             // relocation table offset
      0x00, 0x00,             // overlay number
      0x00, 0x00, 0x00, 0x00, // header padding to paragraph boundary
      0xB4, 0x4C, 0xCD, 0x21, // image
    };
    Assert.That(writer.ToArray(), Is.EqualTo(expected));
  }

  [Test]
  public void ToArray_GivenEntryAndStackSettings_WhenBuilt_ThenHeaderFieldsPlaced() {
    var writer = new MzExeWriter(new byte[16]) {
      EntrySegment = 0x0001,
      EntryOffset = 0x0010,
      StackSegment = 0x0002,
      StackPointer = 0x0100,
      MinExtraParagraphs = 0x0020,
      MaxExtraParagraphs = 0x0040,
    };
    var file = writer.ToArray();
    Assert.Multiple(() => {
      Assert.That(ReadWord(file, 0x0A), Is.EqualTo(0x0020), "min alloc");
      Assert.That(ReadWord(file, 0x0C), Is.EqualTo(0x0040), "max alloc");
      Assert.That(ReadWord(file, 0x0E), Is.EqualTo(0x0002), "SS");
      Assert.That(ReadWord(file, 0x10), Is.EqualTo(0x0100), "SP");
      Assert.That(ReadWord(file, 0x14), Is.EqualTo(0x0010), "IP");
      Assert.That(ReadWord(file, 0x16), Is.EqualTo(0x0001), "CS");
    });
  }

  #endregion

  #region page arithmetic boundaries

  [TestCase(0, 32, 1)]      // header only -> 32 bytes
  [TestCase(479, 511, 1)]   // one byte below a full page
  [TestCase(480, 0, 1)]     // exactly one page: last-page-bytes wraps to 0
  [TestCase(481, 1, 2)]     // one byte into the second page
  [TestCase(992, 0, 2)]     // exactly two pages
  public void ToArray_GivenImageSizeAroundPageBoundary_WhenBuilt_ThenPageFieldsCorrect(int imageSize, int expectedLastPageBytes, int expectedPages) {
    var file = new MzExeWriter(new byte[imageSize]).ToArray();
    Assert.Multiple(() => {
      Assert.That(ReadWord(file, 0x02), Is.EqualTo(expectedLastPageBytes), "bytes in last page");
      Assert.That(ReadWord(file, 0x04), Is.EqualTo(expectedPages), "page count");
      Assert.That(file, Has.Length.EqualTo(32 + imageSize));
    });
  }

  #endregion

  #region relocation table

  [Test]
  public void ToArray_GivenOneRelocation_WhenBuilt_ThenHeaderStays32Bytes() {
    var writer = new MzExeWriter([0x90]);
    writer.AddRelocation(0x0001, 0x0002);
    var file = writer.ToArray();
    Assert.Multiple(() => {
      Assert.That(ReadWord(file, 0x06), Is.EqualTo(1), "relocation count");
      Assert.That(ReadWord(file, 0x08), Is.EqualTo(2), "header paragraphs (28+4 = 32)");
      Assert.That(ReadWord(file, 0x1C), Is.EqualTo(0x0002), "relocation offset");
      Assert.That(ReadWord(file, 0x1E), Is.EqualTo(0x0001), "relocation segment");
      Assert.That(file[32], Is.EqualTo(0x90), "image follows header");
    });
  }

  [Test]
  public void ToArray_GivenTwoRelocations_WhenBuilt_ThenHeaderPaddedToThreeParagraphs() {
    var writer = new MzExeWriter([0x90]);
    writer.AddRelocation(0, 0x0010);
    writer.AddRelocation(0, 0x0020);
    var file = writer.ToArray();
    Assert.Multiple(() => {
      Assert.That(ReadWord(file, 0x06), Is.EqualTo(2));
      Assert.That(ReadWord(file, 0x08), Is.EqualTo(3), "28+8 = 36 -> padded to 48");
      Assert.That(file[48], Is.EqualTo(0x90), "image follows padded header");
      Assert.That(file, Has.Length.EqualTo(49));
    });
  }

  [Test]
  public void ToArray_GivenFiveRelocations_WhenBuilt_ThenHeaderExactlyThreeParagraphs() {
    var writer = new MzExeWriter([]);
    for (var i = 0; i < 5; ++i)
      writer.AddRelocation(0, (ushort)i);

    var file = writer.ToArray();
    Assert.Multiple(() => {
      Assert.That(ReadWord(file, 0x08), Is.EqualTo(3), "28+20 = 48, no padding");
      Assert.That(file, Has.Length.EqualTo(48));
    });
  }

  [Test]
  public void AddRelocations_GivenAssemblerSegmentRelocations_WhenBuilt_ThenAllRecordedAtSegmentZero() {
    var writer = new MzExeWriter(new byte[8]);
    writer.AddRelocations([3, 6]);
    var file = writer.ToArray();
    Assert.Multiple(() => {
      Assert.That(ReadWord(file, 0x06), Is.EqualTo(2));
      Assert.That(ReadWord(file, 0x1C), Is.EqualTo(3));
      Assert.That(ReadWord(file, 0x1E), Is.EqualTo(0));
      Assert.That(ReadWord(file, 0x20), Is.EqualTo(6));
      Assert.That(ReadWord(file, 0x22), Is.EqualTo(0));
    });
  }

  [Test]
  public void AddRelocations_GivenNegativeOffset_WhenAdded_ThenThrows()
    => Assert.Throws<ArgumentOutOfRangeException>(() => new MzExeWriter([]).AddRelocations([-1]));

  #endregion

  #region stack placement

  [Test]
  public void SetStackAfterImage_GivenImageAndStackSize_WhenApplied_ThenStackFieldsComputed() {
    var writer = new MzExeWriter(new byte[100]);
    writer.SetStackAfterImage(256);
    Assert.Multiple(() => {
      Assert.That(writer.StackSegment, Is.EqualTo(7), "first paragraph behind 100-byte image");
      Assert.That(writer.StackPointer, Is.EqualTo(256));
      Assert.That(writer.MinExtraParagraphs, Is.EqualTo(17), "12 slack bytes + 256 stack = 268 -> 17 paragraphs");
    });
  }

  [Test]
  public void SetStackAfterImage_GivenOddStackSize_WhenApplied_ThenStackPointerRoundedToEven() {
    var writer = new MzExeWriter(new byte[16]);
    writer.SetStackAfterImage(255);
    Assert.That(writer.StackPointer, Is.EqualTo(256));
  }

  [Test]
  public void SetStackAfterImage_GivenParagraphAlignedImage_WhenApplied_ThenNoSlack() {
    var writer = new MzExeWriter(new byte[32]);
    writer.SetStackAfterImage(64);
    Assert.Multiple(() => {
      Assert.That(writer.StackSegment, Is.EqualTo(2));
      Assert.That(writer.MinExtraParagraphs, Is.EqualTo(4));
    });
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(0x10000)]
  public void SetStackAfterImage_GivenInvalidStackSize_WhenApplied_ThenThrows(int stackBytes)
    => Assert.Throws<ArgumentOutOfRangeException>(() => new MzExeWriter([]).SetStackAfterImage(stackBytes));

  #endregion

  #region streaming and integration

  [Test]
  public void WriteTo_GivenStream_WhenWritten_ThenSameBytesAsToArray() {
    var writer = new MzExeWriter([0x90, 0xC3]);
    writer.AddRelocation(0, 1);
    using var stream = new MemoryStream();
    writer.WriteTo(stream);
    Assert.That(stream.ToArray(), Is.EqualTo(writer.ToArray()));
  }

  [Test]
  public void ToArray_GivenAssembledProgramWithFarCall_WhenWrapped_ThenRelocationFlowsIntoHeader() {
    var asm = new Assembler();
    var proc = asm.DefineLabel("proc");
    asm.CallFar(proc);            // 9A <ofs> <seg> with relocation at offset 3
    asm.Mov(Reg.AH, 0x4C);
    asm.Int(0x21);
    asm.MarkLabel(proc);
    asm.Retf();

    var writer = new MzExeWriter(asm.ToArray());
    writer.AddRelocations(asm.SegmentRelocations);
    var file = writer.ToArray();
    Assert.Multiple(() => {
      Assert.That(ReadWord(file, 0x06), Is.EqualTo(1), "one relocation");
      Assert.That(ReadWord(file, 0x1C), Is.EqualTo(3), "relocation points at the segment word of CALL FAR");
      Assert.That(file[32], Is.EqualTo(0x9A), "image starts after 32-byte header");
      Assert.That(ReadWord(file, 32 + 1), Is.EqualTo(9), "far call offset patched to label position");
    });
  }

  #endregion
}
