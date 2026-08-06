using System.Text;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The corpus-wide version of <see cref="BackendDifferentialTests"/>: every battery program compiled
/// BOTH ways, both images executed, and their observable behaviour compared - what they printed, and
/// what they wrote to any file they created.
///
/// This is the measurement that says whether the retargetable path produces the same program as the
/// direct emitter, rather than merely a program that assembles. It needs no vintage oracle: the golden
/// battery holds the DIRECT emitter to PBC 3.50, so for the IR path the direct emitter is the
/// reference, and the question is only whether the two agree.
///
/// The three outcomes are kept apart on purpose, because collapsing them is how a coverage number
/// starts lying:
/// <list type="bullet">
///   <item><b>agreed</b> - both images ran to completion and behaved identically.</item>
///   <item><b>not compared</b> - something declined to run (an opcode, console input, or another DOS
///     service the interpreter does not implement). Never counted as agreement.</item>
///   <item><b>disagreed</b> - both ran and behaved differently. Any of these is a miscompilation in
///     one of the two paths and fails the fixture.</item>
/// </list>
/// </summary>
[TestFixture]
public sealed class BackendCorpusDifferentialTests {

  private static readonly string _repoRoot =
    Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  private sealed record Behaviour(string Output, string Files, int ExitCode);

  private sealed record Disagreement(string Program, Behaviour Direct, Behaviour Routed, string Routed64);

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
      return new(cpu.Output, files.ToString(), cpu.ExitCode);
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
  /// Programs where the two back ends genuinely disagree, each with a diagnosis. EMPTY, and it should
  /// stay that way: an entry here is a defect that has been located, not one that has been excused.
  ///
  /// It had two, DIFF01 and DIFF55, on INT/FIX. Neither was a compiler defect - the TEST CPU ignored
  /// FLDCW, and INT and FIX are implemented by setting the x87 rounding mode and calling FRNDINT, so
  /// a CPU that always rounds to nearest turned INT(2.7) into 3. Both back ends were being judged by
  /// a reference that was wrong.
  /// </summary>
  private static readonly Dictionary<string, string> _known = new(StringComparer.OrdinalIgnoreCase);

  private static string Summarize(string reason) {
    var cut = reason.IndexOf(" at ", StringComparison.Ordinal);
    var head = cut > 0 ? reason[..cut] : reason;
    return head.Length > 64 ? head[..64] : head;
  }

  [Test]
  public void Corpus_WhenCompiledBothWaysAndRun_ThenTheBackEndAgreesWithTheDirectEmitter() {
    var dir = Path.Combine(_repoRoot, "tests");
    Assume.That(Directory.Exists(dir), "no tests/*.BAS corpus present");

    int agreed = 0, notCompared = 0, routedSomething = 0;
    var disagreements = new List<Disagreement>();
    var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
    var compileCases = new List<string>();

    foreach (var file in Directory.EnumerateFiles(dir, "*.BAS", SearchOption.AllDirectories)
               .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
      var name = Path.GetFileName(file);
      SemanticModel Bind() {
        var text = File.ReadAllText(file);
        return Binder.Bind(Parser.Parse(Lexer.Tokenize(text, name, Dialect.Pb36), name, Dialect.Pb36), Dialect.Pb36);
      }

      foreach (var optimize in new[] { true, false })
        Compare(optimize);
      continue;

      // Both optimization settings, because they are different emitters: with the optimizer off there
      // is no CSE, no SCCP, no register residency, and the direct path emits the plain AX-serial form.
      // A routed function has to agree with BOTH, and the shapes it must agree with are not the same.
      void Compare(bool optimize) {
      byte[] directImage, routedImage;
      IEnumerable<string> routedNames;
      try {
        var bound = Bind();
        if (bound.Errors.Count > 0)
          return;                                     // a program the front end rejects is not this test's business
        var direct = new CodeGenerator(Bind()) { Optimize = optimize, UseExperimentalBackend = false };
        var routed = new CodeGenerator(Bind()) { Optimize = optimize, UseExperimentalBackend = true };
        directImage = direct.EmitExecutable();
        routedImage = routed.EmitExecutable();
        routedNames = routed.BackendRoutedNames.ToList();
        if (direct.Errors.Count > 0 || routed.Errors.Count > 0)
          return;
      } catch (Exception e) {
        var reason = Summarize("compile: " + e.GetType().Name);
        reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
        compileCases.Add($"{Path.GetRelativePath(dir, file).Replace('\\', '/')} " +
          $"({(optimize ? "optimized" : "unoptimized")}): {e.GetType().Name}: {e.Message}");
        return;
      }

      // a program the back end takes nothing of compares the direct emitter with itself - true, but
      // it measures nothing, so it is not counted as agreement
      if (!routedNames.Any())
        return;
      ++routedSomething;

      var directRun = Observe(directImage, out var directWhy);
      var routedRun = Observe(routedImage, out var routedWhy);
      if (directRun is null || routedRun is null) {
        ++notCompared;
        var reason = Summarize(directRun is null ? "direct: " + directWhy : "routed: " + routedWhy);
        reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
        return;
      }

      if (directRun == routedRun)
        ++agreed;
      else
        disagreements.Add(new(name + (optimize ? " (optimized)" : " (unoptimized)"),
          directRun, routedRun, Convert.ToBase64String(routedImage)[..16]));
      }
    }

    var report = new StringBuilder()
      .AppendLine($"compilations the back end took part in  : {routedSomething} (each program is tried optimized AND unoptimized)")
      .AppendLine($"  ran both ways and AGREED             : {agreed}")
      .AppendLine($"  not compared (nothing ran)           : {notCompared}")
      .AppendLine($"  ran both ways and DISAGREED          : {disagreements.Count}")
      .AppendLine("why a comparison did not happen:");
    foreach (var (reason, count) in reasons.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(12))
      report.AppendLine($"  {count,5}  {reason}");
    foreach (var compileCase in compileCases)
      report.AppendLine($"         {compileCase}");
    foreach (var d in disagreements.Take(5))
      report.AppendLine($"DISAGREEMENT {d.Program}:{Difference(d.Direct, d.Routed)}");
    TestContext.Out.Write(report.ToString());

    // A baseline, not a blanket pass. Each entry is a KNOWN defect with a diagnosis; anything else
    // appearing here is a regression and fails immediately.
    // the program name carries "(optimized)" / "(unoptimized)"; a known defect is known both ways
    var unexpected = disagreements.Where(d => !_known.ContainsKey(d.Program.Split(' ')[0])).ToList();
    Assert.That(unexpected, Is.Empty,
      "the x86-16 back end and the direct emitter produce programs that behave differently:\n" + report);
    Assert.That(compileCases, Is.Empty,
      "a corpus compilation threw before the two back ends could be compared:\n" + report);
    // A floor, so a change that quietly stops routing things fails instead of passing with less
    // compared. 55 when the harness first ran both optimization modes, 57 once procedures with local
    // arrays became routable - the alloca layout and frame zeroing that had kept them out are fixed -
    // 61 once PRINT of a string variable had a runtime ABI entry (and the string-ownership copy that
    // entry needs to be safe), 65 once the back end could EMIT the ON ERROR handler rather than only
    // lower it, and 75 once loop unrolling joined the pipeline - a fully unrolled loop turns its
    // counter into a constant, which makes bodies selectable that were not before.
    // 78 once inlining joined the production pipeline - a call inlined is a callee body the caller's
    // optimizer can see, which makes module bodies selectable that were not.
    // 208 once constant QUAD printing could stage all four words and call PB's DOUBLE formatter.
    // 228 once materialized ordered x87 comparisons routed ten more programs in both optimization
    // modes; 222 execute in both paths and agree, while six remain outside the emulator's opcode set.
    // After signed 32-bit divide/remainder raised whole-body ownership by three, the corpus baseline
    // is 234 participants and 228 agreements; the same six cases remain outside the emulator.
    Assert.That(routedSomething, Is.GreaterThanOrEqualTo(234),
      "the back end participated in fewer compilations than it used to:\n" + report);
    Assert.That(agreed, Is.GreaterThanOrEqualTo(228),
      "fewer programs were compared than used to be:\n" + report);

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
  private static string Difference(Behaviour direct, Behaviour routed) {
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
      return $"\n  {what} differs at {at}:\n    direct: {Show(a, from, at)}\n    routed: {Show(b, from, at)}";
    }
    return Window(direct.Output, routed.Output, "output")
      + Window(direct.Files, routed.Files, "files")
      + (direct.ExitCode == routed.ExitCode ? "" : $"\n  exit code {direct.ExitCode} against {routed.ExitCode}");
  }

  private static string Escape(Behaviour behaviour) {
    static string OneLine(string text, int limit) {
      var flat = text.Replace((char)13, '|').Replace((char)10, '/');
      return flat.Length > limit ? flat[..limit] + "..." : flat;
    }
    return $"out[{OneLine(behaviour.Output, 200)}] files[{OneLine(behaviour.Files, 600)}] exit {behaviour.ExitCode}";
  }
}
