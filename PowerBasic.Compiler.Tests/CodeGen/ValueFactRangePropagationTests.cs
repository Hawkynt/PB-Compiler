using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class ValueFactRangePropagationTests {

  private static readonly BinaryOp[] _rangeClosedOperations = [
    BinaryOp.Add,
    BinaryOp.Subtract,
    BinaryOp.Multiply,
    BinaryOp.And,
    BinaryOp.Or,
    BinaryOp.Xor,
    BinaryOp.Eqv,
    BinaryOp.Imp,
    BinaryOp.ShiftLeft,
    BinaryOp.ShiftRightArith,
    BinaryOp.ShiftRightLogical,
    BinaryOp.RotateLeft,
    BinaryOp.RotateRight,
  ];

  [TestCaseSource(nameof(_rangeClosedOperations))]
  public void Binary_GivenRangeTrackedOperands_WhenFixedWidthOperationRuns_ThenResultRemainsRangeTracked(BinaryOp op) {
    var left = new ValueFacts(new Interval(-1, 0), KnownBits.Unknown, Congruence.Unknown);
    var right = op is BinaryOp.ShiftLeft or BinaryOp.ShiftRightArith or BinaryOp.ShiftRightLogical
      or BinaryOp.RotateLeft or BinaryOp.RotateRight
      ? ValueFacts.Of(1, 16)
      : new ValueFacts(new Interval(-1, 0), KnownBits.Unknown, Congruence.Unknown);

    var result = ValueFactReduction.Binary(op, left, right, width: 16, signed: true);

    Assert.That(result.Range.IsTop, Is.False, $"{op} dropped two finite operand ranges to Top");
    Assert.That(result.Range.Lo, Is.GreaterThanOrEqualTo(short.MinValue));
    Assert.That(result.Range.Hi, Is.LessThanOrEqualTo(short.MaxValue));
  }

  [Test]
  public void Negate_GivenRangeTrackedOperandThatMayWrap_WhenEvaluated_ThenResultRemainsRangeTracked() {
    var value = new ValueFacts(new Interval(short.MinValue, -32000), KnownBits.Unknown, Congruence.Unknown);

    var result = ValueFactReduction.Negate(value, width: 16, signed: true);

    Assert.That(result.Range.IsTop, Is.False);
    Assert.That(result.Range.Lo, Is.GreaterThanOrEqualTo(short.MinValue));
    Assert.That(result.Range.Hi, Is.LessThanOrEqualTo(short.MaxValue));
  }

  [Test]
  public void Analysis_GivenRangeTrackedVariableUsedByXor_WhenStoredAndUsedAgain_ThenRangeTrackingContinues() {
    const string source = "x% = -1\nIF c% THEN x% = 0\ny% = x% XOR 4\nz% = y% AND 7\nEND";
    var model = Bind(source);

    var env = IntervalRangeAnalysis.Analyze(model.MainBody, model);
    var y = FactOf(env, "y");
    var z = FactOf(env, "z");

    Assert.That(y, Is.Not.Null);
    Assert.That(y!.Value.Range, Is.EqualTo(new Interval(short.MinValue, short.MaxValue)),
      "XOR cannot preserve a convex interval across the sign boundary, but it must preserve the finite result-type range");
    Assert.That(z, Is.Not.Null);
    Assert.That(z!.Value.Range, Is.EqualTo(new Interval(0, 7)),
      "a later calculation must be able to consume the preserved range and tighten it again");
  }

  [Test]
  public void Analysis_GivenRangeTrackedCounterAtMax_WhenIncrementWraps_ThenWrappedResultStaysTracked() {
    var model = Bind("x% = 32767\nINCR x%\nEND");

    var facts = FactOf(IntervalRangeAnalysis.Analyze(model.MainBody, model), "x");

    Assert.That(facts, Is.Not.Null);
    Assert.That(facts!.Value.Range, Is.EqualTo(Interval.Of(short.MinValue)));
  }

  [Test]
  public void Analysis_GivenTrackedBranchesInTernary_WhenJoined_ThenResultRemainsRangeTracked() {
    const string source = "x% = 1\nIF c% THEN x% = 3\ny% = IF(d%, x% AND 3, x% OR 4)\nEND";
    var model = Bind(source);

    var facts = FactOf(IntervalRangeAnalysis.Analyze(model.MainBody, model), "y");

    Assert.That(facts, Is.Not.Null);
    Assert.That(facts!.Value.Range.IsTop, Is.False,
      "a conditional expression must join its branch facts rather than erase a tracked input range");
    Assert.That(facts.Value.Range, Is.EqualTo(new Interval(1, 7)),
      "the joined bit/congruence facts retain that every possible branch value is odd");
  }

  [Test]
  public void Analysis_GivenShortCircuitBooleanLoweredToIfExpr_WhenStored_ThenTruthRangeIsTracked() {
    const string source = "x% = 0\nIF c% THEN x% = 1\nflag% = (x% = 0) ANDALSO (x% <= 1)\nEND";
    var model = Bind(source);

    var facts = FactOf(IntervalRangeAnalysis.Analyze(model.MainBody, model), "flag");

    Assert.That(facts, Is.Not.Null);
    Assert.That(facts!.Value.Range, Is.EqualTo(new Interval(-1, 0)));
  }

  [Test]
  public void Analysis_GivenRangeTrackedVariableAndIntegerEquate_WhenCalculated_ThenRangeContinues() {
    const string source = "%K = 3\nx% = 1\nIF c% THEN x% = 5\ny% = x% + %K\nEND";
    var model = Bind(source);

    var facts = FactOf(IntervalRangeAnalysis.Analyze(model.MainBody, model), "y");

    Assert.That(facts, Is.Not.Null);
    Assert.That(facts!.Value.Range, Is.EqualTo(new Interval(4, 8)),
      "an integer equate is an exact operand and must not make a tracked calculation unknown");
  }

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    return model;
  }

  private static ValueFacts? FactOf(IReadOnlyDictionary<VariableSymbol, ValueFacts> env, string name) {
    foreach (var (symbol, facts) in env)
      if (symbol.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        return facts;
    return null;
  }
}
