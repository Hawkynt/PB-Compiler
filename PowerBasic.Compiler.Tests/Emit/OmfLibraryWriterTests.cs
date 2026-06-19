using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Emit.Omf;

namespace PowerBasic.Compiler.Tests.Emit;

/// <summary>
/// The OMF library writer (docs/LINKER.md): emit several units as one .LIB archive and prove the
/// result is consumable by our own reader (<see cref="OmfReader.ReadLibrary(byte[], out System.Collections.Generic.IReadOnlyDictionary{string, int})"/>),
/// the lazy <see cref="OmfLibrary"/>, and the <see cref="Linker"/> for selective extraction.
/// Genuine MS-LINK dictionary-hash compatibility is out of scope (its bucket hash differs and
/// cannot be validated here) - our-reader round-trip + selective extraction is the acceptance bar.
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
