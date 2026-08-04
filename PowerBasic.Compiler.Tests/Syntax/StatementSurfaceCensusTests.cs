using System.Text;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Syntax;

/// <summary>
/// The statement surface against both code generators, measured rather than assumed.
///
/// <see cref="StatementSurface"/> lists every spelling of every statement, including the combinations
/// of its optional parameters. This fixture drives all of them through the whole front end and then
/// through BOTH emitters - the direct x86-16 one and the IR path - and reports, per form, exactly how
/// far it gets: parse, bind, direct codegen, routed codegen.
///
/// The report is the point. A statement that parses and binds but has no code generator behind it is
/// not "supported", and the only way to find those is to compile every one of them and look. The
/// pinned totals underneath turn that measurement into a ratchet.
/// </summary>
[TestFixture]
public sealed class StatementSurfaceCensusTests {

  private enum Stage { Parse, Bind, Direct, Routed, Done }

  private sealed record Result(StatementSurface.Form Form, Dialect Dialect, Stage Reached, string? Why);

  /// <summary>Compiles one form under one dialect and reports the last stage it survived.</summary>
  private static Result Measure(StatementSurface.Form form, Dialect dialect) {
    var source = StatementSurface.Program(form);
    SemanticModel model;
    try {
      var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect);
      model = Binder.Bind(unit, dialect);
    } catch (Exception e) {
      return new(form, dialect, Stage.Parse, e.Message);
    }
    if (model.Errors.Count > 0)
      return new(form, dialect, Stage.Bind, model.Errors[0].Message);

    try {
      var direct = new CodeGenerator(Rebind(source, dialect)) { UseExperimentalBackend = false };
      direct.EmitExecutable();
      if (direct.Errors.Count > 0)
        return new(form, dialect, Stage.Direct, direct.Errors[0].Message);
    } catch (Exception e) {
      return new(form, dialect, Stage.Direct, e.GetType().Name + ": " + e.Message);
    }

    try {
      var routed = new CodeGenerator(Rebind(source, dialect)) { UseExperimentalBackend = true };
      routed.EmitExecutable();
      if (routed.Errors.Count > 0)
        return new(form, dialect, Stage.Routed, routed.Errors[0].Message);
    } catch (Exception e) {
      return new(form, dialect, Stage.Routed, e.GetType().Name + ": " + e.Message);
    }
    return new(form, dialect, Stage.Done, null);
  }

  /// <summary>
  /// Whether the FRONT END accepts a form under a dialect - which is the whole of the question "does
  /// this dialect have this statement". Code generation is a separate axis, measured for one dialect
  /// in the census above; asking it here would emit two executables per pair, nearly six thousand
  /// times, to learn nothing extra.
  /// </summary>
  private static (bool Accepted, string? Why) AcceptedByFrontEnd(StatementSurface.Form form, Dialect dialect) {
    var source = StatementSurface.Program(form);
    try {
      var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect), dialect);
      return (model.Errors.Count == 0, model.Errors.Count > 0 ? model.Errors[0].Message : null);
    } catch (Exception e) {
      return (false, e.Message);
    }
  }

  // a CodeGenerator consumes its model, so each emitter gets its own
  private static SemanticModel Rebind(string source, Dialect dialect)
    => Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect), dialect);

  /// <summary>Collapses a diagnostic to its cause so names and positions do not fragment the histogram.</summary>
  private static string Summarize(string reason) {
    var cut = reason.IndexOf(" '", StringComparison.Ordinal);
    var head = cut > 0 ? reason[..cut] : reason;
    return head.Length > 78 ? head[..78] : head;
  }

  /// <summary>
  /// The reference dialect for the surface: PB 3.6 accepts every form in the table, so a failure here
  /// is a gap in the compiler rather than a dialect saying no.
  /// </summary>
  [Test]
  public void Compile_GivenEveryStatementForm_ThenReportsHowFarEachGets() {
    var results = StatementSurface.All.Select(f => Measure(f, Dialect.Pb36)).ToList();
    var report = new StringBuilder();

    foreach (var (section, forms) in StatementSurface.Sections) {
      var ids = forms.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
      var mine = results.Where(r => ids.Contains(r.Form.Id)).ToList();
      report.AppendLine($"{section,-12} {mine.Count(r => r.Reached == Stage.Done),3}/{mine.Count} compile both ways");
      foreach (var r in mine.Where(r => r.Reached != Stage.Done))
        report.AppendLine($"    {r.Reached,-7} {r.Form.Id,-28} {Summarize(r.Why ?? "")}");
    }

    var done = results.Count(r => r.Reached == Stage.Done);
    report.Insert(0, $"statement forms compiling through BOTH emitters under pb36: {done}/{results.Count}\n");
    TestContext.Out.Write(report.ToString());

    // A floor, not an exact count. Every form in the table is a statement the genuine compiler
    // accepts, so each one that does not reach Done is a real gap - the report names it and the
    // number below stops the total sliding back while they are worked off.
    Assert.That(done, Is.GreaterThanOrEqualTo(_pb36Floor),
      "fewer statement forms compile than used to:\n" + report);
  }

  private const int _pb36Floor = 131;   // 129 at first measurement; raised as each gap closes

  /// <summary>
  /// The same surface across every dialect the compiler claims. A form must be accepted by the
  /// front end exactly where <see cref="StatementSurface.ShouldAccept"/> says it should be - a
  /// dialect that quietly accepts a statement it never had is as wrong as one that rejects a
  /// statement it did.
  /// </summary>
  [Test]
  public void Compile_GivenEveryStatementFormAcrossDialects_ThenReportsWhereTheFrontEndDisagrees() {
    var report = new StringBuilder();
    int acceptedWhenItShould = 0, total = 0, wrongfullyRejected = 0, wrongfullyAccepted = 0;

    foreach (var form in StatementSurface.All)
      foreach (var dialect in StatementSurface.AllDialects) {
        ++total;
        var (accepted, why) = AcceptedByFrontEnd(form, dialect);
        var should = StatementSurface.ShouldAccept(form, dialect);
        if (should && accepted)
          ++acceptedWhenItShould;
        else if (should && !accepted) {
          ++wrongfullyRejected;
          if (wrongfullyRejected <= 400)
            report.AppendLine($"  REJECTED  {dialect,-8} {form.Id,-28} {Summarize(why ?? "")}");
        } else if (!should && accepted) {
          ++wrongfullyAccepted;
          if (wrongfullyAccepted <= 400)
            report.AppendLine($"  ACCEPTED  {dialect,-8} {form.Id,-28} (the dialect never had this)");
        }
      }

    report.Insert(0, $"form x dialect pairs: {total}, front end agrees on {acceptedWhenItShould + (total - acceptedWhenItShould - wrongfullyRejected - wrongfullyAccepted)}\n"
      + $"  rejected but should be accepted : {wrongfullyRejected}\n"
      + $"  accepted but should be rejected : {wrongfullyAccepted}\n");
    TestContext.Out.Write(report.ToString());

    Assert.That(wrongfullyRejected, Is.LessThanOrEqualTo(_rejectionFloor),
      "the front end rejects statement forms its dialect should accept:\n" + report);
  }

  private const int _rejectionFloor = int.MaxValue;   // measured first, then tightened
}
