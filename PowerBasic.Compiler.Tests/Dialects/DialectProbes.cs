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

  internal static FrontEnd Compile(string source, Dialect dialect) {
    try {
      var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, _file, dialect), _file, dialect), dialect);
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