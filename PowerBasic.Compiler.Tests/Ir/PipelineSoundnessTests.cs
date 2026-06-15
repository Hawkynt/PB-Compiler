using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// A safety net for the whole middle-end: lower a spread of representative programs and
/// run the full Standard pipeline (plus inlining) with verification after every pass, so
/// any pass that produces malformed IR on a real shape is caught here rather than silently
/// emitted. Each case must reach a verifier-clean fixpoint.
/// </summary>
[TestFixture]
public sealed class PipelineSoundnessTests {

  private static IEnumerable<TestCaseData> Programs() {
    yield return Case("straight-line", "a% = 2\nb% = 3\nc% = a% * b% + 1");
    yield return Case("if-elseif-else", "x% = 5\nIF x% > 9 THEN\n y% = 1\nELSEIF x% > 3 THEN\n y% = 2\nELSE\n y% = 3\nEND IF\nz% = y%");
    yield return Case("for-const-step", "s% = 0\nFOR i% = 1 TO 100 STEP 2\n s% = s% + i%\nNEXT i%");
    yield return Case("for-runtime-step", "d% = -2\ns% = 0\nFOR i% = 20 TO 0 STEP d%\n s% = s% + i%\nNEXT i%");
    yield return Case("nested-for", "t% = 0\nFOR r% = 1 TO 3\n FOR c% = 1 TO 3\n  t% = t% + r% * c%\n NEXT c%\nNEXT r%");
    yield return Case("do-while", "i% = 0\nDO WHILE i% < 10\n i% = i% + 1\nLOOP");
    yield return Case("do-until-post", "i% = 0\nDO\n i% = i% + 1\nLOOP UNTIL i% >= 10");
    yield return Case("select", "n% = 2\nr% = 0\nSELECT CASE n%\nCASE 1\n r% = 10\nCASE 2, 3\n r% = 20\nCASE 4 TO 6\n r% = 30\nCASE IS > 9\n r% = 40\nCASE ELSE\n r% = 99\nEND SELECT");
    yield return Case("arrays", "DIM a%(1 TO 10)\nFOR i% = 1 TO 10\n a%(i%) = i% * i%\nNEXT i%\ns& = 0\nFOR i% = 1 TO 10\n s& = s& + a%(i%)\nNEXT i%");
    yield return Case("2d-array", "DIM g%(1 TO 3, 1 TO 3)\nFOR r% = 1 TO 3\n FOR c% = 1 TO 3\n  g%(r%, c%) = r% * 10 + c%\n NEXT c%\nNEXT r%");
    yield return Case("intrinsics", "x! = -2.5\na! = ABS(x!)\nb% = SGN(x!)\nc! = FIX(x!)\nd! = INT(x!)");
    yield return Case("swap", "a% = 1\nb% = 2\nSWAP a%, b%\nc% = a% - b%");
    yield return Case("exit-iterate", "s% = 0\nFOR i% = 1 TO 100\n IF i% = 50 THEN EXIT FOR\n IF i% = 7 THEN ITERATE FOR\n s% = s% + i%\nNEXT i%");
  }

  private static IEnumerable<TestCaseData> Modules() {
    yield return Case("byval-fn", "DECLARE FUNCTION sq%(BYVAL n%)\nr% = sq%(7)\n\nFUNCTION sq%(BYVAL n%)\n sq% = n% * n%\nEND FUNCTION");
    yield return Case("byref-sub", "DECLARE SUB inc(x%)\nq% = 5\nCALL inc(q%)\n\nSUB inc(x%)\n x% = x% + 1\nEND SUB");
    yield return Case("multi-block-fn", "DECLARE FUNCTION clamp%(BYVAL n%)\nr% = clamp%(50)\n\nFUNCTION clamp%(BYVAL n%)\n IF n% > 9 THEN\n  clamp% = 9\n ELSE\n  clamp% = n%\n END IF\nEND FUNCTION");
    yield return Case("call-in-loop", "DECLARE FUNCTION dbl%(BYVAL n%)\nDIM a%(0 TO 4)\nFOR i% = 0 TO 4\n a%(i%) = dbl%(i%)\nNEXT i%\n\nFUNCTION dbl%(BYVAL n%)\n dbl% = n% OR n%\nEND FUNCTION");
    // wave 5/6 feature shapes
    yield return Case("gosub", "x% = 1\nGOSUB bump\ny% = x%\nEND\nbump:\nx% = x% + 10\nRETURN");
    yield return Case("data-read", "READ n%\nREAD s$\nPRINT s$\nPRINT n%\nEND\nDATA 42, hi");
    yield return Case("dynamic-array", "REDIM a%(1 TO 8)\nFOR i% = 1 TO 8\n a%(i%) = i% * i%\nNEXT i%\nx% = a%(4)\nEND");
    yield return Case("redim-preserve", "REDIM a%(1 TO 3)\na%(1) = 7\nREDIM PRESERVE a%(1 TO 9)\nx% = a%(1)\nEND");
    yield return Case("udt-record", "TYPE Pt\n X AS INTEGER\n Y AS INTEGER\nEND TYPE\nDIM a AS Pt\nDIM b AS Pt\na.X = 5\nb = a\nIF a = b THEN PRINT \"eq\"\nEND");
    yield return Case("udt-fixed-field", "TYPE Person\n Name AS STRING * 16\n Age AS INTEGER\nEND TYPE\nDIM p AS Person\np.Name = \"Ada\"\np.Age = 36\nPRINT p.Name\nEND");
    yield return Case("string-funcs", "a$ = \"Hello World\"\nb$ = UCASE$(LEFT$(a$, 5))\np% = INSTR(a$, \"World\")\nMID$(a$, 1, 5) = \"HELLO\"\nPRINT b$\nPRINT p%\nEND");
    yield return Case("select-string", "a$ = \"b\"\nSELECT CASE a$\nCASE \"a\"\n PRINT 1\nCASE \"b\", \"c\"\n PRINT 2\nCASE ELSE\n PRINT 9\nEND SELECT\nEND");
    yield return Case("random-file", "OPEN \"d.dat\" FOR RANDOM AS #1 LEN = 2\nFOR i% = 1 TO 5\n PUT #1, i%, i%\nNEXT i%\nGET #1, 3, r%\nCLOSE #1\nEND");
    yield return Case("print-layout", "PRINT \"a\", \"b\"\nPRINT TAB(10); \"x\"\nPRINT SPC(3); \"y\"\nEND");
    yield return Case("power-op", "b# = 2.0\nr# = b# ^ 10.0\nPRINT r#\nEND");
    yield return Case("binary-record", "n% = 42\nr$ = MKI$(n%)\nx% = CVI(r$)\nPRINT x%\nEND");
  }

  private static TestCaseData Case(string name, string source) => new TestCaseData(source) { TestName = name };

  [TestCaseSource(nameof(Programs))]
  public void MainBody_ThroughVerifiedPipeline_StaysWellFormed(string source) {
    var fn = IrLowering.TryLowerMainBody(Bind(source));
    Assert.That(fn, Is.Not.Null, "expected this program to lower");
    var pm = IrPassManager.Standard();
    pm.VerifyEachPass = true;
    pm.RunToFixpoint(fn!);
    Assert.That(IrVerifier.Verify(fn!), Is.Empty);
  }

  [TestCaseSource(nameof(Modules))]
  public void Module_ThroughVerifiedPipelineAndInliner_StaysWellFormed(string source) {
    var module = IrLowering.TryLowerModule(Bind(source));
    Assert.That(module, Is.Not.Null, "expected this program to lower");
    var pm = IrPassManager.Standard();
    pm.VerifyEachPass = true;
    pm.RunOnModule(module!);
    Inliner.Run(module!);
    pm.RunOnModule(module!);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
  }

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    return Binder.Bind(unit, Dialect.Pb35);
  }
}
