using PowerBasic.Compiler.Cli;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Cli;

[TestFixture]
public sealed class XBackendTests {

  [Test]
  public void Run_GivenLegacyEnableSwitch_ThenItRemainsACompatibilityNoOp() {
    var dir = Path.Combine(Path.GetTempPath(), "pbc-x-backend-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var source = Path.Combine(dir, "T.BAS");
      var ordinaryPath = Path.Combine(dir, "ORDINARY.EXE");
      var explicitPath = Path.Combine(dir, "EXPLICIT.EXE");
      File.WriteAllText(source, """
        A% = 0
        FOR I% = 1 TO 6
          A% = A% + I%
        NEXT I%
        A% = A% * 2
        PRINT A%
        END
        """);

      var ordinaryCode = Driver.Run(
        ["--optimize", "-O", ordinaryPath, source],
        new StringWriter(), new StringWriter());
      var explicitCode = Driver.Run(
        ["--optimize", "--x-backend", "-O", explicitPath, source],
        new StringWriter(), new StringWriter());

      Assert.Multiple(() => {
        Assert.That(ordinaryCode, Is.Zero);
        Assert.That(explicitCode, Is.Zero);
        Assert.That(File.ReadAllBytes(explicitPath), Is.EqualTo(File.ReadAllBytes(ordinaryPath)),
          "--x-backend is now only a compatibility spelling for the mandatory production path");
      });
      Assert.That(Cpu8086.Run(File.ReadAllBytes(ordinaryPath)).Output.Trim(), Is.EqualTo("42"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public void Run_GivenLegacyDisableSwitch_ThenItCannotReactivateTheDirectEmitter() {
    using var stderr = new StringWriter();

    var exitCode = Driver.Run(["--no-x-backend", "unused.bas"], TextWriter.Null, stderr);

    Assert.Multiple(() => {
      Assert.That(exitCode, Is.EqualTo(1));
      Assert.That(stderr.ToString(), Does.Contain("was removed"));
      Assert.That(stderr.ToString(), Does.Contain("IR/native backend is mandatory"));
    });
  }
}
