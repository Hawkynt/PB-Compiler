using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Regression coverage for O0056 reciprocal-multiply division in the IR middle end.</summary>
[TestFixture]
public sealed class O0056MiddleEndTests {

  [Test]
  public void ReciprocalDivision_GivenSignedDivideByTenForSpeed_WhenRun_ThenPortableMulHighReplacesDivide() {
    var (function, operation, _) = Build(IrBinaryOp.SDiv, 10);

    Assert.That(VerifiedArithmeticLowering.Run(function, optimizeForSpeed: true), Is.EqualTo(1));

    Assert.Multiple(() => {
      Assert.That(operation.Parent, Is.Null);
      Assert.That(function.AllInstructions.OfType<IrBinary>().Any(i => i.Op == IrBinaryOp.SDiv), Is.False);
      Assert.That(function.AllInstructions.OfType<IrCast>().Any(i => i.Op == IrCastOp.SExt && i.Type.Bits == 32), Is.True);
      Assert.That(function.AllInstructions.OfType<IrBinary>().Any(i => i.Op == IrBinaryOp.Mul && i.Type.Bits == 32), Is.True);
      Assert.That(function.AllInstructions.OfType<IrBinary>().Any(i => i.Op == IrBinaryOp.AShr
        && i.Type.Bits == 32 && i.Rhs is IrConstantInt { Value: 16 }), Is.True);
      Assert.That(function.AllInstructions.OfType<IrCast>().Any(i => i.Op == IrCastOp.Trunc && i.Type.Bits == 16), Is.True);
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });

    var magic = function.AllInstructions.OfType<IrBinary>()
      .Single(i => i.Op == IrBinaryOp.Mul && i.Type.Bits == 32)
      .Operands.OfType<IrConstantInt>().Single();
    Assert.That(magic.Value, Is.EqualTo(26215), "signed /10 uses the standard verified Q16 reciprocal");
  }

  [Test]
  public void ReciprocalDivision_GivenAddBackDivisorForSpeed_WhenRun_ThenSignedMagicAddBackIsPreserved() {
    var (function, operation, source) = Build(IrBinaryOp.SRem, 255);

    Assert.That(VerifiedArithmeticLowering.Run(function, optimizeForSpeed: true), Is.EqualTo(1));

    var wideMultiply = function.AllInstructions.OfType<IrBinary>()
      .Single(i => i.Op == IrBinaryOp.Mul && i.Type.Bits == 32);
    var magic = wideMultiply.Operands.OfType<IrConstantInt>().Single();
    Assert.Multiple(() => {
      Assert.That(operation.Parent, Is.Null);
      Assert.That(magic.Value, Is.EqualTo(-32639), "255 exercises the magic-sign-bit/add-dividend path");
      Assert.That(function.AllInstructions.OfType<IrBinary>().Any(i => i.Op == IrBinaryOp.Add
        && i.Type.Bits == 16 && i.Operands.Contains(source)), Is.True);
      Assert.That(function.AllInstructions.OfType<IrBinary>().Any(i => i.Op == IrBinaryOp.SRem), Is.False);
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  [Test]
  public void ReciprocalDivision_GivenNonPowerOfTwoWithoutSpeed_WhenRun_ThenDivideIsKept() {
    var (function, operation, _) = Build(IrBinaryOp.SDiv, 10);

    Assert.That(VerifiedArithmeticLowering.Run(function), Is.Zero);
    Assert.That(operation.Parent, Is.Not.Null);
    Assert.That(function.AllInstructions.OfType<IrRet>().Single().Value, Is.SameAs(operation));
  }

  [Test]
  public void ReciprocalDivision_GivenNegativeNonPowerOfTwoForSpeed_WhenRun_ThenExistingO0056ScopeIsPreserved() {
    var (function, operation, _) = Build(IrBinaryOp.SDiv, -10);

    Assert.That(VerifiedArithmeticLowering.Run(function, optimizeForSpeed: true), Is.Zero);
    Assert.That(operation.Parent, Is.Not.Null,
      "O0056 currently promises positive signed Int16 constants; negative magic division remains separate work");
  }

  [Test]
  public void ReciprocalDivision_GivenLoweredDivideForX86Speed_WhenSelected_ThenUsesAccumulatorImulInsteadOfIdivOrWideHelper() {
    var (function, _, _) = Build(IrBinaryOp.SDiv, 10);
    Assert.That(VerifiedArithmeticLowering.Run(function, optimizeForSpeed: true), Is.EqualTo(1));

    var machine = InstructionSelector.TrySelect(function, out var reason,
      new SelectionTarget(Optimize: true, OptimizeSpeed: true));

    Assert.That(machine, Is.Not.Null, $"O0056 IR declined during x86 selection: {reason}");
    var opcodes = machine!.AllInstructions.Select(instruction => instruction.Opcode).ToList();
    Assert.Multiple(() => {
      Assert.That(opcodes, Does.Contain(MOpcode.Imul));
      Assert.That(opcodes, Does.Not.Contain(MOpcode.Idiv));
      Assert.That(opcodes, Does.Not.Contain(MOpcode.Call),
        "the portable i32 mul-high spelling must not fall through to the 32-bit runtime multiply helper");
    });
  }

  private static (IrFunction Function, IrBinary Operation, IrArgument Source) Build(IrBinaryOp operation, short divisor) {
    var source = new IrArgument(IrType.I16, 0, "x");
    var function = new IrFunction("f", IrType.I16, [source]);
    var entry = function.CreateBlock("entry");
    var arithmetic = entry.Append(new IrBinary(operation, source, new IrConstantInt(IrType.I16, divisor)));
    entry.Append(new IrRet(arithmetic));
    return (function, arithmetic, source);
  }
}
