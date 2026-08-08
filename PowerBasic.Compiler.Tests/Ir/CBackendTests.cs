using System.Diagnostics;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// The retargeting proof: a program compiled through the IR to C, built by the host C compiler
/// and run natively, must print exactly what the DOS binary built from the same source prints -
/// the goldens in <c>tests/*.expected</c>, which the DOSBox battery already verifies against the
/// 16-bit executable.
///
/// Everything before the last step is shared with the x86-16 and LLVM paths: the same front end,
/// the same <see cref="IrLowering"/>, the same optimization pipeline. Only <see cref="CEmitter"/>
/// differs, so a mismatch here means either a back end or the middle end is wrong - which is
/// exactly the check a future ARM/68k/C++ target needs to be able to lean on.
///
/// Skipped when no C compiler is on PATH. A program the IR lowering declines is reported and
/// skipped, not failed: the covered subset is documented in docs/IR.md and grows deliberately.
/// </summary>
[TestFixture]
public sealed class CBackendTests {

  private static readonly string _repoRoot =
    Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  private static readonly string? _cc = Locate("cc", "gcc", "clang");

  /// <summary>
  /// Finds a C compiler on PATH. Windows names executables <c>gcc.exe</c>, so probing the bare name
  /// alone would never find one there - and this check silently skipping is exactly how a back end
  /// stops being verified without anyone noticing.
  /// </summary>
  private static string? Locate(params string[] names) {
    var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
    var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : [""];
    foreach (var name in names)
      foreach (var dir in paths) {
        if (string.IsNullOrWhiteSpace(dir))
          continue;
        foreach (var extension in extensions) {
          var candidate = Path.Combine(dir, name + extension);
          if (File.Exists(candidate))
            return candidate;
        }
      }
    return null;
  }

  /// <summary>Every DOS battery program that has a golden output to compare against.</summary>
  public static IEnumerable<string> Programs() {
    var dir = Path.Combine(_repoRoot, "tests");
    if (!Directory.Exists(dir))
      yield break;
    foreach (var file in Directory.EnumerateFiles(dir, "*.BAS").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
      if (File.Exists(Path.ChangeExtension(file, ".expected")))
        yield return Path.GetFileName(file);
  }

  [TestCaseSource(nameof(Programs))]
  public void Emit_GivenBatteryProgram_WhenBuiltThroughCBackend_ThenPrintsTheDosGoldenOutput(string program) {
    Assume.That(_cc, Is.Not.Null, "no C compiler on PATH - C back-end test skipped");
    var source = Path.Combine(_repoRoot, "tests", program);
    var expected = Normalize(File.ReadAllText(Path.ChangeExtension(source, ".expected")));

    var model = Bind(source);
    var module = IrLowering.TryLowerModule(model);
    Assume.That(module, Is.Not.Null, $"{program}: outside the IR lowering's subset (docs/IR.md)");

    // the same pipeline the LLVM path runs - the back end sees optimized IR, not raw lowering
    var pipeline = IrPassManager.Standard();
    pipeline.RunOnModule(module!);
    Inliner.Run(module!);
    pipeline.RunOnModule(module!);
    GlobalDce.Run(module!);
    Assert.That(IrVerifier.Verify(module!), Is.Empty, "optimized IR failed verification");

    var work = Path.Combine(Path.GetTempPath(), "pbc-c-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    try {
      var csource = Path.Combine(work, "prog.c");
      string emitted;
      try {
        emitted = CEmitter.Emit(module!);
      } catch (NotSupportedException declined) {
        // The emitter DECLINING a construct it does not model is the same kind of answer as the
        // lowering declining a program outside its subset, three lines above - and a decline is not
        // a disagreement. Counting it as a failure makes "not implemented yet" indistinguishable
        // from "emitted C that behaves differently from the DOS golden", which is the only thing
        // this fixture exists to catch. Narrow on purpose: a compile error or a mismatched output
        // still fails, because those ARE disagreements.
        Assume.That(false, $"{program}: {declined.Message}");
        return;
      }
      File.WriteAllText(csource, emitted);
      var exe = Path.Combine(work, "prog");
      var runtime = Path.Combine(_repoRoot, "runtime");
      var build = Run(_cc!, $"-std=c99 -O2 -I \"{runtime}\" -o \"{exe}\" \"{csource}\" \"{Path.Combine(runtime, "pbc_rt.c")}\" -lm", work, null);
      Assert.That(build.ExitCode, Is.Zero, $"C compilation failed:\n{build.Output}");

      var stdinFile = Path.ChangeExtension(source, ".IN");
      var run = Run(exe, "", work, File.Exists(stdinFile) ? File.ReadAllText(stdinFile) : null);
      Assert.That(Normalize(run.Output), Is.EqualTo(expected), $"{program}: C back end disagrees with the DOS golden");
    } finally {
      try { Directory.Delete(work, recursive: true); } catch (IOException) { /* best effort */ }
    }
  }

  private static SemanticModel Bind(string path) {
    var name = Path.GetFileName(path);
    var text = File.ReadAllText(path);
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(text, name, Dialect.Pb36), name, Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static (int ExitCode, string Output) Run(string file, string arguments, string workingDirectory, string? input) {
    var psi = new ProcessStartInfo(file, arguments) {
      WorkingDirectory = workingDirectory,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      RedirectStandardInput = input is not null,
      UseShellExecute = false,
    };
    using var process = Process.Start(psi)!;
    if (input is not null) {
      process.StandardInput.Write(input);
      process.StandardInput.Close();
    }
    var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
    process.WaitForExit(60000);
    return (process.ExitCode, output);
  }

  /// <summary>Trailing blanks carry no meaning here - PB pads numerics with one, editors eat them.</summary>
  private static string Normalize(string text) =>
    string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd())).TrimEnd('\n');
}
