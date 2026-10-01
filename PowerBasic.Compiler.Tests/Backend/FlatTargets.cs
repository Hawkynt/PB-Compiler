using System.Diagnostics;
using PowerBasic.Compiler.Cli;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.CodeGen;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Runs one pb36 program on a platform and answers what it printed: <c>x86-32</c> and <c>x64</c> as a
/// static ELF in a directory of its own (files it writes land there), <c>6502</c> on
/// <see cref="Cpu6502"/> with an empty 1541, and <c>dos</c> compiled for DOS and run under DOSBox -
/// the oracle the other three are held to. A missing emulator or a host that cannot run the
/// program skips the test.
/// </summary>
public static class FlatTargets {

  public static readonly string[] Platforms = ["x86-32", "x64", "6502", "dos"];

  public static string Run(string platform, string source) => platform == "dos" ? RunOnDos(source) : RunOn(platform, source);

  private static string RunOn(string platform, string source) {
    var work = Directory.CreateTempSubdirectory("pbc-flat-");
    try {
      var path = Path.Combine(work.FullName, "PROG.BAS");
      File.WriteAllText(path, source);
      var stderr = new StringWriter();
      var code = Driver.Run(["--dialect", "pb36", "--platform", platform, path], TextWriter.Null, stderr);
      Assert.That(code, Is.Zero, stderr.ToString());
      if (platform == "6502") {
        var result = Cpu6502.RunC64Program(File.ReadAllBytes(Path.ChangeExtension(path, ".PRG")));
        Assert.That(result.Returned, Is.True, "the program returns to BASIC");
        return result.Output;
      }
      Assume.That(OperatingSystem.IsLinux(), "the executables are Linux programs");
      using var process = Process.Start(new ProcessStartInfo(Path.ChangeExtension(path, null)) {
        RedirectStandardOutput = true, UseShellExecute = false, WorkingDirectory = work.FullName,
      })!;
      var output = process.StandardOutput.ReadToEnd();
      process.WaitForExit();
      return output;
    } finally {
      work.Delete(recursive: true);
    }
  }

  private static string RunOnDos(string source) {
    var tokens = Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(Parser.Parse(tokens, "TEST.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return DosBoxRunner.Run(exe);
  }
}
