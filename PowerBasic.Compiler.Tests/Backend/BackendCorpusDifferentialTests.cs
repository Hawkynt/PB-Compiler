using System.Text;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The corpus-wide differential: every battery program compiled with the optimizer ON and OFF, both
/// images executed, and their observable behaviour compared - what they printed, what the screen
/// shows, and what they wrote to any file they created.
///
/// This is the measurement that says whether the optimizer preserves the program, rather than
/// merely producing one that assembles. It needs no vintage oracle: the unoptimized build is the
/// plain form of the same program - no CSE, no SCCP, no register residency, no inlining - so the
/// optimized one must behave exactly as it does.
///
/// The three outcomes are kept apart on purpose, because collapsing them is how a coverage number
/// starts lying:
/// <list type="bullet">
///   <item><b>agreed</b> - both images ran to completion and behaved identically.</item>
///   <item><b>not compared</b> - something declined to run (an opcode, console input, or another DOS
///     service the interpreter does not implement). Never counted as agreement.</item>
///   <item><b>disagreed</b> - both ran and behaved differently. Any of these is a miscompilation in
///     one of the two builds and fails the fixture.</item>
/// </list>
/// </summary>
[TestFixture, Category("Slow")]
public sealed class BackendCorpusDifferentialTests {

  private static readonly string _repoRoot =
    Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  /// <summary>
  /// What a run can be observed to have done. <c>Screen</c> is the 80x25 text page, and it is a
  /// separate observation rather than a nicety: <c>LOCATE</c>, <c>CLS</c> and a line wrap move
  /// characters around without changing one byte of the stream, so two builds that put the same text
  /// in different places agree about everything <c>Output</c> can be asked.
  /// </summary>
  private sealed record Behaviour(string Output, string Screen, string Files, int ExitCode);

  private sealed record Disagreement(string Program, Behaviour Optimized, Behaviour Unoptimized, string Optimized64);

  /// <summary>Everything a run can be observed to have done: what it printed, what it left in files, how it ended.</summary>
  private static Behaviour? Observe(byte[] image, out string why) {
    why = "";
    try {
      var cpu = Cpu8086.Run(image, maxSteps: 4_000_000);
      var files = new StringBuilder();
      foreach (var name in _fileNames) {
        if (cpu.FileContent(name) is { } content)
          files.Append(name).Append('=').Append(content).Append('\n');
      }
      return new(cpu.Output, string.Join("\n", cpu.Screen), files.ToString(), cpu.ExitCode);
    } catch (Cpu8086Exception e) {
      why = e.Message;
      return null;
    }
  }

  // the names the battery writes its results under; a file the program made under another name is not
  // compared, which is a gap in the comparison rather than a pass
  private static readonly string[] _fileNames =
    ["OUT.TXT", "RESULT.TXT", "T.TXT", "TEST.TXT", "TMP.TXT", "DATA.TXT", "O.TXT", "SCREEN.TXT"];

  /// <summary>
  /// Disagreements that are understood and open. Empty is the goal and currently the fact; an entry
  /// here is a known defect with a diagnosis, never a tolerated one, because the value of this fixture
  /// is that a NEW one fails the build.
  /// </summary>
  /// <summary>
  /// Programs whose two builds genuinely disagree, each with a diagnosis. EMPTY, and it should stay
  /// that way: an entry here is a defect that has been located, not one that has been excused.
  ///
  /// It once had two, DIFF01 and DIFF55, on INT/FIX. Neither was a compiler defect - the TEST CPU
  /// ignored FLDCW, and INT and FIX are implemented by setting the x87 rounding mode and calling
  /// FRNDINT, so a CPU that always rounds to nearest turned INT(2.7) into 3.
  /// </summary>
  private static readonly Dictionary<string, string> _known = new(StringComparer.OrdinalIgnoreCase);

  private static string Summarize(string reason) {
    var cut = reason.IndexOf(" at ", StringComparison.Ordinal);
    var head = cut > 0 ? reason[..cut] : reason;
    return head.Length > 64 ? head[..64] : head;
  }

  [Test]
  public void Corpus_WhenCompiledOptimizedAndUnoptimizedAndRun_ThenBothBuildsBehaveAlike() {
    var dir = Path.Combine(_repoRoot, "tests");
    Assume.That(Directory.Exists(dir), "no tests/*.BAS corpus present");

    int agreed = 0, notCompared = 0, participants = 0;
    var disagreements = new List<Disagreement>();
    var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
    var compileCases = new List<string>();
    var unroutedCases = new List<string>();
    var notComparedCases = new List<string>();

    foreach (var file in Directory.EnumerateFiles(dir, "*.BAS", SearchOption.AllDirectories)
               .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
      var name = Path.GetFileName(file);
      var relative = Path.GetRelativePath(dir, file).Replace('\\', '/');
      // Preprocessor.Expand, not Lexer.Tokenize: it is its own entry point, and it is what resolves
      // $INCLUDE and picks a $IF branch. Tokenizing the file directly spliced EVERY branch of a
      // conditional in and left every $INCLUDE unresolved, so WEIRD.BAS and DIFF10.BAS were compared
      // as programs neither compiler produces - and the two INCLUDE-using ones bound with errors and
      // were dropped by the "the front end rejects it" arm, silently, as though they had been measured.
      SemanticModel Bind()
        => Binder.Bind(Parser.Parse(Preprocessor.Expand(file, new FileSourceProvider(), Dialect.Pb36), name, Dialect.Pb36),
             Dialect.Pb36);

      byte[] optimizedImage, unoptimizedImage;
      bool routedBoth;
      try {
        if (Bind().Errors.Count > 0)
          continue;                                   // a program the front end rejects is not this test's business
        var optimized = new CodeGenerator(Bind()) { Optimize = true };
        var unoptimized = new CodeGenerator(Bind()) { Optimize = false };
        optimizedImage = optimized.EmitExecutable();
        unoptimizedImage = unoptimized.EmitExecutable();
        routedBoth = optimized.BackendRoutedNames.Any() && unoptimized.BackendRoutedNames.Any();
        if (optimized.Errors.Count > 0 || unoptimized.Errors.Count > 0)
          continue;
      } catch (Exception e) {
        var reason = Summarize("compile: " + e.GetType().Name);
        reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
        compileCases.Add($"{relative}: {e.GetType().Name}: {e.Message}");
        continue;
      }

      // a build the back end takes nothing of measures nothing about it, so it is not counted
      if (!routedBoth) {
        unroutedCases.Add(relative);
        continue;
      }
      ++participants;

      var optimizedRun = Observe(optimizedImage, out var optimizedWhy);
      var unoptimizedRun = Observe(unoptimizedImage, out var unoptimizedWhy);
      if (optimizedRun is null || unoptimizedRun is null) {
        ++notCompared;
        var reason = Summarize(optimizedRun is null ? "optimized: " + optimizedWhy : "unoptimized: " + unoptimizedWhy);
        reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
        notComparedCases.Add($"{relative}: {reason}");
        continue;
      }

      if (optimizedRun == unoptimizedRun)
        ++agreed;
      else
        disagreements.Add(new(name, optimizedRun, unoptimizedRun, Convert.ToBase64String(optimizedImage)[..16]));
    }

    var report = new StringBuilder()
      .AppendLine($"programs compiled optimized and unoptimized : {participants}")
      .AppendLine($"  ran both builds and AGREED                : {agreed}")
      .AppendLine($"  not compared (nothing ran)                : {notCompared}")
      .AppendLine($"  ran both builds and DISAGREED             : {disagreements.Count}")
      .AppendLine("why a comparison did not happen:");
    foreach (var (reason, count) in reasons.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(12))
      report.AppendLine($"  {count,5}  {reason}");
    foreach (var compileCase in compileCases)
      report.AppendLine($"         {compileCase}");
    foreach (var unroutedCase in unroutedCases)
      report.AppendLine($"UNROUTED {unroutedCase}");
    foreach (var notComparedCase in notComparedCases)
      report.AppendLine($"NOT COMPARED {notComparedCase}");
    foreach (var d in disagreements.Take(5))
      report.AppendLine($"DISAGREEMENT {d.Program}:{Difference(d.Optimized, d.Unoptimized)}");
    TestContext.Out.Write(report.ToString());

    // A baseline, not a blanket pass. Each entry is a KNOWN defect with a diagnosis; anything else
    // appearing here is a regression and fails immediately.
    var unexpected = disagreements.Where(d => !_known.ContainsKey(d.Program)).ToList();
    Assert.That(unexpected, Is.Empty,
      "the optimized and unoptimized builds produce programs that behave differently:\n" + report);
    Assert.That(compileCases, Is.Empty,
      "a corpus compilation threw before the two builds could be compared:\n" + report);
    // A floor, so a change that quietly stops compiling or running corpus programs fails instead of
    // passing with less compared. Each program counts once, for its optimized and unoptimized build:
    // 185 when the comparison became optimized against unoptimized, every one of them agreeing.
    Assert.That(participants, Is.GreaterThanOrEqualTo(185),
      "fewer corpus programs were compiled both ways than used to be:\n" + report);
    Assert.That(agreed, Is.GreaterThanOrEqualTo(185),
      "fewer programs were compared than used to be:\n" + report);
    Assert.That(notCompared, Is.Zero,
      "a participating corpus program stopped before its behavior could be compared:\n" + report);

    // and a known defect that quietly starts agreeing is worth knowing about too - it means either it
    // was fixed (delete the entry) or the comparison stopped reaching it (a worse problem)
    foreach (var (program, diagnosis) in _known)
      if (disagreements.All(d => d.Program != program))
        TestContext.Out.WriteLine($"NOTE {program} no longer disagrees - check whether this was fixed: {diagnosis}");
  }

  /// <summary>
  /// Where two runs first parted, with a window either side. A whole-output dump is unreadable for a
  /// program that prints thousands of numbers, and the useful question is always "which one first".
  /// </summary>
  private static string Difference(Behaviour optimized, Behaviour unoptimized) {
    static string Window(string a, string b, string what) {
      if (a == b)
        return "";
      var at = 0;
      while (at < a.Length && at < b.Length && a[at] == b[at])
        ++at;
      var from = Math.Max(0, at - 40);
      static string Show(string text, int from, int at) =>
        (from < text.Length ? text[from..Math.Min(text.Length, at + 40)] : "")
          .Replace((char)13, '|').Replace((char)10, '/');
      return $"\n  {what} differs at {at}:\n    optimized:   {Show(a, from, at)}\n    unoptimized: {Show(b, from, at)}";
    }
    return Window(optimized.Output, unoptimized.Output, "output")
      + Window(optimized.Screen, unoptimized.Screen, "screen")
      + Window(optimized.Files, unoptimized.Files, "files")
      + (optimized.ExitCode == unoptimized.ExitCode ? "" : $"\n  exit code {optimized.ExitCode} against {unoptimized.ExitCode}");
  }

  private static string Escape(Behaviour behaviour) {
    static string OneLine(string text, int limit) {
      var flat = text.Replace((char)13, '|').Replace((char)10, '/');
      return flat.Length > limit ? flat[..limit] + "..." : flat;
    }
    return $"out[{OneLine(behaviour.Output, 200)}] files[{OneLine(behaviour.Files, 600)}] exit {behaviour.ExitCode}";
  }
}
