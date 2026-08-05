using System.Text;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Semantics;

/// <summary>
/// Every built-in function, asked the same questions.
///
/// <see cref="Intrinsics"/> is a table of 130-odd entries, each declaring the arity it accepts and
/// what it returns, and until this fixture existed nothing walked it. An intrinsic could be added
/// with the wrong <see cref="IntrinsicInfo.MaxArgs"/>, or gated in
/// <c>DialectFacts.IntrinsicGate</c> and never gated in practice, or bound happily by the binder and
/// then met with "not yet generated" by the code generator, and no test would have noticed - the
/// hand-written fixtures cover the intrinsics somebody thought to write a test for.
///
/// The questions are deliberately cheap ones that hold for every entry regardless of what it does:
/// the arity diagnostic fires outside the declared range and stays quiet inside it, a gated
/// intrinsic is refused below its gate, and an intrinsic that binds also generates code.
/// </summary>
[TestFixture]
public sealed class IntrinsicCensusTests {

  /// <summary>Declarations every probe program gets, so a call has something plausible to name.</summary>
  private const string _preamble = """
    DECLARE SUB P1()
    DIM s AS STRING
    DIM arr%(1 TO 4)
    n% = 1
    """;

  private const string _epilogue = "\nEND\nSUB P1()\nEND SUB\n";

  /// <summary>Binds a program body under a dialect and returns its diagnostics (a throw is one too).</summary>
  private static List<string> Diagnose(string body, Dialect dialect) {
    var source = _preamble + "\n" + body + _epilogue;
    try {
      var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect), dialect);
      return model.Errors.Select(e => e.Message).ToList();
    } catch (Exception e) {
      return [e.Message];
    }
  }

  /// <summary>
  /// Whether the binder complained about the ARITY specifically, as opposed to the types.
  ///
  /// The catalog spells suffixed names in full (MAX%, LEFT$) while the diagnostic uses the bare name,
  /// so the comparison drops the suffix rather than matching the spelling.
  /// </summary>
  private static bool ComplainedAboutArity(IEnumerable<string> errors, IntrinsicInfo intrinsic)
    => errors.Any(e => e.Contains("argument(s), got", StringComparison.Ordinal)
                    && e.StartsWith(intrinsic.Name.TrimEnd('%', '&', '!', '#', '$', '?'), StringComparison.OrdinalIgnoreCase));

  /// <summary>
  /// Whether the call was turned away for its LENGTH rather than for its argument types - either by
  /// the arity check, or by a parser that stopped reading arguments before the call ended.
  /// </summary>
  private static bool RefusedForItsShape(List<string> errors, IntrinsicInfo intrinsic)
    => ComplainedAboutArity(errors, intrinsic)
    || errors.Any(e => e.Contains("expected ')'", StringComparison.Ordinal)
                    || e.Contains("unexpected ')'", StringComparison.Ordinal));

  /// <summary>
  /// Argument spellings to try, as (first argument, the rest). An intrinsic is not a uniform thing -
  /// LEFT$ wants a string then a number, INSTR can want a number then two strings, UBOUND wants a
  /// bare array name and CODEPTR a procedure name - so rather than encode a signature per entry the
  /// census tries a handful of shapes and keeps whichever one the binder accepts without complaint.
  /// </summary>
  /// The separator is part of the shape because it is not always a comma: the PEEK family's
  /// two-argument form is the pb36 segmented PEEK(seg:offset), and spelling it with a comma is a
  /// parse error rather than a two-argument call.
  private static readonly Shape[] _shapes = [
    new("n%", "n%"), new("s", "s"), new("s", "n%"), new("n%", "s"), new("arr%", "n%"), new("P1", "n%"),
    new("n%", "n%", ": "),
  ];

  private sealed record Shape(string First, string Others, string Separator = ", ");

  /// <summary>
  /// A call to <paramref name="intrinsic"/> with <paramref name="count"/> arguments of one shape.
  /// A call of no arguments is spelled bare - TIMER, not TIMER() - because that is how BASIC spells
  /// it and empty parentheses are a parse error rather than a zero-argument call.
  /// </summary>
  private static string CallWith(IntrinsicInfo intrinsic, int count, Shape shape)
    => count == 0
      ? $"PRINT {intrinsic.Name}"
      : $"PRINT {intrinsic.Name}({string.Join(shape.Separator, Enumerable.Range(0, count).Select(i => i == 0 ? shape.First : shape.Others))})";

  /// <summary>Whether any probe shape gets a call of this length past the compiler with no complaint.</summary>
  private static bool AcceptedAtAnyShape(IntrinsicInfo intrinsic, int count)
    => _shapes.Any(shape => Diagnose(CallWith(intrinsic, count, shape), Dialect.Pb36).Count == 0);

  /// <summary>Whether EVERY probe shape is turned away for the call's length rather than its types.</summary>
  private static bool RefusedForItsLengthAtEveryShape(IntrinsicInfo intrinsic, int count)
    => _shapes.All(shape => RefusedForItsShape(Diagnose(CallWith(intrinsic, count, shape), Dialect.Pb36), intrinsic));

  /// <summary>
  /// The arity range each intrinsic declares is the range the compiler enforces.
  ///
  /// For each entry the census first finds an argument shape the compiler accepts outright somewhere
  /// inside the declared range - that call is the control, proving the shape is a legal one - and
  /// then re-issues it one argument short and one argument long. Both must be refused. Anchoring on
  /// a known-clean call is what makes "refused" mean something: without it, a rejection could just as
  /// easily be the placeholder arguments being the wrong types, and an intrinsic that accepts a
  /// wrong-length call would pass vacuously.
  ///
  /// Refusal is not required to come from the arity check specifically. PEEK and ISTRUE are refused
  /// by the parser before the binder ever counts arguments, which is a perfectly good answer; the
  /// question is whether a wrong-length call gets through, not which stage stops it.
  /// </summary>
  [Test]
  public void Intrinsics_GivenEveryCatalogEntry_ThenTheDeclaredArityIsTheEnforcedArity() {
    var report = new StringBuilder();
    var wrong = new List<string>();
    var unprobeable = new List<string>();

    foreach (var intrinsic in Intrinsics.All.OrderBy(i => i.Name, StringComparer.Ordinal)) {
      // the control: some length inside the range that some shape gets past the compiler with no
      // complaint at all. MaxArgs runs to 64 for CHR$, so only the first few lengths are tried.
      var lengths = Enumerable.Range(intrinsic.MinArgs, Math.Min(intrinsic.MaxArgs, intrinsic.MinArgs + 2) - intrinsic.MinArgs + 1);
      if (!lengths.Any(count => AcceptedAtAnyShape(intrinsic, count))) {
        unprobeable.Add(intrinsic.Name);
        report.AppendLine($"  no control  {intrinsic.Name,-12} (declares {intrinsic.MinArgs}..{intrinsic.MaxArgs}) - no probe shape binds cleanly");
        continue;
      }

      // One too few, where there is such a thing. The bare-name spelling is deliberately not tested:
      // intrinsic names are not reserved words, so `ABS = 7 : PRINT ABS` is a program about a
      // variable called ABS and compiles clean, exactly as it does in the genuine compiler. That is
      // not a zero-argument call getting through - it is a different construct - so "too few" only
      // means something once at least one argument is still there to count.
      if (intrinsic.MinArgs > 1 && AcceptedAtAnyShape(intrinsic, intrinsic.MinArgs - 1)) {
        wrong.Add($"{intrinsic.Name}: {intrinsic.MinArgs - 1} arguments accepted, below the declared minimum of {intrinsic.MinArgs}");
        report.AppendLine($"  TOO LAX     {intrinsic.Name,-12} accepted {intrinsic.MinArgs - 1} (declares {intrinsic.MinArgs}..{intrinsic.MaxArgs})");
      }

      // one too many
      if (AcceptedAtAnyShape(intrinsic, intrinsic.MaxArgs + 1)) {
        wrong.Add($"{intrinsic.Name}: {intrinsic.MaxArgs + 1} arguments accepted, above the declared maximum of {intrinsic.MaxArgs}");
        report.AppendLine($"  TOO LAX     {intrinsic.Name,-12} accepted {intrinsic.MaxArgs + 1} (declares {intrinsic.MinArgs}..{intrinsic.MaxArgs})");
      }

      // And neither endpoint of the declared range is turned away for its LENGTH by every spelling
      // there is. The types may still be wrong for a shape this uniform - that is not what is being
      // asked - but a call of a length the table permits must have SOME spelling the compiler takes.
      // This half is what makes the test more than a restatement of the table: the binder's arity
      // check reads MinArgs and MaxArgs, so it can never disagree with them, but PEEK, ISTRUE, MAX%
      // and the UBOUND and CODEPTR families are shaped by hand in the parser or in their own binder
      // branch, and those hand-written limits CAN drift away from what the catalog claims.
      foreach (var endpoint in new[] { intrinsic.MinArgs, intrinsic.MaxArgs }.Distinct())
        if (RefusedForItsLengthAtEveryShape(intrinsic, endpoint)) {
          wrong.Add($"{intrinsic.Name}: {endpoint} arguments refused, inside the declared range {intrinsic.MinArgs}..{intrinsic.MaxArgs}");
          report.AppendLine($"  TOO TIGHT   {intrinsic.Name,-12} refused {endpoint} (declares {intrinsic.MinArgs}..{intrinsic.MaxArgs})");
        }
    }

    var catalog = Intrinsics.All.Count();
    report.Insert(0, $"intrinsics in the catalog: {catalog}, probed: {catalog - unprobeable.Count}, "
      + $"arity enforced as declared: {catalog - unprobeable.Count - wrong.Select(w => w.Split(':')[0]).Distinct().Count()}\n");
    TestContext.Out.Write(report.ToString());

    Assert.Multiple(() => {
      Assert.That(wrong, Is.Empty, "the compiler does not enforce the arity these intrinsics declare:\n" + report);
      // An entry no probe shape can call cleanly is not tested by the census above, so the set is
      // pinned: it may shrink as shapes are added, but a new intrinsic must not quietly join it.
      Assert.That(unprobeable, Is.EquivalentTo(_noCleanProbe), "the set of intrinsics no probe shape can call has changed:\n" + report);
    });
  }

  /// <summary>
  /// Intrinsics that no shape in <see cref="_shapes"/> calls cleanly, so the arity census cannot
  /// anchor on a control call for them. Filled in from the census report; shrink it by teaching
  /// <see cref="_shapes"/> the argument spelling these want, rather than by widening it.
  /// </summary>
  private static readonly string[] _noCleanProbe = [];

  /// <summary>
  /// Every intrinsic that BINDS also generates code.
  ///
  /// The arity census above asks whether the front end takes a call. This asks the question after
  /// it: an intrinsic the binder accepts and the code generator has no case for gets "not yet
  /// generated" - a hard error, so it is honest, but nothing was counting them and the catalog does
  /// not say which. It is the statement-surface census's question asked of the other table.
  /// </summary>
  [Test]
  public void Intrinsics_GivenEveryCatalogEntry_ThenTheOnesThatBindAlsoReachCodeGeneration() {
    var report = new StringBuilder();
    var noCodeGen = new List<string>();
    var unprobeable = new List<string>();

    foreach (var intrinsic in Intrinsics.All.OrderBy(i => i.Name, StringComparer.Ordinal)) {
      // EVERY spelling that binds is tried, not the first: CODEPTR takes a procedure name and
      // STRPTR a string, and both also bind happily against a plain integer that the code generator
      // then refuses. Reporting the first shape's failure would name seven intrinsics that work.
      var lengths = Enumerable.Range(intrinsic.MinArgs, Math.Min(intrinsic.MaxArgs, intrinsic.MinArgs + 2) - intrinsic.MinArgs + 1);
      var callable = (from shape in _shapes
                      from count in lengths
                      let body = CallWith(intrinsic, count, shape)
                      where Diagnose(body, Dialect.Pb36).Count == 0
                      select body).ToList();
      if (callable.Count == 0) {
        unprobeable.Add(intrinsic.Name);
        continue;
      }

      string? complaint = null;
      var generated = false;
      foreach (var body in callable) {
        var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(_preamble + "\n" + body + _epilogue, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
        var generator = new CodeGenerator(model);
        try {
          generator.EmitExecutable();
        } catch (Exception e) {
          complaint ??= e.GetType().Name;
          continue;
        }
        if (generator.Errors.Count == 0) {
          generated = true;
          break;
        }
        complaint ??= generator.Errors[0].Message;
      }
      if (!generated) {
        noCodeGen.Add(intrinsic.Name);
        report.AppendLine($"  NO CODEGEN {intrinsic.Name,-12} {complaint}");
      }
    }

    var catalog = Intrinsics.All.Count();
    report.Insert(0, $"intrinsics: {catalog}, callable: {catalog - unprobeable.Count}, "
      + $"reaching code generation: {catalog - unprobeable.Count - noCodeGen.Count}\n");
    TestContext.Out.Write(report.ToString());

    Assert.That(noCodeGen, Is.EquivalentTo(_noCodeGeneration),
      "the set of intrinsics that bind but generate nothing has changed:\n" + report);
  }

  /// <summary>
  /// Intrinsics the binder accepts and the code generator has no case for. Each is a call a program
  /// can write and nothing compiles - measured, not assumed.
  ///
  /// Eleven when this was first run; ten once LPOS was written - the printer's print column, whose
  /// cell had just been added for LPRINT - eight once CEIL and FRAC followed, both of which are the
  /// x87 doing the work under a rounding mode exactly as INT and FIX beside them do, and six with
  /// MIN$ and MAX$.
  ///
  /// The four left are not missing cases, they are missing FEATURES: BITS, PLAY, SCREEN and FILEATTR
  /// each want something this runtime does not have - a note queue, a text page to read back, a file
  /// attribute beyond the DOS handle. Writing the case is the small part; deciding what it should
  /// answer, and against what, is the rest. FILEATTR is partial rather than absent and says so
  /// itself.
  ///
  /// Strike a name when it gains a case; the test insists either way.
  /// </summary>
  private static readonly string[] _noCodeGeneration = [
    "BITS",
    "FILEATTR",
    "PLAY",
    "SCREEN",
  ];

  /// <summary>
  /// A gated intrinsic is refused below its gate.
  ///
  /// <c>DialectFacts.IntrinsicGate</c> maps a handful of names to the feature that introduced them.
  /// The mapping existing is not the same as it being enforced, and the failure is invisible from
  /// inside a single dialect - a program using TRIM$ under pb20 simply compiles.
  /// </summary>
  [Test]
  public void Intrinsics_GivenAGatedEntry_ThenTheDialectBelowItsGateRefusesIt() {
    var gated = Intrinsics.All
      .Where(i => DialectFacts.IntrinsicGate(i.Name) is not null)
      .OrderBy(i => i.Name, StringComparer.Ordinal)
      .ToList();
    Assume.That(gated, Is.Not.Empty);

    var report = new StringBuilder();
    var ungated = new List<string>();

    foreach (var intrinsic in gated) {
      var feature = DialectFacts.IntrinsicGate(intrinsic.Name)!.Value;
      var min = DialectFacts.MinimumDialect(feature);
      var below = PreviousBorland(min);
      if (below is null) {
        report.AppendLine($"  (no earlier dialect than {min} to test {intrinsic.Name} against)");
        continue;
      }

      // the shape does not matter here: the gate is refused on the name, before the types are weighed
      var errors = Diagnose(CallWith(intrinsic, Math.Max(intrinsic.MinArgs, 1), _shapes[0]), below.Value);
      // the gate's own wording, not the arity or type complaints the placeholder arguments provoke
      var refused = errors.Any(e => e.Contains("requires", StringComparison.OrdinalIgnoreCase));
      report.AppendLine($"  {(refused ? "gated  " : "OPEN   ")} {intrinsic.Name,-12} {feature} (from {min}, tested {below})");
      if (!refused)
        ungated.Add($"{intrinsic.Name} is accepted by {below}, below its {feature} gate at {min}");
    }

    report.Insert(0, $"gated intrinsics: {gated.Count}\n");
    TestContext.Out.Write(report.ToString());

    Assert.That(ungated, Is.Empty, "these intrinsics have a gate that does not gate:\n" + report);
  }

  /// <summary>The Borland dialect immediately before <paramref name="dialect"/>, or null if it is the oldest.</summary>
  private static Dialect? PreviousBorland(Dialect dialect) {
    Dialect[] line = [Dialect.Tb10, Dialect.Tb11, Dialect.Pb20, Dialect.Pb21, Dialect.Pb30, Dialect.Pb31, Dialect.Pb32, Dialect.Pb35, Dialect.Pb36];
    var at = Array.IndexOf(line, dialect);
    return at > 0 ? line[at - 1] : null;
  }
}
