using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The multi-instruction selection patterns (<c>InstructionSelector.Idioms</c>): shapes the optimizer
/// has already reduced to arithmetic, for which this target has a shorter spelling. Each is built as
/// the IR the lowering really produces, so the test says what the pattern is as well as what it
/// becomes; each also has a negative twin, because every one of them is guarded by "nobody else can
/// see the intermediates".
/// </summary>
[TestFixture]
public sealed class BackendIdiomTests {

  private static readonly SelectionTarget _optimized = new(Optimize: true);

  private static MFunction Select(IrFunction fn, SelectionTarget? target = null) {
    var machine = InstructionSelector.TrySelect(fn, out var reason, target ?? _optimized);
    Assert.That(machine, Is.Not.Null, $"declined: {reason}");
    return machine!;
  }

  private static List<MOpcode> Opcodes(MFunction fn) => [.. fn.AllInstructions.Select(i => i.Opcode)];

  /// <summary>A one-block function over one INTEGER argument, ending in a return of <paramref name="build"/>.</summary>
  private static IrFunction OneArg(Func<IrBasicBlock, IrArgument, IrValue> build) {
    var fn = new IrFunction("F", IrType.I16, [new IrArgument(IrType.I16, 0)]);
    var entry = fn.CreateBlock("entry");
    entry.Append(new IrRet(build(entry, (IrArgument)fn.Parameters[0])));
    return fn;
  }

  [Test]
  public void Select_GivenTheBranchlessAbsShape_WhenOptimized_ThenCwdXorSub() {
    // (x XOR (x >>a 15)) - (x >>a 15) is what the optimizer leaves ABS() as
    var fn = OneArg((entry, x) => {
      var mask = entry.Append(new IrBinary(IrBinaryOp.AShr, x, new IrConstantInt(IrType.I16, 15)));
      var flipped = entry.Append(new IrBinary(IrBinaryOp.Xor, x, mask));
      return entry.Append(new IrBinary(IrBinaryOp.Sub, flipped, mask));
    });

    var opcodes = Opcodes(Select(fn));
    Assert.That(opcodes, Does.Contain(MOpcode.Cwd), "CWD is the sign mask, in one instruction");
    Assert.That(opcodes, Does.Not.Contain(MOpcode.Sar), "so the fifteen-step arithmetic shift is gone");
    Assert.That(opcodes.IndexOf(MOpcode.Xor), Is.GreaterThan(opcodes.IndexOf(MOpcode.Cwd)));
    Assert.That(opcodes.IndexOf(MOpcode.Sub), Is.GreaterThan(opcodes.IndexOf(MOpcode.Xor)));
  }

  [Test]
  public void Select_GivenTheAbsShapeWithASecondReaderOfTheMask_WhenOptimized_ThenKeepsTheShift() {
    // the mask is wanted for itself, so the three instructions are not equivalent to the four-step
    // accumulator sequence that discards it
    var fn = new IrFunction("F", IrType.I16, [new IrArgument(IrType.I16, 0)]);
    var entry = fn.CreateBlock("entry");
    var x = fn.Parameters[0];
    var mask = entry.Append(new IrBinary(IrBinaryOp.AShr, x, new IrConstantInt(IrType.I16, 15)));
    var flipped = entry.Append(new IrBinary(IrBinaryOp.Xor, x, mask));
    var abs = entry.Append(new IrBinary(IrBinaryOp.Sub, flipped, mask));
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.Or, abs, mask))));

    var opcodes = Opcodes(Select(fn));
    Assert.That(opcodes, Does.Contain(MOpcode.Sar), "the shift stays because the mask has a reader of its own");
    Assert.That(opcodes, Does.Not.Contain(MOpcode.Cwd));
  }

  [Test]
  public void Select_GivenTheBranchlessAbsShape_WhenNotOptimized_ThenTheShiftChainStands() {
    var fn = OneArg((entry, x) => {
      var mask = entry.Append(new IrBinary(IrBinaryOp.AShr, x, new IrConstantInt(IrType.I16, 15)));
      var flipped = entry.Append(new IrBinary(IrBinaryOp.Xor, x, mask));
      return entry.Append(new IrBinary(IrBinaryOp.Sub, flipped, mask));
    });

    var opcodes = Opcodes(Select(fn, SelectionTarget.Baseline));
    Assert.That(opcodes, Does.Contain(MOpcode.Sar), "with the optimizer off, selection writes what it would have written");
    Assert.That(opcodes, Does.Not.Contain(MOpcode.Cwd));
  }

  [Test]
  public void Select_GivenDivAndModWithHiddenErrorFlow_ThenKeepsBothFaultingOperations() {
    var fn = new IrFunction("F", IrType.I16,
      [new IrArgument(IrType.I16, 0), new IrArgument(IrType.I16, 1)]) {
      HasErrorHandler = true,
    };
    var entry = fn.CreateBlock("entry");
    var quotient = entry.Append(new IrBinary(IrBinaryOp.SDiv, fn.Parameters[0], fn.Parameters[1]));
    var remainder = entry.Append(new IrBinary(IrBinaryOp.SRem, fn.Parameters[0], fn.Parameters[1]));
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.Add, quotient, remainder))));

    Assert.That(Opcodes(Select(fn)).Count(opcode => opcode == MOpcode.Idiv), Is.EqualTo(2),
      "an error handler may resume between the source operations, so neither fault can disappear");
  }

  [Test]
  public void Select_GivenTheSgnShape_WhenOptimized_ThenCwdNegAdc() {
    // (x > 0) - (x < 0), the form the lowering gives SGN
    var fn = OneArg((entry, x) => {
      var zero = new IrConstantInt(IrType.I16, 0);
      var positive = entry.Append(new IrCast(IrCastOp.ZExt, entry.Append(new IrCmp(IrCmpPred.Sgt, x, zero)), IrType.I16));
      var negative = entry.Append(new IrCast(IrCastOp.ZExt, entry.Append(new IrCmp(IrCmpPred.Slt, x, zero)), IrType.I16));
      return entry.Append(new IrBinary(IrBinaryOp.Sub, positive, negative));
    });

    var machine = Select(fn);
    var opcodes = Opcodes(machine);
    Assert.That(opcodes, Does.Contain(MOpcode.Cwd));
    Assert.That(opcodes, Does.Contain(MOpcode.Neg), "NEG is the carry test: it clears CF for zero alone");
    Assert.That(opcodes, Does.Contain(MOpcode.Adc));
    Assert.That(machine.Blocks, Has.Count.EqualTo(1), "branchless: neither comparison is materialized through a diamond");
  }

  [Test]
  public void Select_GivenTheSgnShapeOverDifferentValues_WhenOptimized_ThenNotFolded() {
    // (a > 0) - (b < 0) is not a sign of anything
    var fn = new IrFunction("F", IrType.I16, [new IrArgument(IrType.I16, 0), new IrArgument(IrType.I16, 1)]);
    var entry = fn.CreateBlock("entry");
    var zero = new IrConstantInt(IrType.I16, 0);
    var positive = entry.Append(new IrCast(IrCastOp.ZExt,
      entry.Append(new IrCmp(IrCmpPred.Sgt, fn.Parameters[0], zero)), IrType.I16));
    var negative = entry.Append(new IrCast(IrCastOp.ZExt,
      entry.Append(new IrCmp(IrCmpPred.Slt, fn.Parameters[1], zero)), IrType.I16));
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.Sub, positive, negative))));

    Assert.That(Opcodes(Select(fn)), Does.Not.Contain(MOpcode.Cwd));
  }

  /// <summary>A maximum written the four ways BASIC writes one, as the lowering leaves each.</summary>
  private static IrFunction MinMax(IrCmpPred pred, bool swapArms) {
    var fn = new IrFunction("F", IrType.I16, [new IrArgument(IrType.I16, 0), new IrArgument(IrType.I16, 1)]);
    var entry = fn.CreateBlock("entry");
    var (a, b) = (fn.Parameters[0], fn.Parameters[1]);
    var test = entry.Append(new IrCmp(pred, a, b));
    var (ifTrue, ifFalse) = swapArms ? (b, a) : (a, b);
    entry.Append(new IrRet(entry.Append(new IrSelect(test, ifTrue, ifFalse))));
    return fn;
  }

  private static string Shape(MFunction fn) => string.Join(" | ",
    fn.Blocks.SelectMany(b => b.Instructions).Select(i =>
      $"{i.Opcode}{(i.Condition is { } c ? ":" + c : "")} {string.Join(",", i.Operands)}"));

  [Test]
  public void Select_GivenTheFourSpellingsOfAMaximum_WhenOptimized_ThenAllSelectTheSameSequence() {
    // IF a > b THEN m = a ELSE m = b   /   MAX%(a, b)   -- the compare's own operand order
    // IF b < a THEN m = a ELSE m = b   -- with the arms the other way round, which is the negation
    var strict = Shape(Select(MinMax(IrCmpPred.Sgt, swapArms: false)));
    var orEqual = Shape(Select(MinMax(IrCmpPred.Sge, swapArms: false)));
    var reversed = Shape(Select(MinMax(IrCmpPred.Sle, swapArms: true)));
    var reversedStrict = Shape(Select(MinMax(IrCmpPred.Slt, swapArms: true)));

    Assert.Multiple(() => {
      Assert.That(strict, Is.EqualTo(orEqual), "> and >= differ only where the two are equal, and there both arms agree");
      Assert.That(reversed, Is.EqualTo(orEqual), "reversed arms are the same choice through the negated predicate");
      Assert.That(reversedStrict, Is.EqualTo(orEqual));
    });
  }

  [Test]
  public void Select_GivenAComparisonSomethingElseAlsoReads_WhenOptimized_ThenItKeepsItsOwnPredicate() {
    // CSE can hand one icmp to a select and to another reader; relabelling it would change that
    // reader's answer at equality
    var fn = new IrFunction("F", IrType.I16, [new IrArgument(IrType.I16, 0), new IrArgument(IrType.I16, 1)]);
    var entry = fn.CreateBlock("entry");
    var (a, b) = (fn.Parameters[0], fn.Parameters[1]);
    var test = entry.Append(new IrCmp(IrCmpPred.Sgt, a, b));
    var maximum = entry.Append(new IrSelect(test, a, b));
    var widened = entry.Append(new IrCast(IrCastOp.SExt, test, IrType.I16));
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.Add, maximum, widened))));

    var shared = Select(fn).AllInstructions.First(i => i.Opcode == MOpcode.Jcc);
    Assert.That(shared.Condition, Is.EqualTo(Condition.Greater), "still JG, not JGE");
  }

  [Test]
  public void Select_GivenAFloatTimesALiteral_WhenOptimized_ThenTheConstantPoolCellIsTheOperand() {
    var fn = new IrFunction("F", IrType.F32, [new IrArgument(IrType.F32, 0)]);
    var entry = fn.CreateBlock("entry");
    entry.Append(new IrRet(entry.Append(
      new IrBinary(IrBinaryOp.FMul, fn.Parameters[0], new IrConstantFloat(IrType.F64, 1.5)))));

    var machine = Select(fn);
    Assert.That(Opcodes(machine), Does.Contain(MOpcode.Fmul), "FMUL reads the pool cell in place");
    Assert.That(Opcodes(machine), Does.Not.Contain(MOpcode.Fmulp), "so the literal is never pushed");
  }

  [Test]
  public void Select_GivenAFloatPlusAnInteger_WhenOptimized_ThenTheIntegerCellIsTheOperand() {
    var fn = new IrFunction("F", IrType.F32, [new IrArgument(IrType.F32, 0), new IrArgument(IrType.I16, 1)]);
    var entry = fn.CreateBlock("entry");
    var widened = entry.Append(new IrCast(IrCastOp.SIToFP, fn.Parameters[1], IrType.F32));
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.FAdd, fn.Parameters[0], widened))));

    var opcodes = Opcodes(Select(fn));
    Assert.That(opcodes, Does.Contain(MOpcode.Fiadd), "the x87 converts as it reads");
    Assert.That(opcodes, Does.Not.Contain(MOpcode.Fild), "so neither the conversion nor its 80-bit temporary is emitted");
  }

  [Test]
  public void Select_GivenAWidenedIntegerSomethingElseReads_WhenOptimized_ThenTheConversionIsEmitted() {
    // one consumer that wants the CONVERTED value wants the whole conversion
    var fn = new IrFunction("F", IrType.F32, [new IrArgument(IrType.F32, 0), new IrArgument(IrType.I16, 1)]);
    var entry = fn.CreateBlock("entry");
    var widened = entry.Append(new IrCast(IrCastOp.SIToFP, fn.Parameters[1], IrType.F32));
    var sum = entry.Append(new IrBinary(IrBinaryOp.FAdd, fn.Parameters[0], widened));
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.FMul, widened, sum))));

    var opcodes = Opcodes(Select(fn));
    Assert.That(opcodes, Does.Contain(MOpcode.Fild));
    Assert.That(opcodes, Does.Not.Contain(MOpcode.Fiadd));
  }
}
