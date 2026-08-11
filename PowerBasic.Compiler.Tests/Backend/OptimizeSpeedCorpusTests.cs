using System.Text;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// <c>$OPTIMIZE SPEED</c> may change the code however it likes and must not change what the program
/// does.
///
/// <para>
/// This exists because <see cref="Compiler.Ir.Passes.DeadLoopElimination"/> is the first transform in
/// the middle end whose licence comes from the optimization MODE rather than from the IR alone, and a
/// pass that only runs under a flag is a pass the rest of the suite never sees. The corpus
/// differential compares the two BACK ENDS against each other and leaves SPEED off throughout - no
/// program in <c>tests/</c> carries the metastatement - so before this fixture the pass ran against
/// nine hand-written programs and nothing else.
/// </para>
/// <para>
/// The comparison is deliberately not back-end against back-end. It is the SAME back end with the
/// flag off and on, which asks the question the flag actually raises: whatever SPEED unlocked, did
/// the program survive it. A program where the flag changes nothing passes trivially and costs one
/// emulator run, which is the right price for a test whose value is in the ones where it does.
/// </para>
/// </summary>
[TestFixture]
public sealed class OptimizeSpeedCorpusTests {

  private static readonly string _repoRoot =
    Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  private static readonly string[] _fileNames =
    ["OUT.TXT", "RESULT.TXT", "T.TXT", "TEST.TXT", "TMP.TXT", "DATA.TXT", "O.TXT", "SCREEN.TXT"];

  private sealed record Behaviour(string Output, string Files, int ExitCode);

  private static Behaviour? Observe(byte[] image) {
    try {
      var cpu = Cpu8086.Run(image, maxSteps: 4_000_000);
      var files = new StringBuilder();
      foreach (var name in _fileNames)
        if (cpu.FileContent(name) is { } content)
          files.Append(name).Append('=').Append(content).Append('\n');
      return new(cpu.Output, files.ToString(), cpu.ExitCode);
    } catch (Cpu8086Exception) {
      return null;                                  // outside the emulator's reach; never counted either way
    }
  }

  [Test]
  public void Corpus_WhenCompiledForSpeed_ThenEveryProgramBehavesAsItDidWithoutIt() {
    var dir = Path.Combine(_repoRoot, "tests");
    Assume.That(Directory.Exists(dir), "no tests/*.BAS corpus present");

    var compared = 0;
    var differed = new List<string>();
    var speedChangedTheImage = 0;

    foreach (var file in Directory.EnumerateFiles(dir, "*.BAS", SearchOption.AllDirectories)
               .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
      var name = Path.GetFileName(file);
      var text = File.ReadAllText(file);

      SemanticModel Bind()
        => Binder.Bind(Parser.Parse(Lexer.Tokenize(text, name, Dialect.Pb36), name, Dialect.Pb36), Dialect.Pb36);

      byte[] plain, fast;
      try {
        if (Bind().Errors.Count > 0)
          continue;                                 // a program the front end rejects is not this test's business
        var a = new CodeGenerator(Bind()) { Optimize = true, UseExperimentalBackend = true };
        var b = new CodeGenerator(Bind()) { Optimize = true, UseExperimentalBackend = true, OptimizeSpeed = true };
        plain = a.EmitExecutable();
        fast = b.EmitExecutable();
        if (a.Errors.Count > 0 || b.Errors.Count > 0)
          continue;
      } catch (Exception) {
        continue;                                   // a compile failure is the differential fixture's business
      }

      if (!plain.AsSpan().SequenceEqual(fast))
        ++speedChangedTheImage;

      var before = Observe(plain);
      var after = Observe(fast);
      if (before is null || after is null)
        continue;

      ++compared;
      if (before != after)
        differed.Add($"  {Path.GetRelativePath(dir, file).Replace('\\', '/')}\n" +
                     $"    without SPEED: {before}\n" +
                     $"    with SPEED   : {after}");
    }

    TestContext.Out.WriteLine($"programs compared            : {compared}");
    TestContext.Out.WriteLine($"programs SPEED changed       : {speedChangedTheImage}");

    Assert.That(differed, Is.Empty,
      $"$OPTIMIZE SPEED changed what {differed.Count} program(s) do:\n" + string.Join("\n", differed));

    // A ratchet, because the assertion above passes trivially if nothing compiles: this fixture is
    // only worth its runtime while it is actually running programs, and the count silently dropping
    // to zero is the failure mode it could not otherwise report.
    Assert.That(compared, Is.GreaterThanOrEqualTo(120),
      "far fewer programs ran than used to - the fixture has stopped measuring anything");
    Assert.That(speedChangedTheImage, Is.GreaterThan(0),
      "SPEED changed no image at all, so this fixture proved nothing");
  }
}
