namespace PowerBasic.Compiler.Tests;

[TestFixture]
public sealed class OracleIdeInputTests {
  [TestCase("pb21", "PB.EXE")]
  [TestCase("tb11", "TB.EXE")]
  public void Driver_GivenAnIdeWithSourceArguments_ThenLoadsTheSourceWithoutTypingItsFilename(
      string dialect, string compiler) {
    var root = Path.GetFullPath(Path.Combine(
      TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
    var commands = File.ReadAllLines(Path.Combine(root, "tests", "diff", dialect, "oracle.conf"))
      .Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
    var launch = commands.Single(line => line.StartsWith(compiler, StringComparison.OrdinalIgnoreCase));
    var keyboard = commands.Single(line => line.StartsWith("autotype ", StringComparison.OrdinalIgnoreCase));
    Assert.Multiple(() => {
      Assert.That(launch, Is.EqualTo(compiler + " T.BAS"), "the original IDE supports source autoload");
      Assert.That(keyboard, Does.Not.Contain(" t period b a s "), "do not reintroduce timed filename input");
      Assert.That(keyboard, Does.Not.Contain(" f3 "), "autoload must not reopen the source file dialog");
    });
  }
}
