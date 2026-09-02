using System.Text;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The routed back end must DECLINE what it cannot compile, never THROW.
///
/// A decline is safe: the direct emitter compiles the function instead, and the refusal lands in the
/// coverage histogram where it can be ranked and closed. A throw is none of those things - it kills
/// the compilation with a stack trace, emits no executable, produces no diagnostic, and is INVISIBLE
/// to every census this repository keeps, because the function neither routed nor declined. After
/// <c>CodeGen/</c> is retired each remaining throw stops being a survivable fallback and becomes an
/// unconditional compiler crash.
///
/// So this fixture asserts the one property that covers all of them at once: compiling a
/// front-end-accepted program with <c>UseExperimentalBackend</c> raises NOTHING. It is deliberately
/// not a coverage measurement - it does not care whether anything routed - which is what lets it stay
/// green while a conversion from throw to decline REDUCES coverage. That trade is the correct one.
///
/// <see cref="BackendCoverageTests"/> reaches the same conclusion for the corpus half, from the other
/// direction: its census counts a raise as the FOURTH outcome beside routed, declined and
/// front-end-rejected, and fails on any. The two overlap on purpose - a census that also measures
/// coverage cannot be read as a statement about crashes alone, and it is the generated half below
/// that this fixture exists for.
///
/// Two populations, because the corpus alone is not enough. Every corpus program is a program someone
/// wrote to work, so its operands are the shapes the selector already handles: every corpus shift is
/// either 32-bit (where <c>SelectWideShift</c> declines) or a literal count in 1..31, which is why
/// <c>SHIFT RIGHT a%, n%</c> reaching the assembler with no encoding survived a green corpus AND a
/// green differential battery. The generated half therefore varies exactly the axis the corpus holds
/// constant: the same construct with a RUNTIME operand instead of a constant, and with constants
/// outside the range the immediate form can carry.
/// </summary>
[TestFixture, Category("Slow")]
public sealed class BackendNeverThrowsTests {

  private static readonly string _repoRoot =
    Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  /// <summary>
  /// Compiles <paramref name="source"/> routed and returns the exception it raised, or null.
  /// A front-end rejection is not this fixture's business and reports as no failure - the property
  /// under test is about programs the binder accepted.
  /// </summary>
  private static Exception? RoutedCompileFailure(string source, string name, bool optimize) {
    try {
      var bound = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, name, Dialect.Pb36), name, Dialect.Pb36), Dialect.Pb36);
      if (bound.Errors.Count > 0)
        return null;
      var routed = new CodeGenerator(bound) { Optimize = optimize, UseExperimentalBackend = true };
      routed.EmitExecutable();
      _ = routed.BackendRoutedNames.ToList();
      return null;
    } catch (Exception e) {
      return e;
    }
  }

  private static string Head(Exception e) {
    var frame = e.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("PowerBasic.Compiler."))?.Trim() ?? "";
    return $"{e.GetType().Name}: {e.Message}  [{frame}]";
  }

  [Test]
  public void Corpus_WhenCompiledRouted_ThenNothingThrows() {
    var dir = Path.Combine(_repoRoot, "tests");
    Assume.That(Directory.Exists(dir), "no tests/*.BAS corpus present");

    var failures = new List<string>();
    var compiled = 0;
    foreach (var file in Directory.EnumerateFiles(dir, "*.BAS", SearchOption.AllDirectories)
               .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
      var name = Path.GetFileName(file);
      var text = File.ReadAllText(file);
      foreach (var optimize in new[] { true, false }) {
        ++compiled;
        if (RoutedCompileFailure(text, name, optimize) is { } e)
          failures.Add($"{Path.GetRelativePath(dir, file).Replace('\\', '/')} " +
            $"({(optimize ? "optimized" : "unoptimized")}): {Head(e)}");
      }
    }

    TestContext.Out.WriteLine($"routed compilations attempted: {compiled}, raised: {failures.Count}");
    Assert.That(failures, Is.Empty,
      "the routed back end raised instead of declining:\n  " + string.Join("\n  ", failures));
  }

  /// <summary>
  /// The variations. Each row is a whole program; <c>{0}</c> is substituted with an expression the
  /// optimizer cannot fold, and the same row is also compiled with a constant in that slot so the
  /// generator covers both sides of every immediate-form boundary.
  ///
  /// <c>Opaque%</c> is a <c>NOINLINE FUNCTION</c> called from two sites with different arguments: one
  /// site lets IPCP prove the argument, an empty SUB is not a barrier for the IR pipeline, and a
  /// literal is folded by SCCP before any selector sees it. All three are ways a generator of this
  /// kind quietly measures nothing.
  /// </summary>
  private const string _Preamble = """
    DECLARE FUNCTION Opaque%(BYVAL v%)
    DECLARE FUNCTION OpaqueL&(BYVAL v&)
    DIM sink%, sinkl&, warm%
    warm% = Opaque%(7)
    sinkl& = OpaqueL&(70000)
    """;

  private const string _Epilogue = """
    END
    FUNCTION Opaque%(BYVAL v%) NOINLINE
      Opaque% = v% + 1
    END FUNCTION
    FUNCTION OpaqueL&(BYVAL v&) NOINLINE
      OpaqueL& = v& + 1
    END FUNCTION
    """;

  /// <summary>
  /// The operand shapes substituted into every body: a runtime value the optimizer cannot see
  /// through, and the literal boundaries of the encodings the selector picks between.
  /// </summary>
  private static readonly (string Name, string Word, string Long)[] _operands = [
    ("runtime", "Opaque%(3)", "OpaqueL&(3)"),
    ("zero", "0", "0"),
    ("one", "1", "1"),
    ("two", "2", "2"),
    ("eight", "8", "8"),
    ("fifteen", "15", "15"),
    ("sixteen", "16", "16"),
    ("thirtyone", "31", "31"),
    ("thirtytwo", "32", "32"),
    ("large", "999", "999"),
    ("negative", "-1", "-1"),
  ];

  /// <summary>
  /// The bodies. <c>{W}</c> takes the word-typed operand and <c>{L}</c> the long-typed one; a body
  /// that names neither is compiled once. Each is a construct whose selection has a narrow path -
  /// somewhere the emitted form depends on the operand being a constant of a particular size.
  /// </summary>
  private static readonly (string Name, string Body)[] _bodies = [
    ("shift-right-word", "DIM a% : a% = Opaque%(-1234) : SHIFT RIGHT a%, {W} : sink% = a%"),
    ("shift-left-word", "DIM a% : a% = Opaque%(-1234) : SHIFT LEFT a%, {W} : sink% = a%"),
    ("shift-right-long", "DIM a& : a& = OpaqueL&(-123456) : SHIFT RIGHT a&, {W} : sinkl& = a&"),
    ("shift-left-long", "DIM a& : a& = OpaqueL&(-123456) : SHIFT LEFT a&, {W} : sinkl& = a&"),
    ("rotate-word", "DIM a AS WORD : a = Opaque%(-1234) : ROTATE LEFT a, {W} : sink% = a \\ 2"),
    ("rotate-right-word", "DIM b AS WORD : b = Opaque%(-1234) : ROTATE RIGHT b, {W} : sink% = b \\ 2"),
    ("array-index-word", "DIM t%(0 TO 40) : t%(Opaque%(2)) = {W} : sink% = t%(Opaque%(3))"),
    ("array-index-long", "DIM t&(0 TO 40) : t&(Opaque%(2)) = {L} : sinkl& = t&(Opaque%(3))"),
    ("array-index-double", "DIM t#(0 TO 40) : t#(Opaque%(2)) = {W} : sink% = INT(t#(Opaque%(3)))"),
    ("array-index-byte", "DIM t??(0 TO 40) : t??(Opaque%(2)) = {W} MOD 200 : sink% = t??(Opaque%(3))"),
    ("cast-word-to-long", "DIM a% : a% = {W} : sinkl& = CLNG(a%) + 70000"),
    ("sign-extend", "DIM a% : a% = Opaque%({W}) : sinkl& = CLNG(a%) * 3"),
    ("divide-word", "DIM a% : a% = Opaque%(1234) : sink% = a% \\ ({W} + 1)"),
    ("mod-word", "DIM a% : a% = Opaque%(1234) : sink% = a% MOD ({W} + 1)"),
    ("multiply-word", "DIM a% : a% = Opaque%(1234) : sink% = a% * {W}"),
    ("and-mask", "DIM a% : a% = Opaque%(1234) : sink% = a% AND {W}"),
    ("compare-word", "DIM a% : a% = Opaque%(1234) : IF a% > {W} THEN sink% = 1 ELSE sink% = 2"),
    ("compare-long", "DIM a& : a& = OpaqueL&(123456) : IF a& > {L} THEN sink% = 1 ELSE sink% = 2"),
    ("select-word", "DIM a% : a% = Opaque%(3) : SELECT CASE a% : CASE {W} : sink% = 1 : CASE ELSE : sink% = 2 : END SELECT"),
    ("string-chr", "DIM s$ : s$ = CHR$(64 + ({W} AND 15)) : sink% = LEN(s$)"),
    ("string-mid", "DIM s$ : s$ = \"abcdefgh\" : sink% = LEN(MID$(s$, 1 + ({W} AND 3), 2))"),
    ("string-space", "DIM s$ : s$ = SPACE$({W} AND 7) : sink% = LEN(s$)"),
    ("for-step", "DIM i% : FOR i% = 0 TO 8 STEP ({W} AND 3) + 1 : sink% = sink% + i% : NEXT"),
    ("power", "DIM d# : d# = 2.5 ^ ({W} AND 3) : sink% = INT(d#)"),
    ("float-convert", "DIM d# : d# = CDBL({W}) / 3# : sink% = INT(d#)"),
    ("negate", "DIM a% : a% = -({W}) : sink% = a%"),
    ("abs", "DIM a% : sink% = ABS({W})"),
    ("peek-poke", "POKE &H2000, {W} AND 255 : sink% = PEEK(&H2000)"),
    ("bit-test", "DIM a% : a% = Opaque%(-1234) : sink% = BIT(a%, {W} AND 15)"),
    ("varptr", "DIM a% : a% = {W} : sink% = VARPTR(a%)"),
  ];

  [Test]
  public void GeneratedVariations_WhenCompiledRouted_ThenNothingThrows() {
    var failures = new List<string>();
    var compiled = 0;
    foreach (var (bodyName, body) in _bodies) {
      var takesOperand = body.Contains("{W}") || body.Contains("{L}");
      foreach (var (operandName, word, @long) in _operands) {
        var source = _Preamble + "\n"
          + body.Replace("{W}", word).Replace("{L}", @long) + "\n"
          + "PRINT sink%; sinkl&; warm%\n" + _Epilogue;
        var name = $"{bodyName}.{operandName}";
        foreach (var optimize in new[] { true, false }) {
          ++compiled;
          if (RoutedCompileFailure(source, name + ".BAS", optimize) is { } e)
            failures.Add($"{name} ({(optimize ? "optimized" : "unoptimized")}): {Head(e)}");
        }
        if (!takesOperand)
          break;                                  // the substitution changes nothing - one is the whole row
      }
    }

    TestContext.Out.WriteLine($"generated routed compilations attempted: {compiled}, raised: {failures.Count}");
    Assert.That(failures, Is.Empty,
      "the routed back end raised instead of declining:\n  " + string.Join("\n  ", failures));
  }

  /// <summary>
  /// The generator has to be able to FAIL, or it is a fixture that measures nothing. This pins that
  /// at least one generated variation really does reach the back end - a program where nothing routed
  /// would compile the same image twice and agree by construction.
  /// </summary>
  [Test]
  public void GeneratedVariations_WhenCompiledRouted_ThenTheBackEndTookSomething() {
    var routed = new List<string>();
    var mains = new List<string>();
    var rejected = new List<string>();
    foreach (var (bodyName, body) in _bodies) {
      var source = _Preamble + "\n"
        + body.Replace("{W}", "Opaque%(3)").Replace("{L}", "OpaqueL&(3)") + "\n"
        + "PRINT sink%; sinkl&; warm%\n" + _Epilogue;
      var name = bodyName + ".BAS";
      SemanticModel bound;
      try {
        bound = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, name, Dialect.Pb36), name, Dialect.Pb36), Dialect.Pb36);
      } catch (Exception e) {
        rejected.Add($"{bodyName}: {e.Message}");
        continue;
      }
      if (bound.Errors.Count > 0) {
        rejected.Add($"{bodyName}: {bound.Errors[0].Message}");
        continue;
      }
      var gen = new CodeGenerator(bound) { Optimize = true, UseExperimentalBackend = true };
      gen.EmitExecutable();
      var names = gen.BackendRoutedNames.ToList();
      if (names.Count > 0)
        routed.Add($"{bodyName} -> {string.Join(",", names)}");
      if (names.Contains("main"))
        mains.Add(bodyName);
    }

    TestContext.Out.WriteLine(string.Join("\n", routed));
    TestContext.Out.WriteLine($"module bodies routed: {mains.Count} of {_bodies.Length}");
    // A row the FRONT end rejects is a row that varies nothing, and it would sit in the generator
    // looking like coverage. There is no legitimate reason for one to be here.
    Assert.That(rejected, Is.Empty, "a generated body does not compile at all:\n  " + string.Join("\n  ", rejected));
    // And a row whose module body declines only exercises the two helper functions. Most must reach
    // main, or the generator is varying operands inside a program the back end never took.
    Assert.That(mains, Has.Count.GreaterThan(_bodies.Length / 2),
      "most generated bodies no longer route their module body - the generator measures little:\n"
        + string.Join("\n", routed));
  }

  /// <summary>
  /// The shapes that were found by this audit, each one a program that ENDED the compilation with a
  /// stack trace before it was converted to a decline. They are held apart from the generator because
  /// the assertion is stronger: the routed build must behave exactly like the unrouted one, which is
  /// what a decline promises and a throw cannot.
  /// </summary>
  private static readonly (string Name, string Source)[] _formerlyRaised = [
    // MachineEmitter.EmitInlineAsm. The lowering proved the text parses against its OWN stand-in
    // symbols, where a name that is neither a variable nor a label answers as memory; at emission the
    // same name is the runtime label it really is, and the two disagree about what is an instruction.
    // LEA/INC/CMP/XCHG against a documented string-manager export are the four shapes that differ.
    ("asm-lea-export", "DIM n%\nn% = 3\n! LEA BX, GetStrLoc\nPRINT n%\nEND\n"),
    ("asm-inc-export", "DIM n%\nn% = 3\n! INC GetStrLoc\nPRINT n%\nEND\n"),
    ("asm-cmp-export", "DIM n%\nn% = 3\n! CMP AX, GetStrLoc\nPRINT n%\nEND\n"),
    ("asm-xchg-export", "DIM n%\nn% = 3\n! XCHG AX, GetStrLoc\nPRINT n%\nEND\n"),
    // MachineEmitter.ResolveData. The IR names a module variable by its source spelling WITHOUT the
    // type suffix and the binder's table is keyed WITH it, so these two symbols are one g.total. The
    // resolver refuses to alias them and had nowhere to say so.
    ("ambiguous-global", """
      DIM total% : DIM total&
      total% = 1 : total& = 2
      CALL Bump
      PRINT total%; total&
      END
      SUB Bump
        SHARED total%, total&
        total% = total% + 1
        total& = total& + 1
      END SUB
      """),
  ];

  [Test]
  public void FormerlyRaisingShapes_WhenCompiledRouted_ThenTheyDeclineAndBehaveLikeTheUnroutedBuild() {
    var failures = new List<string>();
    foreach (var (name, source) in _formerlyRaised)
      foreach (var optimize in new[] { true, false }) {
        var label = $"{name} ({(optimize ? "optimized" : "unoptimized")})";
        if (RoutedCompileFailure(source, name + ".BAS", optimize) is { } e) {
          failures.Add($"{label}: {Head(e)}");
          continue;
        }
        // and the decline has to be a real fallback, not merely a non-crash: the direct emitter takes
        // the function and the program is the one it always was, diagnostics included
        SemanticModel Bind() => Binder.Bind(
          Parser.Parse(Lexer.Tokenize(source, name + ".BAS", Dialect.Pb36), name + ".BAS", Dialect.Pb36), Dialect.Pb36);
        var direct = new CodeGenerator(Bind()) { Optimize = optimize, UseExperimentalBackend = false };
        var routed = new CodeGenerator(Bind()) { Optimize = optimize, UseExperimentalBackend = true };
        var directImage = direct.EmitExecutable();
        var routedImage = routed.EmitExecutable();
        if (!directImage.SequenceEqual(routedImage))
          failures.Add($"{label}: declined but produced a different image than the direct build");
        if (direct.Errors.Count != routed.Errors.Count)
          failures.Add($"{label}: direct reported {direct.Errors.Count} diagnostics, routed {routed.Errors.Count}");
      }

    Assert.That(failures, Is.Empty, "\n  " + string.Join("\n  ", failures));
  }

  /// <summary>
  /// And the corpus half needs the same guarantee: at least one corpus program must really route,
  /// otherwise a change that stops routing everything would leave this fixture green.
  /// </summary>
  [Test]
  public void Corpus_WhenCompiledRouted_ThenTheBackEndTookSomething() {
    var dir = Path.Combine(_repoRoot, "tests");
    Assume.That(Directory.Exists(dir), "no tests/*.BAS corpus present");

    var routed = 0;
    foreach (var file in Directory.EnumerateFiles(dir, "*.BAS", SearchOption.AllDirectories)) {
      var name = Path.GetFileName(file);
      SemanticModel bound;
      try {
        // the preprocessor, not the lexer: $INCLUDE and $IF are resolved there, and tokenizing
        // directly left the two INCLUDE-using corpus programs binding with errors and skipped
        bound = Binder.Bind(Parser.Parse(Preprocessor.Expand(file, new FileSourceProvider(), Dialect.Pb36), name, Dialect.Pb36), Dialect.Pb36);
      } catch (Exception) {
        continue;
      }
      if (bound.Errors.Count > 0)
        continue;
      var gen = new CodeGenerator(bound) { Optimize = true, UseExperimentalBackend = true };
      try {
        gen.EmitExecutable();
        routed += gen.BackendRoutedNames.Any() ? 1 : 0;
      } catch (Exception) {
        // a raise is the other test's finding; here it merely does not count as routed
      }
    }

    TestContext.Out.WriteLine($"corpus programs the back end took something of: {routed}");
    Assert.That(routed, Is.GreaterThan(50), "the back end routed almost nothing - the corpus gate measures nothing");
  }
}
