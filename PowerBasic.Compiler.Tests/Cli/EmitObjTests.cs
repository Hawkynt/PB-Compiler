using PowerBasic.Compiler.Cli;
using PowerBasic.Compiler.Emit.Omf;

namespace PowerBasic.Compiler.Tests.Cli;

/// <summary>The <c>pbc --emit-obj</c> front-end path: compile a program's procedures to a linkable Intel OMF .OBJ.</summary>
[TestFixture]
public sealed class EmitObjTests {

  private static (int Code, string Out, string Err, byte[] Obj) RunEmit(string source, string? outputName = null) {
    var path = Path.Combine(Path.GetTempPath(), $"pbc_obj_{Guid.NewGuid():N}.bas");
    var output = outputName ?? Path.Combine(Path.GetTempPath(), $"pbc_obj_{Guid.NewGuid():N}.obj");
    File.WriteAllText(path, source);
    try {
      var stdout = new StringWriter();
      var stderr = new StringWriter();
      var code = Driver.Run(["--emit-obj", "-O", output, path], stdout, stderr);
      var obj = File.Exists(output) ? File.ReadAllBytes(output) : [];
      return (code, stdout.ToString(), stderr.ToString(), obj);
    } finally {
      File.Delete(path);
      if (File.Exists(output))
        File.Delete(output);
    }
  }

  [Test]
  public void EmitObj_ForAProgramWithAProcedure_ExitsZeroAndWritesAParseableObjectWithThePublic() {
    var (code, _, err, obj) = RunEmit(
      "DECLARE FUNCTION Square%(BYVAL n%)\n" +
      "FUNCTION Square%(BYVAL n%)\n  Square% = n% * n%\nEND FUNCTION");

    Assert.That(code, Is.EqualTo(0), err);
    Assert.That(obj, Is.Not.Empty);

    var module = OmfReader.ReadObject(obj);
    Assert.That(module.Publics.Select(p => p.Name), Has.Some.EqualTo("Square"));
  }

  [Test]
  public void EmitObj_RoundTrippedThroughOmfToPbu_YieldsAUnitExportingTheProcedure() {
    var (code, _, err, obj) = RunEmit(
      "DECLARE SUB Greet()\n" +
      "SUB Greet()\n  X% = 1\nEND SUB");

    Assert.That(code, Is.EqualTo(0), err);

    var unit = OmfToPbu.Convert(OmfReader.ReadObject(obj));
    Assert.That(unit.Exports.Select(e => e.Name), Has.Some.EqualTo("Greet"));
  }

  [Test]
  public void EmitObj_WithExplicitOutputName_WritesToThatPath() {
    var output = Path.Combine(Path.GetTempPath(), $"pbc_obj_named_{Guid.NewGuid():N}.obj");
    var (code, stdout, err, obj) = RunEmit(
      "DECLARE FUNCTION Twice%(BYVAL n%)\n" +
      "FUNCTION Twice%(BYVAL n%)\n  Twice% = n% + n%\nEND FUNCTION",
      output);

    Assert.That(code, Is.EqualTo(0), err);
    Assert.That(obj, Is.Not.Empty);
    Assert.That(stdout, Does.Contain(Path.GetFileName(output)));
  }
}
