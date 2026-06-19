namespace PowerBasic.Compiler.Emit.Omf;

/// <summary>
/// Emits an OMF library (.LIB) - the archive counterpart to <see cref="OmfWriter"/> (docs/LINKER.md).
/// Each unit is written as a self-contained object module (via <see cref="OmfWriter.WriteObject"/>),
/// the members are concatenated each starting on a page boundary, and a hashed symbol dictionary is
/// appended so a linker can do dictionary-driven selective extraction. The layout mirrors what
/// <see cref="OmfReader.ReadLibrary(byte[], out System.Collections.Generic.IReadOnlyDictionary{string, int})"/>
/// expects:
/// <list type="bullet">
///   <item>page 0 holds the <c>0xF0</c> library header (record length <c>pageSize-3</c>, dictionary
///   byte offset, and dictionary block count).</item>
///   <item>each member's THEADR starts on a <c>pageSize</c> boundary; padding between members is zero.</item>
///   <item>a <c>0xF1</c> trailer follows the last member, then the dictionary - both page-aligned.</item>
///   <item>the dictionary is one or more 512-byte blocks: 37 bucket bytes (each a half-paragraph offset
///   to an entry) followed by Pascal-counted name + 1-based THEADR page number entries.</item>
/// </list>
/// Our reader / <see cref="OmfLibrary"/> round-trip plus selective extraction is the acceptance bar;
/// genuine MS-LINK dictionary-hash compatibility (its bucket hash differs) is future work.
/// </summary>
public static class OmfLibraryWriter {

  private const int DictBlockSize = 512;   // OMF dictionary blocks are fixed 512-byte pages
  private const int Buckets = 37;          // 37 hash buckets per block (the prime LINK uses)

  public static byte[] WriteLibrary(IReadOnlyList<PbuFile> units) {
    ArgumentNullException.ThrowIfNull(units);
    const int pageSize = 16;               // power-of-two page; page 0 is reserved for the 0xF0 header

    var buf = new List<byte>();
    void Pad(int to) { while (buf.Count < to) buf.Add(0); }
    int NextPage() => (buf.Count + pageSize - 1) / pageSize * pageSize;

    // each member object module, the THEADR starting on a page boundary (page 0 = header)
    var entries = new List<(string Symbol, int Page)>();
    Pad(pageSize);
    foreach (var unit in units) {
      Pad(NextPage());
      var page = buf.Count / pageSize + 1;        // 1-based file page of this member's THEADR
      buf.AddRange(OmfWriter.WriteObject(unit));
      foreach (var export in unit.Exports)
        entries.Add((export.Name, page));
    }

    // 0xF1 trailer, then page-align so the dictionary blocks start on a page boundary
    Pad(NextPage());
    buf.Add(0xF1); buf.Add(0); buf.Add(0); buf.Add(0); // trivial trailer (len 0 -> just the checksum byte)
    Pad(NextPage());

    var dictOffset = buf.Count;
    var blocks = BuildDictionary(entries);
    foreach (var block in blocks) buf.AddRange(block);

    var bytes = buf.ToArray();
    bytes[0] = 0xF0;
    var recLen = pageSize - 3;
    bytes[1] = (byte)recLen; bytes[2] = (byte)(recLen >> 8);
    bytes[3] = (byte)dictOffset; bytes[4] = (byte)(dictOffset >> 8);
    bytes[5] = (byte)(dictOffset >> 16); bytes[6] = (byte)(dictOffset >> 24);
    bytes[7] = (byte)blocks.Count; bytes[8] = (byte)(blocks.Count >> 8);
    return bytes;
  }

  /// <summary>
  /// Packs the symbol entries into 512-byte dictionary blocks. Each entry is a Pascal-counted name
  /// plus a 1-based 16-bit page number, written into the free area after the 37 bucket bytes and
  /// pointed at by a free bucket (its half-paragraph offset). When a block runs out of buckets or
  /// room, the entry spills into the next block, so any symbol count fits.
  /// </summary>
  private static List<byte[]> BuildDictionary(List<(string Symbol, int Page)> entries) {
    var blocks = new List<byte[]> { new byte[DictBlockSize] };
    var cursor = Buckets + 1;          // first even slot after the bucket bytes (entries are 2-byte aligned)
    var bucket = 0;                    // next free bucket in the current block

    foreach (var (symbol, page) in entries) {
      var name = System.Text.Encoding.ASCII.GetBytes(symbol);
      if (name.Length > byte.MaxValue) throw new OmfException($"OMF library symbol too long: {symbol}");
      var entryLen = 1 + name.Length + 2; // count byte + name + 16-bit page

      // spill to a fresh block when this block is out of buckets or out of entry room
      if (bucket >= Buckets || cursor + entryLen > DictBlockSize) {
        blocks.Add(new byte[DictBlockSize]);
        cursor = Buckets + 1;
        bucket = 0;
      }

      var block = blocks[^1];
      var entry = cursor;
      block[entry] = (byte)name.Length;
      Array.Copy(name, 0, block, entry + 1, name.Length);
      block[entry + 1 + name.Length] = (byte)page;
      block[entry + 1 + name.Length + 1] = (byte)(page >> 8);
      block[bucket] = (byte)(entry / 2); // bucket -> half-paragraph (2-byte) pointer to the entry
      ++bucket;
      cursor = (entry + entryLen + 1) & ~1; // next even slot
    }
    return blocks;
  }
}
