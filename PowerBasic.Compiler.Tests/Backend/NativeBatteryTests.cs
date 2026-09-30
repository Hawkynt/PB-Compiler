using System.Diagnostics;
using PowerBasic.Compiler.Cli;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The DOS battery as native Linux programs: every <c>tests/*.BAS</c> with a golden <c>.expected</c>
/// is compiled with <c>--platform x86-32</c> and <c>--platform x64</c>, run, and compared with what
/// the genuine DOS program prints. A program the native back end or the portable runtime declines is
/// skipped with its reason - the to-do list - and <see cref="Coverage_GivenTheBattery_ThenItDoesNotShrink"/>
/// holds a floor under how many compile.
/// </summary>
[TestFixture]
public sealed class NativeBatteryTests {

  private static readonly string _repoRoot =
    Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  /// <summary>How many battery programs each machine compiled when this floor was last raised.</summary>
  private const int CompiledFloor = 13;

  private static readonly string[] _machines = ["x86-32", "x64"];

  public static IEnumerable<TestCaseData> Programs() {
    var dir = Path.Combine(_repoRoot, "tests");
    if (!Directory.Exists(dir))
      yield break;
    foreach (var file in Directory.EnumerateFiles(dir, "*.BAS").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
      if (File.Exists(Path.ChangeExtension(file, ".expected")))
        foreach (var machine in _machines)
          yield return new TestCaseData(machine, Path.GetFileName(file));
  }

  private static string? Compile(string machine, string program, string directory, out string declined) {
    var output = Path.Combine(directory, "prog");
    var stderr = new StringWriter();
    var code = Driver.Run(["--dialect", "pb36", "--platform", machine, "-O", output,
      Path.Combine(_repoRoot, "tests", program)], TextWriter.Null, stderr);
    declined = stderr.ToString().Trim();
    return code == 0 ? output : null;
  }

  [TestCaseSource(nameof(Programs))]
  public void Run_GivenABatteryProgram_WhenCompiledNatively_ThenItPrintsTheGoldenOutput(string machine, string program) {
    Assume.That(OperatingSystem.IsLinux(), "the programs are Linux executables");
    var work = Directory.CreateTempSubdirectory("pbc-native-");
    try {
      var executable = Compile(machine, program, work.FullName, out var declined);
      Assume.That(executable, Is.Not.Null, $"{program} ({machine}): {declined}");

      using var process = Process.Start(new ProcessStartInfo(executable!) {
        RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false, WorkingDirectory = work.FullName,
      })!;
      process.StandardInput.Close();
      var output = process.StandardOutput.ReadToEnd();
      Assert.That(process.WaitForExit(TimeSpan.FromSeconds(30)), Is.True, $"{program} did not finish");

      var expected = File.ReadAllText(Path.Combine(_repoRoot, "tests", Path.ChangeExtension(program, ".expected")));
      Assert.That(Normalize(output), Is.EqualTo(Normalize(expected)));
    } finally {
      work.Delete(recursive: true);
    }
  }

  [TestCaseSource(nameof(_machines))]
  public void Coverage_GivenTheBattery_ThenItDoesNotShrink(string machine) {
    var programs = Programs().Select(data => (string)data.Arguments[1]!).Distinct().ToList();
    Assume.That(programs, Is.Not.Empty, "no battery beside this checkout");
    var work = Directory.CreateTempSubdirectory("pbc-native-coverage-");
    try {
      var compiled = programs.Count(program => Compile(machine, program, work.FullName, out _) is not null);
      Assert.That(compiled, Is.GreaterThanOrEqualTo(CompiledFloor),
        "a battery program that used to compile natively is declined again; raise the floor when coverage grows, never lower it");
    } finally {
      work.Delete(recursive: true);
    }
  }

  private static string Normalize(string text) =>
    string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(line => line.TrimEnd())).Trim('\n');
}
