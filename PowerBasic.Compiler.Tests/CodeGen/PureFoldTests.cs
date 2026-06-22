using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 O19 - automatic compile-time evaluation of pure functions. A function whose
/// result depends only on its BYVAL arguments, called with constant arguments, is
/// interpreted at compile time and the call replaced by the literal. Purity is
/// inferred (no CONSTEXPR keyword); impure functions and runtime arguments emit a
/// real call. Behaviour is verified byte-identical against pb35/genuine PBC by the
/// differential harness; these tests pin the analyzer's fold decisions and values.
/// </summary>
[TestFixture]
public sealed class PureFoldTests {

  private static (SemanticModel Model, Dictionary<CallOrIndexExpr, ConstantValue> Folds) Analyze(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
    return (model, OptPureFold.Analyze(model));
  }

  /// <summary>The single folded constant for a call to <paramref name="name"/>, or null if it was not folded.</summary>
  private static long? FoldedCall(Dictionary<CallOrIndexExpr, ConstantValue> folds, string name) {
    var hit = folds.Where(kv => kv.Key.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(kv => (long?)kv.Value.Integer).ToList();
    return hit.Count == 1 ? hit[0] : (hit.Count == 0 ? null : throw new InvalidOperationException("ambiguous"));
  }

  [Test]
  public void Analyze_GivenRecursiveFactorialWithConstant_ThenFoldsToValue() {
    var (_, folds) = Analyze("FUNCTION Fact&(BYVAL n%)\n IF n% <= 1 THEN\n  Fact& = 1\n ELSE\n  Fact& = n% * Fact&(n% - 1)\n END IF\nEND FUNCTION\nDIM r&\nr& = Fact&(6)\n");
    Assert.That(FoldedCall(folds, "Fact"), Is.EqualTo(720));
  }

  [Test]
  public void Analyze_GivenLoopSum_ThenFoldsToValue() {
    var (_, folds) = Analyze("FUNCTION SumTo&(BYVAL n%)\n DIM s&, i%\n FOR i% = 1 TO n%\n  s& = s& + i%\n NEXT\n SumTo& = s&\nEND FUNCTION\nDIM r&\nr& = SumTo&(100)\n");
    Assert.That(FoldedCall(folds, "SumTo"), Is.EqualTo(5050));
  }

  [Test]
  public void Analyze_GivenDoWhileGcd_ThenFoldsToValue() {
    var (_, folds) = Analyze("FUNCTION Gcd&(BYVAL a&, BYVAL b&)\n DO WHILE b& <> 0\n  DIM t&\n  t& = b&\n  b& = a& MOD b&\n  a& = t&\n LOOP\n Gcd& = a&\nEND FUNCTION\nDIM r&\nr& = Gcd&(48, 36)\n");
    Assert.That(FoldedCall(folds, "Gcd"), Is.EqualTo(12));
  }

  [Test]
  public void Analyze_GivenSelectCase_ThenFoldsTakenArm() {
    var (_, folds) = Analyze("FUNCTION Pick&(BYVAL n%)\n SELECT CASE n%\n  CASE 1 : Pick& = 10\n  CASE 2 TO 4 : Pick& = 20\n  CASE ELSE : Pick& = 99\n END SELECT\nEND FUNCTION\nDIM r&\nr& = Pick&(3)\n");
    Assert.That(FoldedCall(folds, "Pick"), Is.EqualTo(20));
  }

  [Test]
  public void Analyze_GivenRuntimeArgument_ThenDoesNotFold() {
    var (_, folds) = Analyze("FUNCTION Dbl&(BYVAL n&)\n Dbl& = n& * 2\nEND FUNCTION\nDIM x&, r&\nx& = TIMER\nr& = Dbl&(x&)\n");
    Assert.That(FoldedCall(folds, "Dbl"), Is.Null, "a runtime argument cannot be evaluated at compile time");
  }

  [Test]
  public void Analyze_GivenSharedGlobalRead_ThenDoesNotFold() {
    // a function reading a SHARED module variable is not pure - its result is not a function of its args
    var (_, folds) = Analyze("DIM g AS SHARED LONG\nFUNCTION Imp&(BYVAL n&)\n SHARED g\n Imp& = n& + g\nEND FUNCTION\nDIM r&\nr& = Imp&(5)\n");
    Assert.That(FoldedCall(folds, "Imp"), Is.Null, "a SHARED read makes the function impure");
  }

  [Test]
  public void Analyze_GivenPrintSideEffect_ThenDoesNotFold() {
    var (_, folds) = Analyze("FUNCTION Noisy&(BYVAL n&)\n PRINT n&\n Noisy& = n&\nEND FUNCTION\nDIM r&\nr& = Noisy&(5)\n");
    Assert.That(FoldedCall(folds, "Noisy"), Is.Null, "a function with I/O is impure");
  }

  [Test]
  public void Analyze_GivenWrapAroundArithmetic_ThenFoldsWithStorageWrap() {
    // 200 * 200 = 40000 overflows signed 16-bit INTEGER: silent wrap to -25536 (PB QUIRKS)
    var (_, folds) = Analyze("FUNCTION Sq%(BYVAL n%)\n Sq% = n% * n%\nEND FUNCTION\nDIM r%\nr% = Sq%(200)\n");
    Assert.That(FoldedCall(folds, "Sq"), Is.EqualTo(unchecked((short)40000)), "the folded value matches the 16-bit ALU wrap");
  }

  [Test]
  public void Analyze_GivenCallToOtherPureFunction_ThenFoldsTransitively() {
    var (_, folds) = Analyze("FUNCTION Add&(BYVAL a&, BYVAL b&)\n Add& = a& + b&\nEND FUNCTION\nFUNCTION Calc&(BYVAL n&)\n Calc& = Add&(n&, 100)\nEND FUNCTION\nDIM r&\nr& = Calc&(5)\n");
    Assert.That(FoldedCall(folds, "Calc"), Is.EqualTo(105));
  }

  [Test]
  public void Analyze_GivenFloatFunction_ThenDoesNotFold() {
    var (_, folds) = Analyze("FUNCTION Half#(BYVAL n&)\n Half# = n& / 2\nEND FUNCTION\nDIM r#\nr# = Half#(10)\n");
    Assert.That(FoldedCall(folds, "Half"), Is.Null, "v1 folds integer functions only");
  }
}
