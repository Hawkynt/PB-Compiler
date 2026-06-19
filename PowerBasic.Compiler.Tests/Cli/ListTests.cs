using PowerBasic.Compiler.Cli;

namespace PowerBasic.Compiler.Tests.Cli;

/// <summary>The <c>pbc --list</c> front-end path: compile a program and write a human-readable .LST map of the emitted image.</summary>
[TestFixture]
public sealed class ListTests {

  private static (int Code, string Out, string Err, string Lst) RunList(string source, string? outputName = null) {
    var path = Path.Combine(Path.GetTempPath(), $"pbc_lst_{Guid.NewGuid():N}.bas");
    var output = outputName ?? Path.ChangeExtension(path, ".LST");
    File.WriteAllText(path, source);
    try {
      var stdout = new StringWriter();
      var stderr = new StringWriter();
      var args = outputName == null ? new[] { "--list", path } : ["--list", "-O", output, path];
      var code = Driver.Run(args, stdout, stderr);
      var lst = File.Exists(output) ? File.ReadAllText(output) : "";
      return (code, stdout.ToString(), stderr.ToString(), lst);
    } finally {
      File.Delete(path);
      if (File.Exists(output))
        File.Delete(output);
    }
  }

  [Test]
  public void List_ForAProgramWithASubAndAFunction_ExitsZeroAndWritesADescriptiveListing() {
    // Given a program defining both a SUB and a FUNCTION
    var (code, stdout, err, lst) = RunList(
      "DECLARE FUNCTION Square%(BYVAL n%)\n" +
      "DECLARE SUB Greet()\n" +
      "FUNCTION Square%(BYVAL n%)\n  Square% = n% * n%\nEND FUNCTION\n" +
      "SUB Greet()\n  PRINT \"hi\"\nEND SUB\n" +
      "PRINT Square%(3)\n");

    // Then the listing is produced and names both procedures, a code size, and the dialect
    Assert.That(code, Is.EqualTo(0), err);
    Assert.That(lst, Is.Not.Empty);
    Assert.That(stdout, Does.Contain(".LST"));
    Assert.That(lst, Does.Contain("Square"));
    Assert.That(lst, Does.Contain("Greet"));
    Assert.That(lst, Does.Contain("FUNCTION"));
    Assert.That(lst, Does.Contain("SUB"));
    Assert.That(lst, Does.Contain("code     :"));
    Assert.That(lst, Does.Contain("PB 3.5"));
  }

  [Test]
  public void List_ForAUnit_ShowsExportsAndImports() {
    // Given a $COMPILE UNIT that exports a procedure and imports the runtime
    var (code, _, err, lst) = RunList(
      "$COMPILE UNIT\n" +
      "FUNCTION Twice%(BYVAL n%)\n  Twice% = n% + n%\nEND FUNCTION\n");

    // Then the listing labels the target as a unit and shows an exports section
    Assert.That(code, Is.EqualTo(0), err);
    Assert.That(lst, Does.Contain("PBU unit"));
    Assert.That(lst, Does.Contain("Exports"));
    Assert.That(lst, Does.Contain("Twice"));
    Assert.That(lst, Does.Contain("Imports"));
  }

  [Test]
  public void List_WithExplicitOutputName_WritesToThatPath() {
    // Given an explicit -O output name
    var output = Path.Combine(Path.GetTempPath(), $"pbc_lst_named_{Guid.NewGuid():N}.lst");
    var (code, stdout, err, lst) = RunList(
      "DECLARE FUNCTION Cube%(BYVAL n%)\n" +
      "FUNCTION Cube%(BYVAL n%)\n  Cube% = n% * n% * n%\nEND FUNCTION\n" +
      "PRINT Cube%(2)\n",
      output);

    // Then the listing is written to exactly that path
    Assert.That(code, Is.EqualTo(0), err);
    Assert.That(lst, Is.Not.Empty);
    Assert.That(stdout, Does.Contain(Path.GetFileName(output)));
    Assert.That(lst, Does.Contain("Cube"));
  }

  [Test]
  public void List_ForAProgramWithNoProcedures_StillProducesAHeaderAndExitsZero() {
    // Given a trivial program with no SUB/FUNCTION
    var (code, _, err, lst) = RunList("PRINT \"hello\"\n");

    // Then a listing with the header is still emitted
    Assert.That(code, Is.EqualTo(0), err);
    Assert.That(lst, Does.Contain("PB-Compiler listing"));
    Assert.That(lst, Does.Contain("Procedures"));
  }

  [Test]
  public void List_ForASourceWithASyntaxError_ReportsTheErrorAndExitsNonZero() {
    // Given a source that fails to bind
    var (code, _, err, lst) = RunList("PRINT Undeclared%(1)\nEND IF\n");

    // Then no listing is written and the error surfaces, like the other front-end paths
    Assert.That(code, Is.Not.EqualTo(0));
    Assert.That(err, Is.Not.Empty);
    Assert.That(lst, Is.Empty);
  }
}
