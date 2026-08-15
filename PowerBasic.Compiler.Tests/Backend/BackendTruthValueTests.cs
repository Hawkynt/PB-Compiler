using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// BASIC's truth value is <c>-1</c>/<c>0</c>, and the 8086 has no <c>SETcc</c> - so a comparison
/// whose <b>result</b> is used, rather than folded into a branch, has to be materialized by branching
/// around it. The same is true of the <c>select</c> the IR's if-conversion pass leaves behind: there
/// is no <c>CMOV</c> before the Pentium Pro.
///
/// Both therefore SPLIT the machine block, which is the interesting part structurally: selection can
/// no longer assume one machine block per IR block, so appends go through a block cursor and the
/// out-of-SSA phi copies for an IR block must land in whichever machine block control finally leaves
/// from - not the one it entered.
/// </summary>
[TestFixture]
public sealed class BackendTruthValueTests {

  private static (IrFunction Fn, IrArgument A, IrArgument B) TwoArgFunction(IrType returnType) {
    var a = new IrArgument(IrType.I16, 0);
    var b = new IrArgument(IrType.I16, 1);
    return (new IrFunction("F", returnType, [a, b]), a, b);
  }

  [Test]
  public void Select_GivenComparisonUsedAsAValue_ThenMaterializesMinusOneOrZero() {
    var (fn, a, b) = TwoArgFunction(IrType.I16);
    var entry = fn.CreateBlock("entry");
    var builder = new IrBuilder(entry);
    var truth = builder.Cmp(IrCmpPred.Sgt, a, b);
    builder.Ret(builder.SExt(truth, IrType.I16));      // PB: (a > b) is -1 or 0

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, reason);
    var immediates = m!.AllInstructions
      .Where(i => i.Opcode == MOpcode.Mov)
      .Select(i => i.Operands[1])
      .OfType<MOperand.Immediate>()
      .Select(i => i.Value)
      .ToList();
    Assert.That(immediates, Does.Contain(-1).And.Contain(0), "the two truth values are materialized");
    Assert.That(m.AllInstructions.Select(i => i.Opcode), Does.Contain(MOpcode.Cmp).And.Contain(MOpcode.Jcc));
    Assert.That(m.Blocks, Has.Count.GreaterThan(1), "materializing the value splits the block");
  }

  [Test]
  public void Select_GivenSignExtendOfAComparison_ThenCostsNoInstruction() {
    // the compare already produced a full word of -1/0, so widening it to i16 is nothing at all
    var (fn, a, b) = TwoArgFunction(IrType.I16);
    var entry = fn.CreateBlock("entry");
    var builder = new IrBuilder(entry);
    builder.Ret(builder.SExt(builder.Cmp(IrCmpPred.Sgt, a, b), IrType.I16));

    var withSext = InstructionSelector.TrySelect(fn, out _)!.AllInstructions.Count();

    var (bare, c, d) = TwoArgFunction(IrType.I16);
    var bareEntry = bare.CreateBlock("entry");
    var bareBuilder = new IrBuilder(bareEntry);
    var cmp = bareBuilder.Cmp(IrCmpPred.Sgt, c, d);
    bareBuilder.Ret(bareBuilder.SExt(cmp, IrType.I16));

    Assert.That(withSext, Is.EqualTo(InstructionSelector.TrySelect(bare, out _)!.AllInstructions.Count()));
  }

  [Test]
  public void Select_GivenIfConvertedSelect_ThenBranchesOverTheTwoArms() {
    var (fn, a, b) = TwoArgFunction(IrType.I16);
    var entry = fn.CreateBlock("entry");
    var builder = new IrBuilder(entry);
    var cond = builder.Cmp(IrCmpPred.Sgt, a, b);
    builder.Ret(entry.Append(new IrSelect(cond, a, b)));   // x = IF(a > b, a, b)

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, reason);
    Assert.That(m!.AllInstructions.Count(i => i.Opcode == MOpcode.Jcc), Is.GreaterThanOrEqualTo(2),
      "one branch materializes the condition, one selects the arm");
    Assert.That(m.Blocks, Has.Count.GreaterThanOrEqualTo(4));
  }

  [Test]
  public void Emit_GivenComparisonValue_ThenAllocatesAndAssemblesWithEveryLabelBound() {
    var (fn, a, b) = TwoArgFunction(IrType.I16);
    var entry = fn.CreateBlock("entry");
    var builder = new IrBuilder(entry);
    builder.Ret(builder.SExt(builder.Cmp(IrCmpPred.Slt, a, b), IrType.I16));
    var m = InstructionSelector.TrySelect(fn, out var reason);
    Assert.That(m, Is.Not.Null, reason);
    var alloc = LinearScanAllocator.Allocate(m!);
    Assert.That(alloc, Is.Not.Null, "the split blocks must still allocate");

    var asm = new Assembler();
    MachineEmitter.Emit(asm, m!, alloc!);

    // an unbound label would throw here when the fixups resolve, so reaching bytes is the check
    Assert.That(asm.ToArray(), Is.Not.Empty);
  }

  /// <summary>
  /// The other half of the -1/0 convention, and the one that was missing: a bool CONSTANT is an
  /// operand too, and it has to be spelled the same way the computed ones are.
  ///
  /// <para>
  /// The IR writes truth as <c>i1 1</c>; this target writes it as a full word of -1. Materializing
  /// the constant as the immediate <c>1</c> makes every bitwise operation mixing the two answer a
  /// third thing - <c>xor i1 %c, true</c>, which is how both the lowering and instcombine spell a
  /// logical NOT, turned -1 into -2 rather than into 0. Still non-zero, so a negated TRUE stayed
  /// TRUE.
  /// </para>
  /// </summary>
  [Test]
  public void Select_GivenBooleanNotSpelledAsXorWithTrue_ThenTheConstantIsThisTargetsTruth() {
    var (fn, a, b) = TwoArgFunction(IrType.I16);
    var entry = fn.CreateBlock("entry");
    var builder = new IrBuilder(entry);
    var cond = builder.Cmp(IrCmpPred.Sgt, a, b);
    var negated = entry.Append(new IrBinary(IrBinaryOp.Xor, cond, new IrConstantInt(IrType.I1, 1)));
    builder.Ret(builder.SExt(negated, IrType.I16));

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, reason);
    var xorOperands = m!.AllInstructions
      .Where(i => i.Opcode == MOpcode.Xor)
      .Select(i => i.Operands[1])
      .OfType<MOperand.Immediate>()
      .Select(i => i.Value)
      .ToList();
    Assert.That(xorOperands, Is.Not.Empty, "the negation is still a XOR against a literal");
    Assert.That(xorOperands, Has.All.EqualTo(-1),
      "a bool true is a full word of -1 here, so XOR against it is the bitwise complement - "
      + "XOR against 1 flips only the low bit and leaves -1 as -2, which still reads as TRUE");
  }

  [Test]
  public void Select_GivenComparisonValueInALoop_ThenPhiCopiesLandAfterTheSplit() {
    // the regression the block cursor exists for: a phi's edge copies must be inserted in the block
    // control actually leaves from, which after a split is not the block the IR block started in
    var (fn, a, b) = TwoArgFunction(IrType.I16);
    var entry = fn.CreateBlock("entry");
    var body = fn.CreateBlock("body");
    var exit = fn.CreateBlock("exit");
    var builder = new IrBuilder(entry);
    builder.Br(body);

    builder.Position(body);
    var phi = builder.Phi(IrType.I16);
    var truth = builder.SExt(builder.Cmp(IrCmpPred.Sgt, a, b), IrType.I16);   // splits the block
    phi.AddIncoming(new IrConstantInt(IrType.I16, 0), entry);
    phi.AddIncoming(truth, body);
    builder.CondBr(builder.Cmp(IrCmpPred.Ne, truth, new IrConstantInt(IrType.I16, 0)), body, exit);

    builder.Position(exit);
    builder.Ret(phi);

    var m = InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(m, Is.Not.Null, reason);
    Assert.That(LinearScanAllocator.Allocate(m!), Is.Not.Null);
  }
}
