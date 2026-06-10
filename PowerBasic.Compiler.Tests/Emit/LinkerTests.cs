using PowerBasic.Compiler.Emit;

namespace PowerBasic.Compiler.Tests.Emit;

[TestFixture]
public sealed class LinkerTests {

  private const string _signature = "WORK(byval:word)";

  /// <summary>Main image: E8 xx xx = near CALL with an import fixup at offset 1, then C3 (RET).</summary>
  private static PbuFile MainCalling(string import) {
    var main = new PbuFile { Name = "MAIN" };
    main.Code = [0xE8, 0x00, 0x00, 0xC3];
    main.Imports.Add(new(import, PbuFile.HashSignature(_signature)));
    main.Fixups.Add(new(1, PbuFixupKind.ImportCall, 0));
    return main;
  }

  private static PbuFile UnitExporting(string name, string unitName = "UNITA", uint signatureHashOverride = 0) {
    var unit = new PbuFile { Name = unitName };
    unit.Code = [0x90, 0xC3]; // NOP; RET at offset 0
    unit.Exports.Add(new(name, PbuExportKind.Sub, signatureHashOverride != 0 ? signatureHashOverride : PbuFile.HashSignature(_signature), 0));
    return unit;
  }

  [Test]
  public void Link_GivenImportSatisfiedByUnit_WhenLinked_ThenCallDisplacementPatched() {
    var linker = new Linker();
    linker.AddUnit(UnitExporting("Work"));

    var image = linker.Link(MainCalling("Work"));

    // unit code starts at 4; call site operand at 1, displacement relative to 3
    Assert.That(image.Code.Length, Is.EqualTo(6));
    var displacement = (ushort)(image.Code[1] | (image.Code[2] << 8));
    Assert.That(displacement, Is.EqualTo(4 - 3));
    Assert.That(image.ResolvedExports["work"], Is.EqualTo(4));
  }

  [Test]
  public void Link_GivenUnresolvedImport_WhenLinked_ThenLinkException() {
    var ex = Assert.Throws<LinkException>(() => new Linker().Link(MainCalling("Missing")));
    Assert.That(ex!.Message, Does.Contain("Missing"));
  }

  [Test]
  public void Link_GivenSignatureMismatch_WhenLinked_ThenLinkException() {
    var linker = new Linker();
    linker.AddUnit(UnitExporting("Work", signatureHashOverride: 0xDEADBEEF));
    var ex = Assert.Throws<LinkException>(() => linker.Link(MainCalling("Work")));
    Assert.That(ex!.Message, Does.Contain("signature mismatch"));
  }

  [Test]
  public void Link_GivenDuplicateExports_WhenLinked_ThenLinkException() {
    var linker = new Linker();
    linker.AddUnit(UnitExporting("Work", "UNITA"));
    linker.AddUnit(UnitExporting("Work", "UNITB"));
    Assert.Throws<LinkException>(() => linker.Link(MainCalling("Work")));
  }

  [Test]
  public void Link_GivenLibrary_WhenOnlySomeUnitsNeeded_ThenUnusedUnitsNotPulled() {
    var library = new PblFile();
    library.Units.Add(UnitExporting("Work", "NEEDED"));
    library.Units.Add(UnitExporting("Other", "UNUSED"));
    var linker = new Linker();
    linker.AddLibrary(library);

    var image = linker.Link(MainCalling("Work"));

    Assert.That(image.Code.Length, Is.EqualTo(4 + 2), "only NEEDED should be pulled");
    Assert.That(image.ResolvedExports.ContainsKey("Other"), Is.False);
  }

  [Test]
  public void Link_GivenTransitiveLibraryDependency_WhenLinked_ThenChainPulled() {
    // NEEDED imports Helper, exported by SECOND - both live in the library
    var needed = UnitExporting("Work", "NEEDED");
    needed.Code = [0xE8, 0x00, 0x00, 0xC3];
    needed.Imports.Add(new("Helper", PbuFile.HashSignature(_signature)));
    needed.Fixups.Add(new(1, PbuFixupKind.ImportCall, 0));

    var library = new PblFile();
    library.Units.Add(needed);
    library.Units.Add(UnitExporting("Helper", "SECOND"));

    var linker = new Linker();
    linker.AddLibrary(library);
    var image = linker.Link(MainCalling("Work"));

    Assert.That(image.ResolvedExports.ContainsKey("Helper"), Is.True);
    // NEEDED's call to Helper: site at 4+1=5, target 8, displacement 8-7=1
    var displacement = (ushort)(image.Code[5] | (image.Code[6] << 8));
    Assert.That(displacement, Is.EqualTo(1));
  }

  [Test]
  public void Link_GivenDataAndBss_WhenLinked_ThenConcatenatedAndDataFixupsRebased() {
    var main = new PbuFile { Name = "MAIN" };
    main.Code = [0xA1, 0x00, 0x00, 0xC3]; // MOV AX,[disp16] with data fixup at 1
    main.Data = [0x11, 0x22];
    main.BssSize = 10;
    main.Fixups.Add(new(1, PbuFixupKind.DataOffset, 0));

    var unit = new PbuFile { Name = "U" };
    unit.Data = [0x33];
    unit.BssSize = 6;
    var linker = new Linker();
    linker.AddUnit(unit);

    var image = linker.Link(main);

    Assert.That(image.Data, Is.EqualTo(new byte[] { 0x11, 0x22, 0x33 }));
    Assert.That(image.BssSize, Is.EqualTo(16));
    // data fixup: site value 0 + code length (4) + main's data base (0)
    var patched = (ushort)(image.Code[1] | (image.Code[2] << 8));
    Assert.That(patched, Is.EqualTo(4));
  }

  [Test]
  public void Link_GivenSegmentFixups_WhenLinked_ThenSitesReportedForMzRelocation() {
    var main = new PbuFile { Name = "MAIN" };
    main.Code = [0xB8, 0x00, 0x00, 0xC3];
    main.Fixups.Add(new(1, PbuFixupKind.Segment, 0));

    var image = new Linker().Link(main);

    Assert.That(image.SegmentRelocationSites, Is.EqualTo(new[] { 1 }));
  }
}
