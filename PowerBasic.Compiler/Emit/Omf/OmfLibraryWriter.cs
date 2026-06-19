namespace PowerBasic.Compiler.Emit.Omf;

/// <summary>
/// Emits an OMF library (.LIB) - the archive counterpart to <see cref="OmfWriter"/> (docs/LINKER.md).
/// Each unit is written as a self-contained object module (via <see cref="OmfWriter.WriteObject"/>),
/// the members are concatenated each starting on a page boundary, and a hashed symbol dictionary is
/// appended so a linker can do dictionary-driven selective extraction.
///
/// The dictionary is genuinely <b>MS-LINK / Watcom / Borland compatible</b>: it uses the exact OMF
/// library hash (<see cref="OmfLibHash"/>, validated against a real MS C 6.0 SLIBCR.LIB) to place each
/// symbol so a foreign linker's hashed lookup finds it. Layout:
/// <list type="bullet">
///   <item>page 0 holds the <c>0xF0</c> header (record length <c>pageSize-3</c>, dictionary byte offset,
///   dictionary block count).</item>
///   <item>each member's THEADR starts on a <c>pageSize</c> boundary; the dictionary stores its 0-based
///   file page (THEADR offset / <c>pageSize</c>), the value a linker multiplies back to the member.</item>
///   <item>a <c>0xF1</c> trailer follows the members, then the 512-byte dictionary blocks (page-aligned).</item>
///   <item>each block: 37 bucket bytes (a free bucket is 0, else a half-paragraph offset to a
///   count-prefixed name + 16-bit page), then a free-space pointer byte, then the entries. A symbol is
///   placed at the first free bucket along its probe (<c>bucket += bucketd</c> mod 37) within its hash
///   block, mirroring the linker's search so the same probe locates it.</item>
/// </list>
/// </summary>
public static class OmfLibraryWriter {

  private const int DictBlockSize = 512;   // OMF dictionary blocks are fixed 512-byte pages
  private const int Buckets = 37;          // 37 hash buckets per block (the prime LINK uses)
  private const int HeaderBytes = Buckets + 1; // 37 bucket bytes + the free-space pointer byte
  private const int PageSize = 16;         // power-of-two page; page 0 is reserved for the 0xF0 header

  public static byte[] WriteLibrary(IReadOnlyList<PbuFile> units) {
    ArgumentNullException.ThrowIfNull(units);

    var buf = new List<byte>();
    void Pad(int to) { while (buf.Count < to) buf.Add(0); }
    int NextPage() => (buf.Count + PageSize - 1) / PageSize * PageSize;

    // each member object module, the THEADR starting on a page boundary (page 0 = header)
    var entries = new List<(string Symbol, int Page)>();
    Pad(PageSize);
    foreach (var unit in units) {
      Pad(NextPage());
      var page = buf.Count / PageSize;            // 0-based file page of this member's THEADR
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
    var recLen = PageSize - 3;
    bytes[1] = (byte)recLen; bytes[2] = (byte)(recLen >> 8);
    bytes[3] = (byte)dictOffset; bytes[4] = (byte)(dictOffset >> 8);
    bytes[5] = (byte)(dictOffset >> 16); bytes[6] = (byte)(dictOffset >> 24);
    bytes[7] = (byte)blocks.Count; bytes[8] = (byte)(blocks.Count >> 8);
    return bytes;
  }

  /// <summary>
  /// Builds the hashed dictionary. Tries an increasing block count until every symbol places at a free
  /// bucket within its own hash block (the linker's search never crosses into another block, so no
  /// page-full handling is needed and a hashed lookup always lands on the symbol's probe chain).
  /// </summary>
  private static List<byte[]> BuildDictionary(List<(string Symbol, int Page)> entries) {
    // start with enough blocks to keep each well under the 37-bucket / 512-byte limits, then grow
    var start = Math.Max(1, (entries.Count + 19) / 20);
    for (var numBlocks = start; ; ++numBlocks) {
      if (TryBuild(entries, numBlocks) is { } blocks)
        return blocks;
      if (numBlocks > entries.Count + 4)              // safety: ~1 symbol/block must always succeed
        throw new OmfException("could not lay out the OMF library dictionary");
    }
  }

  private static List<byte[]>? TryBuild(List<(string Symbol, int Page)> entries, int numBlocks) {
    var blocks = new byte[numBlocks][];
    var free = new int[numBlocks];                    // next free byte offset within each block
    for (var b = 0; b < numBlocks; ++b) { blocks[b] = new byte[DictBlockSize]; free[b] = HeaderBytes; }

    foreach (var (symbol, page) in entries) {
      var name = System.Text.Encoding.ASCII.GetBytes(symbol);
      if (name.Length > byte.MaxValue) throw new OmfException($"OMF library symbol too long: {symbol}");
      var entryLen = (1 + name.Length + 2 + 1) & ~1;  // count + name + 16-bit page, padded to even

      var (block, _, bucket, bucketd) = OmfLibHash(name, numBlocks);
      var placed = false;
      for (var j = 0; j < Buckets; ++j) {
        if (blocks[block][bucket] == 0) {             // a free bucket along this symbol's probe
          // keep the free pointer below 0xFF*2 so the byte field never collides with a "full" sentinel
          if (free[block] + entryLen > DictBlockSize - 2)
            return null;                              // out of entry room -> grow the dictionary
          var at = free[block];
          blocks[block][at] = (byte)name.Length;
          Array.Copy(name, 0, blocks[block], at + 1, name.Length);
          blocks[block][at + 1 + name.Length] = (byte)page;
          blocks[block][at + 1 + name.Length + 1] = (byte)(page >> 8);
          blocks[block][bucket] = (byte)(at / 2);     // half-paragraph offset to the entry
          free[block] = at + entryLen;
          placed = true;
          break;
        }
        bucket += bucketd;
        if (bucket >= Buckets) bucket -= Buckets;
      }
      if (!placed) return null;                        // this block's 37 buckets are full -> grow
    }

    for (var b = 0; b < numBlocks; ++b)
      blocks[b][Buckets] = (byte)(free[b] / 2);        // free-space pointer (in 2-byte units)
    return [.. blocks];
  }

  /// <summary>
  /// The OMF library dictionary hash (the genuine MS/Intel/Watcom algorithm - Open Watcom's
  /// <c>omflib_hash</c>). Every byte is OR'd with 0x20 (case-folded); the bucket index and block delta
  /// accumulate the name back-to-front, the block index and bucket delta front-to-back; both index and
  /// delta are reduced mod their table size and the deltas forced nonzero. Validated bit-for-bit against
  /// a genuine MS C 6.0 SLIBCR.LIB (every public is located by the matching dictionary search).
  /// </summary>
  internal static (int Block, int BlockDelta, int Bucket, int BucketDelta) OmfLibHash(byte[] name, int numBlocks) {
    var count = name.Length;
    int left = 0, right = count;
    ushort block = (ushort)(count | 0x20), blockd = 0, bucket = 0, bucketd = (ushort)(count | 0x20);
    for (; ; ) {
      var curr = name[--right] | 0x20;
      blockd = (ushort)(curr ^ Rotl(blockd, 2));
      bucket = (ushort)(curr ^ Rotr(bucket, 2));
      if (--count == 0) break;
      curr = name[left++] | 0x20;
      block = (ushort)(curr ^ Rotl(block, 2));
      bucketd = (ushort)(curr ^ Rotr(bucketd, 2));
    }
    var bk = bucket % Buckets;
    var bkd = bucketd % Buckets; if (bkd == 0) bkd = 1;
    var bl = block % numBlocks;
    var bld = blockd % numBlocks; if (bld == 0) bld = 1;
    return (bl, bld, bk, bkd);
  }

  private static ushort Rotl(ushort a, int b) => (ushort)((a << b) | (a >> (16 - b)));
  private static ushort Rotr(ushort a, int b) => (ushort)((a << (16 - b)) | (a >> b));
}
