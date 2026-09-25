using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Emit.Hosted;
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
    var listing = false;
    var dialect = Dialect.Pb35;
    var checkBounds = false;
    var checkNumeric = false;
    var checkOverflow = false;
    var checkStack = false;
    var optimizeSpeed = false;
    var parallelLoops = false;
    bool? optimize = null; // null = dialect default (on for pb36); --optimize/--no-optimize override
    HostedPlatform? platform = null; // null = 16-bit DOS, the native back end; else built through C

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
        case "--platform" when i + 1 < args.Length: {
          var name = args[++i];
          if (!TryParsePlatform(name, out platform)) {
            stderr.WriteLine($"pbc: unknown platform '{name}' (use x86-16|x86-32|x64)");
            return 1;
          }
          break;
        }
        case "--dialect" when i + 1 < args.Length: {
          var name = args[++i];
          if (!DialectFacts.TryParse(name, out dialect)) {
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
        case "--parallel-loops":
          parallelLoops = true;
          break;
        case "--x-backend" or "--x-backend-strict" or "--no-x-backend":
          stderr.WriteLine($"pbc: {args[i]} was removed; the IR/native backend is mandatory");
          return 1;
        case "--dump-tokens" or "--dump-ast" or "--dump-bind" or "--emit-llvm" or "--emit-c" or "--emit-obj" or "--emit-com" or "--emit-lib" or "--emit-basic":
          dumpStage = args[i];
          break;
        case "--list":
          listing = true;
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
    if (parallelLoops && dumpStage is not ("--emit-c" or "--emit-llvm")) {
      stderr.WriteLine("pbc: --parallel-loops is only available with --emit-c or --emit-llvm");
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

      if (dumpStage == "--emit-basic") {
        // back-emitter: turn the program back into PB 3.5-compatible PowerBASIC - declarations and
        // signatures from the surface unit, executable bodies (with the binder's pb36->pb35 lowering)
        // from the bound model. When the optimizer is in effect (its dialect default, unless
        // --no-optimize), run the AST-level passes whose effect is visible at the source level: the
        // statement pruner (dead-code / DEF SEG) mutates the tree, and pure-function folding produces
        // a call->constant map the back-emitter substitutes, so the output shows what the optimizer yields.
        Dictionary<Syntax.Ast.CallOrIndexExpr, Semantics.ConstantValue>? folds = null;
        if (optimize ?? (dialect == Dialect.Pb36)) {
          CodeGen.OptPruner.Prune(model);
          folds = CodeGen.OptPureFold.Analyze(model);
        }
        var basic = Emit.PowerBasic35Emitter.Render(model, unit, folds);
        if (output != null) {
          File.WriteAllText(output, basic, System.Text.Encoding.Latin1);   // DOS text: one byte per character, as it was read
          stdout.WriteLine($"{Path.GetFileName(output)}: {basic.Length} bytes of PowerBASIC");
        } else {
          stdout.Write(basic);
        }
        return 0;
      }

      if (dumpStage is "--emit-llvm" or "--emit-c") {
        var emittedC = dumpStage == "--emit-c";
        if (!TryEmitHostedSource(model, emittedC, optimize, optimizeSpeed, parallelLoops, dumpStage, stderr, out var text))
          return 1;
        if (output != null) {
          File.WriteAllText(output, text);
          stdout.WriteLine($"{Path.GetFileName(output)}: {text.Length} bytes of {(emittedC ? "C" : "LLVM IR")}");
        } else {
          stdout.Write(text);
        }
        return 0;
      }

      if (platform is { } hosted)
        return BuildHosted(model, source, hosted, dumpStage, output, optimize, optimizeSpeed, stdout, stderr);

      if (dumpStage == "--emit-lib") {
        stderr.WriteLine("pbc: --emit-lib builds a hosted archive; a DOS library is 'pbc lib build <out.PBL|out.LIB> <unit.PBU>...'");
        return 1;
      }

      var generator = new CodeGenerator(model) {
        CheckBounds = checkBounds,
        CheckNumeric = checkNumeric,
        CheckOverflow = checkOverflow,
        CheckStack = checkStack,
        OptimizeSpeed = optimizeSpeed,
      };
      if (optimize is { } opt)
        generator.Optimize = opt;

      if (dumpStage == "--emit-com") {
        if (model.MetaStatements.Any(m => m.Command == "LINK")) {
          stderr.WriteLine("error: COM output cannot use $LINK; DOS COM has no relocation table (use EXE)");
          return 1;
        }
        var com = generator.EmitCom();
        if (generator.Errors.Count > 0) {
          foreach (var error in generator.Errors)
            stderr.WriteLine($"error: {error}");
          return 1;
        }
        output ??= Path.ChangeExtension(source, ".COM");
        File.WriteAllBytes(output, com);
        stdout.WriteLine($"{Path.GetFileName(output)}: {com.Length} bytes");
        return 0;
      }

      if (dumpStage == "--emit-obj") {
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

      if (listing) {
        PbuFile? listedUnit = null;
        if (IsUnitCompile(model)) {
          var unitName = Path.GetFileNameWithoutExtension(source).ToUpperInvariant();
          listedUnit = generator.EmitUnit(unitName);
        } else if (IsComCompile(model)) {
          if (model.MetaStatements.Any(m => m.Command == "LINK")) {
            stderr.WriteLine("error: COM output cannot use $LINK; DOS COM has no relocation table (use EXE)");
            return 1;
          }
          var image = generator.EmitCom();
          if (image.Length == 0 && generator.Errors.Count == 0)
            return 1;
        } else {
          if (!TryLoadLinkTargets(model, [.. linkPaths, sourceDir], stderr, out var units, out var libraries))
            return 1;
          var image = generator.EmitExecutable(units, libraries);
          if (image.Length == 0 && generator.Errors.Count == 0)
            return 1;
        }
        if (generator.Errors.Count > 0) {
          foreach (var error in generator.Errors)
            stderr.WriteLine($"error: {error}");
          return 1;
        }
        var listText = Listing.Render(Path.GetFileName(source), model, generator.DescribeImage(), listedUnit);
        output ??= Path.ChangeExtension(source, ".LST");
        File.WriteAllText(output, listText);
        stdout.WriteLine($"{Path.GetFileName(output)}: {listText.Length} bytes");
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
      } else if (IsComCompile(model)) {
        if (model.MetaStatements.Any(m => m.Command == "LINK")) {
          stderr.WriteLine("error: COM output cannot use $LINK; DOS COM has no relocation table (use EXE)");
          return 1;
        }
        artifact = generator.EmitCom();
        output ??= Path.ChangeExtension(source, ".COM");
      } else {
        if (!TryLoadLinkTargets(model, [.. linkPaths, sourceDir], stderr, out var units, out var libraries))
          return 1;
        artifact = generator.EmitExecutable(units, libraries);
        var isChain = model.MetaStatements.Any(m => m.Command == "COMPILE"
          && m.Arguments is [{ } chainTarget, ..] && chainTarget.Text.Equals("CHAIN", StringComparison.OrdinalIgnoreCase));
        // an optimized self-contained program is written as a flat COM (CodeGenerator.Container), and
        // the file is named for what it holds
        var isCom = artifact is not [(byte)'M', (byte)'Z', ..] && artifact.Length > 0;
        output ??= Path.ChangeExtension(source, isChain ? ".PBC" : isCom ? ".COM" : ".EXE");
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

  /// <summary>$COMPILE UNIT selects unit emission; EXE remains the default.</summary>
  /// <summary>
  /// The IR, through the hosted middle end, rendered as C99 or LLVM text - what <c>--emit-c</c> and
  /// <c>--emit-llvm</c> print and what a hosted <c>--platform</c> build compiles. The source's own
  /// <c>$OPTIMIZE</c> is honoured the way the DOS build honours it.
  /// </summary>
  private static bool TryEmitHostedSource(SemanticModel model, bool emitC, bool? optimize, bool optimizeSpeed,
      bool parallelLoops, string label, TextWriter stderr, out string text) {
    text = "";
    var target = emitC ? IrBackendTarget.C : IrBackendTarget.Llvm;
    var compiled = IrBackendModule.TryCompile(model, new IrBackendOptions {
      Target = target,
      Optimize = optimize ?? true,
      OptimizeForSpeed = optimizeSpeed,
      EnableFpLookupTables = !emitC,
      RecoverIntegerArithmetic = optimize ?? true,
      PrepareParallelLoops = parallelLoops,
    }, out var declined);
    if (compiled is null) {
      stderr.WriteLine($"pbc: {label}: {declined ?? "unsupported construct"} - outside the IR lowering's subset (see docs/IR.md)");
      return false;
    }
    var module = compiled.Module;
    module.AsciiOnly = model.AsciiOnly;

    var optimizeMetas = model.MetaStatements
      .Where(meta => meta.Command.Equals("OPTIMIZE", StringComparison.OrdinalIgnoreCase))
      .ToList();
    if (optimizeMetas.Count > 1) {
      stderr.WriteLine($"error: {optimizeMetas[1].Position}: only one $OPTIMIZE per module");
      return false;
    }
    var hostedOptimize = optimize ?? true;
    var hostedSpeed = optimizeSpeed;
    if (optimizeMetas.FirstOrDefault()?.Arguments is [{ } mode, ..]) {
      if (mode.Text.Equals("OFF", StringComparison.OrdinalIgnoreCase))
        hostedOptimize = false;
      hostedSpeed = mode.Text.Equals("SPEED", StringComparison.OrdinalIgnoreCase);
    }
    if (parallelLoops && !hostedOptimize) {
      stderr.WriteLine("pbc: --parallel-loops requires optimization; remove --no-optimize / $OPTIMIZE OFF");
      return false;
    }

    if (hostedOptimize != (optimize ?? true) || hostedSpeed != optimizeSpeed) {
      compiled = IrBackendModule.TryCompile(model, new IrBackendOptions {
        Target = target,
        Optimize = hostedOptimize,
        OptimizeForSpeed = hostedSpeed,
        EnableFpLookupTables = !emitC,
        RecoverIntegerArithmetic = hostedOptimize,
        PrepareParallelLoops = parallelLoops,
      }, out declined);
      if (compiled is null) {
        stderr.WriteLine($"pbc: {label}: {declined ?? "unsupported construct"}");
        return false;
      }
      module = compiled.Module;
    }

    var verifyErrors = IrVerifier.Verify(module);
    if (verifyErrors.Count > 0) {
      stderr.WriteLine($"pbc: {label}: internal error, optimized IR failed verification:");
      foreach (var e in verifyErrors)
        stderr.WriteLine("  " + e);
      return false;
    }
    var rendered = emitC
      ? CEmitter.TryEmit(module, out var refused)
      : LlvmEmitter.TryEmit(module, "x86_64-unknown-linux-gnu", out refused);
    if (rendered is null) {
      stderr.WriteLine($"pbc: {label}: {refused ?? "unsupported construct"} "
        + "- outside what this back end renders (see docs/BACKENDS.md)");
      return false;
    }
    text = rendered;
    return true;
  }

  /// <summary>
  /// A program for x86-32 or x64: the C back end's translation unit, built by the host toolchain
  /// against the portable runtime. An executable by default, the program's object with
  /// <c>--emit-obj</c>, or an archive of it and the runtime with <c>--emit-lib</c>. COM images and
  /// PBU/PBL units are DOS containers and have no hosted form.
  /// </summary>
  private static int BuildHosted(SemanticModel model, string source, HostedPlatform platform, string dumpStage,
      string? output, bool? optimize, bool optimizeSpeed, TextWriter stdout, TextWriter stderr) {
    if (dumpStage == "--emit-com" || IsComCompile(model)) {
      stderr.WriteLine("error: a COM image is a DOS container; build it with --platform x86-16");
      return 1;
    }
    if (IsUnitCompile(model)) {
      stderr.WriteLine("error: a PBU unit is a DOS container; for a hosted platform use --emit-obj or --emit-lib");
      return 1;
    }
    var (artifact, extension) = dumpStage switch {
      "--emit-obj" => (HostedArtifact.Object, ".o"),
      "--emit-lib" => (HostedArtifact.Library, ".a"),
      _ => (HostedArtifact.Executable, ""),
    };
    if (!TryEmitHostedSource(model, emitC: true, optimize, optimizeSpeed, parallelLoops: false, "--platform", stderr, out var text))
      return 1;
    output ??= Path.ChangeExtension(source, extension == "" ? null : extension);
    if (!HostToolchain.TryBuild(text, platform, artifact, output, out var error)) {
      stderr.WriteLine($"error: {error}");
      return 1;
    }
    stdout.WriteLine($"{Path.GetFileName(output)}: {new FileInfo(output).Length} bytes ({HostToolchain.Describe(platform)})");
    return 0;
  }

  private static bool TryParsePlatform(string name, out HostedPlatform? platform) {
    platform = null;
    switch (name.ToLowerInvariant()) {
      case "x86-16" or "x86_16" or "dos":
        return true;
      case "x86-32" or "x86_32" or "i386" or "ia32":
        platform = HostedPlatform.X86_32;
        return true;
      case "x64" or "x86-64" or "x86_64" or "amd64":
        platform = HostedPlatform.X64;
        return true;
      default:
        return false;
    }
  }

  private static bool IsUnitCompile(SemanticModel model)
    => model.MetaStatements.Any(m => m.Command == "COMPILE" && m.Arguments is [{ } target, ..] && target.Text.Equals("UNIT", StringComparison.OrdinalIgnoreCase));

  /// <summary>$COMPILE COM selects a flat PSP:0100h DOS image.</summary>
  private static bool IsComCompile(SemanticModel model)
    => model.MetaStatements.Any(m => m.Command == "COMPILE" && m.Arguments is [{ } target, ..] && target.Text.Equals("COM", StringComparison.OrdinalIgnoreCase));

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
          units.Add(Emit.Omf.OmfToPbu.Convert(Emit.Omf.OmfReader.ReadObject(File.ReadAllBytes(path))));
          continue;
        }
        if (path.EndsWith(".LIB", StringComparison.OrdinalIgnoreCase)) {
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

  private static int RunLib(string[] args, TextWriter stdout, TextWriter stderr) {
    switch (args) {
      case ["build", var output, .. var unitFiles] when unitFiles.Length > 0: {
        var units = new List<PbuFile>();
        foreach (var file in unitFiles) {
          if (!File.Exists(file)) {
            stderr.WriteLine($"pbc lib: unit '{file}' not found");
            return 1;
          }
          using var stream = File.OpenRead(file);
          units.Add(PbuFile.Read(stream));
        }
        if (output.EndsWith(".LIB", StringComparison.OrdinalIgnoreCase)) {
          File.WriteAllBytes(output, Emit.Omf.OmfLibraryWriter.WriteLibrary(units));
        } else {
          var library = new PblFile();
          library.Units.AddRange(units);
          using var stream = File.Create(output);
          library.Write(stream);
        }
        stdout.WriteLine($"{Path.GetFileName(output)}: {units.Count} unit(s)");
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
        stderr.WriteLine("usage: pbc lib build <out.PBL|out.LIB> <unit.PBU>...");
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
    w.WriteLine("       pbc lib build <out.PBL|out.LIB> <unit.PBU>...");
    w.WriteLine("       pbc lib list <file.PBL|file.PBU>");
    w.WriteLine();
    w.WriteLine("A source with $COMPILE UNIT produces .PBU; $COMPILE COM produces flat .COM,");
    w.WriteLine("as does any optimized program without $LINK ($COMPILE EXE keeps the .EXE);");
    w.WriteLine("$LINK \"X.PBU\" / $LINK \"Y.PBL\" directives (relative to the source");
    w.WriteLine("directory) are linked into the executable.");
    w.WriteLine();
    w.WriteLine("Options:");
    w.WriteLine("  -O <file>      output file name (default: <source>.EXE / .PBU)");
    w.WriteLine("  -I <dir>       additional $INCLUDE search directory");
    w.WriteLine("  --dialect <d>  language level: tb1x|pb2x..pb35 (default)|pb36 (optimizer)|qb1x..qb45|pds7x");
    w.WriteLine("  -G386          allow 80386 instructions (PBC.EXE compatibility)");
    w.WriteLine("  -OZF           prefer SPEED; enables size-for-speed and relaxed-FP transforms when optimizing");
    w.WriteLine("  --optimize     enable the optimizer for any dialect");
    w.WriteLine("  --no-optimize  disable optimization even for pb36 / $OPTIMIZE SPEED");
    w.WriteLine("  --parallel-loops opt in to O0311 for hosted C/LLVM; link runtime/pbc_parallel.c with OpenMP");
    w.WriteLine("  --dump-tokens  stop after lexing/preprocessing and list tokens");
    w.WriteLine("  --dump-ast     stop after parsing");
    w.WriteLine("  --dump-bind    stop after semantic analysis");
    w.WriteLine("  --emit-obj     compile to a linkable OMF .OBJ object instead of an EXE");
    w.WriteLine("  --emit-com     compile to a flat DOS .COM image (no $LINK/segment relocations)");
    w.WriteLine("  --platform <p> x86-16 (DOS, default) | x86-32 | x64: the latter two build a native");
    w.WriteLine("                 executable through the C back end and the host C compiler");
    w.WriteLine("  --emit-lib     with a hosted --platform: an archive of the program and its runtime");
    w.WriteLine("  --emit-basic   render optimized IR back to readable PowerBASIC");
    w.WriteLine("  --emit-llvm    optimize through the IR middle end and emit textual LLVM");
    w.WriteLine("  --emit-c       optimize through the IR middle end and emit portable C99");
    w.WriteLine("  --list         write a human-readable .LST map of the compiled image");
    w.WriteLine("  -h, --help     show this help");
  }
}
