using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Emit.Omf;

namespace PowerBasic.Compiler.Tests.Emit;

/// <summary>
/// The OMF library writer (docs/LINKER.md): emit several units as one .LIB archive and prove the
/// result is consumable by our own reader (<see cref="OmfReader.ReadLibrary(byte[], out System.Collections.Generic.IReadOnlyDictionary{string, int})"/>),
/// the lazy <see cref="OmfLibrary"/>, and the <see cref="Linker"/> for selective extraction.
/// It is also <b>genuine MS-LINK compatible</b>: <see cref="DictionarySearch_GivenManySymbols_ThenEveryOneIsFoundByTheGenuineOmfSearch"/>
/// drives an independent port of the real OMF library hash + dictionary search (the algorithm a
/// genuine LINK/Watcom/Borland linker uses, validated bit-for-bit against a real MS C 6.0 SLIBCR.LIB)
/// and proves every emitted symbol is located by it.
/// </summary>
[TestFixture]
public sealed class OmfLibraryWriterTests {

  // a tiny leaf member: MOV AX,<n> ; RET, exporting one public.
  private static PbuFile Member(string name, string pub, byte n) {
    var unit = new PbuFile { Name = name, Code = [0xB8, n, 0x00, 0xC3] };
    unit.Exports.Add(new PbuExport(pub, PbuExportKind.Function, 0, 0));
    return unit;
  }

  [Test]
  public void WriteThenReadLibrary_GivenThreeMembers_ThenAllParseAndDictionaryMapsEachExport() {
    PbuFile[] units = [Member("ALPHA", "_alpha", 1), Member("BETA", "_beta", 2), Member("GAMMA", "_gamma", 3)];

    var lib = OmfLibraryWriter.WriteLibrary(units);
    var modules = OmfReader.ReadLibrary(lib, out var dict);

    Assert.Multiple(() => {
      Assert.That(modules, Has.Count.EqualTo(3));
      Assert.That(modules[0].Publics[0].Name, Is.EqualTo("_alpha"));
      Assert.That(modules[1].Publics[0].Name, Is.EqualTo("_beta"));
      Assert.That(modules[2].Publics[0].Name, Is.EqualTo("_gamma"));
      // the parsed hash dictionary maps each export to its member index (not the PUBDEF fallback)
      Assert.That(dict["_alpha"], Is.EqualTo(0));
      Assert.That(dict["_beta"], Is.EqualTo(1));
      Assert.That(dict["_gamma"], Is.EqualTo(2));
    });
  }

  [Test]
  public void WriteThenReadLibrary_GivenSingleMember_ThenItParses() {
    // boundary: the smallest non-empty library (one member, one symbol).
    var lib = OmfLibraryWriter.WriteLibrary([Member("SOLO", "_solo", 7)]);
    var modules = OmfReader.ReadLibrary(lib, out var dict);
    Assert.Multiple(() => {
      Assert.That(modules, Has.Count.EqualTo(1));
      Assert.That(dict["_solo"], Is.EqualTo(0));
    });
  }

  [Test]
  public void WriteThenOmfLibrary_GivenTwoMembers_ThenDefinesAndProvideTargetRightMember() {
    PbuFile[] units = [Member("ALPHA", "_alpha", 1), Member("BETA", "_beta", 2)];

    var library = new OmfLibrary(OmfLibraryWriter.WriteLibrary(units));

    Assert.Multiple(() => {
      Assert.That(library.MemberCount, Is.EqualTo(2));
      Assert.That(library.Defines("_beta"), Is.True);
      Assert.That(library.Defines("_missing"), Is.False);
    });
    // Provide returns the member that actually defines _beta (its code is the BETA blob)
    var provided = library.Provide("_beta");
    Assert.That(provided, Is.Not.Null);
    Assert.That(provided!.Code, Is.EqualTo(new byte[] { 0xB8, 0x02, 0x00, 0xC3 }));
  }

  [Test]
  public void WriteThenLink_GivenMainImportingOneSymbol_ThenOnlyThatMemberIsPulled() {
    PbuFile[] units = [Member("ALPHA", "_alpha", 1), Member("BETA", "_beta", 2)];
    var library = new OmfLibrary(OmfLibraryWriter.WriteLibrary(units));

    // main calls only _beta; the linker must pull BETA and leave ALPHA out of the image.
    var main = new PbuFile { Name = "MAIN", Code = [0xE8, 0x00, 0x00] };
    main.Imports.Add(new PbuImport("_beta", 0));
    main.Fixups.Add(new PbuFixup(1, PbuFixupKind.ImportCall, 0));

    var linker = new Linker();
    linker.AddOmfLibrary(library);
    var image = linker.Link(main);

    Assert.Multiple(() => {
      Assert.That(image.ResolvedExports.ContainsKey("_beta"), Is.True);
      Assert.That(image.ResolvedExports.ContainsKey("_alpha"), Is.False);
      Assert.That(library.ProvidedCount, Is.EqualTo(1));
      // main (3 bytes) -> word-aligned 4; _beta at 4; near-call disp = 4 - (1+2) = 1
      Assert.That(image.ResolvedExports["_beta"], Is.EqualTo(4u));
      Assert.That(image.Code[1] | (image.Code[2] << 8), Is.EqualTo(1));
    });
  }

  // ---- genuine MS-LINK dictionary compatibility ------------------------------

  [Test]
  public void DictionarySearch_GivenManySymbols_ThenEveryOneIsFoundByTheGenuineOmfSearch() {
    // Enough symbols to span several 512-byte dictionary blocks with real bucket collisions.
    var big = new PbuFile { Name = "BIG", Code = [0xC3] };
    var syms = new List<string>();
    for (var i = 0; i < 80; ++i) { var s = $"_sym{i:D2}"; syms.Add(s); big.Exports.Add(new PbuExport(s, PbuExportKind.Function, 0, 0)); }
    // a couple of extra members + mixed-case names (the hash case-folds, as genuine LINK does)
    var more = new PbuFile { Name = "MORE", Code = [0xC3] };
    foreach (var s in new[] { "_Strlen", "_MemCpy", "FARPROC", "__chkstk" }) { syms.Add(s); more.Exports.Add(new PbuExport(s, PbuExportKind.Function, 0, 0)); }

    var lib = OmfLibraryWriter.WriteLibrary([big, more]);

    // every emitted symbol must be locatable by the genuine OMF dictionary search...
    foreach (var s in syms)
      Assert.That(GenuineDictFind(lib, s), Is.True, $"genuine OMF search failed to find {s}");
    // ...and a symbol that is not present must not be found (the search terminates correctly)
    Assert.That(GenuineDictFind(lib, "_does_not_exist"), Is.False);
  }

  // --- independent port of the genuine OMF library hash + dictionary search (Open Watcom omflib_hash
  //     + OMFSearchExtLib), used here purely as a reference oracle to validate OmfLibraryWriter's output.
  private static ushort Rotl(ushort a, int b) => (ushort)((a << b) | (a >> (16 - b)));
  private static ushort Rotr(ushort a, int b) => (ushort)((a << (16 - b)) | (a >> b));

  private static (int block, int blockd, int bucket, int bucketd) GenuineHash(string sym, int numBlocks) {
    var name = System.Text.Encoding.ASCII.GetBytes(sym);
    var count = name.Length;
    int l = 0, r = count;
    ushort block = (ushort)(count | 0x20), blockd = 0, bucket = 0, bucketd = (ushort)(count | 0x20);
    for (; ; ) {
      var curr = name[--r] | 0x20;
      blockd = (ushort)(curr ^ Rotl(blockd, 2));
      bucket = (ushort)(curr ^ Rotr(bucket, 2));
      if (--count == 0) break;
      curr = name[l++] | 0x20;
      block = (ushort)(curr ^ Rotl(block, 2));
      bucketd = (ushort)(curr ^ Rotr(bucketd, 2));
    }
    var bkd = bucketd % 37; if (bkd == 0) bkd = 1;
    var bld = blockd % numBlocks; if (bld == 0) bld = 1;
    return (block % numBlocks, bld, bucket % 37, bkd);
  }

  private static bool GenuineDictFind(byte[] lib, string sym) {
    var pageSize = (lib[1] | (lib[2] << 8)) + 3;
    var dictOff = lib[3] | (lib[4] << 8) | (lib[5] << 16) | (lib[6] << 24);
    var numBlocks = lib[7] | (lib[8] << 8);
    var (block, blockd, bucket, bucketd) = GenuineHash(sym, numBlocks);
    for (var i = 0; i < numBlocks; ++i) {
      var bbase = dictOff + block * 512;
      var bk = bucket;
      for (var j = 0; j < 37; ++j) {
        var slot = lib[bbase + bk];
        if (slot == 0) return false;                 // empty bucket, page not full -> absent
        var e = bbase + slot * 2;
        var ln = lib[e];
        var name = System.Text.Encoding.ASCII.GetString(lib, e + 1, ln);
        if (name == sym) return true;
        bk += bucketd; if (bk >= 37) bk -= 37;
      }
      block += blockd; if (block >= numBlocks) block -= numBlocks;
    }
    return false;
  }

  [Test]
  public void WriteThenReadLibrary_GivenMemberWithInternalFixup_ThenFixupSurvivesTheArchive() {
    // a member carrying an internal NearCode fixup: CALL near self at offset 0 (E8 disp16),
    // the disp at offset 1 holds its addend. The archive round trip must preserve it.
    var withFixup = new PbuFile { Name = "FX", Code = [0xE8, 0x00, 0x00, 0xC3] };
    withFixup.Exports.Add(new PbuExport("_fx", PbuExportKind.Function, 0, 0));
    withFixup.Fixups.Add(new PbuFixup(1, PbuFixupKind.NearCode, 0));

    var lib = OmfLibraryWriter.WriteLibrary([Member("PLAIN", "_plain", 5), withFixup]);
    var modules = OmfReader.ReadLibrary(lib, out var dict);

    var roundtrip = OmfToPbu.Convert(modules[dict["_fx"]]);
    Assert.Multiple(() => {
      Assert.That(roundtrip.Code, Is.EqualTo(withFixup.Code));
      Assert.That(roundtrip.Fixups, Has.Count.EqualTo(1));
      Assert.That(roundtrip.Fixups[0].Kind, Is.EqualTo(PbuFixupKind.NearCode));
      Assert.That(roundtrip.Fixups[0].Offset, Is.EqualTo(1u));
    });
  }
}
