using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Cli;

/// <summary>Command-line front end for the PowerBASIC 3.5 compiler.</summary>
public static class Driver {

  public static int Run(string[] args, TextWriter stdout, TextWriter stderr) {
    if (args.Length == 0 || args is ["-h" or "--help" or "/?"]) {
      PrintUsage(stdout);
      return args.Length == 0 ? 1 : 0;
    }

    if (args[0].Equals("lib", StringComparison.OrdinalIgnoreCase))
      return RunLib(args[1..], stdout, stderr);

    string? source = null;
    string? output = null;
    var includePaths = new List<string>();
    var dumpStage = "";

    for (var i = 0; i < args.Length; ++i)
      switch (args[i]) {
        case "-O" or "-o" or "--output" when i + 1 < args.Length:
          output = args[++i];
          break;
        case "-I" or "--include" when i + 1 < args.Length:
          includePaths.Add(args[++i]);
          break;
        case "-G386":
          break; // accepted for PBC.EXE compatibility; 386 codegen is driven by $CPU
        case "--dump-tokens" or "--dump-ast" or "--dump-bind":
          dumpStage = args[i];
          break;
        case ['-', ..] when args[i] is not "-": // unknown switches are tolerated like PBC.EXE's
          break;
        default:
          if (source != null) {
            stderr.WriteLine($"pbc: more than one source file ('{source}', '{args[i]}')");
            return 1;
          }
          source = args[i];
          break;
      }

    if (source == null) {
      stderr.WriteLine("pbc: no source file");
      return 1;
    }
    if (!File.Exists(source)) {
      stderr.WriteLine($"pbc: source file '{source}' not found");
      return 1;
    }

    var sourceDir = Path.GetDirectoryName(Path.GetFullPath(source))!;
    var provider = new SearchPathSourceProvider([sourceDir, .. includePaths]);

    try {
      var tokens = Preprocessor.Expand(source, provider);

      if (dumpStage == "--dump-tokens") {
        foreach (var token in tokens)
          stdout.WriteLine(token);
        return 0;
      }

      var unit = Parser.Parse(tokens, source);
      if (dumpStage == "--dump-ast") {
        stdout.WriteLine($"{unit.Statements.Count} top-level statements");
        return 0;
      }

      var model = Binder.Bind(unit);
      foreach (var warning in model.Warnings)
        stderr.WriteLine($"warning: {warning}");
      if (!model.Success) {
        foreach (var error in model.Errors)
          stderr.WriteLine($"error: {error}");
        return 1;
      }
      if (dumpStage == "--dump-bind") {
        stdout.WriteLine($"{model.Procedures.Count} procedures, {model.ModuleVariables.Count} module variables, {model.Equates.Count} equates");
        return 0;
      }

      var generator = new CodeGenerator(model);
      var exe = generator.EmitExecutable();
      if (generator.Errors.Count > 0) {
        foreach (var error in generator.Errors)
          stderr.WriteLine($"error: {error}");
        return 1;
      }

      output ??= Path.ChangeExtension(source, ".EXE");
      File.WriteAllBytes(output, exe);
      stdout.WriteLine($"{Path.GetFileName(output)}: {exe.Length} bytes");
      return 0;
    } catch (Exception e) when (e is LexerException or PreprocessorException or ParserException) {
      stderr.WriteLine($"error: {e.Message}");
      return 1;
    }
  }

  /// <summary>pblib-style library maintenance: build a .PBL from .PBUs, or list contents.</summary>
  private static int RunLib(string[] args, TextWriter stdout, TextWriter stderr) {
    switch (args) {
      case ["build", var output, .. var unitFiles] when unitFiles.Length > 0: {
        var library = new PblFile();
        foreach (var file in unitFiles) {
          if (!File.Exists(file)) {
            stderr.WriteLine($"pbc lib: unit '{file}' not found");
            return 1;
          }
          using var stream = File.OpenRead(file);
          library.Units.Add(PbuFile.Read(stream));
        }
        using (var stream = File.Create(output))
          library.Write(stream);
        stdout.WriteLine($"{Path.GetFileName(output)}: {library.Units.Count} unit(s)");
        return 0;
      }

      case ["list", var file] when File.Exists(file): {
        using var stream = File.OpenRead(file);
        if (file.EndsWith(".PBU", StringComparison.OrdinalIgnoreCase)) {
          DescribeUnit(PbuFile.Read(stream), stdout);
          return 0;
        }
        foreach (var unit in PblFile.Read(stream).Units)
          DescribeUnit(unit, stdout);
        return 0;
      }

      default:
        stderr.WriteLine("usage: pbc lib build <out.PBL> <unit.PBU>...");
        stderr.WriteLine("       pbc lib list <file.PBL|file.PBU>");
        return 1;
    }
  }

  private static void DescribeUnit(PbuFile unit, TextWriter stdout) {
    stdout.WriteLine($"{unit.Name}: code={unit.Code.Length} data={unit.Data.Length} bss={unit.BssSize} cpu={unit.CpuFlags}");
    foreach (var e in unit.Exports)
      stdout.WriteLine($"  exports {(e.Kind == PbuExportKind.Function ? "FUNCTION" : "SUB")} {e.Name} @{e.CodeOffset:X4}");
    foreach (var i in unit.Imports)
      stdout.WriteLine($"  imports {i.Name}");
  }

  private static void PrintUsage(TextWriter w) {
    w.WriteLine("PB-Compiler - PowerBASIC 3.5 compatible compiler for 16-bit real-mode DOS");
    w.WriteLine();
    w.WriteLine("Usage: pbc [options] <source.BAS>");
    w.WriteLine("       pbc lib build <out.PBL> <unit.PBU>...");
    w.WriteLine("       pbc lib list <file.PBL|file.PBU>");
    w.WriteLine();
    w.WriteLine("Options:");
    w.WriteLine("  -O <file>      output file name (default: <source>.EXE)");
    w.WriteLine("  -I <dir>       additional $INCLUDE search directory");
    w.WriteLine("  -G386          allow 80386 instructions (PBC.EXE compatibility)");
    w.WriteLine("  --dump-tokens  stop after lexing/preprocessing and list tokens");
    w.WriteLine("  --dump-ast     stop after parsing");
    w.WriteLine("  --dump-bind    stop after semantic analysis");
    w.WriteLine("  -h, --help     show this help");
  }
}
