using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// <c>FOR i = a TO b STEP s</c> where <b>s is a runtime value</b>, compiled both ways and executed.
///
/// <para>
/// A constant step settles the loop's direction at compile time and gets one comparison. A runtime
/// step does not, so the lowering asks the whole question -
/// <c>(s &gt;= 0 AND i &lt;= limit) OR (s &lt; 0 AND i &gt;= limit)</c> - and the second conjunct's
/// guard is the first one NEGATED. That negation is <c>xor i1 %asc, true</c>, and the x86-16 back end
/// holds a bool as BASIC's full word of -1/0 while the IR writes truth as 1: the literal came through
/// as the immediate 1, so the complement of -1 was -2 rather than 0, which still reads as TRUE. Both
/// arms of the disjunction were then live and an ASCENDING loop never terminated.
/// </para>
/// <para>
/// A DESCENDING loop was correct throughout (the negation of FALSE is 1, which is as true as -1), and
/// every counted loop in the corpus has a constant step - which is the whole reason this survived.
/// </para>
/// <para>
/// <b>The interpreter's verdict is folded into the compared output, never thrown.</b> A loop that
/// does not terminate makes <see cref="Cpu8086"/> give up, and the usual <c>Assert.Ignore</c> idiom
/// would report that as "the interpreter cannot run this image" - the defect wearing an excuse. Here
/// it is a difference from the direct build like any other.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendLoopStepTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  /// <summary>The program's output, with whatever stopped the machine appended rather than thrown.</summary>
  private static string Run(string source, bool routed, bool optimize, out IReadOnlyList<string> routedNames) {
    var generator = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = routed };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    routedNames = [.. generator.BackendRoutedNames];
    var cpu = Cpu8086.Run(image, new Dictionary<string, byte[]>(), out var fault, 2_000_000);
    return (fault is null ? cpu.Output : cpu.Output + "\n[stopped: " + fault.Message + "]").Replace("\r\n", "\n");
  }

  private static void BothPathsAgree(string source, string expected) {
    foreach (var optimize in new[] { true, false }) {
      var direct = Run(source, routed: false, optimize, out _);
      var routed = Run(source, routed: true, optimize, out var names);
      Assert.That(names, Is.Not.Empty, $"nothing routed at optimize={optimize}, so this compares the direct emitter with itself");
      Assert.That(routed, Is.EqualTo(direct), $"the two back ends disagree at optimize={optimize}");
      // PB gives every printed numeric a trailing space; the shape under test is the trip counts
      Assert.That(string.Join("|", routed.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)),
        Is.EqualTo(expected), $"at optimize={optimize}");
    }
  }

  /// <summary>
  /// The bounds AND the step come back from a <c>NOINLINE</c> function called from several sites with
  /// different arguments. One site would let interprocedural constant propagation prove the step and
  /// fold the direction test away, after which the program says nothing about a runtime step at all.
  /// </summary>
  private const string _RUNTIME_STEP = """
    DECLARE FUNCTION Op%(BYVAL v%)
    DECLARE SUB Walk(BYVAL a%, BYVAL b%, BYVAL s%)
    Walk Op%(1), Op%(10), Op%(4)
    Walk Op%(10), Op%(1), Op%(-3)
    Walk Op%(5), Op%(5), Op%(1)
    Walk Op%(5), Op%(4), Op%(1)
    END
    SUB Walk(BYVAL a%, BYVAL b%, BYVAL s%) NOINLINE
      DIM i AS INTEGER
      DIM n AS INTEGER
      n = 0
      FOR i = a% TO b% STEP s%
        n = n + 1
        IF n > 40 THEN EXIT FOR
      NEXT i
      PRINT "n"; n; "i"; i
    END SUB
    FUNCTION Op%(BYVAL v%) NOINLINE
      Op% = v%
    END FUNCTION
    """;

  /// <summary>
  /// Ascending, descending, one-trip and zero-trip, all with a runtime step. The ascending arms are
  /// the ones that ran to the <c>EXIT FOR</c> escape hatch instead of to their limit.
  /// </summary>
  [Test]
  public void Execute_GivenARuntimeStep_WhenRouted_ThenEveryLoopTerminatesWhereItShould() =>
    BothPathsAgree(_RUNTIME_STEP, "n 3 i 13|n 4 i-2|n 1 i 6|n 0 i 5");

  /// <summary>
  /// The same question over a LONG counter, where the compare is a register pair rather than one
  /// word - a different selection path reaching the same disjunction.
  /// </summary>
  [Test]
  public void Execute_GivenARuntimeStepOverALongCounter_WhenRouted_ThenTheLoopTerminates() =>
    BothPathsAgree("""
      DECLARE FUNCTION Opl&(BYVAL v&)
      DECLARE SUB Walk(BYVAL a&, BYVAL b&, BYVAL s&)
      Walk Opl&(70000), Opl&(70003), Opl&(1)
      Walk Opl&(-70000), Opl&(-70004), Opl&(-2)
      END
      SUB Walk(BYVAL a&, BYVAL b&, BYVAL s&) NOINLINE
        DIM i AS LONG
        DIM n AS INTEGER
        n = 0
        FOR i = a& TO b& STEP s&
          n = n + 1
          IF n > 20 THEN EXIT FOR
        NEXT i
        PRINT "n"; n; "i"; i
      END SUB
      FUNCTION Opl&(BYVAL v&) NOINLINE
        Opl& = v&
      END FUNCTION
      """, "n 4 i 70004|n 3 i-70006");

  /// <summary>
  /// The module body's own copy of the loop, which routes as <c>main</c> rather than as a procedure -
  /// the same lowering, a different owner.
  /// </summary>
  [Test]
  public void Execute_GivenARuntimeStepInTheModuleBody_WhenRouted_ThenTheLoopTerminates() =>
    BothPathsAgree("""
      DECLARE FUNCTION Op%(BYVAL v%)
      DIM i AS INTEGER
      DIM n AS INTEGER
      n = 0
      FOR i = Op%(1) TO Op%(9) STEP Op%(2)
        n = n + 1
        IF n > 40 THEN EXIT FOR
      NEXT i
      PRINT "n"; n; "i"; i
      END
      FUNCTION Op%(BYVAL v%) NOINLINE
        Op% = v%
      END FUNCTION
      """, "n 5 i 11");
}
