using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PowerBasic.Compiler.Tests.Exec;

/// <summary>
/// Runs a <c>.PRG</c> on VICE, the reference C64 emulator, headless under Xvfb, with the real KERNAL
/// and a drive 8 that is a host directory. A tracepoint on the screen editor's output (<c>$E716</c>)
/// logs the accumulator at every character that reaches the screen, which is the program's output
/// one PETSCII byte at a time - bytes written to a file go to the drive instead and are not in it.
/// </summary>
public static partial class Vice {

  /// <summary>Whether <c>x64sc</c> and <c>xvfb-run</c> are installed.</summary>
  public static bool IsAvailable => OnPath("x64sc") && OnPath("xvfb-run");

  /// <summary>
  /// What <paramref name="prg"/> printed before BASIC's <c>READY.</c>, in ASCII with <c>\n</c> line
  /// ends and trailing blanks trimmed; fails the test when the program never returned to BASIC.
  /// </summary>
  public static string Run(byte[] prg) {
    var work = Directory.CreateTempSubdirectory("pbc-6502-vice-");
    try {
      var path = Path.Combine(work.FullName, "PROG.PRG");
      var commands = Path.Combine(work.FullName, "trace.mon");
      var log = Path.Combine(work.FullName, "monitor.log");
      File.WriteAllBytes(path, prg);
      // the screen editor's output, which CHROUT reaches only for the screen - not for a file
      File.WriteAllText(commands, "trace exec $e716\n");
      var disk = work.CreateSubdirectory("disk");
      string viceOutput;
      // a display number of its own, so a parallel run - or another Xvfb on this machine - never collides
      var display = Random.Shared.Next(200, 900).ToString(System.Globalization.CultureInfo.InvariantCulture);
      using (var vice = Process.Start(new ProcessStartInfo("xvfb-run", [
          "-a", "-n", display, "x64sc", "-default", "-warp", "-sounddev", "dummy", "-autostart", path,
          "-moncommands", commands, "-monlog", "-monlogname", log, "-autostartprgmode", "1", "-limitcycles", "150000000",
          // drive 8 is the host directory, answering on the serial bus as a 1541 would
          "-drive8type", "0", "-busdevice8", "-fs8", disk.FullName]) {
          RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        })!) {
        var stdout = vice.StandardOutput.ReadToEndAsync();
        var stderr = vice.StandardError.ReadToEndAsync();
        Assert.That(vice.WaitForExit(TimeSpan.FromMinutes(2)), Is.True, "VICE did not stop at its cycle limit");
        viceOutput = stdout.Result + stderr.Result;
      }

      // after the program, BASIC prints its own READY. prompt; the program's output is what precedes it
      var printed = PetsciiAfterCharsetSwitch(File.ReadAllText(log));
      var prompt = printed.LastIndexOf("ready.", StringComparison.Ordinal);
      Assert.That(prompt, Is.GreaterThanOrEqualTo(0), $"the program never returned to BASIC; VICE printed: {printed}\n{viceOutput}");
      return Normalize(printed[..prompt]);
    } finally {
      work.Delete(recursive: true);
    }
  }

  /// <summary>Line ends as <c>\n</c>, each line's trailing blanks and the blank lines around the text gone.</summary>
  public static string Normalize(string text) =>
    string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(line => line.TrimEnd())).Trim('\n');

  /// <summary>
  /// The accumulator at every traced screen output, decoded, from the program's switch to the
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

  [GeneratedRegex(@"\.C:e716 .*? A:([0-9A-Fa-f]{2})")]
  private static partial Regex TraceAccumulator();

  private static bool OnPath(string name)
    => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
      .Any(dir => dir.Length > 0 && File.Exists(Path.Combine(dir, name)));
}
