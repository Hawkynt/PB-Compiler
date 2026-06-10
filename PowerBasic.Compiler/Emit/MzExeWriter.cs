namespace PowerBasic.Compiler.Emit;

/// <summary>A single MZ relocation: the load-time segment:offset of a word the DOS loader adjusts by the start segment.</summary>
public readonly record struct MzRelocation(ushort Segment, ushort Offset);

/// <summary>
/// Writes a standard DOS MZ executable: header, relocation table (padded to a
/// paragraph boundary) and the raw code/data image. The checksum field is
/// left zero, which DOS ignores.
/// </summary>
public sealed class MzExeWriter(byte[] image) {

  private const int _HEADER_FIXED_SIZE = 0x1C;
  private const int _PAGE_SIZE = 512;
  private const int _PARAGRAPH_SIZE = 16;

  private readonly byte[] _image = image ?? throw new ArgumentNullException(nameof(image));
  private readonly List<MzRelocation> _relocations = [];

  /// <summary>Initial CS, relative to the load segment.</summary>
  public ushort EntrySegment { get; set; }

  /// <summary>Initial IP.</summary>
  public ushort EntryOffset { get; set; }

  /// <summary>Initial SS, relative to the load segment.</summary>
  public ushort StackSegment { get; set; }

  /// <summary>Initial SP.</summary>
  public ushort StackPointer { get; set; }

  /// <summary>Paragraphs required beyond the image (BSS, stack, ...).</summary>
  public ushort MinExtraParagraphs { get; set; }

  /// <summary>Maximum paragraphs DOS may allocate; defaults to "all available".</summary>
  public ushort MaxExtraParagraphs { get; set; } = 0xFFFF;

  public IReadOnlyList<MzRelocation> Relocations => this._relocations;

  /// <summary>Registers a word at load-time <paramref name="segment"/>:<paramref name="offset"/> for loader patching.</summary>
  public void AddRelocation(ushort segment, ushort offset) => this._relocations.Add(new(segment, offset));

  /// <summary>Registers relocations for flat image offsets (segment 0).</summary>
  public void AddRelocations(IEnumerable<int> imageOffsets) {
    ArgumentNullException.ThrowIfNull(imageOffsets);
    foreach (var offset in imageOffsets) {
      if (offset is < 0 or > ushort.MaxValue)
        throw new ArgumentOutOfRangeException(nameof(imageOffsets), offset, "Relocation offsets must fit in 16 bits.");

      this.AddRelocation(0, (ushort)offset);
    }
  }

  /// <summary>
  /// Places the stack directly behind the image: SS:SP point at
  /// <paramref name="stackBytes"/> bytes past the image end, and
  /// <see cref="MinExtraParagraphs"/> is raised to cover them.
  /// </summary>
  public void SetStackAfterImage(int stackBytes) {
    if (stackBytes is < 2 or > ushort.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(stackBytes), stackBytes, "Stack size must be 2..65535 bytes.");

    var stackBase = (this._image.Length + _PARAGRAPH_SIZE - 1) / _PARAGRAPH_SIZE;
    this.StackSegment = (ushort)stackBase;
    this.StackPointer = (ushort)((stackBytes + 1) & ~1);

    var bytesBeyondImage = stackBase * _PARAGRAPH_SIZE + this.StackPointer - this._image.Length;
    this.MinExtraParagraphs = (ushort)((bytesBeyondImage + _PARAGRAPH_SIZE - 1) / _PARAGRAPH_SIZE);
  }

  /// <summary>Builds the complete EXE file.</summary>
  public byte[] ToArray() {
    var headerSize = HeaderSize(this._relocations.Count);
    var fileSize = headerSize + this._image.Length;
    var result = new byte[fileSize];

    var lastPageBytes = fileSize % _PAGE_SIZE;
    var pages = (fileSize + _PAGE_SIZE - 1) / _PAGE_SIZE;

    WriteWord(result, 0x00, 0x5A4D);                          // "MZ"
    WriteWord(result, 0x02, (ushort)lastPageBytes);           // bytes in last page (0 = full)
    WriteWord(result, 0x04, (ushort)pages);                   // pages in file
    WriteWord(result, 0x06, (ushort)this._relocations.Count); // relocation count
    WriteWord(result, 0x08, (ushort)(headerSize / _PARAGRAPH_SIZE)); // header paragraphs
    WriteWord(result, 0x0A, this.MinExtraParagraphs);
    WriteWord(result, 0x0C, this.MaxExtraParagraphs);
    WriteWord(result, 0x0E, this.StackSegment);
    WriteWord(result, 0x10, this.StackPointer);
    WriteWord(result, 0x12, 0);                               // checksum (unused)
    WriteWord(result, 0x14, this.EntryOffset);
    WriteWord(result, 0x16, this.EntrySegment);
    WriteWord(result, 0x18, _HEADER_FIXED_SIZE);              // relocation table offset
    WriteWord(result, 0x1A, 0);                               // overlay number

    var position = _HEADER_FIXED_SIZE;
    foreach (var (segment, offset) in this._relocations) {
      WriteWord(result, position, offset);
      WriteWord(result, position + 2, segment);
      position += 4;
    }

    this._image.CopyTo(result, headerSize);
    return result;
  }

  /// <summary>Writes the complete EXE file to <paramref name="target"/>.</summary>
  public void WriteTo(Stream target) {
    ArgumentNullException.ThrowIfNull(target);
    var bytes = this.ToArray();
    target.Write(bytes, 0, bytes.Length);
  }

  private static int HeaderSize(int relocationCount) {
    var raw = _HEADER_FIXED_SIZE + 4 * relocationCount;
    return (raw + _PARAGRAPH_SIZE - 1) / _PARAGRAPH_SIZE * _PARAGRAPH_SIZE;
  }

  private static void WriteWord(byte[] buffer, int offset, ushort value) {
    buffer[offset] = (byte)value;
    buffer[offset + 1] = (byte)(value >> 8);
  }
}
