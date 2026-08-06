using System.Text;

namespace PowerBasic.Compiler.Tests.Syntax;

/// <summary>
/// Exports the same exhaustive statement-form matrix used by the in-process tests as isolated DOS
/// source files. <c>scripts/run-syntax-oracle-tests.sh</c> feeds those files to the genuine vintage
/// compilers and compares their accept/reject decision with <see cref="StatementSurface.ShouldAccept"/>.
///
/// The generated files live under build/: the matrix remains the single source of truth and 4,000+
/// mechanically generated files do not become repository ballast.
/// </summary>
[TestFixture]
public sealed class StatementSurfaceOracleMaterialTests {

  [Test]
  public void Export_GivenEveryFormAndDialect_ThenWritesACompleteOracleProbeManifest() {
    var root = RepositoryRoot();
    var output = Path.Combine(root, "build", "conformance", "syntax");
    if (Directory.Exists(output))
      Directory.Delete(output, recursive: true);
    Directory.CreateDirectory(output);

    var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    var manifest = new StringBuilder()
      .AppendLine("# dialect\tform\texpected\tsource");
    var rows = 0;
    var forms = StatementSurface.All.ToList();
    Assert.That(forms.Select(f => f.Id), Is.Unique, "oracle probe file names require unique form IDs");

    foreach (var dialect in StatementSurface.AllDialects) {
      var dialectName = dialect.CanonicalName();
      var directory = Path.Combine(output, dialectName);
      Directory.CreateDirectory(directory);

      foreach (var form in forms) {
        var fileName = form.Id + ".BAS";
        File.WriteAllText(Path.Combine(directory, fileName),
          StatementSurface.OracleProgram(form, dialect), utf8);
        manifest.Append(dialectName).Append('\t')
          .Append(form.Id).Append('\t')
          .Append(StatementSurface.ShouldAccept(form, dialect) ? "accept" : "reject").Append('\t')
          .Append(dialectName).Append('/').Append(fileName).AppendLine();
        ++rows;
      }

      foreach (var invalid in InvalidSyntaxSurfaceTests.Forms) {
        var id = "invalid." + invalid.Id;
        var fileName = id + ".BAS";
        var source = dialect.IsGwBasica()
          ? StatementSurface.NumberPhysicalLines(invalid.Source)
          : invalid.Source;
        File.WriteAllText(Path.Combine(directory, fileName),
          source.Replace("\n", "\r\n", StringComparison.Ordinal), utf8);
        manifest.Append(dialectName).Append('\t')
          .Append(id).Append("\treject\t")
          .Append(dialectName).Append('/').Append(fileName).AppendLine();
        ++rows;
      }
    }

    File.WriteAllText(Path.Combine(output, "manifest.tsv"), manifest.ToString(), utf8);
    Assert.That(rows, Is.EqualTo(
      (forms.Count + InvalidSyntaxSurfaceTests.Forms.Length) * StatementSurface.AllDialects.Length));
  }

  private static string RepositoryRoot() {
    var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
    while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PB-Compiler.slnx")))
      directory = directory.Parent;
    return directory?.FullName
      ?? throw new DirectoryNotFoundException("could not locate PB-Compiler.slnx");
  }
}
