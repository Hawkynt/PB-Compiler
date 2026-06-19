using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Syntax;

/// <summary>
/// Dialect gating (--dialect pb20..pb35): every feature from the
/// <see cref="DialectFacts"/> table must be rejected below its minimum dialect
/// (with the documented message shape) and accepted at it. Front-end only.
/// </summary>
[TestFixture]
public sealed class DialectGateTests {

  private static List<string> Compile(string source, Dialect dialect) {
    var tokens = Lexer.Tokenize(source, "TEST.BAS", dialect);
    var unit = Parser.Parse(tokens, "TEST.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    return [.. model.Errors.Select(e => e.Message)];
  }

  private static void AssertRejected(string source, Dialect dialect, string requiresFragment) {
    try {
      var errors = Compile(source, dialect);
      Assert.That(errors, Has.Some.Contains("requires PowerBASIC").And.Contains(requiresFragment),
        $"expected a '{requiresFragment}' gate under {dialect.DisplayName()} for: {source}\ngot: {string.Join("; ", errors)}");
    } catch (LexerException e) {
      Assert.That(e.Message, Does.Contain("requires PowerBASIC").And.Contain(requiresFragment), source);
    } catch (ParserException e) {
      Assert.That(e.Message, Does.Contain("requires PowerBASIC").And.Contain(requiresFragment), source);
    }
  }

  private static void AssertAccepted(string source, Dialect dialect) {
    var errors = Compile(source, dialect); // must not throw
    Assert.That(errors, Has.None.Contains("requires PowerBASIC"),
      $"unexpected gate under {dialect.DisplayName()} for: {source}\ngot: {string.Join("; ", errors)}");
  }

  #region message shape

  [Test]
  public void RequirementMessage_GivenFeatureAndDialect_WhenFormatted_ThenDocumentedShape()
    => Assert.That(DialectFacts.RequirementMessage(LanguageFeature.Pointers, Dialect.Pb30),
      Is.EqualTo("data pointers (PTR types, '@' dereference) requires PowerBASIC 3.2 (current dialect: PB 3.0)"));

  [TestCase(Dialect.Pb20, "PB 2.0")]
  [TestCase(Dialect.Pb35, "PB 3.5")]
  public void DisplayName_GivenDialect_WhenFormatted_ThenDottedVersion(Dialect dialect, string expected)
    => Assert.That(dialect.DisplayName(), Is.EqualTo(expected));

  #endregion

  #region PB 3.0 gates

  [TestCase("! mov ax, 1", "3.0")]
  public void Gate_GivenInlineAsm_WhenPb21_ThenRejected(string source, string version)
    => AssertRejected(source, Dialect.Pb21, version);

  [TestCase("x? = 1")]
  [TestCase("x?? = 1")]
  [TestCase("x??? = 1")]
  [TestCase("DIM x AS BYTE")]
  [TestCase("DIM x AS WORD")]
  [TestCase("DIM x AS DWORD")]
  public void Gate_GivenUnsignedTypes_WhenPb21_ThenRejectedButPb30Accepts(string source) {
    AssertRejected(source, Dialect.Pb21, "3.0");
    AssertAccepted(source, Dialect.Pb30);
  }

  [TestCase("x&& = 1")]
  [TestCase("DIM x AS QUAD")]
  [TestCase("DEFQUD Q")]
  public void Gate_GivenQuad_WhenPb21_ThenRejectedButPb30Accepts(string source) {
    AssertRejected(source, Dialect.Pb21, "3.0");
    AssertAccepted(source, Dialect.Pb30);
  }

  [Test]
  public void Gate_GivenTypeDeclaration_WhenPb21_ThenRejected()
    => AssertRejected("TYPE T\n  a AS INTEGER\nEND TYPE", Dialect.Pb21, "3.0");

  [Test]
  public void Gate_GivenDimHuge_WhenPb21_ThenRejectedButPb30Accepts() {
    AssertRejected("DIM HUGE x%(10)", Dialect.Pb21, "3.0");
    AssertAccepted("DIM HUGE x%(10)", Dialect.Pb30);
  }

  #endregion

  #region PB 3.1 gates

  [TestCase("x% = &HFFFF%")]
  [TestCase("x% = &HFFFF??")]
  public void Gate_GivenTypedRadixLiteral_WhenPb30_ThenRejectedButPb31Accepts(string source) {
    AssertRejected(source, Dialect.Pb30, "3.1");
    AssertAccepted(source, Dialect.Pb31);
  }

  [Test]
  public void Gate_GivenAlias_WhenPb30_ThenRejected()
    => AssertRejected("DECLARE SUB Foo ALIAS \"FOO\" (a AS INTEGER)", Dialect.Pb30, "3.1");

  [Test]
  public void Gate_GivenAnyParameter_WhenPb30_ThenRejected()
    => AssertRejected("DECLARE SUB Foo (a AS ANY)", Dialect.Pb30, "3.1");

  [Test]
  public void Gate_GivenUdtComparison_WhenPb30_ThenRejectedButPb31Accepts() {
    const string source = "TYPE T\n  a AS INTEGER\nEND TYPE\nDIM x AS T\nDIM y AS T\nz% = x = y";
    AssertRejected(source, Dialect.Pb30, "3.1");
    AssertAccepted(source, Dialect.Pb31);
  }

  #endregion

  #region PB 3.2 gates

  [TestCase("DIM p AS INTEGER PTR")]
  [TestCase("x% = @p")]
  public void Gate_GivenPointers_WhenPb31_ThenRejected(string source)
    => AssertRejected(source, Dialect.Pb31, "3.2");

  [TestCase("GOTO DWORD g???")]
  [TestCase("GOSUB DWORD g???")]
  [TestCase("CALL DWORD g???")]
  public void Gate_GivenCodePointers_WhenPb31_ThenRejectedButPb32Accepts(string source) {
    AssertRejected(source, Dialect.Pb31, "3.2");
    AssertAccepted(source, Dialect.Pb32);
  }

  [Test]
  public void Gate_GivenVarPtr32_WhenPb31_ThenRejected()
    => AssertRejected("x??? = VARPTR32(y%)", Dialect.Pb31, "3.2");

  [Test]
  public void Gate_GivenUnderscoreIdentifier_WhenPb31_ThenRejectedButPb32Accepts() {
    AssertRejected("My_Var = 1", Dialect.Pb31, "3.2");
    AssertAccepted("My_Var = 1", Dialect.Pb32);
  }

  #endregion

  #region PB 3.5 gates

  [Test]
  public void Gate_GivenAsciiz_WhenPb32_ThenRejectedButPb35Accepts() {
    AssertRejected("DIM z AS ASCIIZ * 8", Dialect.Pb32, "3.5");
    AssertAccepted("DIM z AS ASCIIZ * 8", Dialect.Pb35);
  }

  [Test]
  public void Gate_GivenConcatOperator_WhenPb32_ThenRejectedButPb35Accepts() {
    AssertRejected("c$ = a$ & b$", Dialect.Pb32, "3.5");
    AssertAccepted("c$ = a$ & b$", Dialect.Pb35);
  }

  [TestCase("s$ = TRIM$(x$)", "TRIM$")]
  [TestCase("n& = SIZEOF(x%)", "SIZEOF")]
  [TestCase("e% = ERRCLEAR", "ERRCLEAR")]
  [TestCase("c% = CONSIN", "CONSIN")]
  public void Gate_GivenNewIntrinsics_WhenPb32_ThenRejected(string source, string what)
    => AssertRejected(source, Dialect.Pb32, what);

  [Test]
  public void Gate_GivenRedimPreserve_WhenPb32_ThenRejectedButPb35Accepts() {
    AssertRejected("REDIM PRESERVE a%(50)", Dialect.Pb32, "3.5");
    AssertAccepted("REDIM PRESERVE a%(50)", Dialect.Pb35);
  }

  [Test]
  public void Gate_GivenIndexedPointer_WhenPb32_ThenRejected()
    => AssertRejected("DIM p AS INTEGER PTR\nx% = @p[2]", Dialect.Pb32, "3.5");

  [TestCase("STDOUT a$")]
  [TestCase("STDIN LINE, s$")]
  public void Gate_GivenStdInOut_WhenPb32_ThenRejected(string source)
    => AssertRejected(source, Dialect.Pb32, "3.5");

  [Test]
  public void Gate_GivenSetEof_WhenPb32_ThenRejected()
    => AssertRejected("SETEOF #1", Dialect.Pb32, "3.5");

  [Test]
  public void Gate_GivenRndRange_WhenPb32_ThenRejectedButPb35Accepts() {
    AssertRejected("r& = RND(1, 6)", Dialect.Pb32, "3.5");
    AssertAccepted("r& = RND(1, 6)", Dialect.Pb35);
  }

  [Test]
  public void Gate_GivenCvOffset_WhenPb32_ThenRejectedButPb35Accepts() {
    AssertRejected("n& = CVL(x$, 3)", Dialect.Pb32, "3.5");
    AssertAccepted("n& = CVL(x$, 3)", Dialect.Pb35);
  }

  [Test]
  public void Gate_GivenAscStatement_WhenPb32_ThenRejected()
    => AssertRejected("ASC(s$, 1) = 65", Dialect.Pb32, "3.5");

  [Test]
  public void Gate_GivenDimVirtual_WhenPb32_ThenRejected()
    => AssertRejected("DIM VIRTUAL x%(100)", Dialect.Pb32, "3.5");

  [Test]
  public void Gate_GivenStringPtrInType_WhenPb32_ThenRejectedButPb35Accepts() {
    const string source = "TYPE T\n  s AS STRING PTR\nEND TYPE";
    AssertRejected(source, Dialect.Pb32, "3.5");
    AssertAccepted(source, Dialect.Pb35);
  }

  #endregion

  #region PB 3.6 gates

  [Test]
  public void Gate_GivenLambda_WhenPb35_ThenRejectedButPb36Accepts() {
    const string source = "DIM f???\nf??? = FUNCTION(BYVAL x AS LONG) AS LONG => x * x";
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [Test]
  public void Bind_GivenCapturingLambdaIntoDelegate_WhenPb36_ThenBindsWithoutError() {
    // a lambda that references an outer local is now a stack closure (its env is the
    // enclosing frame, reached through the env pointer of the fat delegate value)
    var tokens = Lexer.Tokenize("SUB Outer()\n  DIM base AS LONG\n  DIM f AS FUNCTION(LONG) AS LONG\n  f = FUNCTION(BYVAL x AS LONG) AS LONG => x + base\nEND SUB", "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(Parser.Parse(tokens, "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors.Select(e => e.Message)));
  }

  [Test]
  public void Gate_GivenNestedProcedure_WhenPb35_ThenRejectedButPb36Accepts() {
    const string source = "SUB Outer()\n  DIM x AS LONG\n  SUB Inner()\n    x = 1\n  END SUB\nEND SUB";
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [Test]
  public void Gate_GivenArrayInitializer_WhenPb35_ThenRejectedButPb36Accepts() {
    const string source = "DIM a%() = {1, 2, 3}";
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [Test]
  public void Gate_GivenFromEndIndex_WhenPb35_ThenRejectedButPb36Accepts() {
    const string source = "DIM a%(5)\nx% = a%(^1)";
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [TestCase("DIM EMS a%(10)")]
  [TestCase("DIM XMS a%(10)")]
  public void Gate_GivenExternalMemoryArray_WhenPb35_ThenRejectedButPb36Accepts(string source) {
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [Test]
  public void Gate_GivenNamedArgument_WhenPb35_ThenRejectedButPb36Accepts() {
    const string source = "DECLARE FUNCTION Foo&(BYVAL y AS LONG)\nx& = Foo&(y := 5)\nFUNCTION Foo&(BYVAL y AS LONG)\nFoo& = y\nEND FUNCTION";
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [Test]
  public void Gate_GivenDefaultParameter_WhenPb35_ThenRejectedButPb36Accepts() {
    const string source = "SUB Foo(BYVAL x AS INTEGER = 5)\nEND SUB";
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [Test]
  public void Gate_GivenWithBlock_WhenPb35_ThenRejectedButPb36Accepts() {
    const string source = "TYPE T\n  X AS INTEGER\nEND TYPE\nDIM p AS T\nWITH p\n.X = 1\nEND WITH";
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [Test]
  public void Gate_GivenEnum_WhenPb35_ThenRejectedButPb36Accepts() {
    const string source = "ENUM Color\nRed\nGreen\nBlue\nEND ENUM";
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [Test]
  public void Gate_GivenExpressionBodiedFunction_WhenPb35_ThenRejectedButPb36Accepts() {
    const string source = "FUNCTION Sq&(BYVAL x AS LONG) = x * x";
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [TestCase("n% += 1")]
  [TestCase("s$ &= \"x\"")]
  public void Gate_GivenCompoundAssignment_WhenPb35_ThenRejectedButPb36Accepts(string source) {
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [TestCase("DIM x = 5")]
  [TestCase("DIM n AS LONG = 100000")]
  public void Gate_GivenDimInitializer_WhenPb35_ThenRejectedButPb36Accepts(string source) {
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [TestCase("DIM f AS FUNCTION(LONG) AS LONG")]
  [TestCase("DIM g AS SUB(INTEGER)")]
  public void Gate_GivenProcPointerType_WhenPb35_ThenRejectedButPb36Accepts(string source) {
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [TestCase("DIM a%() = [1..4]")]
  public void Gate_GivenCollectionLiteral_WhenPb35_ThenRejectedButPb36Accepts(string source) {
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [Test]
  public void Gate_GivenForEach_WhenPb35_ThenRejectedButPb36Accepts() {
    const string source = "FOR EACH i% IN [1..4]\n  PRINT i%\nNEXT";
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [Test]
  public void Gate_GivenNamedDelegateType_WhenPb35_ThenRejectedButPb36Accepts() {
    const string source = """
      DECLARE FUNCTION Cmp(BYVAL a AS LONG) AS LONG
      DIM f AS Cmp
      """;
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [Test]
  public void Gate_GivenTernaryIf_WhenPb35_ThenRejectedButPb36Accepts() {
    const string source = "x = IF(1, 2, 3)";
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [TestCase("x = a ANDALSO b")]
  [TestCase("x = a ORELSE b")]
  public void Gate_GivenShortCircuitOps_WhenPb35_ThenRejectedButPb36Accepts(string source) {
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [TestCase("x = a << 2")]
  [TestCase("x = a >> 2")]
  [TestCase("x = a <<> 2")]
  [TestCase("x = a <>> 2")]
  [TestCase("x = a | b")]
  [TestCase("x <<= 2")]
  [TestCase("x |= 4")]
  public void Gate_GivenShiftRotateOps_WhenPb35_ThenRejectedButPb36Accepts(string source) {
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [TestCase("DIM p AS INTEGER PTR\nDIM q AS INTEGER PTR\nq = p +* 1")]
  [TestCase("DIM p AS INTEGER PTR\nDIM q AS INTEGER PTR\nq = p -* 1")]
  public void Gate_GivenScaledPointerArith_WhenPb35_ThenRejectedButPb36Accepts(string source) {
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [Test]
  public void Gate_GivenOverloadedFunction_WhenPb35_ThenRejectedButPb36Accepts() {
    const string source = """
      FUNCTION F(BYVAL a AS LONG)
        F = a
      END FUNCTION
      FUNCTION F(BYVAL a AS LONG, BYVAL b AS LONG)
        F = a + b
      END FUNCTION
      """;
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [TestCase("y$ = $\"a {x%} b\"")]
  [TestCase("y$ = $\"{x%:###.##}\"")]
  public void Gate_GivenStringInterpolation_WhenPb35_ThenRejectedButPb36Accepts(string source) {
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [Test]
  public void Gate_GivenYield_WhenPb35_ThenRejectedButPb36Accepts() {
    const string source = "FUNCTION Gen&()\n  YIELD 1\nEND FUNCTION";
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  [Test]
  public void Gate_GivenObjectInitializer_WhenPb35_ThenRejectedButPb36Accepts() {
    const string source = "TYPE Point\n  X AS INTEGER\n  Y AS INTEGER\nEND TYPE\nDIM p = NEW Point { .X = 1, .Y = 2 }";
    AssertRejected(source, Dialect.Pb35, "3.6");
    AssertAccepted(source, Dialect.Pb36);
  }

  #endregion

  #region preprocessor gate

  [Test]
  public void Gate_GivenElseIfMeta_WhenPb32_ThenRejectedButPb35Accepts() {
    const string source = "%X = 1\n$IF %X\na = 1\n$ELSEIF %X\nb = 2\n$ENDIF";
    var provider = new InMemorySource(source);
    Assert.Throws<PreprocessorException>(() => Preprocessor.Expand("MAIN.BAS", provider, Dialect.Pb32).ToList());
    Assert.DoesNotThrow(() => Preprocessor.Expand("MAIN.BAS", provider, Dialect.Pb35).ToList());
  }

  private sealed class InMemorySource(string text) : ISourceProvider {
    public bool TryReadSource(string name, string? includedFrom, out string source, out string resolvedName) {
      source = text;
      resolvedName = name;
      return true;
    }
  }

  #endregion

  #region defaults

  [Test]
  public void Compile_GivenEverything35_WhenDefaultDialect_ThenAccepted()
    => AssertAccepted("DIM p AS INTEGER PTR\nDIM z AS ASCIIZ * 4\nc$ = a$ & TRIM$(b$)\nx&& = &HFFFF&&", Dialect.Pb35);

  #endregion
}
