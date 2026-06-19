using System.Text;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Emit.Omf;

namespace PowerBasic.Compiler.Tests.Emit;

/// <summary>
/// The external OMF object linker (docs/LINKER.md, M1): parse a genuine-shaped 16-bit
/// OMF .OBJ, lower it to a synthetic unit, and link it through the existing Linker so
/// a BASIC call site resolves to the object's public. Uses a hand-built object module
/// (a leaf cdecl function) so the test is hermetic - no external toolchain needed.
/// </summary>
[TestFixture]
public sealed class OmfTests {

  // --- minimal OMF record builders (checksum byte 0 = "ignore", accepted everywhere) ---
  private static byte[] Record(byte type, params byte[][] parts) {
    var body = parts.SelectMany(p => p).ToArray();
    var rec = new byte[3 + body.Length + 1];
    rec[0] = type;
    var len = body.Length + 1;
    rec[1] = (byte)len; rec[2] = (byte)(len >> 8);
    body.CopyTo(rec, 3);
    return rec; // rec[^1] stays 0
  }
  private static byte[] Str(string s) { var b = Encoding.ASCII.GetBytes(s); return [(byte)b.Length, .. b]; }
  private static byte[] U16(int v) => [(byte)v, (byte)(v >> 8)];
  private static byte[] B(params byte[] v) => v;

  // a leaf cdecl FUNCTION addone(x as long) as long  ->  returns x + 1 in DX:AX
  private static readonly byte[] _addOneCode =
    [0x55, 0x8B, 0xEC, 0x8B, 0x46, 0x04, 0x8B, 0x56, 0x06, 0x05, 0x01, 0x00, 0x83, 0xD2, 0x00, 0x5D, 0xC3];

  private static byte[] BuildAddOneObj() {
    var theadr = Record(0x80, Str("TEST"));
    var lnames = Record(0x96, Str("_TEXT"), Str("CODE"));               // names: 1=_TEXT, 2=CODE
    var segdef = Record(0x98, B(0x28), U16(_addOneCode.Length), B(1), B(2), B(0)); // A=1,C=2; seg=_TEXT class=CODE
    var pubdef = Record(0x90, B(0), B(1), Str("_addone"), U16(0), B(0)); // group 0, seg 1, _addone @ 0
    var ledata = Record(0xA0, B(1), U16(0), _addOneCode);                // seg 1, offset 0, the code
    var modend = Record(0x8A, B(0));
    return [.. theadr, .. lnames, .. segdef, .. pubdef, .. ledata, .. modend];
  }

  [Test]
  public void Read_GivenLeafObject_ThenSegmentPublicAndCodeParsed() {
    var m = OmfReader.ReadObject(BuildAddOneObj());
    Assert.Multiple(() => {
      Assert.That(m.Name, Is.EqualTo("TEST"));
      Assert.That(m.Segments, Has.Count.EqualTo(1));
      Assert.That(m.Segments[0].Name, Is.EqualTo("_TEXT"));
      Assert.That(m.Segments[0].IsCode, Is.True);
      Assert.That(m.Segments[0].Data, Is.EqualTo(_addOneCode));
      Assert.That(m.Publics, Has.Count.EqualTo(1));
      Assert.That(m.Publics[0].Name, Is.EqualTo("_addone"));
      Assert.That(m.Publics[0].Offset, Is.EqualTo(0));
      Assert.That(m.Externals, Is.Empty);
      Assert.That(m.Fixups, Is.Empty);
    });
  }

  [Test]
  public void Convert_GivenLeafObject_ThenSyntheticUnitExportsPublic() {
    var pbu = OmfToPbu.Convert(OmfReader.ReadObject(BuildAddOneObj()));
    Assert.Multiple(() => {
      Assert.That(pbu.Code, Is.EqualTo(_addOneCode));
      Assert.That(pbu.Exports, Has.Count.EqualTo(1));
      Assert.That(pbu.Exports[0].Name, Is.EqualTo("_addone"));
      Assert.That(pbu.Exports[0].CodeOffset, Is.EqualTo(0u));
      Assert.That(pbu.Imports, Is.Empty);
    });
  }

  [Test]
  public void Link_GivenMainCallingObjectPublic_ThenCallResolvesToObjectCode() {
    // a "main" unit: NEAR CALL _addone (E8 disp16) with an ImportCall fixup on the disp
    var main = new PbuFile { Name = "MAIN", Code = [0xE8, 0x00, 0x00] };
    main.Imports.Add(new PbuImport("_addone", 0));
    main.Fixups.Add(new PbuFixup(1, PbuFixupKind.ImportCall, 0)); // patch the disp16 at offset 1

    var linker = new Linker();
    linker.AddUnit(OmfToPbu.Convert(OmfReader.ReadObject(BuildAddOneObj())));
    var image = linker.Link(main);

    // main code (3 bytes) word-aligned to 4, then the object's _TEXT -> _addone at 4
    Assert.That(image.ResolvedExports["_addone"], Is.EqualTo(4u));
    // near-call disp = target - (siteAfterOperand) = 4 - (1 + 2) = 1
    Assert.That(image.Code[1] | (image.Code[2] << 8), Is.EqualTo(1));
    // the object's code is present in the linked image at offset 4
    Assert.That(image.Code.Skip(4).Take(_addOneCode.Length), Is.EqualTo(_addOneCode));
  }

  [Test]
  public void Link_GivenForeignSymbolsDifferingOnlyInCase_ThenResolvedDistinctly() {
    // C/C++ publics are case-sensitive: _foo and _FOO must coexist and resolve apart
    var foreign = new PbuFile { Name = "F", Code = new byte[16], Foreign = true };
    foreign.Exports.Add(new PbuExport("_foo", PbuExportKind.Function, 0, 0));
    foreign.Exports.Add(new PbuExport("_FOO", PbuExportKind.Function, 0, 8));

    var main = new PbuFile { Name = "MAIN", Code = [0xE8, 0x00, 0x00] };
    main.Imports.Add(new PbuImport("_FOO", 0));
    main.Fixups.Add(new PbuFixup(1, PbuFixupKind.ImportCall, 0));

    var linker = new Linker();
    linker.AddUnit(foreign);
    var image = linker.Link(main); // must not throw "duplicate symbol"

    // foreign code placed after main (3 -> word-aligned 4); _FOO at 4 + 8 = 12, _foo at 4.
    // The call resolves to _FOO (offset 12), NOT _foo (4) - case-sensitive resolution.
    Assert.That(image.Code[1] | (image.Code[2] << 8), Is.EqualTo(12 - 3));

    // and the lowercase _foo resolves to its own distinct offset (4)
    var mainLower = new PbuFile { Name = "M2", Code = [0xE8, 0x00, 0x00] };
    mainLower.Imports.Add(new PbuImport("_foo", 0));
    mainLower.Fixups.Add(new PbuFixup(1, PbuFixupKind.ImportCall, 0));
    var linker2 = new Linker();
    linker2.AddUnit(foreign);
    var image2 = linker2.Link(mainLower);
    Assert.That(image2.Code[1] | (image2.Code[2] << 8), Is.EqualTo(4 - 3)); // call -> _foo
  }

  [Test]
  public void Link_GivenCdeclImportWithoutUnderscore_ThenResolvesToDecoratedPublic() {
    // DECLARE ... CDECL with no ALIAS imports the bare name; the C public is "_name"
    var foreign = OmfToPbu.Convert(OmfReader.ReadObject(BuildAddOneObj())); // exports "_addone"
    var main = new PbuFile { Name = "MAIN", Code = [0xE8, 0x00, 0x00] };
    main.Imports.Add(new PbuImport("addone", 0)); // bare, undecorated
    main.Fixups.Add(new PbuFixup(1, PbuFixupKind.ImportCall, 0));

    var linker = new Linker();
    linker.AddUnit(foreign);
    var image = linker.Link(main); // resolves "addone" -> "_addone" via underscore fallback

    Assert.That(image.Code[1] | (image.Code[2] << 8), Is.EqualTo(4 - 3));
  }

  [Test]
  public void Link_GivenUnresolvedMangledSymbol_ThenDiagnosticIncludesDemangledSignature() {
    // a main that imports a C++-mangled external nothing provides
    var main = new PbuFile { Name = "MAIN", Code = [0xE8, 0x00, 0x00] };
    main.Imports.Add(new PbuImport("@square$qi", 0));
    main.Fixups.Add(new PbuFixup(1, PbuFixupKind.ImportCall, 0));

    var ex = Assert.Throws<LinkException>(() => new Linker().Link(main));
    // the diagnostic names the raw symbol AND its demangled signature, so the user
    // can write the matching DECLARE ... CDECL ALIAS "@square$qi"
    Assert.That(ex!.Message, Does.Contain("@square$qi"));
    Assert.That(ex.Message, Does.Contain("square(int)"));
  }

  [Test]
  public void Link_GivenUnresolvedPlainSymbol_ThenDiagnosticHasNoDemanglingHint() {
    // a plain (non-mangled) unresolved external must not gain a spurious C++ hint
    var main = new PbuFile { Name = "MAIN", Code = [0xE8, 0x00, 0x00] };
    main.Imports.Add(new PbuImport("_missing", 0));
    main.Fixups.Add(new PbuFixup(1, PbuFixupKind.ImportCall, 0));

    var ex = Assert.Throws<LinkException>(() => new Linker().Link(main));
    Assert.That(ex!.Message, Does.Contain("_missing"));
    Assert.That(ex.Message, Does.Not.Contain("C++"));
  }

  // ---- #21: data-segment & multi-segment fixups -----------------------------

  // MOV AX, <off16> ; RET   -- the off16 (at code offset 1) is fixed up into a data segment
  private static readonly byte[] _loadFromData = [0xB8, 0x00, 0x00, 0xC3];
  private static readonly byte[] _dataBytes = [0xDE, 0xAD, 0xBE, 0xEF];

  // FIXUPP one offset(16) fixup at <siteOff> in the last-written segment, absolute (M=1),
  // frame=target, target = SEGMENT index <segDatum> (1-based), no displacement.
  private static byte[] FixupSegTarget(int siteOff, int segDatum) {
    // LOCAT high byte: F(0x80) | M-absolute(0x40) | LOC=1<<2 | site[9:8].
    var locat = 0x80 | 0x40 | (1 << 2) | ((siteOff >> 8) & 0x3);
    // FIXDAT: frame method 0 (SEGDEF), P-bit(0x4)=no displacement, target method 0 (SEGDEF).
    const int fixdat = (0 << 4) | (1 << 2) | 0;
    return Record(0x9C, B((byte)locat, (byte)siteOff), B((byte)fixdat), B((byte)segDatum), B((byte)segDatum));
  }

  [Test]
  public void ConvertAndLink_GivenCodeFixupIntoDataSegment_ThenSiteRelocatedToDataArea() {
    // names: 1=_TEXT 2=CODE 3=_DATA 4=DATA ; segs: 1=_TEXT/CODE, 2=_DATA/DATA
    byte[] obj = [
      .. Record(0x80, Str("DREF")),
      .. Record(0x96, Str("_TEXT"), Str("CODE"), Str("_DATA"), Str("DATA")),
      .. Record(0x98, B(0x28), U16(_loadFromData.Length), B(1), B(2), B(0)), // seg1 _TEXT/CODE
      .. Record(0x98, B(0x28), U16(_dataBytes.Length),    B(3), B(4), B(0)), // seg2 _DATA/DATA
      .. Record(0xA0, B(2), U16(0), _dataBytes),     // data first
      .. Record(0xA0, B(1), U16(0), _loadFromData),  // then code -> FIXUPP binds to seg1
      .. FixupSegTarget(1, 2),                        // patch off16 at code+1, target = seg 2 (data)
      .. Record(0x8A, B(0)),
    ];
    var foreign = OmfToPbu.Convert(OmfReader.ReadObject(obj));
    Assert.That(foreign.Fixups, Has.Count.EqualTo(1));
    Assert.That(foreign.Fixups[0].Kind, Is.EqualTo(PbuFixupKind.DataOffset));

    // link with an empty main so the foreign unit sits at code base 0
    var main = new PbuFile { Name = "MAIN", Code = [] };
    var linker = new Linker();
    linker.AddUnit(foreign);
    var image = linker.Link(main);

    // code is 4 bytes -> data area begins at codeSize (4, already even). data base of the
    // unit is 0, so the off16 must now point at codeSize + 0 = 4 (start of the data area).
    var off = image.Code[1] | (image.Code[2] << 8);
    Assert.That(off, Is.EqualTo(4));
    Assert.That(image.Data.Take(_dataBytes.Length), Is.EqualTo(_dataBytes));
  }

  [Test]
  public void Convert_GivenPublicInSecondCodeSegment_ThenExportOffsetCountsSegmentBase() {
    // two code segments; the public lives in the second, so its export offset must be
    // (length of first code segment) + its in-segment offset.
    byte[] first = [0x90, 0x90, 0x90, 0x90];   // 4x NOP
    byte[] second = [0xB8, 0x07, 0x00, 0xC3];  // MOV AX,7 ; RET
    byte[] obj = [
      .. Record(0x80, Str("TWOSEG")),
      .. Record(0x96, Str("_TEXT"), Str("CODE")),
      .. Record(0x98, B(0x28), U16(first.Length),  B(1), B(2), B(0)), // seg1 _TEXT/CODE
      .. Record(0x98, B(0x28), U16(second.Length), B(1), B(2), B(0)), // seg2 _TEXT/CODE
      .. Record(0x90, B(0), B(2), Str("_second"), U16(0), B(0)),      // public in seg 2 @ 0
      .. Record(0xA0, B(1), U16(0), first),
      .. Record(0xA0, B(2), U16(0), second),
      .. Record(0x8A, B(0)),
    ];
    var pbu = OmfToPbu.Convert(OmfReader.ReadObject(obj));
    Assert.Multiple(() => {
      Assert.That(pbu.Code.Length, Is.EqualTo(first.Length + second.Length));
      Assert.That(pbu.Exports, Has.Count.EqualTo(1));
      Assert.That(pbu.Exports[0].Name, Is.EqualTo("_second"));
      Assert.That(pbu.Exports[0].CodeOffset, Is.EqualTo((uint)first.Length)); // base of seg2 + 0
      Assert.That(pbu.Code.Skip(first.Length).Take(second.Length), Is.EqualTo(second));
    });
  }

  // A FIXUPP record: one location of kind <loc> at <siteOff> in the last-written segment,
  // targeting SEGMENT index <segDatum> (1-based). Absolute unless <selfRel>. An optional
  // target displacement (e.g. &arr[3]) follows when <disp> is given (P-bit cleared).
  private static byte[] FixupLoc(int loc, int siteOff, int segDatum, bool selfRel = false, int? disp = null) {
    var locat = 0x80 | (selfRel ? 0 : 0x40) | (loc << 2) | ((siteOff >> 8) & 0x3);
    var fixdat = (0 << 4) | (disp is null ? 0x04 : 0) | 0; // frame SEGDEF, P-bit=no-disp unless given, target SEGDEF
    byte[] tail = disp is null
      ? [(byte)segDatum, (byte)segDatum]
      : [(byte)segDatum, (byte)segDatum, .. U16(disp.Value)];
    return Record(0x9C, B((byte)locat, (byte)siteOff), B((byte)fixdat), tail);
  }

  [Test]
  public void ConvertAndLink_GivenFarSegmentFixup_ThenHostedAsLoadSegmentRelocation() {
    // MOV AX,<seg> ; RET - the segment word (at code+1) is a Base16 (LOC=2) fixup. The whole
    // program is one segment, so it becomes an MZ relocation: the site is zeroed and the DOS
    // loader adds the load segment. seg 2 (data) is the nominal target.
    byte[] loadSeg = [0xB8, 0xFF, 0xFF, 0xC3];
    byte[] obj = [
      .. Record(0x80, Str("FARSEG")),
      .. Record(0x96, Str("_TEXT"), Str("CODE"), Str("_DATA"), Str("DATA")),
      .. Record(0x98, B(0x28), U16(loadSeg.Length),   B(1), B(2), B(0)), // seg1 _TEXT/CODE
      .. Record(0x98, B(0x28), U16(_dataBytes.Length), B(3), B(4), B(0)), // seg2 _DATA/DATA
      .. Record(0xA0, B(2), U16(0), _dataBytes),
      .. Record(0xA0, B(1), U16(0), loadSeg),     // code last -> FIXUPP binds to seg1
      .. FixupLoc(2, 1, 2),                        // Base16 at code+1, target = seg 2
      .. Record(0x8A, B(0)),
    ];
    var foreign = OmfToPbu.Convert(OmfReader.ReadObject(obj));
    Assert.That(foreign.Fixups, Has.Count.EqualTo(1));
    Assert.That(foreign.Fixups[0].Kind, Is.EqualTo(PbuFixupKind.Segment));
    Assert.That(foreign.Fixups[0].InData, Is.False);
    Assert.That(foreign.Code[1] | (foreign.Code[2] << 8), Is.EqualTo(0), "segment word must be zeroed for the loader to fill");

    var linker = new Linker();
    linker.AddUnit(foreign);
    var image = linker.Link(new PbuFile { Name = "MAIN", Code = [] });
    // empty main -> the unit sits at code base 0, so the relocation is at image offset 1
    Assert.That(image.SegmentRelocationSites, Does.Contain(1));
  }

  [Test]
  public void ConvertAndLink_GivenFarPointerInitializerInData_ThenSplitsIntoOffsetAndSegment() {
    // a far pointer (Pointer32, LOC=3) sitting IN the data segment: it points at a datum 4
    // bytes further on (the value 42). The far ptr lowers to an offset half (a DataOffset
    // into the combined image) plus a segment half (an MZ relocation), both data-image sites.
    byte[] code = [0xC3];                                    // a 1-byte RET
    byte[] data = [0x00, 0x00, 0x00, 0x00, 0x2A, 0x00, 0x00, 0x00]; // [far ptr][value 42]
    byte[] obj = [
      .. Record(0x80, Str("FARPTR")),
      .. Record(0x96, Str("_TEXT"), Str("CODE"), Str("_DATA"), Str("DATA")),
      .. Record(0x98, B(0x28), U16(code.Length), B(1), B(2), B(0)), // seg1 _TEXT/CODE
      .. Record(0x98, B(0x28), U16(data.Length), B(3), B(4), B(0)), // seg2 _DATA/DATA
      .. Record(0xA0, B(1), U16(0), code),
      .. Record(0xA0, B(2), U16(0), data),         // data last -> FIXUPP binds to seg2 (the site)
      .. FixupLoc(3, 0, 2, disp: 4),               // Pointer32 at data+0, target seg2, +4
      .. Record(0x8A, B(0)),
    ];
    var foreign = OmfToPbu.Convert(OmfReader.ReadObject(obj));
    Assert.Multiple(() => {
      Assert.That(foreign.Fixups, Has.Count.EqualTo(2));
      Assert.That(foreign.Fixups[0].Kind, Is.EqualTo(PbuFixupKind.DataOffset)); // offset half
      Assert.That(foreign.Fixups[1].Kind, Is.EqualTo(PbuFixupKind.Segment));    // segment half
      Assert.That(foreign.Fixups.All(f => f.InData), Is.True, "both halves live in the data image");
    });

    var linker = new Linker();
    linker.AddUnit(foreign);
    var image = linker.Link(new PbuFile { Name = "MAIN", Code = [] });
    var off = image.Data[0] | (image.Data[1] << 8);
    Assert.Multiple(() => {
      // the far pointer's offset is the target datum's offset in the combined segment:
      // code occupies [0..codeSize), the value 42 is at data offset 4 -> codeSize + 4.
      Assert.That(off, Is.EqualTo(image.Code.Length + 4));
      Assert.That(image.Data[2] | (image.Data[3] << 8), Is.EqualTo(0), "segment half zeroed for the loader");
      // the segment half is relocated where it sits in the [code .. data] image: codeSize + 2
      Assert.That(image.SegmentRelocationSites, Does.Contain(image.Code.Length + 2));
      // the pointed-at value survived unchanged
      Assert.That(image.Data[4], Is.EqualTo(0x2A));
    });
  }

  // ---- OMF emission (OmfWriter round-trip) ----------------------------------

  private static (PbuFixupKind Kind, uint Offset, ushort Target, bool InData) Key(PbuFixup f)
    => (f.Kind, f.Offset, f.Target, f.InData);

  [Test]
  public void WriteThenReadBack_GivenUnitWithEveryFixupKind_ThenRoundTripsThroughOurReader() {
    // a unit exercising all five fixup kinds (code sites) plus a data-image site. The located
    // bytes hold each fixup's addend; a Segment site holds 0 (the loader fills the paragraph).
    var original = new PbuFile {
      Name = "RT",
      Code = [0x06, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0xC3, 0x90],
      Data = [0x01, 0x00, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF],
    };
    original.Exports.Add(new PbuExport("_pub", PbuExportKind.Function, 0, 4));
    original.Imports.Add(new PbuImport("_ext", 0));
    original.Fixups.Add(new PbuFixup(0, PbuFixupKind.NearCode, 0));
    original.Fixups.Add(new PbuFixup(2, PbuFixupKind.DataOffset, 0));
    original.Fixups.Add(new PbuFixup(4, PbuFixupKind.Segment, 0));
    original.Fixups.Add(new PbuFixup(6, PbuFixupKind.ImportCall, 0));
    original.Fixups.Add(new PbuFixup(8, PbuFixupKind.ImportOffset, 0));
    original.Fixups.Add(new PbuFixup(0, PbuFixupKind.DataOffset, 0, InData: true));

    var roundtrip = OmfToPbu.Convert(OmfReader.ReadObject(OmfWriter.WriteObject(original)));

    Assert.Multiple(() => {
      Assert.That(roundtrip.Code, Is.EqualTo(original.Code), "code bytes (with addends) survive the round trip");
      Assert.That(roundtrip.Data, Is.EqualTo(original.Data), "data bytes survive the round trip");
      // OMF carries the public name + its offset (BASIC export kind/signature hash are not OMF concepts)
      Assert.That(roundtrip.Exports.Select(e => (e.Name, e.CodeOffset)), Is.EquivalentTo(new[] { ("_pub", 4u) }));
      Assert.That(roundtrip.Imports.Select(i => i.Name), Is.EquivalentTo(new[] { "_ext" }));
      Assert.That(roundtrip.Fixups.Select(Key), Is.EquivalentTo(original.Fixups.Select(Key)));
    });
  }

  [Test]
  public void WriteThenReadBack_GivenSegmentLargerThanOneLedata_ThenChunkedFixupOffsetsStayCorrect() {
    // a >1024-byte code segment forces the writer to split it across several LEDATA records;
    // a fixup in a later chunk only round-trips if the reader folds in each LEDATA's base.
    var code = new byte[2050];
    code[10] = 0x06;          // NearCode site in the first chunk
    code[1500] = 0x0A;        // NearCode site in the second chunk (offset 1024..2047)
    var original = new PbuFile { Name = "BIG", Code = code };
    original.Exports.Add(new PbuExport("_big", PbuExportKind.Function, 0, 0));
    original.Fixups.Add(new PbuFixup(10, PbuFixupKind.NearCode, 0));
    original.Fixups.Add(new PbuFixup(1500, PbuFixupKind.NearCode, 0));

    var obj = OmfWriter.WriteObject(original);
    Assert.That(obj.Count(b => b == 0xA0), Is.GreaterThan(1), "a >1024-byte segment must span multiple LEDATA records");

    var roundtrip = OmfToPbu.Convert(OmfReader.ReadObject(obj));
    Assert.Multiple(() => {
      Assert.That(roundtrip.Code, Is.EqualTo(original.Code));
      // the far-chunk fixup keeps its absolute segment offset (1500), not the chunk-relative one
      Assert.That(roundtrip.Fixups.Select(Key), Is.EquivalentTo(original.Fixups.Select(Key)));
    });
  }

  [Test]
  public void WriteThenLink_GivenEmittedObject_ThenItsPublicLinksAndCallsCorrectly() {
    // emit a leaf object with OmfWriter, then link it through our own linker behind a near call
    // - end-to-end proof the emitted OMF is consumable, not merely re-readable.
    var leaf = new PbuFile { Name = "LEAF", Code = [0xB8, 0x2A, 0x00, 0xC3] }; // MOV AX,42 ; RET
    leaf.Exports.Add(new PbuExport("_leaf", PbuExportKind.Function, 0, 0));
    var foreign = OmfToPbu.Convert(OmfReader.ReadObject(OmfWriter.WriteObject(leaf)));

    var main = new PbuFile { Name = "MAIN", Code = [0xE8, 0x00, 0x00] };
    main.Imports.Add(new PbuImport("_leaf", 0));
    main.Fixups.Add(new PbuFixup(1, PbuFixupKind.ImportCall, 0));

    var linker = new Linker();
    linker.AddUnit(foreign);
    var image = linker.Link(main);

    Assert.That(image.ResolvedExports["_leaf"], Is.EqualTo(4u));        // main (3) -> word-aligned 4
    Assert.That(image.Code[1] | (image.Code[2] << 8), Is.EqualTo(4 - 3)); // near-call disp
    Assert.That(image.Code.Skip(4).Take(4), Is.EqualTo(leaf.Code));
  }

  // ---- #22: .LIB symbol dictionary ------------------------------------------

  // Builds a single self-contained code module (THEADR..MODEND) exporting one public.
  private static byte[] LibMember(string name, string pub, byte[] code) => [
    .. Record(0x80, Str(name)),
    .. Record(0x96, Str("_TEXT"), Str("CODE")),
    .. Record(0x98, B(0x28), U16(code.Length), B(1), B(2), B(0)),
    .. Record(0x90, B(0), B(1), Str(pub), U16(0), B(0)),
    .. Record(0xA0, B(1), U16(0), code),
    .. Record(0x8A, B(0)),
  ];

  // Builds a 2-member OMF .LIB: 0xF0 header (page size, dictionary offset/blocks), two
  // page-aligned members, a 0xF1 trailer, then a one-block (512B) hash dictionary that
  // maps each public to its member's 1-based page number.
  private static byte[] BuildTwoMemberLib((string Name, string Pub, byte[] Code)[] members, int pageSize = 16) {
    var buf = new List<byte>();
    void Pad(int to) { while (buf.Count < to) buf.Add(0); }

    // member modules, each starting on a page boundary (page 0 is the header)
    var memberPage = new int[members.Length];
    Pad(pageSize); // reserve the header page (page 0)
    for (var i = 0; i < members.Length; i++) {
      Pad((buf.Count + pageSize - 1) / pageSize * pageSize);
      memberPage[i] = buf.Count / pageSize; // 0-based file page; dictionary stores +1
      buf.AddRange(LibMember(members[i].Name, members[i].Pub, members[i].Code));
    }
    // 0xF1 trailer, padded to a page boundary so the dictionary starts page-aligned
    Pad((buf.Count + pageSize - 1) / pageSize * pageSize);
    buf.Add(0xF1); buf.Add(0); buf.Add(0); buf.Add(0); // trivial trailer record (len 0 -> just checksum byte)
    Pad((buf.Count + pageSize - 1) / pageSize * pageSize);

    // one 512-byte dictionary block: 37 bucket bytes, then entries packed after them.
    var dictOffset = buf.Count;
    var block = new byte[512];
    var cursor = 38;                 // first even slot after the 37 bucket bytes (entries are 2-byte aligned)
    for (var i = 0; i < members.Length; i++) {
      var entry = cursor;           // byte offset within the block
      block[entry] = (byte)members[i].Pub.Length;
      var nb = Encoding.ASCII.GetBytes(members[i].Pub);
      Array.Copy(nb, 0, block, entry + 1, nb.Length);
      var page = memberPage[i] + 1; // dictionary uses 1-based page numbers
      block[entry + 1 + nb.Length] = (byte)page;
      block[entry + 1 + nb.Length + 1] = (byte)(page >> 8);
      block[i] = (byte)(entry / 2); // bucket -> half-paragraph (2-byte) pointer to the entry
      cursor = (entry + 1 + nb.Length + 2 + 1) & ~1; // next even slot
    }
    buf.AddRange(block);

    var bytes = buf.ToArray();
    bytes[0] = 0xF0;
    var recLen = pageSize - 3;
    bytes[1] = (byte)recLen; bytes[2] = (byte)(recLen >> 8);
    bytes[3] = (byte)dictOffset; bytes[4] = (byte)(dictOffset >> 8);
    bytes[5] = (byte)(dictOffset >> 16); bytes[6] = (byte)(dictOffset >> 24);
    bytes[7] = 1; bytes[8] = 0; // one 512-byte dictionary block
    return bytes;
  }

  [Test]
  public void ReadLibrary_GivenTwoMembersAndDictionary_ThenBothParseAndDictionaryMaps() {
    (string, string, byte[])[] members = [("ALPHA", "_alpha", [0xB8, 0x01, 0x00, 0xC3]), ("BETA", "_beta", [0xB8, 0x02, 0x00, 0xC3])];
    var lib = BuildTwoMemberLib(members);
    var modules = OmfReader.ReadLibrary(lib, out var dict);
    Assert.Multiple(() => {
      Assert.That(modules, Has.Count.EqualTo(2));
      Assert.That(modules[0].Publics[0].Name, Is.EqualTo("_alpha"));
      Assert.That(modules[1].Publics[0].Name, Is.EqualTo("_beta"));
      // the parsed dictionary maps each public to the correct member index
      Assert.That(dict["_alpha"], Is.EqualTo(0));
      Assert.That(dict["_beta"], Is.EqualTo(1));
    });
  }

  [Test]
  public void Link_GivenLibrary_ThenOnlyTheMemberSatisfyingTheImportIsPulled() {
    (string, string, byte[])[] members = [("ALPHA", "_alpha", [0xB8, 0x01, 0x00, 0xC3]), ("BETA", "_beta", [0xB8, 0x02, 0x00, 0xC3])];
    var modules = OmfReader.ReadLibrary(BuildTwoMemberLib(members));
    var pbl = new PblFile();
    foreach (var mod in modules) pbl.Units.Add(OmfToPbu.Convert(mod));

    // main calls only _beta; the linker must pull BETA and leave ALPHA out of the image.
    var main = new PbuFile { Name = "MAIN", Code = [0xE8, 0x00, 0x00] };
    main.Imports.Add(new PbuImport("_beta", 0));
    main.Fixups.Add(new PbuFixup(1, PbuFixupKind.ImportCall, 0));

    var linker = new Linker();
    linker.AddLibrary(pbl);
    var image = linker.Link(main);

    Assert.Multiple(() => {
      // _beta resolved; _alpha was never pulled (not present in the resolved table)
      Assert.That(image.ResolvedExports.ContainsKey("_beta"), Is.True);
      Assert.That(image.ResolvedExports.ContainsKey("_alpha"), Is.False);
      // main (3 bytes) -> aligned 4; _beta at 4; call disp = 4 - (1+2) = 1
      Assert.That(image.ResolvedExports["_beta"], Is.EqualTo(4u));
      Assert.That(image.Code[1] | (image.Code[2] << 8), Is.EqualTo(1));
    });
  }
}
