namespace PowerBasic.Compiler.Emit.Omf;

/// <summary>Raised when an OMF object/library cannot be parsed.</summary>
public sealed class OmfException(string message) : Exception(message);

/// <summary>
/// Parser for 16-bit Intel OMF object modules (.OBJ) - the format every DOS-era C,
/// asm and BASIC compiler emits. Handles the record set a small/tiny-model object
/// uses: THEADR, LNAMES, SEGDEF, GRPDEF, PUBDEF, EXTDEF, LEDATA/LIDATA and FIXUPP,
/// terminated by MODEND. COMENT and other records are skipped. See docs/LINKER.md.
/// </summary>
public sealed class OmfReader {

  private readonly byte[] _b;
  private int _pos;
  private readonly List<string> _names = ["" /* LNAMES is 1-based */];

  private OmfReader(byte[] bytes) => this._b = bytes;

  /// <summary>Parses a single object module starting at <paramref name="start"/>; returns the byte just past its MODEND (for library iteration).</summary>
  public static OmfModule ReadObject(byte[] bytes, int start = 0) => new OmfReader(bytes) { _pos = start }.Read(out _);

  /// <summary>Parses a single module and reports where it ended (the offset after MODEND).</summary>
  public static OmfModule ReadObject(byte[] bytes, int start, out int end) => new OmfReader(bytes) { _pos = start }.Read(out end);

  /// <summary>
  /// Parses an OMF library (.LIB): a 0xF0 header whose record spans one page, then the
  /// member object modules each padded to a page boundary, ending at the 0xF1 library
  /// trailer. Returns every member module (the linker pulls only the ones it needs).
  /// </summary>
  public static List<OmfModule> ReadLibrary(byte[] bytes) => ReadLibrary(bytes, out _);

  /// <summary>
  /// As <see cref="ReadLibrary(byte[])"/>, but also returns the library's hashed symbol
  /// dictionary lowered to a <c>symbol → member index</c> map (into the returned list).
  /// The dictionary follows the 0xF1 trailer; the 0xF0 header gives its block offset and
  /// count. When the dictionary is absent or cannot be parsed (vendor quirks), the map is
  /// rebuilt from the members' own PUBDEFs so selective extraction still works.
  /// </summary>
  public static List<OmfModule> ReadLibrary(byte[] bytes, out IReadOnlyDictionary<string, int> symbolToMember) {
    if (bytes.Length < 3 || bytes[0] != 0xF0)
      throw new OmfException("not an OMF library (expected a 0xF0 header record)");
    var pageSize = (bytes[1] | (bytes[2] << 8)) + 3;
    if (pageSize <= 0) throw new OmfException("invalid OMF library page size");
    var modules = new List<OmfModule>();
    var pageToMember = new Dictionary<int, int>();          // 0-based file page of THEADR -> member index
    for (var pos = pageSize; pos < bytes.Length;) {
      var t = bytes[pos];
      if (t == 0xF1) break;                 // library trailer -> dictionary follows
      if (t is not (0x80 or 0x82)) throw new OmfException($"unexpected library record 0x{t:X2} at offset {pos}");
      pageToMember[pos / pageSize] = modules.Count;
      modules.Add(ReadObject(bytes, pos, out var end));
      pos = (end + pageSize - 1) / pageSize * pageSize;   // next page boundary
    }
    symbolToMember = BuildSymbolMap(bytes, modules, pageToMember);
    return modules;
  }

  /// <summary>
  /// Lowers the OMF library hash dictionary to a symbol→member map. The dictionary is a
  /// run of 512-byte blocks at the header's dictionary offset; each block holds 37 buckets
  /// (a half-paragraph-scaled pointer into the block) and each occupied bucket is a
  /// Pascal-counted symbol name followed by a 16-bit page number (1-based) of the member
  /// that defines it. We translate those page numbers back to indices in <paramref name="modules"/>.
  /// Any malformation falls back to indexing the members' own publics.
  /// </summary>
  private static IReadOnlyDictionary<string, int> BuildSymbolMap(byte[] bytes, List<OmfModule> modules, Dictionary<int, int> pageToMember) {
    var map = new Dictionary<string, int>(StringComparer.Ordinal);
    if (TryParseDictionary(bytes, pageToMember, map) && map.Count > 0)
      return map;
    // fallback: derive the map directly from each member's PUBDEFs (page-walk result)
    map.Clear();
    for (var i = 0; i < modules.Count; i++)
      foreach (var p in modules[i].Publics)
        map.TryAdd(p.Name, i);
    return map;
  }

  private static bool TryParseDictionary(byte[] bytes, Dictionary<int, int> pageToMember, Dictionary<string, int> map) {
    try {
      // 0xF0 header: [0]=0xF0 [1..2]=reclen [3..6]=dictionary byte offset [7..8]=block count.
      if (bytes.Length < 9 || bytes[0] != 0xF0) return false;
      var dictOffset = bytes[3] | (bytes[4] << 8) | (bytes[5] << 16) | (bytes[6] << 24);
      var blockCount = bytes[7] | (bytes[8] << 8);
      if (blockCount <= 0 || dictOffset <= 0 || dictOffset + blockCount * 512 > bytes.Length) return false;
      for (var blk = 0; blk < blockCount; ++blk) {
        var bbase = dictOffset + blk * 512;
        for (var bucket = 0; bucket < 37; ++bucket) {
          var slot = bytes[bbase + bucket];
          if (slot is 0 or 0xFF) continue;        // empty / deleted bucket
          var entry = bbase + slot * 2;           // bucket value is a half-paragraph offset within the block
          if (entry >= bbase + 512) return false;
          var nameLen = bytes[entry];
          var nameStart = entry + 1;
          if (nameStart + nameLen + 2 > bytes.Length) return false;
          var name = System.Text.Encoding.ASCII.GetString(bytes, nameStart, nameLen);
          var page = bytes[nameStart + nameLen] | (bytes[nameStart + nameLen + 1] << 8); // 1-based page number
          if (pageToMember.TryGetValue(page - 1, out var member))
            map.TryAdd(name, member);
          // a symbol pointing at a page we didn't parse is ignored (kept robust)
        }
      }
      return true;
    } catch {
      return false; // any out-of-range / malformed dictionary -> caller falls back to the page-walk
    }
  }

  private OmfModule Read(out int end) {
    var m = new OmfModule();
    // FIXUPP threads: frame/target method+datum kept by thread number (0..3)
    var frameThread = new (int Method, int Datum)[4];
    var targetThread = new (int Method, int Datum)[4];

    for (;;) {
      if (this._pos + 3 > this._b.Length)
        throw new OmfException("truncated OMF record header");
      var type = this._b[this._pos];
      var len = this._b[this._pos + 1] | (this._b[this._pos + 2] << 8);
      var content = this._pos + 3;
      var recEnd = content + len;          // len includes the trailing checksum byte
      if (recEnd > this._b.Length)
        throw new OmfException($"OMF record 0x{type:X2} overruns the buffer");
      this._pos = content;
      var bodyEnd = recEnd - 1;            // exclude checksum

      switch (type) {
        case 0x80: // THEADR / LHEADR
          m.Name = this.Str();
          break;
        case 0x96: // LNAMES
          while (this._pos < bodyEnd) this._names.Add(this.Str());
          break;
        case 0x98: // SEGDEF (16-bit)
          this.SegDef(m);
          break;
        case 0x90: // PUBDEF (16-bit)
          this.PubDef(m, bodyEnd);
          break;
        case 0x8C: // EXTDEF
          while (this._pos < bodyEnd) { m.Externals.Add(this.Str()); this.Index(); /* type index */ }
          break;
        case 0xA0: // LEDATA (16-bit)
          this.LeData(m, bodyEnd);
          break;
        case 0xA2: // LIDATA (16-bit)
          this.LiData(m, bodyEnd);
          break;
        case 0x9C: // FIXUPP (16-bit)
          this.Fixupp(m, bodyEnd, frameThread, targetThread);
          break;
        case 0x8A or 0x8B: // MODEND
          end = recEnd;
          return m;
        // COMENT(0x88), GRPDEF(0x9A), and everything else: skip the body
      }
      this._pos = recEnd;
    }
  }

  // ---- field readers -------------------------------------------------------

  private byte U8() => this._b[this._pos++];
  private int U16() { var v = this._b[this._pos] | (this._b[this._pos + 1] << 8); this._pos += 2; return v; }

  private string Str() { var n = this._b[this._pos++]; var s = System.Text.Encoding.ASCII.GetString(this._b, this._pos, n); this._pos += n; return s; }

  /// <summary>OMF index: 1 byte if &lt; 0x80, else a 2-byte big-endian-ish 15-bit value.</summary>
  private int Index() {
    var b = this._b[this._pos++];
    return b < 0x80 ? b : ((b & 0x7F) << 8) | this._b[this._pos++];
  }

  // ---- records -------------------------------------------------------------

  private void SegDef(OmfModule m) {
    var acbp = this.U8();
    if ((acbp >> 5) == 0)        // A == 0: absolute segment -> frame(2) + offset(1)
      this._pos += 3;
    var length = this.U16();     // 16-bit segment length (B bit => 64K, ignored here)
    var nameIdx = this.Index();
    var classIdx = this.Index();
    this.Index();                // overlay name index
    m.Segments.Add(new OmfSegment {
      Name = nameIdx < this._names.Count ? this._names[nameIdx] : "",
      ClassName = classIdx < this._names.Count ? this._names[classIdx] : "",
      Length = length,
      Data = new byte[length],
    });
  }

  private void PubDef(OmfModule m, int bodyEnd) {
    var groupIdx = this.Index();
    var segIdx = this.Index();
    if (groupIdx == 0 && segIdx == 0) this._pos += 2; // base frame when both are 0
    while (this._pos < bodyEnd) {
      var name = this.Str();
      var offset = this.U16();
      this.Index();             // type index
      m.Publics.Add(new OmfPublic(name, segIdx, offset));
    }
  }

  private void LeData(OmfModule m, int bodyEnd) {
    var segIdx = this.Index();
    var dataOffset = this.U16();
    var n = bodyEnd - this._pos;
    var seg = this.Seg(m, segIdx);
    EnsureRoom(seg, dataOffset + n);
    Array.Copy(this._b, this._pos, seg.Data, dataOffset, n);
    this._pos += n;
  }

  private void LiData(OmfModule m, int bodyEnd) {
    var segIdx = this.Index();
    var dataOffset = this.U16();
    var seg = this.Seg(m, segIdx);
    var block = this.IteratedBlock(bodyEnd);
    EnsureRoom(seg, dataOffset + block.Length);
    block.CopyTo(seg.Data, dataOffset);
  }

  /// <summary>Expands one LIDATA iterated-data block (repeat count, then either nested blocks or a literal run).</summary>
  private byte[] IteratedBlock(int bodyEnd) {
    var repeat = this.U16();
    var blockCount = this.U16();
    using var ms = new MemoryStream();
    if (blockCount == 0) {
      var n = this.U8();
      var run = new byte[n];
      Array.Copy(this._b, this._pos, run, 0, n);
      this._pos += n;
      for (var r = 0; r < repeat; r++) ms.Write(run);
    } else {
      var inner = new List<byte[]>();
      for (var i = 0; i < blockCount && this._pos < bodyEnd; i++) inner.Add(this.IteratedBlock(bodyEnd));
      for (var r = 0; r < repeat; r++) foreach (var ib in inner) ms.Write(ib);
    }
    return ms.ToArray();
  }

  private void Fixupp(OmfModule m, int bodyEnd, (int Method, int Datum)[] frameThread, (int Method, int Datum)[] targetThread) {
    while (this._pos < bodyEnd) {
      var first = this._b[this._pos];
      if ((first & 0x80) == 0) { // THREAD subrecord
        var trd = this.U8();
        var isFrame = (trd & 0x40) != 0;
        var method = (trd >> 2) & 0x7;
        var thread = trd & 0x3;
        var datum = method is 0 or 1 or 2 ? this.Index() : 0;
        if (isFrame) frameThread[thread] = (method, datum); else targetThread[thread] = (method, datum);
        continue;
      }
      // FIXUP subrecord: LOCAT (2 bytes, high byte first)
      var hi = this.U8();
      var lo = this.U8();
      var selfRel = (hi & 0x40) == 0;          // M bit: 0 = self-relative, 1 = segment-relative
      var loc = (hi >> 2) & 0xF;               // location type (1 = 16-bit offset)
      var dataOffset = ((hi & 0x3) << 8) | lo;
      var fixdat = this.U8();
      var frameByThread = (fixdat & 0x80) != 0;
      var frameMethod = (fixdat >> 4) & 0x7;
      var targetByThread = (fixdat & 0x8) != 0;
      var noDisp = (fixdat & 0x4) != 0;
      var targetMethod = fixdat & 0x3;
      if (frameByThread) frameMethod = frameThread[(fixdat >> 4) & 0x3].Method;
      else if (frameMethod is 0 or 1 or 2) this.Index();   // frame datum (unused here)
      int targetDatum;
      if (targetByThread) { var t = targetThread[fixdat & 0x3]; targetMethod = t.Method; targetDatum = t.Datum; }
      else targetDatum = this.Index();
      if (!noDisp) this.U16();                  // target displacement (carried in the located bytes instead)
      var kind = (targetMethod & 0x3) == 2 ? OmfTargetKind.External : OmfTargetKind.Segment;
      var location = loc is >= 0 and <= 5 ? (OmfLocation)loc : OmfLocation.Other;
      // segment index 1-based for the LEDATA segment is tracked per fixup record start; OMF
      // ties FIXUPP to the preceding LEDATA, whose segment is the last one written. Every
      // location kind is recorded; OmfToPbu decides which it can host (far ones are rejected).
      m.Fixups.Add(new OmfFixup(this._lastDataSeg, dataOffset, selfRel, kind, targetDatum, location));
    }
  }

  private int _lastDataSeg = 1;

  private OmfSegment Seg(OmfModule m, int segIdx) {
    this._lastDataSeg = segIdx;
    if (segIdx < 1 || segIdx > m.Segments.Count)
      throw new OmfException($"LxDATA references segment {segIdx} of {m.Segments.Count}");
    return m.Segments[segIdx - 1];
  }

  private static void EnsureRoom(OmfSegment seg, int needed) {
    if (needed <= seg.Data.Length) return;
    var grown = new byte[Math.Max(needed, seg.Length)];
    seg.Data.CopyTo(grown, 0);
    seg.Data = grown;
    if (seg.Length < needed) seg.Length = needed;
  }
}
