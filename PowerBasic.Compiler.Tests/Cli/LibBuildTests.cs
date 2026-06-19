using PowerBasic.Compiler.Cli;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Emit.Omf;

namespace PowerBasic.Compiler.Tests.Cli;

/// <summary>The <c>pbc lib build</c> path: a <c>.LIB</c> output is a foreign-consumable Intel OMF archive (otherwise our own .PBL).</summary>
[TestFixture]
public sealed class LibBuildTests {

  /// <summary>Writes a minimal .PBU exporting one public, returns its path.</summary>
  private static string WriteUnit(string dir, string name, string export, byte[] code) {
    var unit = new PbuFile { Name = name, Code = code };
    unit.Exports.Add(new PbuExport(export, PbuExportKind.Function, 0, 0));
    var path = Path.Combine(dir, name + ".PBU");
    using var stream = File.Create(path);
    unit.Write(stream);
    return path;
  }

  [Test]
  public void LibBuild_GivenLibOutput_ThenWritesOmfArchiveOurReaderConsumes() {
    var dir = Path.Combine(Path.GetTempPath(), $"pbc_lib_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      // --- given: two compiled units, each exporting one public ----------------
      var a = WriteUnit(dir, "ALPHA", "_alpha", [0xB8, 0x01, 0x00, 0xC3]); // MOV AX,1 ; RET
      var b = WriteUnit(dir, "BETA", "_beta", [0xB8, 0x02, 0x00, 0xC3]);   // MOV AX,2 ; RET
      var outLib = Path.Combine(dir, "OUT.LIB");

      // --- when: pbc lib build OUT.LIB ALPHA.PBU BETA.PBU ----------------------
      var stdout = new StringWriter();
      var stderr = new StringWriter();
      var code = Driver.Run(["lib", "build", outLib, a, b], stdout, stderr);

      // --- then: it produced an OMF .LIB our reader parses, with both publics ---
      Assert.That(code, Is.EqualTo(0), stderr.ToString());
      Assert.That(File.Exists(outLib), Is.True, "no OUT.LIB written");
      var modules = OmfReader.ReadLibrary(File.ReadAllBytes(outLib), out var dict);
      Assert.Multiple(() => {
        Assert.That(modules, Has.Count.EqualTo(2));
        Assert.That(dict.ContainsKey("_alpha"), Is.True);
        Assert.That(dict.ContainsKey("_beta"), Is.True);
        Assert.That(modules[dict["_alpha"]].Publics.Any(p => p.Name == "_alpha"), Is.True);
      });
    } finally {
      try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
    }
  }

  [Test]
  public void LibBuild_GivenPblOutput_ThenStillWritesOurOwnLibraryFormat() {
    var dir = Path.Combine(Path.GetTempPath(), $"pbc_pbl_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var a = WriteUnit(dir, "ALPHA", "_alpha", [0xB8, 0x01, 0x00, 0xC3]);
      var outPbl = Path.Combine(dir, "OUT.PBL");

      var code = Driver.Run(["lib", "build", outPbl, a], new StringWriter(), new StringWriter());

      Assert.That(code, Is.EqualTo(0));
      using var stream = File.OpenRead(outPbl);
      Assert.That(PblFile.Read(stream).Units, Has.Count.EqualTo(1)); // our PBL format, unchanged
    } finally {
      try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
    }
  }
}
