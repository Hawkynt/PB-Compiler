using PowerBasic.Compiler.Cli;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// The C and LLVM emitters must DECLINE what they cannot render, never THROW.
///
/// <para>
/// It matters MORE here than on the x86-16 path. There a decline is caught by <c>CodeGenerator</c>
/// and the direct emitter compiles the function instead, so a throw was a crash where a survivable
/// fallback would have done. These two back ends have <b>no fallback at all</b>: the only thing a
/// decline buys is the diagnostic that names the construct, and a throw produces no output, no
/// actionable exit code and no name - just a stack trace out of the compiler.
/// </para>
///
/// <para>
/// So the property is asserted where a throw IS the crash: <c>pbc --emit-c</c> and
/// <c>pbc --emit-llvm</c> over the whole path from source text to emitted text, the lowering and
/// every IR pass included. Either it prints a translation unit and answers 0, or it names what it
/// declined on stderr and answers 1. Nothing else is an acceptable outcome, and this fixture
/// deliberately does NOT measure coverage, so it stays green while a throw-to-decline conversion
/// reduces what renders - which is the correct trade.
/// </para>
///
/// <para>
/// Two populations, for the reason <see cref="Tests.Backend.BackendNeverThrowsTests"/> states: every
/// corpus program is one somebody wrote to work under the DOS back end, so the constructs it carries
/// are the ones that path already handles. The generated half varies the axis the corpus holds
/// constant - the construct KIND that reaches the emitter, and its operand, which is always a
/// runtime value the optimizer cannot fold away before emission.
/// </para>
/// </summary>
[TestFixture]
public sealed class EmitterNeverThrowsTests {

  private static readonly string _repoRoot =
    Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  private static readonly string[] _stages = ["--emit-c", "--emit-llvm"];

  private static (int Code, string Err) Emit(string path, string stage, params string[] extra) {
    var stdout = new StringWriter();
    var stderr = new StringWriter();
    var code = Driver.Run([.. extra, stage, path], stdout, stderr);
    return (code, stderr.ToString().Trim());
  }

  /// <summary>
  /// Runs one emission and reports the exception it raised, or null. A program the front end or the
  /// lowering REJECTS is not a finding here: that is a diagnostic and an exit code, which is the
  /// behaviour under test rather than a violation of it.
  /// </summary>
  private static Exception? EmitFailure(string path, string stage, params string[] extra) {
    try {
      _ = Emit(path, stage, extra);
      return null;
    } catch (Exception e) {
      return e;
    }
  }

  private static string Head(Exception e) {
    var frame = e.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("PowerBasic.Compiler."))?.Trim() ?? "";
    return $"{e.GetType().Name}: {e.Message}  [{frame}]";
  }

  #region the corpus half

  [Test]
  public void Corpus_WhenEmittedAsCAndLlvm_ThenNothingThrows() {
    var dir = Path.Combine(_repoRoot, "tests");
    Assume.That(Directory.Exists(dir), "no tests/*.BAS corpus present");

    var failures = new List<string>();
    var attempted = 0;
    foreach (var file in Directory.EnumerateFiles(dir, "*.BAS", SearchOption.AllDirectories)
               .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
      foreach (var stage in _stages) {
        ++attempted;
        if (EmitFailure(file, stage) is { } e)
          failures.Add($"{Path.GetRelativePath(dir, file).Replace('\\', '/')} {stage}: {Head(e)}");
      }

    TestContext.Out.WriteLine($"emissions attempted: {attempted}, raised: {failures.Count}");
    Assert.That(failures, Is.Empty,
      "an emitter raised instead of declining:\n  " + string.Join("\n  ", failures));
  }

  /// <summary>
  /// And the corpus half needs its own can-this-measure-anything check: a gate over programs that all
  /// stop at the LOWERING would never reach an emitter at all, and would stay green through any
  /// change to either of them.
  /// </summary>
  [Test]
  public void Corpus_WhenEmittedAsCAndLlvm_ThenTheEmittersRenderedRealPrograms() {
    var dir = Path.Combine(_repoRoot, "tests");
    Assume.That(Directory.Exists(dir), "no tests/*.BAS corpus present");

    var rendered = new Dictionary<string, int> { ["--emit-c"] = 0, ["--emit-llvm"] = 0 };
    foreach (var file in Directory.EnumerateFiles(dir, "*.BAS", SearchOption.AllDirectories))
      foreach (var stage in _stages)
        if (Emit(file, stage).Code == 0)
          ++rendered[stage];

    TestContext.Out.WriteLine(string.Join(", ", rendered.Select(kv => $"{kv.Key}: {kv.Value} rendered")));
    Assert.Multiple(() => {
      foreach (var (stage, count) in rendered)
        Assert.That(count, Is.GreaterThan(50),
          $"{stage} renders almost no corpus program - the corpus gate measures nothing");
    });
  }

  #endregion

  #region the generated half

  /// <summary>
  /// The runtime values every generated body is built on. <c>INPUT</c> is the opaque source rather
  /// than a <c>NOINLINE</c> helper because it is the one that exists in EVERY dialect: a literal is
  /// folded by SCCP long before an emitter sees it, a single call site lets IPCP prove the argument,
  /// and an empty SUB is not a barrier for the IR pipeline - three ways a generator of this kind
  /// quietly measures nothing.
  /// </summary>
  private const string _Preamble = """
    DIM sink%, sinkl&, sinkd#, sinks$
    INPUT "", iw%
    INPUT "", id#
    """;

  private const string _Epilogue = """
    PRINT sink%; sinkl&; sinkd#; sinks$
    END
    """;

  /// <summary>
  /// What is substituted for the runtime operand. All four are derived from <c>INPUT</c>, so none can
  /// be folded; they differ in the SHAPE the arithmetic around them takes, which is what decides the
  /// cast, the width and the runtime entry that reach the emitter.
  /// </summary>
  private static readonly (string Name, string Word, string Double)[] _operands = [
    ("plain", "iw%", "id#"),
    ("offset", "iw% + 1", "id# + 0.5"),          // lands a conversion on a .5 boundary
    ("negated", "-iw%", "-id#"),
    ("scaled", "iw% * 3", "id# * 1000#"),
  ];

  /// <summary>
  /// The bodies. Each names a construct whose IR shape reaches the emitters differently - a float
  /// width, an unsigned conversion, a block address, an indirect branch, a far pointer, a runtime
  /// entry the portable runtime has no counterpart for. <c>{W}</c> takes the word-typed runtime
  /// operand and <c>{D}</c> the double-typed one.
  /// </summary>
  private static readonly (string Name, string Body)[] _bodies = [
    ("float-add", "DIM d# : d# = {D} + 1.5 : sinkd# = d#"),
    ("float-divide", "DIM d# : d# = {D} / 3# : sinkd# = d#"),
    ("float-compare", "IF {D} > 1.5 THEN sink% = 1 ELSE sink% = 2"),
    ("single-narrow", "DIM s! : s! = {D} : sinkd# = s! * 2!"),
    ("extended", "DIM e## : e## = {D} * 3## : sinkd# = e##"),
    ("quad", "DIM q&& : q&& = {W} : sinkl& = CLNG(q&& MOD 1000)"),
    ("byte-from-float", "DIM b?? : b?? = {D} : sink% = b??"),
    ("word-from-float", "DIM w AS WORD : w = {D} : sink% = w \\ 2"),
    ("dword-from-float", "DIM u AS DWORD : u = {D} : sinkl& = CLNG(u \\ 2)"),
    ("byte-from-integer", "DIM b?? : b?? = {W} AND 255 : sink% = b??"),
    ("float-to-long", "sinkl& = CLNG({D} * 1000#)"),
    ("fix-int-cint", "sinkd# = FIX({D}) + INT({D}) + CINT({D})"),
    ("power", "sinkd# = {D} ^ 3"),
    ("integer-divide", "sink% = {W} \\ 7 : sinkl& = {W} MOD 7"),
    ("shift", "DIM a% : a% = {W} : SHIFT LEFT a%, 3 : sink% = a%"),
    ("bit-test", "sink% = BIT({W}, 3)"),
    ("string-concat", "sinks$ = \"a\" + MID$(\"abcdef\", 1 + ({W} AND 3), 2) : sink% = LEN(sinks$)"),
    ("string-compare", "IF MID$(\"abc\", 1 + ({W} AND 1), 1) > \"a\" THEN sink% = 1"),
    ("string-array", "DIM t$(0 TO 4) : t$({W} AND 3) = \"x\" : sinks$ = t$({W} AND 3)"),
    ("numeric-array", "DIM t#(0 TO 8) : t#({W} AND 7) = {D} : sinkd# = t#({W} AND 7)"),
    ("dynamic-array", "REDIM t%(0 TO ({W} AND 7) + 1) : t%(1) = {W} : sink% = t%(1)"),
    ("udt", "TYPE R : a AS INTEGER : b AS DOUBLE : END TYPE\nDIM r AS R\nr.a = {W}\nr.b = {D}\nsinkd# = r.a + r.b"),
    ("select-case", "SELECT CASE {W}\nCASE 1\nsink% = 1\nCASE 2 TO 4\nsink% = 2\nCASE ELSE\nsink% = 3\nEND SELECT"),
    ("on-goto", "ON ({W} AND 1) + 1 GOTO Ga, Gb\nGa:\nsink% = 1\nGOTO Gc\nGb:\nsink% = 2\nGc:"),
    ("gosub", "GOSUB Sa\nGOTO Sb\nSa:\nsink% = {W}\nRETURN\nSb:"),
    ("goto-dword", "DIM p&& : p&& = CODEPTR32(Gd)\nsink% = {W}\nGOTO DWORD p&&\nGd:\nsink% = sink% + 1"),
    ("on-error", "ON ERROR GOTO Eh\nsink% = {W}\nGOTO Ed\nEh:\nsink% = 9\nRESUME NEXT\nEd:\nON ERROR GOTO 0"),
    ("print-using", "PRINT USING \"###.##\"; {D}"),
    ("using-dollar", "sinks$ = USING$(\"###.##\", {D}) : sink% = LEN(sinks$)"),
    ("lprint", "LPRINT {W}"),
    ("call-interrupt", "REG 1, {W} AND 255 : CALL INTERRUPT &H21 : sink% = REG(1)"),
    ("peek-poke", "POKE &H2000, {W} AND 255 : sink% = PEEK(&H2000)"),
    ("far-peek", "DEF SEG = &HB800 : sink% = PEEK({W} AND 255) : DEF SEG"),
    ("array-at", "DIM t%({W} AND 3) AT &H1000 : sink% = t%(0)"),
    ("inline-asm", "DIM a% : a% = {W}\n! MOV AX, a%\n! ADD AX, 1\n! MOV a%, AX\nsink% = a%"),
    ("varptr", "DIM a% : a% = {W} : sink% = VARPTR(a%) : sinkl& = VARSEG(a%)"),
    ("pointer", "DIM a% : DIM p AS INTEGER PTR : a% = {W} : p = VARPTR(a%) : sink% = @p"),
    ("data-read", "DATA 11, 22\nDIM x%, y%\nREAD x%, y%\nsink% = x% + y% + {W}"),
    ("swap", "DIM a%, b% : a% = {W} : b% = 3 : SWAP a%, b% : sink% = a% - b%"),
    ("file-io", "OPEN \"T.TMP\" FOR OUTPUT AS #1 : PRINT #1, {W} : CLOSE #1 : KILL \"T.TMP\""),
    ("loop-phi", "DIM i%, acc# : acc# = 0 : FOR i% = 1 TO ({W} AND 3) + 1 : acc# = acc# + {D} : NEXT : sinkd# = acc#"),
    ("while-swap", "DIM a%, b%, n% : a% = 1 : b% = 2 : n% = {W} AND 3 : WHILE n% > 0 : SWAP a%, b% : n% = n% - 1 : WEND : sink% = a%"),
    ("nested-if", "IF {W} > 0 THEN\nIF {D} > 0# THEN sink% = 1 ELSE sink% = 2\nELSE\nsink% = 3\nEND IF"),
  ];

  private static string SourceFor(string body, string word, string @double) =>
    _Preamble + "\n" + body.Replace("{W}", word).Replace("{D}", @double) + "\n" + _Epilogue;

  /// <summary>
  /// The dialects the matrix runs under. Not decoration: the dialect decides which runtime entries a
  /// statement lowers to and how wide a float is, and pb36 is the only one where every body above
  /// binds - so running only pb36 would leave the other lowerings untested, and running only an older
  /// one would reject half the matrix at the front end.
  /// </summary>
  private static readonly string[] _dialects = ["pb36", "pb35", "qb45"];

  [Test]
  public void GeneratedVariations_WhenEmittedAsCAndLlvm_ThenNothingThrows() {
    var work = Path.Combine(Path.GetTempPath(), "pbc-emit-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    var failures = new List<string>();
    var attempted = 0;
    try {
      foreach (var (bodyName, body) in _bodies)
        foreach (var (operandName, word, @double) in _operands) {
          var path = Path.Combine(work, "p.bas");
          File.WriteAllText(path, SourceFor(body, word, @double));
          foreach (var dialect in _dialects)
            foreach (var stage in _stages) {
              ++attempted;
              if (EmitFailure(path, stage, "--dialect", dialect) is { } e)
                failures.Add($"{bodyName}.{operandName} [{dialect}] {stage}: {Head(e)}");
            }
        }
    } finally {
      try { Directory.Delete(work, recursive: true); } catch (IOException) { /* best effort */ }
    }

    TestContext.Out.WriteLine($"generated emissions attempted: {attempted}, raised: {failures.Count}");
    Assert.That(failures, Is.Empty,
      "an emitter raised instead of declining:\n  " + string.Join("\n  ", failures));
  }

  /// <summary>
  /// The generator has to be able to FAIL, or it is a fixture that measures nothing. Two separate
  /// ways it could stop measuring, so two separate assertions: a body the FRONT END rejects varies
  /// nothing while looking like coverage, and a matrix where nothing survives the lowering never
  /// reaches an emitter at all.
  /// </summary>
  [Test]
  public void GeneratedVariations_WhenEmitted_ThenEveryBodyBindsAndMostReachTheEmitters() {
    var work = Path.Combine(Path.GetTempPath(), "pbc-emit-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    var rejected = new List<string>();
    var rendered = new List<string>();
    var declined = new List<string>();
    try {
      foreach (var (bodyName, body) in _bodies) {
        var path = Path.Combine(work, "p.bas");
        File.WriteAllText(path, SourceFor(body, "iw%", "id#"));
        foreach (var stage in _stages) {
          var (code, err) = Emit(path, stage, "--dialect", "pb36");
          if (code == 0)
            rendered.Add($"{bodyName} {stage}");
          else if (err.StartsWith("pbc:", StringComparison.Ordinal))
            declined.Add($"{bodyName} {stage}: {err}");
          else
            rejected.Add($"{bodyName} {stage}: {err}");         // a front-end diagnostic
        }
      }
    } finally {
      try { Directory.Delete(work, recursive: true); } catch (IOException) { /* best effort */ }
    }

    TestContext.Out.WriteLine($"rendered: {rendered.Count} of {_bodies.Length * _stages.Length}");
    TestContext.Out.WriteLine("declined:\n  " + string.Join("\n  ", declined));
    Assert.Multiple(() => {
      Assert.That(rejected, Is.Empty,
        "a generated body does not compile at all, so it varies nothing:\n  " + string.Join("\n  ", rejected));
      Assert.That(rendered, Has.Count.GreaterThan(_bodies.Length),
        "fewer than half the generated emissions reach an emitter - the generator measures little:\n  "
          + string.Join("\n  ", declined));
    });
  }

  /// <summary>
  /// The shapes this audit found, each one a program that ENDED the compilation with a stack trace
  /// before it was converted to a decline. They are held apart from the generator because the
  /// assertion is stronger: the refusal has to NAME the construct, which is the entire value a back
  /// end with no fallback can offer, and a decline that says "unsupported" is barely better than the
  /// crash it replaced.
  /// </summary>
  private static readonly (string Name, string Stage, string Names, string Source)[] _formerlyRaised = [
    // CEmitter.Ref - the address of a basic block, which ON ERROR arms its handler with.
    ("on-error", "--emit-c", "basic block",
      "ON ERROR GOTO Eh\nINPUT \"\", n%\nPRINT n%\nEND\nEh:\nRESUME NEXT\n"),
    // CEmitter/LlvmEmitter default arm - IrFarPtr, the segment:offset pointer an array DIMmed AT an
    // absolute address is addressed through. Deliberately not the DEF SEG form of PEEK: that one
    // lowers to a near access and renders, so it would have looked like coverage and been none.
    ("array-at", "--emit-c", "far (segment:offset) pointer",
      "INPUT \"\", n%\nDIM t%(n% AND 3) AT &H1000\nPRINT t%(0)\n"),
    ("array-at", "--emit-llvm", "far (segment:offset) pointer",
      "INPUT \"\", n%\nDIM t%(n% AND 3) AT &H1000\nPRINT t%(0)\n"),
    // ...and IrInlineAsm, which is x86-16 machine code by definition.
    ("inline-asm", "--emit-c", "inline assembly",
      "INPUT \"\", n%\n! MOV AX, n%\n! INC AX\n! MOV n%, AX\nPRINT n%\n"),
    ("inline-asm", "--emit-llvm", "inline assembly",
      "INPUT \"\", n%\n! MOV AX, n%\n! INC AX\n! MOV n%, AX\nPRINT n%\n"),
    // CEmitter's IrCall arm - a runtime entry runtime/pbc_rt.c has no counterpart for.
    ("print-using", "--emit-c", "rt_using_field", "INPUT \"\", d#\nPRINT USING \"###.##\"; d#\n"),
    ("using-dollar", "--emit-c", "rt_capture_begin", "INPUT \"\", d#\nPRINT USING$(\"###.##\", d#)\n"),
    ("lprint", "--emit-c", "rt_lprint_on", "INPUT \"\", n%\nLPRINT n%\n"),
    ("call-interrupt", "--emit-c", "rt_reg_set", "INPUT \"\", n%\nREG 1, n% AND 255\nCALL INTERRUPT &H21\n"),
  ];

  [Test]
  public void FormerlyRaisingShapes_WhenEmitted_ThenTheyDeclineNamingTheConstruct() {
    var work = Path.Combine(Path.GetTempPath(), "pbc-emit-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    var failures = new List<string>();
    try {
      foreach (var (name, stage, names, source) in _formerlyRaised) {
        var path = Path.Combine(work, "p.bas");
        File.WriteAllText(path, source);
        var label = $"{name} {stage}";
        try {
          var (code, err) = Emit(path, stage, "--dialect", "pb36");
          if (code == 0)
            failures.Add($"{label}: rendered - this shape no longer exercises the decline");
          else if (!err.Contains(names, StringComparison.Ordinal))
            failures.Add($"{label}: declined without naming '{names}': {err}");
        } catch (Exception e) {
          failures.Add($"{label}: {Head(e)}");
        }
      }
    } finally {
      try { Directory.Delete(work, recursive: true); } catch (IOException) { /* best effort */ }
    }

    Assert.That(failures, Is.Empty, "\n  " + string.Join("\n  ", failures));
  }

  #endregion
}
