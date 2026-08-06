using PowerBasic.Compiler.Cli;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Cli;

[TestFixture]
public sealed class XBackendTests {

  /// <summary>
  /// Given the documented switch, when a routable program is compiled, then the CLI must actually
  /// select the IR/x86-16 path rather than silently accepting and ignoring the option.
  /// </summary>
  [Test]
  public void Run_GivenXBackendSwitch_ThenItEmitsTheRoutedExecutable() {
    var dir = Path.Combine(Path.GetTempPath(), "pbc-x-backend-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var source = Path.Combine(dir, "T.BAS");
      var directPath = Path.Combine(dir, "DIRECT.EXE");
      var routedPath = Path.Combine(dir, "ROUTED.EXE");
      File.WriteAllText(source, """
        A% = 0
        FOR I% = 1 TO 6
          A% = A% + I%
        NEXT I%
        A% = A% * 2
        PRINT A%
        END
        """);

      var directCode = Driver.Run(
        ["--optimize", "--no-x-backend", "-O", directPath, source],
        new StringWriter(), new StringWriter());
      var routedCode = Driver.Run(
        ["--optimize", "--x-backend", "-O", routedPath, source],
        new StringWriter(), new StringWriter());

      Assert.Multiple(() => {
        Assert.That(directCode, Is.Zero);
        Assert.That(routedCode, Is.Zero);
        Assert.That(File.ReadAllBytes(routedPath), Is.Not.EqualTo(File.ReadAllBytes(directPath)),
          "the documented switch was ignored");
      });
      Assert.That(Cpu8086.Run(File.ReadAllBytes(routedPath)).Output.Trim(), Is.EqualTo("42"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
