using System.Text;
using PowerBasic.Compiler.Syntax;

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
        // A directive of this compiler's own is not a question a vintage compiler can answer: it
        // will reject it, correctly, and that rejection is the feature rather than a divergence.
        if (form.OwnExtension)
          continue;
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
        // Some invalidity is a property of ONE lineage's keywords rather than of BASIC: CALL DWORD
        // is a missing target where DWORD is a type, and an ordinary call where it is not.
        if (invalid.BorlandOnly && dialect.Family() == DialectFamily.Microsoft)
          continue;
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
    // Own-extension forms are deliberately absent: no oracle is asked about a directive this
    // compiler invented, so they are subtracted here rather than silently loosening the count.
    var borland = StatementSurface.AllDialects.Count(d => d.Family() == DialectFamily.Borland);
    var probed = forms.Count(f => !f.OwnExtension) + InvalidSyntaxSurfaceTests.Forms.Count(f => !f.BorlandOnly);
    Assert.That(rows, Is.EqualTo(probed * StatementSurface.AllDialects.Length
      + InvalidSyntaxSurfaceTests.Forms.Count(f => f.BorlandOnly) * borland));
  }

  private static string RepositoryRoot() {
    var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
    while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PB-Compiler.slnx")))
      directory = directory.Parent;
    return directory?.FullName
      ?? throw new DirectoryNotFoundException("could not locate PB-Compiler.slnx");
  }
}
