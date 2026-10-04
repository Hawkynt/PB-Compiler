using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Backend.Mos6502;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Emit.Commodore;
using PowerBasic.Compiler.Backend.X86Native;
using PowerBasic.Compiler.Emit.Elf;
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
    var platform = Platform.X86_16;

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
            stderr.WriteLine($"pbc: unknown platform '{name}' (use x86-16|x86-32|x64|6502)");
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

      switch (platform) {
        case Platform.X86_32:
          return BuildNative(model, source, X86Machine.I386, dumpStage, output, optimize, optimizeSpeed, [.. linkPaths, sourceDir], stdout, stderr);
        case Platform.X64:
          return BuildNative(model, source, X86Machine.Amd64, dumpStage, output, optimize, optimizeSpeed, [.. linkPaths, sourceDir], stdout, stderr);
        case Platform.Mos6502:
          return BuildC64(model, source, dumpStage, output, optimize, optimizeSpeed, [.. linkPaths, sourceDir], stdout, stderr);
      }

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

  /// <summary>
  /// The optimizer setting an IR-built platform compiles with: the command line's, unless the source
  /// says otherwise with its one <c>$OPTIMIZE</c> - OFF turns it off, SPEED or SIZE picks the goal -
  /// exactly as the DOS build reads it.
  /// </summary>
  private static bool TryResolveOptimize(SemanticModel model, bool? optimize, bool optimizeSpeed, TextWriter stderr,
      out bool effectiveOptimize, out bool effectiveSpeed) {
    effectiveOptimize = optimize ?? true;
    effectiveSpeed = optimizeSpeed;
    var metas = model.MetaStatements
      .Where(meta => meta.Command.Equals("OPTIMIZE", StringComparison.OrdinalIgnoreCase))
      .ToList();
    if (metas.Count > 1) {
      stderr.WriteLine($"error: {metas[1].Position}: only one $OPTIMIZE per module");
      return false;
    }
    if (metas.FirstOrDefault()?.Arguments is [{ } mode, ..]) {
      if (mode.Text.Equals("OFF", StringComparison.OrdinalIgnoreCase))
        effectiveOptimize = false;
      effectiveSpeed = mode.Text.Equals("SPEED", StringComparison.OrdinalIgnoreCase);
    }
    return true;
  }

  /// <summary>
  /// The IR, through the hosted middle end, rendered as C99 or LLVM text - what <c>--emit-c</c> and
  /// <c>--emit-llvm</c> print. The source's own <c>$OPTIMIZE</c> is honoured the way the DOS build
  /// honours it.
  /// </summary>
  private static bool TryEmitHostedSource(SemanticModel model, bool emitC, bool? optimize, bool optimizeSpeed,
      bool parallelLoops, string label, TextWriter stderr, out string text) {
    text = "";
    if (!TryResolveOptimize(model, optimize, optimizeSpeed, stderr, out var effectiveOptimize, out var effectiveSpeed))
      return false;
    if (parallelLoops && !effectiveOptimize) {
      stderr.WriteLine("pbc: --parallel-loops requires optimization; remove --no-optimize / $OPTIMIZE OFF");
      return false;
    }
    var compiled = IrBackendModule.TryCompile(model, new IrBackendOptions {
      Target = emitC ? IrBackendTarget.C : IrBackendTarget.Llvm,
      Optimize = effectiveOptimize,
      OptimizeForSpeed = effectiveSpeed,
      EnableFpLookupTables = !emitC,
      RecoverIntegerArithmetic = effectiveOptimize,
      PrepareParallelLoops = parallelLoops,
    }, out var declined);
    if (compiled is null) {
      stderr.WriteLine($"pbc: {label}: {declined ?? "unsupported construct"} - outside the IR lowering's subset (see docs/IR.md)");
      return false;
    }
    var module = compiled.Module;
    module.AsciiOnly = model.AsciiOnly;

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
  /// A program for x86-32 or x64 Linux: the IR, through the hosted middle end, with the portable
  /// runtime defined into it and compiled by the native x86 back end - no C compiler, assembler or
  /// linker. A static executable by default; with <c>--emit-obj</c> an ELF object exporting
  /// <c>pb_main</c>, with <c>--emit-lib</c> an archive of it. <c>$COMPILE UNIT</c> writes an IR unit
  /// (<see cref="IrUnitFile"/>) and <c>$LINK</c> links IR units and libraries. A COM image is DOS's
  /// own container - a PSP and an entry at 0100h - and has no Linux form.
  /// </summary>
  private static int BuildNative(SemanticModel model, string source, X86Machine machine, string dumpStage,
      string? output, bool? optimize, bool optimizeSpeed, IReadOnlyList<string> linkDirs, TextWriter stdout, TextWriter stderr) {
    var name = machine == X86Machine.Amd64 ? "x64" : "x86-32";
    if (dumpStage == "--emit-com" || IsComCompile(model)) {
      stderr.WriteLine("error: a COM image is a DOS container; build it with --platform x86-16");
      return 1;
    }
    if (IsUnitCompile(model))
      return CompileIrUnit(model, source, output, name, library: false, stdout, stderr);
    if (!TryLoadIrLinkTargets(model, linkDirs, name, stderr, out var linked))
      return 1;
    if (!TryResolveOptimize(model, optimize, optimizeSpeed, stderr, out var effectiveOptimize, out var effectiveSpeed))
      return 1;
    var compiled = IrBackendModule.TryCompile(model, new IrBackendOptions {
      Target = machine == X86Machine.Amd64 ? IrBackendTarget.X64 : IrBackendTarget.X86_32,
      Optimize = effectiveOptimize,
      OptimizeForSpeed = effectiveSpeed,
      RecoverIntegerArithmetic = effectiveOptimize,
      PortableRuntimeHeapBytes = NativeHeapBytes,
      LinkedModules = linked,
    }, out var declined);
    var program = compiled is null ? null : X86NativeCompiler.TryCompile(compiled.Module, machine, out declined);
    if (program is null) {
      stderr.WriteLine($"error: {name}: {declined ?? "unsupported construct"}");
      return 1;
    }
    var (bytes, extension) = dumpStage switch {
      "--emit-obj" => (ElfWriter.Object(program), ".o"),
      "--emit-lib" => (ElfWriter.Archive([(Path.GetFileNameWithoutExtension(source).ToLowerInvariant() + ".o",
        ElfWriter.Object(program), ["pb_main", "pb_start"])]), ".a"),
      _ => (ElfWriter.Executable(program), ""),
    };
    output ??= Path.ChangeExtension(source, extension == "" ? null : extension);
    File.WriteAllBytes(output, bytes);
    if (extension == "" && !OperatingSystem.IsWindows())
      File.SetUnixFileMode(output, File.GetUnixFileMode(output) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    stdout.WriteLine($"{Path.GetFileName(output)}: {bytes.Length} bytes ({name})");
    return 0;
  }

  /// <summary>The string heap a C64 program starts with: grown into whatever the program leaves of its 46 KB, or shrunk towards <see cref="C64HeapFloor"/> for one a little too big.</summary>
  private const int C64HeapBytes = 4096;

  /// <summary>The smallest a program too big for the usual heap is given instead of being refused.</summary>
  private const int C64HeapFloor = 1024;

  /// <summary>The largest: a heap block's end has to be a positive 16-bit index.</summary>
  private const int C64HeapLimit = 28 << 10;

  /// <summary>The string heap of a native Linux program: uninitialised storage, so it costs no file space.</summary>
  private const int NativeHeapBytes = 16 << 20;

  /// <summary>The machines <c>--platform</c> selects.</summary>
  private enum Platform { X86_16, X86_32, X64, Mos6502 }

  private static bool TryParsePlatform(string name, out Platform platform) {
    platform = name.ToLowerInvariant() switch {
      "x86-16" or "x86_16" or "dos" => Platform.X86_16,
      "x86-32" or "x86_32" or "i386" or "ia32" => Platform.X86_32,
      "x64" or "x86-64" or "x86_64" or "amd64" => Platform.X64,
      "6502" or "mos6502" or "c64" => Platform.Mos6502,
      _ => (Platform)(-1),
    };
    return Enum.IsDefined(platform);
  }

  /// <summary>
  /// A program for the 6502: the IR, through the native middle end, compiled by the 6502 back end
  /// into a Commodore 64 <c>.PRG</c>. Its object and library formats are the IR unit and library
  /// (<see cref="IrUnitFile"/>): <c>$COMPILE UNIT</c> or <c>--emit-obj</c> writes a unit,
  /// <c>--emit-lib</c> a library holding it, and <c>$LINK</c> links either. A COM image is DOS's own
  /// container and has no C64 form.
  /// </summary>
  private static int BuildC64(SemanticModel model, string source, string dumpStage, string? output, bool? optimize,
      bool optimizeSpeed, IReadOnlyList<string> linkDirs, TextWriter stdout, TextWriter stderr) {
    if (dumpStage == "--emit-com" || IsComCompile(model)) {
      stderr.WriteLine("error: a COM image is a DOS container; the 6502 builds a C64 .PRG, and units, objects and libraries of IR");
      return 1;
    }
    if (IsUnitCompile(model) || dumpStage == "--emit-obj")
      return CompileIrUnit(model, source, output, "6502", library: false, stdout, stderr);
    if (dumpStage == "--emit-lib")
      return CompileIrUnit(model, source, output, "6502", library: true, stdout, stderr);
    if (!TryLoadIrLinkTargets(model, linkDirs, "6502", stderr, out var linked))
      return 1;
    if (!TryResolveOptimize(model, optimize, optimizeSpeed, stderr, out var effectiveOptimize, out var effectiveSpeed))
      return 1;
    // a C64 has 46 KB for program and data: unless SPEED is asked for, optimize for size - and a
    // SPEED build that does not fit is built again for size, since a program that runs slower beats
    // one that does not load
    var image = CompileC64(model, effectiveOptimize, effectiveSpeed, linked, out var declined);
    if (image is null && effectiveSpeed && declined is { } tooLarge && IsC64SizeDecline(tooLarge)) {
      image = CompileC64(model, effectiveOptimize, speed: false, linked, out declined);
      if (image is not null)
        stderr.WriteLine($"warning: 6502: the $OPTIMIZE SPEED build does not fit a C64 ({tooLarge}); built for size instead");
    }
    if (image is null) {
      stderr.WriteLine($"error: 6502: {declined ?? "unsupported construct"}");
      return 1;
    }
    output ??= Path.ChangeExtension(source, ".PRG");
    var file = C64Prg.Write(image);
    File.WriteAllBytes(output, file);
    stdout.WriteLine($"{Path.GetFileName(output)}: {file.Length} bytes (6502, C64)");
    return 0;
  }

  /// <summary>
  /// The C64 build: once with the smallest string heap, and - when that leaves memory unused below
  /// the soft stack - again with the heap grown into it. The heap's size is only a constant the code
  /// compares against, so the second build is the first one's size with a longer heap behind it.
  /// </summary>
  private static Mos6502Assembler.Image? CompileC64(SemanticModel model, bool optimize, bool speed,
      IReadOnlyList<IrModule> linked, out string? declined) {
    var image = CompileC64(model, optimize, speed, linked, C64HeapBytes, out declined);
    if (image is null)
      return null;
    // a page kept back, in case a constant the larger heap needs encodes longer somewhere
    var spare = C64Prg.MemoryTop - image.End - 256;
    if (spare < 0) {
      // too big by less than the heap can give up: a smaller heap, down to a floor a program that
      // works its strings at all still needs, rather than no program
      var smaller = C64HeapBytes + spare;
      if (smaller < C64HeapFloor)
        return Overflowed(image, out declined);
      var tighter = CompileC64(model, optimize, speed, linked, smaller, out var tighterDeclined);
      declined = tighterDeclined;
      return tighter is not null && tighter.End <= C64Prg.MemoryTop ? tighter : Overflowed(tighter ?? image, out declined);
    }
    if (spare < 1024)
      return image;
    // the runtime counts the heap in signed 16-bit indexes, so a block's end must stay below 32 KB
    var roomier = CompileC64(model, optimize, speed, linked, Math.Min(C64HeapBytes + spare, C64HeapLimit), out var roomierDeclined);
    if (roomier is null)
      return image;
    declined = roomierDeclined;
    return roomier;
  }

  private static Mos6502Assembler.Image? CompileC64(SemanticModel model, bool optimize, bool speed,
      IReadOnlyList<IrModule> linked, int heapBytes, out string? declined) {
    var compiled = IrBackendModule.TryCompile(model, new IrBackendOptions {
      Target = IrBackendTarget.Mos6502,
      Optimize = optimize,
      OptimizeForSpeed = speed,
      OptimizeForSize = !speed,
      RecoverIntegerArithmetic = optimize,
      PortableRuntimeHeapBytes = heapBytes,
      PortableRuntimeIndexBits = 16,
      PortableRuntimeSoftMath = true,
      LinkedModules = linked,
    }, out declined);
    // laid out against the whole address space, so a program too big is measured rather than refused
    return compiled is null ? null
      : Mos6502Compiler.TryCompile(compiled.Module, C64Prg.CodeOrigin, 0x10000, out declined);
  }

  /// <summary>The decline for an image past <see cref="C64Prg.MemoryTop"/>, saying how far past.</summary>
  private static Mos6502Assembler.Image? Overflowed(Mos6502Assembler.Image image, out string? declined) {
    declined = $"the program needs memory up to ${image.End:X4}, past the ${C64Prg.MemoryTop:X4} available";
    return null;
  }

  /// <summary>Whether a 6502 decline is the image not fitting, rather than a construct it cannot lower.</summary>
  private static bool IsC64SizeDecline(string declined)
    => declined.StartsWith("the program needs ", StringComparison.Ordinal);

  /// <summary>
  /// A unit for a platform compiled from IR: the lowered module, without its (empty) <c>main</c>, as
  /// an IR unit - or, with <paramref name="library"/>, a library holding that one unit. As on DOS, a
  /// unit is procedures only: module-level code has no caller to run it.
  /// </summary>
  private static int CompileIrUnit(SemanticModel model, string source, string? output, string platform, bool library,
      TextWriter stdout, TextWriter stderr) {
    var module = IrLowering.TryLowerModule(model, flatArrayDescriptors: true, out var declined);
    if (module is null) {
      stderr.WriteLine($"error: {platform}: {declined ?? "the unit does not lower"}");
      return 1;
    }
    module.AsciiOnly = model.AsciiOnly;
    if (module.FindFunction("main") is { } main) {
      if (main.AllInstructions.Any(instruction => instruction is not (IrRet or IrAlloca))) {
        stderr.WriteLine($"error: {platform}: a unit cannot contain module-level code (only SUBs and FUNCTIONs)");
        return 1;
      }
      module.RemoveFunction(main);
    }
    var unit = IrUnitFile.Write(module);
    var name = Path.GetFileNameWithoutExtension(source).ToUpperInvariant();
    var bytes = library ? IrUnitFile.WriteLibrary([(name, unit)]) : unit;
    output ??= Path.ChangeExtension(source, library ? ".PBL" : ".PBU");
    File.WriteAllBytes(output, bytes);
    var procedures = module.Functions.Count(function => !function.IsDeclaration);
    stdout.WriteLine($"{Path.GetFileName(output)}: {bytes.Length} bytes, {procedures} procedure(s) as IR ({platform})");
    return 0;
  }

  /// <summary>
  /// The IR units and libraries a program for an IR platform <c>$LINK</c>s, told apart by their magic
  /// rather than their extension - on these platforms <c>.OBJ</c> and <c>.LIB</c> are IR too. A DOS
  /// unit or OMF object holds 8086 code and is refused with the reason.
  /// </summary>
  private static bool TryLoadIrLinkTargets(SemanticModel model, IReadOnlyList<string> searchDirs, string platform,
      TextWriter stderr, out List<IrModule> modules) {
    modules = [];
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
      var bytes = File.ReadAllBytes(path);
      try {
        if (IrUnitFile.IsIrUnit(bytes))
          modules.Add(IrUnitFile.Read(bytes));
        else if (IrUnitFile.IsIrLibrary(bytes))
          modules.AddRange(IrUnitFile.ReadLibrary(bytes).Select(member => IrUnitFile.Read(member.Unit)));
        else {
          stderr.WriteLine($"error: {meta.Position}: $LINK '{file.Text}' holds 8086 code (a DOS unit, library or object); "
            + $"a program for {platform} links units compiled with --platform {platform}");
          return false;
        }
      } catch (InvalidDataException e) {
        stderr.WriteLine($"error: {meta.Position}: $LINK '{file.Text}': {e.Message}");
        return false;
      }
    }
    return true;
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
      var head = File.ReadAllBytes(path);
      if (IrUnitFile.IsIrUnit(head) || IrUnitFile.IsIrLibrary(head)) {
        stderr.WriteLine($"error: {meta.Position}: $LINK '{file.Text}' holds IR for x86-32, x64 or the 6502; "
          + "a DOS program links units compiled with --platform x86-16");
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
      case ["build", var output, .. var unitFiles] when unitFiles.Length > 0
          && unitFiles.All(File.Exists) && unitFiles.Any(file => IrUnitFile.IsIrUnit(File.ReadAllBytes(file))): {
        // units for an IR platform: a library of IR units, whatever the extension asked for
        var members = new List<(string, byte[])>();
        foreach (var file in unitFiles) {
          var bytes = File.ReadAllBytes(file);
          if (!IrUnitFile.IsIrUnit(bytes)) {
            stderr.WriteLine($"pbc lib: '{file}' is not an IR unit, and one library cannot mix DOS and IR units");
            return 1;
          }
          members.Add((Path.GetFileNameWithoutExtension(file).ToUpperInvariant(), bytes));
        }
        File.WriteAllBytes(output, IrUnitFile.WriteLibrary(members));
        stdout.WriteLine($"{Path.GetFileName(output)}: {members.Count} IR unit(s)");
        return 0;
      }

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

      case ["list", var file] when File.Exists(file) && File.ReadAllBytes(file) is var bytes
          && (IrUnitFile.IsIrUnit(bytes) || IrUnitFile.IsIrLibrary(bytes)): {
        var members = IrUnitFile.IsIrUnit(bytes)
          ? [(Path.GetFileNameWithoutExtension(file).ToUpperInvariant(), bytes)]
          : IrUnitFile.ReadLibrary(bytes);
        foreach (var (name, unit) in members)
          DescribeIrUnit(name, IrUnitFile.Read(unit), stdout);
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

  private static void DescribeIrUnit(string name, IrModule unit, TextWriter stdout) {
    stdout.WriteLine($"{name}: IR unit, {unit.EffectiveDialect}, {unit.Globals.Count} global(s)");
    foreach (var function in unit.Functions.Where(function => !function.IsDeclaration))
      stdout.WriteLine($"  exports {function.Name}({string.Join(", ", function.Parameters.Select(parameter => parameter.Type))}) -> {function.ReturnType}");
    foreach (var function in unit.Functions.Where(function => function.IsDeclaration))
      stdout.WriteLine($"  imports {function.Name}");
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
    w.WriteLine("A source with $COMPILE UNIT produces .PBU (of IR for x86-32, x64 and 6502);");
    w.WriteLine("$COMPILE COM produces flat .COM,");
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
    w.WriteLine("  --emit-obj     compile to a linkable object: OMF .OBJ on DOS, ELF .o on x86-32|x64,");
    w.WriteLine("                 an IR unit .OBJ on the 6502");
    w.WriteLine("  --emit-com     compile to a flat DOS .COM image (no $LINK/segment relocations)");
    w.WriteLine("  --platform <p> x86-16 (DOS, default) | x86-32 | x64 | 6502: x86-32 and x64 build a static");
    w.WriteLine("                 Linux ELF executable, 6502 a C64 .PRG - all emitted by pbc itself");
    w.WriteLine("  --emit-lib     x86-32|x64: an ELF archive of the program and its runtime; 6502: an IR library");
    w.WriteLine("  --emit-basic   render optimized IR back to readable PowerBASIC");
    w.WriteLine("  --emit-llvm    optimize through the IR middle end and emit textual LLVM");
    w.WriteLine("  --emit-c       optimize through the IR middle end and emit portable C99");
    w.WriteLine("  --list         write a human-readable .LST map of the compiled image");
    w.WriteLine("  -h, --help     show this help");
  }
}
