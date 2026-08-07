using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Syntax;

namespace PowerBasic.Compiler.Tests.Dialects;

/// <summary>
/// The measurements behind <see cref="DialectBattery"/>. Each probe answers one dimension for one
/// dialect and reports a count, so the generated README carries numbers rather than ticks.
///
/// A probe that cannot yet be written returns <see cref="DialectBattery.State.Unprobed"/> instead of
/// quietly passing. That distinction is the whole value of the battery: "nobody has checked" and "it
/// holds" look identical in a green test run and must not look identical in the checklist.
/// </summary>
internal static class DialectProbes {

  private const string _file = "T.BAS";

  /// <summary>Whether the front end accepts a source, and whether a rejection was a controlled diagnostic.</summary>
  internal readonly record struct FrontEnd(bool Accepted, bool Controlled, string? Why);

  /// <summary>Feeds an in-memory source to the preprocessor, which is a separate entry point from the lexer.</summary>
  private sealed class MemorySource(string text) : ISourceProvider {
    public bool TryReadSource(string name, string? includedFrom, out string sourceText, out string resolvedName) {
      sourceText = text;
      resolvedName = name;
      return true;
    }
  }

  /// <summary>
  /// The whole front end, metastatements included.
  ///
  /// <c>Preprocessor.Expand</c> is NOT reached by tokenizing and parsing - it is its own entry point,
  /// and a probe that goes straight to the lexer never sees a `$IF` resolved. The dead-branch probe
  /// did exactly that and reported seventeen dialects failing to skip a false branch, which the
  /// preprocessor skips correctly when it is actually asked.
  /// </summary>
  internal static FrontEnd Compile(string source, Dialect dialect) {
    try {
      var tokens = Preprocessor.Expand(_file, new MemorySource(source), dialect);
      var model = Binder.Bind(Parser.Parse(tokens, _file, dialect), dialect);
      return model.Errors.Count == 0 ? new(true, false, null) : new(false, true, model.Errors[0].Message);
    } catch (Exception e) when (e is LexerException or ParserException or PreprocessorException or BindException) {
      return new(false, true, e.Message);
    } catch (Exception e) {
      return new(false, false, e.GetType().Name + ": " + e.Message);
    }
  }

  /// <summary>D1 - every form the dialect provides is accepted.</summary>
  internal static DialectBattery.Measurement Syntax(Dialect dialect) {
    int total = 0, covered = 0;
    var failed = new List<string>();
    foreach (var form in StatementSurface.All.Where(f => StatementSurface.ShouldAccept(f, dialect))) {
      ++total;
      if (Compile(StatementSurface.Program(form, dialect), dialect).Accepted)
        ++covered;
      else
        failed.Add(form.Id);
    }
    return Report(covered, total, failed, "accepted");
  }

  /// <summary>D5 - a form the dialect never had is refused, and refused CLEANLY.</summary>
  internal static DialectBattery.Measurement Foreign(Dialect dialect) {
    int total = 0, covered = 0;
    var failed = new List<string>();
    foreach (var form in StatementSurface.All.Where(f => !StatementSurface.ShouldAccept(f, dialect))) {
      ++total;
      var result = Compile(StatementSurface.Program(form, dialect), dialect);
      if (!result.Accepted && result.Controlled)
        ++covered;
      else
        failed.Add(form.Id + (result.Accepted ? " (accepted)" : " (uncontrolled)"));
    }
    return total == 0
      ? new(DialectBattery.State.NotApplicable, 0, 0, "this dialect provides every form in the surface")
      : Report(covered, total, failed, "cleanly refused");
  }

  /// <summary>
  /// D2 - every accepted form reaches the IR, or declines with a NAMED reason.
  ///
  /// The bar is deliberately "declines by name" rather than "lowers": the lowering has a documented
  /// subset and refusing outside it is correct behaviour. What is not acceptable is an internal
  /// exception, which is a crash wearing a decline's clothes.
  /// </summary>
  internal static DialectBattery.Measurement Lowering(Dialect dialect) {
    int total = 0, lowered = 0;
    var crashed = new List<string>();
    foreach (var form in StatementSurface.All.Where(f => StatementSurface.ShouldAccept(f, dialect))) {
      var source = StatementSurface.Program(form, dialect);
      SemanticModel model;
      try {
        model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, _file, dialect), _file, dialect), dialect);
        if (model.Errors.Count > 0)
          continue;                              // D1's business, not this one's
      } catch {
        continue;
      }
      ++total;
      try {
        if (IrLowering.TryLowerModule(model, out var why) is not null)
          ++lowered;
        else if (string.IsNullOrWhiteSpace(why))
          crashed.Add(form.Id + " (declined with no reason)");
      } catch (Exception e) {
        crashed.Add($"{form.Id} ({e.GetType().Name})");
      }
    }
    if (crashed.Count > 0)
      return new(DialectBattery.State.Partial, lowered, total,
        $"{crashed.Count} form(s) fail without a named reason: {string.Join(", ", crashed.Take(4))}");
    return new(DialectBattery.State.Held, lowered, total,
      $"{lowered} of {total} reach the IR; the rest decline by name, which is the documented subset");
  }

  /// <summary>
  /// The malformed text both branch probes use. It is not a typo or a near-miss - it is a sequence no
  /// BASIC of any lineage could parse - so "the compiler accepted it" can only mean it never looked.
  /// </summary>
  private const string _garbage = "THIS IS ARBITRARY TEXT";

  /// <summary>
  /// Why a dialect has no way to express "text control never reaches", or null when it has one.
  ///
  /// Two different absences. A compiled Microsoft dialect has no conditional compilation at all -
  /// $STATIC and $DYNAMIC are array-storage directives, not branches. QBasic is an interpreter but
  /// not a DEFERRED one: its environment syntax-checks the whole program, so unlike BASICA and
  /// GW-BASIC there is no line it declines to parse just because control skipped it.
  /// </summary>
  private static string? NoUnreachableTextConstruct(Dialect dialect) {
    if (dialect == Dialect.Qbasic)
      return "QBasic syntax-checks the whole program rather than deferring per line, so no line goes unparsed";
    if (!dialect.IsInterpreter() && dialect.Family() == DialectFamily.Microsoft)
      return "no conditional compilation in this family; $STATIC/$DYNAMIC are storage directives, not branches";
    return null;
  }

  /// <summary>
  /// How a dialect spells "control cannot reach this".
  ///
  /// The two lineages answer differently and both answers are right, but the interpreter case has a
  /// trap in it. BASICA and GW-BASIC parse a whole LINE at a time, so
  /// <c>IF 0 THEN &lt;garbage&gt;</c> still fails: the garbage is on the line being parsed, and the
  /// false condition only stops it being EXECUTED. What is genuinely never parsed is a line control
  /// jumps over - <c>10 IF -1 GOTO 30</c> leaves line 20 untouched - which is why the dead branch here
  /// is a skipped LINE and not a dead clause.
  ///
  /// A COMPILER parses everything it is given, so the only text it genuinely never sees is what the
  /// preprocessor removed: <c>$IF 0 ... $ENDIF</c>.
  /// </summary>
  private static string DeadBranchSource(Dialect dialect)
    => dialect.IsInterpreter()
      ? string.Join("\n", Numbered(["IF -1 GOTO 40", _garbage, "PRINT 1", "END"])) + "\n"
      : $"$IF 0\n{_garbage}\n$ENDIF\nPRINT 1\nEND\n";

  /// <summary>
  /// The same line, reached. <c>IF 0 GOTO 40</c> falls through onto the garbage, so the interpreter
  /// parses it and fails - which is what makes the dead-branch case above a real distinction rather
  /// than the compiler simply never looking at anything.
  /// </summary>
  private static string LiveBranchSource(Dialect dialect)
    => dialect.IsInterpreter()
      ? string.Join("\n", Numbered(["IF 0 GOTO 40", _garbage, "PRINT 1", "END"])) + "\n"
      : $"$IF 1\n{_garbage}\n$ENDIF\nPRINT 1\nEND\n";

  /// <summary>
  /// D3 - malformed source control cannot reach compiles, AND says so.
  ///
  /// The warning is half the claim. Silently accepting unreachable rubbish is indistinguishable from
  /// not having looked, and the whole point of matching the interpreters here is that the acceptance
  /// is deliberate rather than accidental.
  /// </summary>
  internal static DialectBattery.Measurement DeadBranch(Dialect dialect) {
    // A compiled Microsoft dialect has no way to express "control cannot reach this". QuickBASIC and
    // PDS have no conditional compilation - `$STATIC` and `$DYNAMIC` are the only metacommands, and
    // they are array-storage directives - so there is no construct to test rather than a construct
    // that fails. Reporting that as a gap would be inventing one.
    if (NoUnreachableTextConstruct(dialect) is { } why)
      return new(DialectBattery.State.NotApplicable, 0, 0, why);

    var source = DeadBranchSource(dialect);
    var accepted = Compile(source, dialect).Accepted;
    var warned = accepted && Warnings(source, dialect) > 0;
    var failed = new List<string>();
    if (!accepted)
      failed.Add("unreachable malformed source was rejected");
    else if (!warned)
      failed.Add("accepted but silent - acceptance must be deliberate, not indistinguishable from not looking");
    return Report(failed.Count == 0 ? 1 : 0, 1, failed, "held");
  }

  /// <summary>D4 - the same malformed source, where control CAN reach it, is a diagnostic.</summary>
  internal static DialectBattery.Measurement LiveBranch(Dialect dialect) {
    if (NoUnreachableTextConstruct(dialect) is { } why)
      return new(DialectBattery.State.NotApplicable, 0, 0, "the dead-branch dimension's reason: " + why);

    var result = Compile(LiveBranchSource(dialect), dialect);
    var failed = new List<string>();
    if (result.Accepted)
      failed.Add("reachable malformed source was accepted");
    else if (!result.Controlled)
      failed.Add($"rejected, but not cleanly: {result.Why}");
    return Report(failed.Count == 0 ? 1 : 0, 1, failed, "held");
  }

  /// <summary>
  /// D7 - the runtime implementation the dialect selects, read out of the lowered IR.
  ///
  /// Each claim is checked in BOTH directions: the marker must be there where the dialect uses that
  /// implementation and absent where it does not. Only checking the positive half would pass a
  /// compiler that emitted the marker unconditionally, which is precisely the bug worth catching -
  /// two dialects lowering to the same shape and only the callee telling them apart.
  /// </summary>
  internal static DialectBattery.Measurement RuntimeSelection(Dialect dialect) {
    int total = 0, covered = 0;
    var failed = new List<string>();
    foreach (var claim in DialectRuntimeClaims.All) {
      ++total;
      var source = dialect.IsGwBasica()
        ? string.Join("\n", Numbered([.. claim.Body.Split('\n')])) + "\n"
        : claim.Body + "\n";

      string ir;
      try {
        var tokens = Preprocessor.Expand(_file, new MemorySource(source), dialect);
        var model = Binder.Bind(Parser.Parse(tokens, _file, dialect), dialect);
        if (model.Errors.Count > 0) {
          failed.Add($"{claim.Id}: the probe program did not bind ({model.Errors[0].Message})");
          continue;
        }
        var module = IrLowering.TryLowerModule(model, out var why);
        if (module is null) {
          failed.Add($"{claim.Id}: lowering declined ({why})");
          continue;
        }
        ir = string.Join("\n", module.Functions.Where(f => !f.IsDeclaration).Select(IrPrinter.Print))
             + string.Join("\n", module.Functions.Select(f => f.Name));
      } catch (Exception e) {
        failed.Add($"{claim.Id}: {e.GetType().Name}");
        continue;
      }

      var present = ir.Contains(claim.Marker, StringComparison.OrdinalIgnoreCase);
      if (present == claim.Applies(dialect))
        ++covered;
      else
        failed.Add(claim.Applies(dialect)
          ? $"{claim.Id}: '{claim.Marker}' is missing - {claim.Why}"
          : $"{claim.Id}: '{claim.Marker}' is present, but this dialect does not use it - {claim.Why}");
    }
    return Report(covered, total, failed, "selected as the dialect requires");
  }

  /// <summary>
  /// D9 - each metastatement really changes the produced executable.
  ///
  /// A directive that parses and is then ignored is worse than one that is rejected: the source says
  /// "target an 80386" and the program does not, silently. So the same body is compiled twice under
  /// two SETTINGS of the directive - not with and without it - and the images must differ.
  /// </summary>
  internal static DialectBattery.Measurement Metastatements(Dialect dialect) {
    if (!DialectMetaClaims.Applies(dialect))
      return new(DialectBattery.State.NotApplicable, 0, 0,
        "no compiler metastatements in this family; REM $STATIC/$DYNAMIC are array-storage directives");

    int total = 0, covered = 0;
    var failed = new List<string>();
    foreach (var claim in DialectMetaClaims.All) {
      if (claim.Applies is { } applies && !applies(dialect))
        continue;                                // the claim is about dialects that have the feature
      ++total;
      var first = Image(claim.Directive + "\n" + claim.Body, dialect);
      var second = Image(claim.Against + "\n" + claim.Body, dialect);

      // A CPU-tier claim about inline assembly is not "the images differ" - it is "the narrower target
      // REFUSES an instruction it cannot execute". Accepting it produces an image that faults on the
      // very machine the source named, which no amount of difference between two images would show.
      if (claim.Kind == DialectMetaClaims.Kind.RefusedUnderDirective) {
        if (second is null)
          failed.Add($"{claim.Id}: the body did not compile even under '{claim.Against}', so the claim is untestable");
        else if (first is null)
          ++covered;
        else
          failed.Add($"{claim.Id}: '{claim.Directive}' accepted it - {claim.Why}");
        continue;
      }

      if (first is null || second is null) {
        failed.Add($"{claim.Id}: {(first is null ? claim.Directive : claim.Against)} did not compile");
        continue;
      }
      if (!first.SequenceEqual(second))
        ++covered;
      else
        failed.Add($"{claim.Id}: '{claim.Directive}' and '{claim.Against}' produce the SAME image, "
                   + $"so the directive changed nothing - it should change {claim.Why}");
    }
    return Report(covered, total, failed, "reflected in the image");
  }

  /// <summary>
  /// D10 - the documented quirks, and how many of them this dialect actually reproduces.
  ///
  /// The catalogue is <c>docs/QUIRKS.md</c>, whose "Our behavior" column already draws the distinction
  /// this dimension needs: a quirk is either emulated (and, where a genuine binary exists, verified
  /// against it) or it is <b>pending oracle</b> - documented, understood in outline, and deliberately
  /// NOT cloned because the precise wrong behaviour was never written down well enough to clone
  /// safely. Guessing at a bug's exact output is worse than not emulating it, because the guess looks
  /// like fidelity.
  ///
  /// So the probe counts rather than judges: it reads the table and reports how many quirks are
  /// reproduced against how many are documented. That number is the honest answer to "does this
  /// dialect mimic its bugs", and it moves only when the catalogue or the emulation does.
  /// </summary>
  internal static DialectBattery.Measurement Quirks(Dialect dialect, string repositoryRoot) {
    var path = Path.Combine(repositoryRoot, "docs", "QUIRKS.md");
    if (!File.Exists(path))
      return Unprobed("docs/QUIRKS.md is not present");

    // The Borland lineage is what the catalogue documents - it is a PowerBASIC quirks list. The
    // Microsoft dialects have their own oddities, but they are pinned by the tests/diff corpora
    // rather than enumerated here, so claiming a count for them would be claiming a catalogue that
    // does not exist.
    if (dialect.Family() != DialectFamily.Borland)
      return new(DialectBattery.State.Unprobed, 0, 0,
        "docs/QUIRKS.md catalogues the PowerBASIC lineage; the Microsoft dialects need their own list "
        + "before a count here would mean anything");

    var rows = File.ReadAllLines(path)
      .Where(line => line.StartsWith("| ", StringComparison.Ordinal) && char.IsDigit(line[2]))
      .ToList();
    if (rows.Count == 0)
      return Unprobed("no quirk rows found in docs/QUIRKS.md");

    var pending = rows.Where(r => r.Contains("Pending oracle", StringComparison.OrdinalIgnoreCase)).ToList();
    var reproduced = rows.Count - pending.Count;
    var names = pending
      .Select(r => r.Split('|', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "?")
      .ToList();

    return pending.Count == 0
      ? new(DialectBattery.State.Held, reproduced, rows.Count, $"all {rows.Count} catalogued quirks are emulated")
      : new(DialectBattery.State.Partial, reproduced, rows.Count,
          $"{pending.Count} pending oracle (the wrong behaviour is not documented precisely enough to "
          + $"clone safely, and a guess would look like fidelity): {string.Join(", ", names)}");
  }

  /// <summary>
  /// D11 - bit-exact arithmetic, in the one part that can be settled without a vintage binary: the
  /// literal's own bits.
  ///
  /// This is deliberately a partial answer and says so. Comparing whole computations bit for bit
  /// needs the genuine implementation to compare against, which is the runtime-differential harness's
  /// job and needs the toolchains present. What is checkable here, and worth checking first, is that
  /// the numbers the two programs START from are the same: a decimal-to-binary conversion that is one
  /// unit in the last place out makes every later agreement meaningless.
  /// </summary>
  internal static DialectBattery.Measurement BitExact(Dialect dialect) {
    int total = 0, covered = 0;
    var failed = new List<string>();
    foreach (var claim in DialectBitExactClaims.All) {
      ++total;
      var bound = BindExpressionType(claim.Literal, "", dialect);
      if (bound is null) {
        failed.Add($"{claim.Literal}: the dialect refused the literal");
        continue;
      }
      if (LiteralBits(claim.Literal, dialect) is not { } bits) {
        failed.Add($"{claim.Literal}: no constant reached the bound tree");
        continue;
      }
      if (bits == claim.DoubleBits)
        ++covered;
      else
        failed.Add($"{claim.Literal}: got 0x{bits:X16}, want 0x{claim.DoubleBits:X16} - {claim.Why}");
    }
    return Report(covered, total, failed, "converted to the exact IEEE-754 bits");
  }

  /// <summary>The binary64 pattern the front end produced for a float literal, or null.</summary>
  private static ulong? LiteralBits(string literal, Dialect dialect) {
    var lines = new List<string> { "probeResult# = " + literal, "END" };
    var source = string.Join("\n", dialect.IsGwBasica() ? Numbered(lines) : lines) + "\n";
    try {
      var tokens = Preprocessor.Expand(_file, new MemorySource(source), dialect);
      var model = Binder.Bind(Parser.Parse(tokens, _file, dialect), dialect);
      if (model.Errors.Count > 0)
        return null;
      foreach (var statement in model.MainBody)
        if (statement is AssignStmt { Value: FloatLiteralExpr f })
          return BitConverter.DoubleToUInt64Bits(f.Value);
      return null;
    } catch {
      return null;
    }
  }

  /// <summary>The produced executable, or null when the program did not compile.</summary>
  private static byte[]? Image(string source, Dialect dialect) {
    try {
      var tokens = Preprocessor.Expand(_file, new MemorySource(source), dialect);
      var model = Binder.Bind(Parser.Parse(tokens, _file, dialect), dialect);
      if (model.Errors.Count > 0)
        return null;
      var generator = new PowerBasic.Compiler.CodeGen.CodeGenerator(model);
      var image = generator.EmitExecutable();
      return generator.Errors.Count > 0 ? null : image;
    } catch {
      return null;
    }
  }

  private static int Warnings(string source, Dialect dialect) {
    try {
      var tokens = Preprocessor.Expand(_file, new MemorySource(source), dialect);
      return Binder.Bind(Parser.Parse(tokens, _file, dialect), dialect).Warnings.Count;
    } catch {
      return 0;
    }
  }

  private static DialectBattery.Measurement Report(int covered, int total, List<string> failed, string verb)
    => failed.Count == 0
      ? new(DialectBattery.State.Held, covered, total, $"all {total} {verb}")
      : new(DialectBattery.State.Partial, covered, total,
          $"{failed.Count} not {verb}: {string.Join(", ", failed.Take(6))}{(failed.Count > 6 ? ", ..." : "")}");


  /// <summary>
  /// D6 - the dialect's numeric typing. A claim that applies must bind to the type it names; a claim
  /// that does not apply must be REJECTED, not bound to something plausible.
  ///
  /// That second half is the point. A dialect quietly accepting `n???` and calling it a LONG compiles,
  /// runs, and gives a different answer only at the boundary - which is exactly the class of bug a
  /// conformance battery exists to catch.
  /// </summary>
  internal static DialectBattery.Measurement NumericTypes(Dialect dialect) {
    int total = 0, covered = 0;
    var failed = new List<string>();
    foreach (var claim in DialectNumericClaims.All) {
      ++total;
      var applies = claim.Applies(dialect);
      var bound = BindExpressionType(claim.Expression, claim.Declaration, dialect);

      if (applies) {
        if (bound is { } type && claim.Expected(type))
          ++covered;
        else
          failed.Add($"{claim.Id} wanted {claim.Describe}, got {(bound?.ToString() ?? "a rejection")}");
      } else if (bound is null)
        ++covered;                               // refused, which is what a foreign spelling must do
      else
        failed.Add($"{claim.Id} is not this dialect's, yet it bound to {bound}");
    }
    return Report(covered, total, failed, "as claimed");
  }

  /// <summary>
  /// The bound type of an expression, or null when the dialect refuses the source at all.
  ///
  /// The expression is put on the RIGHT of an assignment and read back from the bound tree, rather
  /// than matched by spelling: the binder's map is keyed by node identity, and asking it about the
  /// node the parser produced is the only way to be sure the answer is about this expression and not
  /// about one that happens to read the same.
  /// </summary>
  private static PbType? BindExpressionType(string expression, string declaration, Dialect dialect) {
    var lines = new List<string>();
    if (declaration.Length > 0)
      lines.Add(declaration);
    // The target is a DOUBLE, not an EXT. It was `probeResult##` until the EXT suffix was gated -
    // at which point the probe's own scaffolding became foreign syntax in ten dialects and reported
    // thirteen failures that were entirely its own fault.
    lines.Add("probeResult# = " + expression);
    lines.Add("END");
    var source = string.Join("\n", dialect.IsGwBasica() ? Numbered(lines) : lines) + "\n";
    try {
      var unit = Parser.Parse(Lexer.Tokenize(source, _file, dialect), _file, dialect);
      var model = Binder.Bind(unit, dialect);
      if (model.Errors.Count > 0)
        return null;
      foreach (var statement in model.MainBody)
        if (statement is AssignStmt assign && model.ExpressionTypes.TryGetValue(assign.Value, out var type))
          return type;
      return null;
    } catch {
      return null;
    }
  }

  /// <summary>
  /// BASICA and GW-BASIC need a line number on every non-empty physical line - it is a real label, not
  /// decoration, so a probe that omits it measures the numbering rather than the thing it asked about.
  /// </summary>
  private static List<string> Numbered(List<string> lines)
    => [.. lines.Select((line, i) => $"{(i + 1) * 10} {line}")];

  /// <summary>A dimension with no probe yet, carrying why it is worth having one.</summary>
  internal static DialectBattery.Measurement Unprobed(string note)
    => new(DialectBattery.State.Unprobed, 0, 0, note);
}