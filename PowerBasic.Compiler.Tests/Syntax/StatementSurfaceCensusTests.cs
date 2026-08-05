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

    // The gaps are pinned by NAME, not by count. A count is a ratchet that only catches the total
    // sliding back, so implementing one statement while breaking another nets to zero and says
    // nothing; and it never notices a gap being closed, which leaves the number quietly pessimistic.
    // Set equality fails in both directions: a form that stops compiling is not in the set, and a
    // form that starts compiling has to be struck from it.
    Assert.That(results.Where(r => r.Reached != Stage.Done).Select(r => r.Form.Id), Is.EquivalentTo(_pb36Gaps),
      "the set of statement forms that do not compile has changed:\n" + report);
  }

  /// <summary>
  /// The statement forms that reach no code generator under pb36 - every one a statement the genuine
  /// compiler accepts, so every one a real gap rather than a dialect saying no.
  ///
  /// `files` has no emitter case at all; the three `circle.*` forms are the arc and aspect-ratio
  /// arguments, which the midpoint circle in the runtime cannot express - an ellipse needs 32-bit
  /// arithmetic on this target, since the radius squared leaves 16 bits somewhere around 181.
  /// Strike a name from here when it starts compiling - the test insists on it.
  /// </summary>
  private static readonly string[] _pb36Gaps = [
    "circle.arc", "circle.aspect", "circle.elided.color",
    "files",
  ];

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

    // Both directions are errors, and both are currently zero over all 4237 pairs, so both are pinned
    // there. The second half is the half that is easy to forget: a dialect quietly accepting a
    // statement it never had is a fidelity bug exactly like rejecting one it did, and it is the more
    // likely of the two to be introduced, because adding a feature without a gate entry costs nothing
    // and fails nowhere else.
    Assert.Multiple(() => {
      Assert.That(wrongfullyRejected, Is.Zero,
        "the front end rejects statement forms its dialect should accept:\n" + report);
      Assert.That(wrongfullyAccepted, Is.Zero,
        "the front end accepts statement forms whose dialect never had them:\n" + report);
    });
  }
}
