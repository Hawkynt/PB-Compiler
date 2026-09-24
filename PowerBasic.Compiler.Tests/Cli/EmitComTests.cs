using PowerBasic.Compiler.Cli;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Cli;

[TestFixture]
public sealed class EmitComTests {

  [Test]
  public void Compile_GivenCompileCom_ThenCliWritesFlatExecutableThatRuns() {
    var dir = Path.Combine(Path.GetTempPath(), "pbc-com-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var source = Path.Combine(dir, "HELLO.BAS");
      File.WriteAllText(source, "$COMPILE COM\nPRINT \"COM OK\"\nEND\n");
      var stdout = new StringWriter();
      var stderr = new StringWriter();

      var code = Driver.Run(["--dialect", "pb36", source], stdout, stderr);
      var output = Path.ChangeExtension(source, ".COM");

      Assert.Multiple(() => {
        Assert.That(code, Is.EqualTo(0), stderr.ToString());
        Assert.That(File.Exists(output), Is.True, "CLI did not create the default .COM output");
      });
      var bytes = File.ReadAllBytes(output);
      Assert.That(bytes.Length, Is.GreaterThan(0));
      Assert.That(bytes.Length >= 2 && bytes[0] == (byte)'M' && bytes[1] == (byte)'Z', Is.False,
        "COM output must be a flat image, not an MZ file with a different extension");
      Assert.That(Cpu8086.Run(bytes).Output.Trim(), Is.EqualTo("COM OK"));
    } finally {
      try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }
  }

  [Test]
  public void EmitCom_GivenLinkDirective_ThenCliRejectsUnrelocatableExternalLinking() {
    var dir = Path.Combine(Path.GetTempPath(), "pbc-com-link-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var source = Path.Combine(dir, "BAD.BAS");
      File.WriteAllText(source, "$COMPILE COM\n$LINK \"X.PBU\"\nEND\n");
      var stdout = new StringWriter();
      var stderr = new StringWriter();

      var code = Driver.Run(["--dialect", "pb36", source], stdout, stderr);

      Assert.Multiple(() => {
        Assert.That(code, Is.EqualTo(1));
        Assert.That(stderr.ToString(), Does.Contain("COM output cannot use $LINK"));
      });
    } finally {
      try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }
  }
}
