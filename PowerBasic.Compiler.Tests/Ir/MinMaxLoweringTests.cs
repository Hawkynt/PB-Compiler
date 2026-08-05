using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// MIN and MAX on the IR path, as a left fold of compare-and-select.
///
/// The direct emitter folds them with CMP and JGE/JLE, keeping the accumulator when it already wins.
/// The IR has to agree, and not only on the answer: the tie rule decides which of two equal
/// arguments comes back, and any-arity means the fold has to chain rather than special-case two.
///
/// Constant arguments are checked through the pass pipeline rather than by reading the select, so
/// what is asserted is the value the program would print, not the shape of the instructions that
/// compute it - the two can disagree only if the predicate is wrong, which is exactly the bug worth
/// catching.
/// </summary>
[TestFixture]
public sealed class MinMaxLoweringTests {

  private static IrModule Lower(string body, bool optimize = true) {
    var source = body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    if (optimize)
      IrPassManager.Standard().RunOnModule(module!);
    return module!;
  }

  /// <summary>
  /// Whether the module ends up handing <paramref name="value"/> to the print routine as a constant.
  ///
  /// The call argument is the thing to look at rather than a store: mem2reg promotes the variable
  /// the answer was assigned to, so after the pass pipeline there is no store left to inspect - only
  /// the value that reaches the program's output, which is the one worth asserting anyway.
  /// </summary>
  private static bool FoldsTo(IrModule module, long value)
    => module.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions)
      .OfType<IrCall>()
      .SelectMany(c => c.Args)
      .Any(a => a is IrConstantInt c && c.Value == value);

  [TestCase("MAX%(3, 8)", 8)]
  [TestCase("MIN%(3, 8)", 3)]
  [TestCase("MAX%(-5, -9)", -5)]
  [TestCase("MIN%(-5, -9)", -9)]
  [TestCase("MAX%(1, 5, 3)", 5)]
  [TestCase("MIN%(7, 2, 9)", 2)]
  [TestCase("MAX%(1, 2, 3, 4, 5)", 5)]
  public void Lower_GivenConstantArguments_WhenOptimized_ThenItFoldsToTheRightAnswer(string call, int expected) {
    // negatives are the half that a wrong signedness gets wrong: an unsigned compare makes -5 the
    // larger of -5 and -9 by accident and the smaller of -5 and 3 as well
    Assert.That(FoldsTo(Lower($"DIM r AS INTEGER\nr = {call}\nPRINT r"), expected), Is.True,
      $"{call} did not fold to {expected}");
  }

  [Test]
  public void Lower_GivenARuntimeComparison_ThenItIsASignedSelectRatherThanACall() {
    var module = Lower("DIM a AS INTEGER\nDIM b AS INTEGER\na = 3\nb = 8\nPRINT MAX%(a, b)", optimize: false);
    var instructions = module.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions).ToList();

    Assert.Multiple(() => {
      Assert.That(instructions.OfType<IrSelect>(), Is.Not.Empty, "MAX% should lower to a select");
      Assert.That(instructions.OfType<IrCmp>().Select(c => c.Pred), Does.Contain(IrCmpPred.Sge),
        "an INTEGER MAX is a SIGNED compare - Uge would make every negative argument the larger one");
      Assert.That(instructions.OfType<IrCall>().Select(c => c.Callee.Name), Has.None.Contains("min").IgnoreCase,
        "MIN/MAX should need no runtime helper");
    });
  }

  [Test]
  public void Lower_GivenEqualArguments_ThenTheAccumulatorWinsAsItDoesInTheDirectEmitter() {
    // The fold keeps the accumulator on a tie, matching CMP + JGE. Numerically it makes no
    // difference; it matters because the two code generators are checked against each other.
    var module = Lower("DIM a AS INTEGER\nDIM b AS INTEGER\na = 4\nb = 4\nPRINT MAX%(a, b)", optimize: false);
    var select = module.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions).OfType<IrSelect>().First();
    var compare = (IrCmp)select.Condition;

    Assert.That(compare.Pred, Is.EqualTo(IrCmpPred.Sge), "'accumulator >= candidate' is what keeps the earlier one");
    Assert.That(select.IfTrue, Is.SameAs(compare.Lhs), "the true arm has to be the accumulator the compare tested");
  }

  [Test]
  public void Lower_GivenAFloatingPointMax_ThenTheCompareIsOrdered() {
    var module = Lower("DIM a AS SINGLE\nDIM b AS SINGLE\na = 1.5\nb = 2.5\nPRINT MAX(a, b)", optimize: false);
    Assert.That(module.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions).OfType<IrCmp>().Select(c => c.Pred),
      Does.Contain(IrCmpPred.Foge));
  }
}
