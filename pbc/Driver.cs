using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
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
    var linkPaths = new List<string>();
    var dumpStage = "";
    var dialect = Dialect.Pb35;
    var checkBounds = false;
    var checkNumeric = false;
    var checkOverflow = false;
    var checkStack = false;
    var optimizeSpeed = false;
    bool? optimize = null; // null = dialect default (on for pb36); --optimize/--no-optimize override

    for (var i = 0; i < args.Length; ++i)
      switch (args[i]) {
        case "-O" or "-o" or "--output" when i + 1 < args.Length:
          output = args[++i];
          break;
        case "-I" or "--include" when i + 1 < args.Length:
          includePaths.Add(args[++i]);
          break;
        case "-L" or "--linkdir" when i + 1 < args.Length:
          linkPaths.Add(args[++i]);
          break;
        case "--dialect" when i + 1 < args.Length: {
          var name = args[++i];
          if (!TryParseDialect(name, out dialect)) {
            stderr.WriteLine($"pbc: unknown dialect '{name}' (use tb10|tb11|pb20|..|pb36|qb10|..|qb45|pds70|pds71)");
            return 1;
          }
          break;
        }
        case "-G386":
          break; // accepted for PBC.EXE compatibility; 386 codegen is driven by $CPU
        case "-EB":
          checkBounds = true;
          break;
        case "-EN":
          checkNumeric = true;
          break;
        case "-EO":
          checkOverflow = true;
          break;
        case "-ES":
          checkStack = true;
          break;
        case "-OZF":
          optimizeSpeed = true;
          break;
        case "--optimize":
          optimize = true; // enable the (dialect-agnostic) optimizer for any dialect
          break;
        case "--no-optimize":
          optimize = false; // the pb35-faithful escape hatch, even for pb36
          break;
        case "--dump-tokens" or "--dump-ast" or "--dump-bind" or "--emit-llvm" or "--emit-obj":
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
      var tokens = Preprocessor.Expand(source, provider, dialect);

      if (dumpStage == "--dump-tokens") {
        foreach (var token in tokens)
          stdout.WriteLine(token);
        return 0;
      }

      var unit = Parser.Parse(tokens, source, dialect);
      if (dumpStage == "--dump-ast") {
        stdout.WriteLine($"{unit.Statements.Count} top-level statements");
        return 0;
      }

      var model = Binder.Bind(unit, dialect);
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

      if (dumpStage == "--emit-llvm") {
        var module = IrLowering.TryLowerModule(model);
        if (module is null) {
          stderr.WriteLine("pbc: --emit-llvm: this program uses constructs the IR lowering does not yet support (strings, dynamic arrays, GOTO/GOSUB, I/O, intrinsics)");
          return 1;
        }
        var pipeline = IrPassManager.Standard();
        pipeline.RunOnModule(module);
        Inliner.Run(module);
        pipeline.RunOnModule(module);              // re-optimize the inlined bodies
        var verifyErrors = IrVerifier.Verify(module);
        if (verifyErrors.Count > 0) {
          stderr.WriteLine("pbc: --emit-llvm: internal error, optimized IR failed verification:");
          foreach (var e in verifyErrors)
            stderr.WriteLine("  " + e);
          return 1;
        }
        var ll = LlvmEmitter.Emit(module, "x86_64-unknown-linux-gnu");
        if (output != null) {
          File.WriteAllText(output, ll);
          stdout.WriteLine($"{Path.GetFileName(output)}: {ll.Length} bytes of LLVM IR");
        } else {
          stdout.Write(ll);
        }
        return 0;
      }

      var generator = new CodeGenerator(model) {
        CheckBounds = checkBounds,    // -EB
        CheckNumeric = checkNumeric,  // -EN
        CheckOverflow = checkOverflow,// -EO
        CheckStack = checkStack,      // -ES
        OptimizeSpeed = optimizeSpeed,// -OZF
      };
      if (optimize is { } opt)        // --optimize / --no-optimize override the dialect default
        generator.Optimize = opt;

      if (dumpStage == "--emit-obj") {
        // emit the program's procedures as a linkable Intel OMF object, so C/asm/foreign
        // linkers can consume PB output - regardless of $COMPILE UNIT (docs/LINKER.md)
        var unitName = Path.GetFileNameWithoutExtension(source).ToUpperInvariant();
        var compiledUnit = generator.EmitUnit(unitName);
        if (generator.Errors.Count > 0) {
          foreach (var error in generator.Errors)
            stderr.WriteLine($"error: {error}");
          return 1;
        }
        var obj = Emit.Omf.OmfWriter.WriteObject(compiledUnit);
        output ??= Path.ChangeExtension(source, ".OBJ");
        File.WriteAllBytes(output, obj);
        stdout.WriteLine($"{Path.GetFileName(output)}: {obj.Length} bytes");
        return 0;
      }

      byte[] artifact;
      if (IsUnitCompile(model)) {
        var unitName = Path.GetFileNameWithoutExtension(source).ToUpperInvariant();
        var compiledUnit = generator.EmitUnit(unitName);
        output ??= Path.ChangeExtension(source, ".PBU");
        using var buffer = new MemoryStream();
        compiledUnit.Write(buffer);
        artifact = buffer.ToArray();
      } else {
        if (!TryLoadLinkTargets(model, [.. linkPaths, sourceDir], stderr, out var units, out var libraries))
          return 1;
        artifact = generator.EmitExecutable(units, libraries);
        // $COMPILE CHAIN: same MZ image, .PBC extension (our own chain artifact)
        var isChain = model.MetaStatements.Any(m => m.Command == "COMPILE"
          && m.Arguments is [{ } chainTarget, ..] && chainTarget.Text.Equals("CHAIN", StringComparison.OrdinalIgnoreCase));
        output ??= Path.ChangeExtension(source, isChain ? ".PBC" : ".EXE");
      }

      if (generator.Errors.Count > 0) {
        foreach (var error in generator.Errors)
          stderr.WriteLine($"error: {error}");
        return 1;
      }

      File.WriteAllBytes(output, artifact);
      stdout.WriteLine($"{Path.GetFileName(output)}: {artifact.Length} bytes");
      return 0;
    } catch (Exception e) when (e is LexerException or PreprocessorException or ParserException) {
      stderr.WriteLine($"error: {e.Message}");
      return 1;
    }
  }

  private static bool TryParseDialect(string name, out Dialect dialect) {
    switch (name.ToLowerInvariant()) {
      case "basica": dialect = Dialect.Basica; return true;
      case "gw": case "gwbasic": dialect = Dialect.Gw; return true;
      case "qbasic": dialect = Dialect.Qbasic; return true;
      case "qb10": dialect = Dialect.Qb10; return true;
      case "qb20": dialect = Dialect.Qb20; return true;
      case "qb30": dialect = Dialect.Qb30; return true;
      case "qb40": dialect = Dialect.Qb40; return true;
      case "qb45": dialect = Dialect.Qb45; return true;
      case "pds70": dialect = Dialect.Pds70; return true;
      case "pds71": dialect = Dialect.Pds71; return true;
      case "tb10": dialect = Dialect.Tb10; return true;
      case "tb11": dialect = Dialect.Tb11; return true;
      case "pb20": dialect = Dialect.Pb20; return true;
      case "pb21": dialect = Dialect.Pb21; return true;
      case "pb30": dialect = Dialect.Pb30; return true;
      case "pb31": dialect = Dialect.Pb31; return true;
      case "pb32": dialect = Dialect.Pb32; return true;
      case "pb35": dialect = Dialect.Pb35; return true;
      case "pb36": dialect = Dialect.Pb36; return true;
      default: dialect = Dialect.Pb35; return false;
    }
  }

  /// <summary>$COMPILE UNIT selects unit emission; $COMPILE EXE (the default) is a no-op.</summary>
  private static bool IsUnitCompile(SemanticModel model)
    => model.MetaStatements.Any(m => m.Command == "COMPILE" && m.Arguments is [{ } target, ..] && target.Text.Equals("UNIT", StringComparison.OrdinalIgnoreCase));

  /// <summary>
  /// Loads every $LINK "X.PBU"/"Y.PBL" target, trying the -L link directories
  /// first, then the source directory (so rebuilt units shadow foreign ones).
  /// </summary>
  private static bool TryLoadLinkTargets(SemanticModel model, IReadOnlyList<string> searchDirs, TextWriter stderr, out List<PbuFile> units, out List<PblFile> libraries) {
    units = [];
    libraries = [];
    foreach (var meta in model.MetaStatements.Where(m => m.Command == "LINK")) {
      if (meta.Arguments is not [{ Kind: Syntax.TokenKind.StringLiteral } file]) {
        stderr.WriteLine($"error: {meta.Position}: $LINK expects a quoted file name");
        return false;
      }
      var path = Path.IsPathRooted(file.Text)
        ? file.Text
        : searchDirs.Select(dir => Path.Combine(dir, file.Text)).FirstOrDefault(File.Exists);
      if (path == null || !File.Exists(path)) {
        stderr.WriteLine($"error: {meta.Position}: $LINK file '{file.Text}' not found");
        return false;
      }
      try {
        if (path.EndsWith(".OBJ", StringComparison.OrdinalIgnoreCase)) {
          // external Intel OMF object: lower to a synthetic unit (docs/LINKER.md)
          units.Add(Emit.Omf.OmfToPbu.Convert(Emit.Omf.OmfReader.ReadObject(File.ReadAllBytes(path))));
          continue;
        }
        if (path.EndsWith(".LIB", StringComparison.OrdinalIgnoreCase)) {
          // external OMF library: each module becomes a unit in a library so the
          // linker pulls only the ones that satisfy unresolved symbols
          var lib = new PblFile();
          foreach (var module in Emit.Omf.OmfReader.ReadLibrary(File.ReadAllBytes(path)))
            lib.Units.Add(Emit.Omf.OmfToPbu.Convert(module));
          libraries.Add(lib);
          continue;
        }
        using var stream = File.OpenRead(path);
        if (path.EndsWith(".PBL", StringComparison.OrdinalIgnoreCase))
          libraries.Add(PblFile.Read(stream));
        else
          units.Add(PbuFile.Read(stream));
      } catch (Emit.Omf.OmfException e) {
        stderr.WriteLine($"error: {meta.Position}: $LINK '{file.Text}': {e.Message}");
        return false;
      } catch (InvalidDataException e) {
        stderr.WriteLine($"error: {meta.Position}: $LINK '{file.Text}': {e.Message} (genuine PowerBASIC units are not binary-compatible; rebuild with pbc and point -L at them)");
        return false;
      }
    }
    return true;
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
    w.WriteLine("A source with $COMPILE UNIT produces a .PBU unit instead of an EXE;");
    w.WriteLine("$LINK \"X.PBU\" / $LINK \"Y.PBL\" directives (relative to the source");
    w.WriteLine("directory) are linked into the executable.");
    w.WriteLine();
    w.WriteLine("Options:");
    w.WriteLine("  -O <file>      output file name (default: <source>.EXE / .PBU)");
    w.WriteLine("  -I <dir>       additional $INCLUDE search directory");
    w.WriteLine("  --dialect <d>  language level: tb1x|pb2x..pb35 (default)|pb36 (optimizer)|qb1x..qb45|pds7x");
    w.WriteLine("  -G386          allow 80386 instructions (PBC.EXE compatibility)");
    w.WriteLine("  --dump-tokens  stop after lexing/preprocessing and list tokens");
    w.WriteLine("  --dump-ast     stop after parsing");
    w.WriteLine("  --dump-bind    stop after semantic analysis");
    w.WriteLine("  --emit-obj     compile to a linkable OMF .OBJ object instead of an EXE");
    w.WriteLine("  -h, --help     show this help");
  }
}
