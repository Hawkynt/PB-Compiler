using System.Text;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Scratch sweep driver: every .BAS under $PBC_PROBE_DIR compiled both ways, both images run, and the
/// WHOLE observation compared - stdout, the text screen, the cursor, the printer, every file on the
/// disk, and the exit code. Not part of the suite; it exists to drive a hunt.
/// </summary>
[TestFixture, Category("Probe")]
public sealed class Wave3SweepHarness {

  private sealed record Behaviour(string Output, string Screen, string Attributes, string Cursor, string Printer,
    string Files, int ExitCode);

  /// <summary>
  /// The ATTRIBUTE half of the text page. <see cref="Cpu8086.Screen"/> reads the character cells only,
  /// so a COLOR statement given the wrong arguments moves no character and is invisible to it - the
  /// same blind spot the screen comparison was added to close, one byte over.
  /// </summary>
  private static string Attributes(Cpu8086 cpu) {
    var sb = new StringBuilder();
    for (var cell = 0; cell < 80 * 25; ++cell)
      sb.Append(cpu.MemoryAt(0xB800, cell * 2 + 1).ToString("X2"));
    return sb.ToString();
  }

  private static Behaviour? Observe(byte[] image, IReadOnlyDictionary<string, byte[]> disk, out string why) {
    why = "";
    try {
      var cpu = Cpu8086.Run(image, disk, out var fault, maxSteps: 8_000_000);
      if (fault is not null) { why = fault.Message; return null; }
      var files = new StringBuilder();
      foreach (var name in cpu.FileNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        files.Append(name).Append('=').Append(Dump(cpu.FileBytes(name)!)).Append('\n');
      return new(cpu.Output, string.Join("\n", cpu.Screen), Attributes(cpu), cpu.Cursor.ToString(),
        cpu.PrinterOutput, files.ToString(), cpu.ExitCode);
    } catch (Cpu8086Exception e) {
      why = e.Message;
      return null;
    }
  }

  private static string Dump(byte[] bytes) {
    var sb = new StringBuilder();
    foreach (var b in bytes)
      sb.Append(b is >= 32 and < 127 ? ((char)b).ToString() : "<" + b.ToString("X2") + ">");
    return sb.ToString();
  }

  /// <summary>The dialects a probe declares with a <c>@dialect a,b</c> header comment; pb36 alone by default.</summary>
  private static IReadOnlyList<Dialect> DialectsOf(string text) {
    foreach (var line in text.Split('\n').Take(6)) {
      var at = line.IndexOf("@dialect ", StringComparison.OrdinalIgnoreCase);
      if (at < 0)
        continue;
      var named = line[(at + 9)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(name => Enum.TryParse<Dialect>(name, ignoreCase: true, out var d) ? d : (Dialect?)null)
        .OfType<Dialect>().ToList();
      if (named.Count > 0)
        return named;
    }
    return [Dialect.Pb36];
  }

  [Test]
  public void Sweep() {
    var dir = Environment.GetEnvironmentVariable("PBC_PROBE_DIR");
    Assume.That(!string.IsNullOrEmpty(dir) && Directory.Exists(dir), "no PBC_PROBE_DIR");

    var report = new StringBuilder();
    int ran = 0, routedCount = 0, declined = 0, agreed = 0, unmeasured = 0, disagreed = 0;

    foreach (var file in Directory.EnumerateFiles(dir!, "*.BAS", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)) {
      var name = Path.GetFileName(file);
      var text = File.ReadAllText(file);

      // any *.DAT sitting beside the probe is seeded on the disk under its own name
      var disk = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
      foreach (var seed in Directory.EnumerateFiles(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + ".*.SEED"))
        disk[Path.GetFileName(seed)[(Path.GetFileNameWithoutExtension(file).Length + 1)..^5]] = File.ReadAllBytes(seed);

      foreach (var dialect in DialectsOf(text))
      foreach (var optimize in new[] { true, false }) {
        SemanticModel Bind() => Binder.Bind(Parser.Parse(Lexer.Tokenize(text, name, dialect), name, dialect), dialect);
        var tag = $"{name} {dialect} {(optimize ? "O" : "-")}";
        ++ran;
        byte[] directImage, routedImage;
        List<string> routedNames;
        var declines = "";
        try {
          var bound = Bind();
          if (bound.Errors.Count > 0) { report.AppendLine($"BIND-ERROR {tag}: {string.Join("; ", bound.Errors)}"); continue; }
          var direct = new CodeGenerator(Bind()) { Optimize = optimize, UseExperimentalBackend = false };
          var routed = new CodeGenerator(Bind()) { Optimize = optimize, UseExperimentalBackend = true };
          directImage = direct.EmitExecutable();
          routedImage = routed.EmitExecutable();
          routedNames = routed.BackendRoutedNames.ToList();
          declines = string.Join("; ", routed.BackendDeclines.Select(d => d.Name + ": " + d.Reason));
          if (direct.Errors.Count > 0) { report.AppendLine($"DIRECT-ERROR {tag}: {string.Join("; ", direct.Errors)}"); continue; }
          if (routed.Errors.Count > 0) { report.AppendLine($"ROUTED-ERROR {tag}: {string.Join("; ", routed.Errors)}"); continue; }
        } catch (Exception e) {
          report.AppendLine($"THREW {tag}: {e.GetType().Name}: {e.Message}");
          continue;
        }

        if (routedNames.Count == 0) { ++declined; report.AppendLine($"DECLINED {tag}: {declines}"); continue; }
        ++routedCount;

        var directRun = Observe(directImage, disk, out var directWhy);
        var routedRun = Observe(routedImage, disk, out var routedWhy);
        if (directRun is null || routedRun is null) {
          ++unmeasured;
          report.AppendLine($"UNMEASURED {tag} [{string.Join(",", routedNames)}]: " +
            (directRun is null ? "direct: " + directWhy : "routed: " + routedWhy));
          continue;
        }

        if (directRun == routedRun) {
          ++agreed;
          report.AppendLine($"AGREE {tag} [{string.Join(",", routedNames)}]" +
            (declines.Length == 0 ? "" : " !{" + declines + "}") + $" out={Flat(directRun.Output, 90)}");
          continue;
        }

        ++disagreed;
        report.AppendLine($"DISAGREE {tag} [{string.Join(",", routedNames)}]");
        Show(report, "output", directRun.Output, routedRun.Output);
        Show(report, "screen", directRun.Screen, routedRun.Screen);
        Show(report, "attrib", directRun.Attributes, routedRun.Attributes);
        Show(report, "cursor", directRun.Cursor, routedRun.Cursor);
        Show(report, "printer", directRun.Printer, routedRun.Printer);
        Show(report, "files", directRun.Files, routedRun.Files);
        if (directRun.ExitCode != routedRun.ExitCode)
          report.AppendLine($"    exit  direct={directRun.ExitCode} routed={routedRun.ExitCode}");
      }
    }

    report.AppendLine($"== compilations {ran}  routed {routedCount}  declined {declined}  " +
      $"agreed {agreed}  unmeasured {unmeasured}  DISAGREED {disagreed}");
    if (Environment.GetEnvironmentVariable("PBC_PROBE_LOG") is { Length: > 0 } log)
      File.WriteAllText(log, report.ToString());
    TestContext.Out.Write(report.ToString());
    Assert.That(disagreed, Is.Zero, report.ToString());
  }

  private static string Flat(string text, int limit) {
    var flat = text.Replace((char)13, '|').Replace((char)10, '/');
    return flat.Length > limit ? flat[..limit] + "..." : flat;
  }

  private static void Show(StringBuilder report, string what, string a, string b) {
    if (a == b)
      return;
    report.AppendLine($"    {what}  direct={Flat(a, 400)}");
    report.AppendLine($"    {what}  routed={Flat(b, 400)}");
  }
}
