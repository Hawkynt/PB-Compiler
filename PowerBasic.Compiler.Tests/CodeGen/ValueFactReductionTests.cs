using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class ValueFactReductionTests {

  [Test]
  public void Reduce_GivenZeroToOneRange_ThenOnlyBitZeroMayBeOne() {
    var facts = ValueFactReduction.Reduce(
      new(new Interval(0, 1), KnownBits.Unknown, Congruence.Unknown), 16, signed: true);

    Assert.That(facts.Range, Is.EqualTo(new Interval(0, 1)));
    Assert.That(facts.Bits.Zeros & 0xfffeUL, Is.EqualTo(0xfffeUL));
    Assert.That(ValueFactReduction.OnlyBitMayBeOne(facts, bit: 0, width: 16), Is.True);
  }

  [Test]
  public void Reduce_GivenOnlyBitZeroUnknown_ThenRangeBecomesZeroToOne() {
    var facts = ValueFactReduction.Reduce(
      new(Interval.Top, new KnownBits(Ones: 0, Zeros: 0xfffe), Congruence.Unknown), 16, signed: true);

    Assert.That(facts.Range, Is.EqualTo(new Interval(0, 1)));
  }

  [Test]
  public void Reduce_GivenPowerOfTwoCongruence_ThenLowBitsBecomeKnown() {
    var facts = ValueFactReduction.Reduce(
      new(Interval.Top, KnownBits.Unknown, new Congruence(8, 5)), 16, signed: true);

    Assert.That(facts.Bits.Ones & 0b111UL, Is.EqualTo(0b101UL));
    Assert.That(facts.Bits.Zeros & 0b111UL, Is.EqualTo(0b010UL));
  }

  [Test]
  public void Reduce_GivenKnownLowBits_ThenPowerOfTwoCongruenceAppears() {
    var facts = ValueFactReduction.Reduce(
      new(Interval.Top, new KnownBits(Ones: 0b010, Zeros: 0b101), Congruence.Unknown), 16, signed: true);

    Assert.That(facts.Mod.Modulus, Is.EqualTo(8));
    Assert.That(facts.Mod.Residue, Is.EqualTo(2));
  }

  [Test]
  public void Reduce_GivenIndependentModSixAndModFourFacts_ThenIntersectsToModTwelve() {
    var facts = ValueFactReduction.Reduce(
      new(Interval.Top, new KnownBits(Ones: 0b01, Zeros: 0b10), new Congruence(6, 1)), 16, signed: true);

    Assert.That(facts.Mod.Modulus, Is.EqualTo(12));
    Assert.That(facts.Mod.Residue, Is.EqualTo(1));
  }

  [Test]
  public void AddSub_GivenZeroOrOnePlusOne_ThenCarryAnalysisKeepsHighBitsZero() {
    var left = ValueFactReduction.Reduce(
      new(new Interval(0, 1), KnownBits.Unknown, Congruence.Unknown), 16, signed: true);
    var result = ValueFactReduction.Binary(BinaryOp.Add, left, ValueFacts.Of(1, 16), 16, signed: true);

    Assert.That(result.Range, Is.EqualTo(new Interval(1, 2)));
    Assert.That(result.Bits.Zeros & 0xfffcUL, Is.EqualTo(0xfffcUL));
  }

  [Test]
  public void Add_GivenExactResultWraps_WhenReduced_ThenEveryDomainNamesTheRuntimeValue() {
    var result = ValueFactReduction.Binary(
      BinaryOp.Add, ValueFacts.Of(30000, 16), ValueFacts.Of(30000, 16), 16, signed: true);

    Assert.Multiple(() => {
      Assert.That(result.Range, Is.EqualTo(Interval.Of(-5536)));
      Assert.That(result.Bits.Allows(-5536, 16), Is.True);
      Assert.That(result.Bits.Allows(0, 16), Is.False);
      Assert.That(result.Mod.IsExact, Is.True);
      Assert.That(result.Mod.Residue, Is.EqualTo(-5536));
    });
  }

  [Test]
  public void Divide_GivenFiniteDivisorRangeExcludingZero_ThenResultRangeRemainsFinite() {
    var result = ValueFactReduction.Binary(
      BinaryOp.IntegerDivide,
      new(new Interval(-100, 100), KnownBits.Unknown, Congruence.Unknown),
      new(new Interval(2, 4), KnownBits.Unknown, Congruence.Unknown),
      16,
      signed: true);

    Assert.That(result.Range, Is.EqualTo(new Interval(-50, 50)));
  }

  [Test]
  public void Modulo_GivenFiniteNonconstantDivisorRangeExcludingZero_ThenResultRangeRemainsFinite() {
    var result = ValueFactReduction.Binary(
      BinaryOp.Modulo,
      new(new Interval(-100, 100), KnownBits.Unknown, Congruence.Unknown),
      new(new Interval(2, 4), KnownBits.Unknown, Congruence.Unknown),
      16,
      signed: true);

    Assert.That(result.Range, Is.EqualTo(new Interval(-3, 3)));
  }

  [TestCase(BinaryOp.IntegerDivide)]
  [TestCase(BinaryOp.Modulo)]
  public void DivMod_GivenDivisorRangeMayContainZero_ThenRangeStaysUnknown(BinaryOp op) {
    var result = ValueFactReduction.Binary(
      op,
      new(new Interval(-100, 100), KnownBits.Unknown, Congruence.Unknown),
      new(new Interval(-1, 1), KnownBits.Unknown, Congruence.Unknown),
      16,
      signed: true);

    Assert.That(result.Range.IsTop, Is.True);
  }

  [Test]
  public void Multiply_GivenNegativePowerOfTwo_ThenKnownBitsIncludeNegation() {
    var result = ValueFactReduction.Binary(
      BinaryOp.Multiply, ValueFacts.Of(3, 16), ValueFacts.Of(-2, 16), 16, signed: true);

    Assert.That(result.Range, Is.EqualTo(Interval.Of(-6)));
    Assert.That(result.Bits.Allows(-6, 16), Is.True);
    Assert.That(result.Bits.Allows(6, 16), Is.False);
  }

  [Test]
  public void Reduce_GivenUnsigned64HighBitExact_ThenDoesNotInventSignedCongruence() {
    var exactBits = new KnownBits(Ones: 1UL << 63, Zeros: ~(1UL << 63));
    var facts = ValueFactReduction.Reduce(new(Interval.Top, exactBits, Congruence.Unknown), 64, signed: false);

    Assert.That(facts.Bits.Ones, Is.EqualTo(1UL << 63));
    Assert.That(facts.Range.IsTop, Is.True);
    Assert.That(facts.Mod.IsUnknown, Is.True);
  }

  [Test]
  public void Analysis_GivenAndOne_ThenStoredValueCarriesRangeAndBitShape() {
    var (_, model) = Bind("x% = a% AND 1\ny% = x%\nEND");
    var points = IntervalRangeAnalysis.AnalyzeProgramPoints(model.MainBody, model);
    var facts = FactAt(points, model.MainBody[1], "x");

    Assert.That(facts, Is.Not.Null);
    Assert.That(facts!.Value.Range, Is.EqualTo(new Interval(0, 1)));
    Assert.That(ValueFactReduction.OnlyBitMayBeOne(facts.Value, bit: 0, width: 16), Is.True);
  }

  [Test]
  public void Analysis_GivenUnknownComparison_ThenTruthRangeIsMinusOneOrZero() {
    var (_, model) = Bind("flag% = a% < 10\ny% = flag%\nEND");
    var points = IntervalRangeAnalysis.AnalyzeProgramPoints(model.MainBody, model);
    var facts = FactAt(points, model.MainBody[1], "flag");

    Assert.That(facts, Is.Not.Null);
    Assert.That(facts!.Value.Range, Is.EqualTo(new Interval(-1, 0)));
  }

  [Test]
  public void Analysis_GivenProvableComparison_ThenTruthValueIsExact() {
    var (_, model) = Bind("a% = 5\nflag% = a% < 10\ny% = flag%\nEND");
    var points = IntervalRangeAnalysis.AnalyzeProgramPoints(model.MainBody, model);
    var facts = FactAt(points, model.MainBody[2], "flag");

    Assert.That(facts, Is.Not.Null);
    Assert.That(facts!.Value.Range, Is.EqualTo(Interval.Of(-1)));
    Assert.That(facts.Value.Mod.IsExact, Is.True);
    Assert.That(facts.Value.Mod.Residue, Is.EqualTo(-1));
  }

  [Test]
  public void Evaluate_GivenFixedWidthExpressionAtProgramPoint_ThenEmitterStyleQueryGetsReducedFacts() {
    var (_, model) = Bind("x% = a% AND 1\ny% = x% XOR 1\nEND");
    var points = IntervalRangeAnalysis.AnalyzeProgramPoints(model.MainBody, model);
    var statement = (AssignStmt)model.MainBody[1];
    var env = points[statement];

    var facts = IntervalRangeAnalysis.Evaluate(statement.Value, env, model);

    Assert.That(facts.Range, Is.EqualTo(new Interval(0, 1)));
    Assert.That(facts.Bits.Zeros & 0xfffeUL, Is.EqualTo(0xfffeUL));
    Assert.That(ValueFactReduction.OnlyBitMayBeOne(facts, bit: 0, width: 16), Is.True);
  }

  private static (CompilationUnit Unit, SemanticModel Model) Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    return (unit, model);
  }

  private static ValueFacts? FactAt(
      IReadOnlyDictionary<Statement, IReadOnlyDictionary<VariableSymbol, ValueFacts>> points,
      Statement statement,
      string name) {
    if (!points.TryGetValue(statement, out var env))
      return null;
    foreach (var (symbol, facts) in env)
      if (symbol.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        return facts;
    return null;
  }
}
