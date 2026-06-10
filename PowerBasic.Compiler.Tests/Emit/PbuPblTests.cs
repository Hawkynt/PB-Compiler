using PowerBasic.Compiler.Emit;

namespace PowerBasic.Compiler.Tests.Emit;

[TestFixture]
public sealed class PbuPblTests {

  private static PbuFile SampleUnit(string name = "GRAPHICS") {
    var unit = new PbuFile { Name = name, CpuFlags = PbuCpuFlags.Needs386 | PbuCpuFlags.UsesFpu };
    unit.Exports.Add(new("Graphics_Bar", PbuExportKind.Sub, PbuFile.HashSignature("GRAPHICS_BAR(byval:word,byval:word)"), 0x10));
    unit.Exports.Add(new("Graphics_Version", PbuExportKind.Function, PbuFile.HashSignature("GRAPHICS_VERSION()->integer"), 0x80));
    unit.Imports.Add(new("Svga_LineDraw", PbuFile.HashSignature("SVGA_LINEDRAW(byref:word)")));
    unit.Commons.Add(new("VESACTX", 64));
    unit.Code = [0xB8, 0x01, 0x00, 0xC3];
    unit.Data = [1, 2, 3];
    unit.BssSize = 128;
    unit.Fixups.Add(new(1, PbuFixupKind.ImportCall, 0));
    unit.Fixups.Add(new(2, PbuFixupKind.Segment, 0));
    return unit;
  }

  private static PbuFile RoundTrip(PbuFile unit) {
    using var stream = new MemoryStream();
    unit.Write(stream);
    stream.Position = 0;
    return PbuFile.Read(stream);
  }

  #region PBU

  [Test]
  public void RoundTrip_GivenPopulatedUnit_WhenWrittenAndRead_ThenAllFieldsSurvive() {
    var unit = RoundTrip(SampleUnit());

    Assert.That(unit.Name, Is.EqualTo("GRAPHICS"));
    Assert.That(unit.CpuFlags, Is.EqualTo(PbuCpuFlags.Needs386 | PbuCpuFlags.UsesFpu));
    Assert.That(unit.Exports, Has.Count.EqualTo(2));
    Assert.That(unit.Exports[0].Name, Is.EqualTo("Graphics_Bar"));
    Assert.That(unit.Exports[1].Kind, Is.EqualTo(PbuExportKind.Function));
    Assert.That(unit.Imports.Single().Name, Is.EqualTo("Svga_LineDraw"));
    Assert.That(unit.Commons.Single(), Is.EqualTo(new PbuCommonBlock("VESACTX", 64)));
    Assert.That(unit.Code, Is.EqualTo(new byte[] { 0xB8, 0x01, 0x00, 0xC3 }));
    Assert.That(unit.Data, Is.EqualTo(new byte[] { 1, 2, 3 }));
    Assert.That(unit.BssSize, Is.EqualTo(128));
    Assert.That(unit.Fixups, Has.Count.EqualTo(2));
    Assert.That(unit.Fixups[0].Kind, Is.EqualTo(PbuFixupKind.ImportCall));
  }

  [Test]
  public void RoundTrip_GivenEmptyUnit_WhenWrittenAndRead_ThenEmptyCollectionsSurvive() {
    var unit = RoundTrip(new PbuFile { Name = "EMPTY" });
    Assert.That(unit.Exports, Is.Empty);
    Assert.That(unit.Imports, Is.Empty);
    Assert.That(unit.Code, Is.Empty);
    Assert.That(unit.BssSize, Is.Zero);
  }

  [Test]
  public void Read_GivenWrongMagic_WhenRead_ThenInvalidDataException() {
    using var stream = new MemoryStream("NOPE"u8.ToArray());
    Assert.Throws<InvalidDataException>(() => PbuFile.Read(stream));
  }

  [Test]
  public void HashSignature_GivenSameSignatureDifferentCase_WhenHashed_ThenEqual() {
    Assert.That(PbuFile.HashSignature("Foo(byval:word)"), Is.EqualTo(PbuFile.HashSignature("FOO(BYVAL:WORD)")));
  }

  [Test]
  public void HashSignature_GivenDifferentSignatures_WhenHashed_ThenDiffer() {
    Assert.That(PbuFile.HashSignature("FOO(byval:word)"), Is.Not.EqualTo(PbuFile.HashSignature("FOO(byref:word)")));
  }

  #endregion

  #region PBL

  [Test]
  public void RoundTrip_GivenLibraryWithUnits_WhenWrittenAndRead_ThenUnitsIntact() {
    var library = new PblFile();
    library.Units.Add(SampleUnit("GRAPHICS"));
    library.Units.Add(SampleUnit("TIMER"));

    using var stream = new MemoryStream();
    library.Write(stream);
    stream.Position = 0;
    var read = PblFile.Read(stream);

    Assert.That(read.Units, Has.Count.EqualTo(2));
    Assert.That(read.Units[0].Name, Is.EqualTo("GRAPHICS"));
    Assert.That(read.Units[1].Name, Is.EqualTo("TIMER"));
    Assert.That(read.Units[1].Code, Is.EqualTo(SampleUnit().Code));
  }

  [Test]
  public void RoundTrip_GivenEmptyLibrary_WhenWrittenAndRead_ThenNoUnits() {
    var library = new PblFile();
    using var stream = new MemoryStream();
    library.Write(stream);
    stream.Position = 0;
    Assert.That(PblFile.Read(stream).Units, Is.Empty);
  }

  [Test]
  public void FindExporter_GivenSymbol_WhenSearched_ThenUnitContainingItReturned() {
    var library = new PblFile();
    library.Units.Add(SampleUnit("GRAPHICS"));
    Assert.That(library.FindExporter("graphics_version")?.Name, Is.EqualTo("GRAPHICS"));
    Assert.That(library.FindExporter("Nope"), Is.Null);
  }

  [Test]
  public void Read_GivenLibraryAtNonZeroStreamPosition_WhenRead_ThenOffsetsAnchorCorrectly() {
    var library = new PblFile();
    library.Units.Add(SampleUnit());
    using var stream = new MemoryStream();
    stream.Write([0xAA, 0xBB, 0xCC]); // leading junk - offsets are TOC-relative
    library.Write(stream);
    stream.Position = 3;
    Assert.That(PblFile.Read(stream).Units.Single().Name, Is.EqualTo("GRAPHICS"));
  }

  #endregion
}
