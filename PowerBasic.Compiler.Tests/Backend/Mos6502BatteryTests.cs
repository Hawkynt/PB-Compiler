using System.Diagnostics;
using System.Text.RegularExpressions;
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
public sealed partial class Mos6502BatteryTests {

  private static readonly string _repoRoot =
    Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  /// <summary>How many battery programs the 6502 compiled when this floor was last raised.</summary>
  private const int CompiledFloor = 4;

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

    var result = Cpu6502.RunC64Program(prg!);

    Assert.That(result.Returned, Is.True, $"{program} did not return to BASIC within the step budget");
    var expected = File.ReadAllText(Path.Combine(_repoRoot, "tests", Path.ChangeExtension(program, ".expected")));
    Assert.That(Normalize(result.Output), Is.EqualTo(Normalize(expected)));
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
  /// The same program on VICE, the reference C64 emulator, with the real KERNAL behind
  /// <c>CHROUT</c>: a tracepoint on <c>$FFD2</c> logs the accumulator at every call, which is the
  /// program's output one PETSCII byte at a time. It proves the start-up, the page-zero save and the
  /// return to BASIC on the machine they were written for, and that <see cref="Cpu6502"/> agrees
  /// with it. Skipped unless <c>x64sc</c> and <c>xvfb-run</c> are installed.
  /// </summary>
  [Test]
  public void Run_GivenAProgramOnVice_ThenTheRealKernalPrintsWhatTheInterpreterPrints() {
    Assume.That(OnPath("x64sc") && OnPath("xvfb-run"), "VICE or Xvfb is not installed");
    const string program = "CTRL.BAS";
    var prg = Compile(program, out var declined);
    Assert.That(prg, Is.Not.Null, declined);

    var work = Directory.CreateTempSubdirectory("pbc-6502-vice-");
    try {
      var path = Path.Combine(work.FullName, "PROG.PRG");
      var commands = Path.Combine(work.FullName, "trace.mon");
      var log = Path.Combine(work.FullName, "monitor.log");
      File.WriteAllBytes(path, prg!);
      File.WriteAllText(commands, "trace exec $ffd2\n");
      using (var vice = Process.Start(new ProcessStartInfo("xvfb-run", [
          "-a", "x64sc", "-default", "-warp", "-sounddev", "dummy", "-autostart", path,
          "-moncommands", commands, "-monlog", "-monlogname", log, "-limitcycles", "40000000"]) {
          RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        })!) {
        vice.StandardOutput.ReadToEndAsync();
        vice.StandardError.ReadToEndAsync();
        Assert.That(vice.WaitForExit(TimeSpan.FromMinutes(2)), Is.True, "VICE did not stop at its cycle limit");
      }

      // after the program, BASIC prints its own READY. prompt; the program's output is what precedes it
      var printed = PetsciiAfterCharsetSwitch(File.ReadAllText(log));
      var prompt = printed.LastIndexOf("ready.", StringComparison.Ordinal);
      Assert.That(prompt, Is.GreaterThanOrEqualTo(0), "the program never returned to BASIC");
      Assert.That(Normalize(printed[..prompt]), Is.EqualTo(Normalize(Cpu6502.RunC64Program(prg!).Output)));
    } finally {
      work.Delete(recursive: true);
    }
  }

  /// <summary>
  /// The accumulator at every traced <c>CHROUT</c>, decoded, from the program's switch to the
  /// lower-case character set on: what came before it is the autostart's own <c>LOAD</c> and <c>RUN</c>.
  /// </summary>
  private static string PetsciiAfterCharsetSwitch(string log) {
    var bytes = TraceAccumulator().Matches(log).Select(match => Convert.ToByte(match.Groups[1].Value, 16)).ToList();
    var start = bytes.LastIndexOf(0x0E) + 1;
    return string.Concat(bytes.Skip(start).Select(character => character switch {
      0x0D => "\n",
      >= 0x41 and <= 0x5A => ((char)(character + 0x20)).ToString(),
      >= 0xC1 and <= 0xDA => ((char)(character - 0x80)).ToString(),
      _ => ((char)character).ToString(),
    }));
  }

  [GeneratedRegex(@"\.C:ffd2 .*? A:([0-9A-Fa-f]{2})")]
  private static partial Regex TraceAccumulator();

  private static bool OnPath(string name)
    => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
      .Any(dir => dir.Length > 0 && File.Exists(Path.Combine(dir, name)));

  private static string Normalize(string text) =>
    string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(line => line.TrimEnd())).Trim('\n');
}
