using PowerBasic.Compiler.Cli;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The idiom battery: each program in <c>tests/idioms</c> exercises one family of BASIC idioms, and
/// each must print on x86-32, x64 and the 6502 exactly what its DOS build prints on
/// <see cref="Cpu8086"/>. The DOS build is the oracle because it is the one held byte for byte to the
/// genuine compilers; both builds go through the driver, so the preprocessor and every pass are the
/// ones a user gets.
/// </summary>
[TestFixture]
public sealed class FlatTargetIdiomTests {

  private static string Root => Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../../tests/idioms"));

  private static IEnumerable<TestCaseData> Cases() {
    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../tests/idioms"));
    if (!Directory.Exists(root))
      yield break;
    foreach (var file in Directory.GetFiles(root, "*.BAS").Order())
      foreach (var platform in (string[])["x86-32", "x64", "6502"])
        yield return new TestCaseData(platform, Path.GetFileName(file))
          .SetName($"Run_GivenAnIdiomProgram_ThenItPrintsWhatDosPrints({platform}, {Path.GetFileName(file)})");
  }

  private static string Lines(string text)
    => string.Join("\n", Vice.Normalize(text).Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')
      .Select(line => line.TrimEnd(' ', '\x1A'))).TrimEnd('\n');

  private static string OnDos(string source) {
    var work = Directory.CreateTempSubdirectory("pbc-idiom-dos-");
    try {
      var path = Path.Combine(work.FullName, "PROG.BAS");
      File.WriteAllText(path, source);
      var stderr = new StringWriter();
      Assert.That(Driver.Run(["--dialect", "pb36", path], TextWriter.Null, stderr), Is.Zero, stderr.ToString());
      var exe = Directory.GetFiles(work.FullName).Single(f => f.EndsWith(".EXE", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".COM", StringComparison.OrdinalIgnoreCase));
      var cpu = Cpu8086.Run(File.ReadAllBytes(exe), new Dictionary<string, byte[]>(), out var fault, maxSteps: 400_000_000, exactFloatingPoint: true);
      Assert.That(fault, Is.Null, fault?.Message);
      return cpu.Output;
    } finally {
      work.Delete(recursive: true);
    }
  }

  [TestCaseSource(nameof(Cases))]
  public void Run_GivenAnIdiomProgram_ThenItPrintsWhatDosPrints(string platform, string name) {
    var source = File.ReadAllText(Path.Combine(Root, name));
    Assert.That(Lines(FlatTargets.Run(platform, source)), Is.EqualTo(Lines(OnDos(source))));
  }
}
