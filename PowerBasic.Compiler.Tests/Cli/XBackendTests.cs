using PowerBasic.Compiler.Cli;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Cli;

[TestFixture]
public sealed class XBackendTests {

  [TestCase("--x-backend")]
  [TestCase("--x-backend-strict")]
  public void Run_GivenLegacyEnableSwitch_ThenItIsRejected(string option) {
    using var stderr = new StringWriter();

    var exitCode = Driver.Run([option, "unused.bas"], TextWriter.Null, stderr);

    Assert.Multiple(() => {
      Assert.That(exitCode, Is.EqualTo(1));
      Assert.That(stderr.ToString(), Does.Contain(option));
      Assert.That(stderr.ToString(), Does.Contain("was removed"));
      Assert.That(stderr.ToString(), Does.Contain("IR/native backend is mandatory"));
    });
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
