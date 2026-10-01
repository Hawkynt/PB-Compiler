using PowerBasic.Compiler.Cli;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The DOS battery on the 6502: every <c>tests/*.BAS</c> with a golden <c>.expected</c> is compiled
/// with <c>--platform 6502</c>, run on <see cref="Cpu6502"/>, and compared with the output the
/// genuine DOS program prints. A program the back end declines is skipped with its reason, never
/// passed - the reasons are the 6502's to-do list - and <see cref="Coverage_GivenTheBattery_ThenItDoesNotShrink"/>
/// keeps the covered part from quietly getting smaller.
/// </summary>
[TestFixture]
public sealed class Mos6502BatteryTests {

  private static readonly string _repoRoot =
    Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  /// <summary>How many battery programs the 6502 compiled when this floor was last raised.</summary>
  private const int CompiledFloor = 18;

  public static IEnumerable<string> Programs() {
    var dir = Path.Combine(_repoRoot, "tests");
    if (!Directory.Exists(dir))
      yield break;
    foreach (var file in Directory.EnumerateFiles(dir, "*.BAS").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
      if (File.Exists(Path.ChangeExtension(file, ".expected")))
        yield return Path.GetFileName(file);
  }

  /// <summary>Compiles <paramref name="program"/> for the 6502; the PRG, or null with the reason it was declined.</summary>
  private static byte[]? Compile(string program, out string declined) {
    var work = Directory.CreateTempSubdirectory("pbc-6502-battery-");
    try {
      var output = Path.Combine(work.FullName, "PROG.PRG");
      var stderr = new StringWriter();
      var code = Driver.Run(["--dialect", "pb36", "--platform", "6502", "-O", output,
        Path.Combine(_repoRoot, "tests", program)], TextWriter.Null, stderr);
      declined = stderr.ToString().Trim();
      return code == 0 ? File.ReadAllBytes(output) : null;
    } finally {
      work.Delete(recursive: true);
    }
  }

  [TestCaseSource(nameof(Programs))]
  public void Run_GivenABatteryProgram_WhenCompiledForThe6502_ThenItPrintsTheGoldenOutput(string program) {
    var prg = Compile(program, out var declined);
    Assume.That(prg, Is.Not.Null, $"{program}: {declined}");

    var input = Path.Combine(_repoRoot, "tests", Path.ChangeExtension(program, ".IN"));
    var result = Cpu6502.RunC64Program(prg!, input: File.Exists(input) ? File.ReadAllText(input) : null);

    Assert.That(result.Returned, Is.True, $"{program} did not return to BASIC within the step budget");
    var expected = File.ReadAllText(Path.Combine(_repoRoot, "tests", Path.ChangeExtension(program, ".expected")));
    Assert.That(Vice.Normalize(result.Output), Is.EqualTo(Vice.Normalize(expected)));
  }

  [Test]
  public void Coverage_GivenTheBattery_ThenItDoesNotShrink() {
    var programs = Programs().ToList();
    Assume.That(programs, Is.Not.Empty, "no battery beside this checkout");

    var compiled = programs.Count(program => Compile(program, out _) is not null);

    Assert.That(compiled, Is.GreaterThanOrEqualTo(CompiledFloor),
      "a battery program the 6502 used to compile is declined again; raise the floor when coverage grows, never lower it");
  }

  /// <summary>
  /// The same program on VICE (<see cref="Vice"/>), with the real KERNAL and a host directory as
  /// drive 8. It proves the start-up, the page-zero save, the return to BASIC and the file routines on
  /// the machine they were written for, and that <see cref="Cpu6502"/>'s KERNAL and 1541 agree with
  /// it. Skipped unless <c>x64sc</c> and <c>xvfb-run</c> are installed.
  /// </summary>
  [TestCase("CTRL.BAS")]
  [TestCase("FILEIO1.BAS")]
  [TestCase("ONERR.BAS")]
  [TestCase("RANDFILE.BAS")]
  public void Run_GivenAProgramOnVice_ThenTheRealKernalPrintsWhatTheInterpreterPrints(string program) {
    Assume.That(Vice.IsAvailable, "VICE or Xvfb is not installed");
    var prg = Compile(program, out var declined);
    Assert.That(prg, Is.Not.Null, declined);

    Assert.That(Vice.Run(prg!), Is.EqualTo(Vice.Normalize(Cpu6502.RunC64Program(prg!).Output)));
  }
}
