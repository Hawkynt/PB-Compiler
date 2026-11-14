using System.Reflection;
using System.Text.RegularExpressions;
using PowerBasic.Compiler.Cli;

namespace PowerBasic.Compiler.Tests.Cli;

/// <summary>
/// <c>pbc --version</c>: the one line a toolchain lock or a bug report reads to know WHICH pbc ran -
/// the version of the build and, where the build knew it, the commit it was built from.
/// </summary>
[TestFixture]
public sealed class VersionTests {

  private static readonly string _informationalVersion =
    typeof(Driver).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

  private static (int Code, string Out, string Err) Run(params string[] args) {
    var stdout = new StringWriter();
    var stderr = new StringWriter();
    var code = Driver.Run(args, stdout, stderr);
    return (code, stdout.ToString(), stderr.ToString());
  }

  [Test]
  public void Version_WhenAskedAlone_ThenPrintsTheBuildVersionAndExitsZero() {
    var (code, output, err) = Run("--version");
    Assert.Multiple(() => {
      Assert.That(code, Is.EqualTo(0));
      Assert.That(output, Is.EqualTo($"pbc {_informationalVersion}{Environment.NewLine}"));
      Assert.That(err, Is.Empty);
    });
  }

  /// <summary>
  /// The version, then <c>+</c> and the full commit id when the build had one: the SDK writes the
  /// commit of a git checkout, and a build from a plain source tree states it with
  /// <c>-p:SourceRevisionId=&lt;commit&gt;</c>.
  /// </summary>
  [Test]
  public void Version_WhenPrinted_ThenIsTheVersionAndOptionallyTheFullCommit() {
    var (_, output, _) = Run("--version");
    Assert.That(output.TrimEnd(), Does.Match(@"^pbc \d+\.\d+\.\d+(\.\d+)?(\+[0-9a-f]{40})?$"));
  }

  [Test]
  public void Version_WhenGivenWithASourceFile_ThenPrintsTheVersionAndCompilesNothing() {
    var path = Path.Combine(Path.GetTempPath(), $"pbc_version_{Guid.NewGuid():N}.BAS");
    File.WriteAllText(path, "PRINT 1\n");
    try {
      var (code, output, _) = Run(path, "--version");
      Assert.Multiple(() => {
        Assert.That(code, Is.EqualTo(0));
        Assert.That(output, Does.StartWith("pbc "));
        Assert.That(File.Exists(Path.ChangeExtension(path, ".EXE")), Is.False);
      });
    } finally {
      File.Delete(path);
    }
  }

  [Test]
  public void Help_ListsTheVersionOption() {
    var (code, output, _) = Run("--help");
    Assert.Multiple(() => {
      Assert.That(code, Is.EqualTo(0));
      Assert.That(Regex.IsMatch(output, @"^\s+--version\s", RegexOptions.Multiline), Is.True, output);
    });
  }
}
