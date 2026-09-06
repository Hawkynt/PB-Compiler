using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0340-O0347 — strict floating-point facts plus explicitly relaxed SPEED transforms.</summary>
[TestFixture]
public sealed class FpOptimizationTests {

  private static IrFunction Function(IrType result, params IrArgument[] arguments) {
    var function = new IrFunction("f", result, arguments);
    function.CreateBlock("entry");
    return function;
  }

  [Test]
  public void Pipeline_GivenDefaultSettings_ThenFloatingPointRemainsStrict() {
    var a = new IrArgument(IrType.F80, 0, "a");
    var b = new IrArgument(IrType.F80, 1, "b");
    var c = new IrArgument(IrType.F80, 2, "c");
    var function = Function(IrType.F80, a, b, c);
    var block = function.Entry!;
    var product = block.Append(new IrBinary(IrBinaryOp.FMul, a, b));
    var sum = block.Append(new IrBinary(IrBinaryOp.FAdd, product, c));
    block.Append(new IrRet(sum));

    IrPassManager.Standard(includeModulePasses: false).RunToFixpoint(function);

    Assert.Multiple(() => {
      Assert.That(product.FastMathFlags, Is.EqualTo(IrFastMathFlags.None));
      Assert.That(sum.FastMathFlags, Is.EqualTo(IrFastMathFlags.None));
      Assert.That(LlvmEmitter.Emit(function), Does.Not.Contain(" contract "));
    });
  }

  [Test]
  public void Fma_GivenContractPermission_ThenMultiplyAndAddCarryTheLLVMContract() {
    var a = new IrArgument(IrType.F64, 0, "a");
    var b = new IrArgument(IrType.F64, 1, "b");
    var c = new IrArgument(IrType.F64, 2, "c");
    var function = Function(IrType.F64, a, b, c);
    var block = function.Entry!;
    var product = block.Append(new IrBinary(IrBinaryOp.FMul, a, b));
    var sum = block.Append(new IrBinary(IrBinaryOp.FAdd, product, c));
    block.Append(new IrRet(sum));

    FpFastMath.Run(function, IrFastMathFlags.AllowContract);
    var llvm = LlvmEmitter.Emit(function);

    Assert.Multiple(() => {
      Assert.That(product.FastMathFlags, Is.EqualTo(IrFastMathFlags.AllowContract));
      Assert.That(sum.FastMathFlags, Is.EqualTo(IrFastMathFlags.AllowContract));
      Assert.That(llvm, Does.Contain("fmul contract double"));
      Assert.That(llvm, Does.Contain("fadd contract double"));
    });
  }

  [Test]
  public void Reciprocal_GivenArcpPermission_ThenDivisionCarriesOnlyReciprocalPermission() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var d = new IrArgument(IrType.F64, 1, "d");
    var function = Function(IrType.F64, x, d);
    var block = function.Entry!;
    var division = block.Append(new IrBinary(IrBinaryOp.FDiv, x, d));
    block.Append(new IrRet(division));

    FpFastMath.Run(function, IrFastMathFlags.AllowReciprocal);

    Assert.Multiple(() => {
      Assert.That(division.FastMathFlags, Is.EqualTo(IrFastMathFlags.AllowReciprocal));
      Assert.That(LlvmEmitter.Emit(function), Does.Contain("fdiv arcp double"));
    });
  }

  [Test]
  public void Rsqrt_GivenApproximateFunctionAndReciprocalPermissions_ThenSqrtAndDivideExposeBothFreedoms() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var sqrt = new IrFunction("llvm.sqrt.f64", IrType.F64, [new IrArgument(IrType.F64, 0)]);
    var function = Function(IrType.F64, x);
    var block = function.Entry!;
    var root = block.Append(new IrCall(IrType.F64, sqrt, [x]));
    var reciprocal = block.Append(new IrBinary(IrBinaryOp.FDiv, new IrConstantFloat(IrType.F64, 1), root));
    block.Append(new IrRet(reciprocal));

    FpFastMath.Run(function, IrFastMathFlags.ApproxFunc | IrFastMathFlags.AllowReciprocal);
    var llvm = LlvmEmitter.Emit(function);

    Assert.Multiple(() => {
      Assert.That(root.FastMathFlags, Is.EqualTo(IrFastMathFlags.ApproxFunc));
      Assert.That(reciprocal.FastMathFlags, Is.EqualTo(IrFastMathFlags.AllowReciprocal));
      Assert.That(llvm, Does.Contain("call afn double @llvm.sqrt.f64"));
      Assert.That(llvm, Does.Contain("fdiv arcp double"));
    });
  }

  [Test]
  public void Transcendental_GivenApproximateFunctionPermission_ThenMathCallCarriesAfn() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var sin = new IrFunction("llvm.sin.f64", IrType.F64, [new IrArgument(IrType.F64, 0)]);
    var function = Function(IrType.F64, x);
    var block = function.Entry!;
    var call = block.Append(new IrCall(IrType.F64, sin, [x]));
    block.Append(new IrRet(call));

    FpFastMath.Run(function, IrFastMathFlags.ApproxFunc);

    Assert.Multiple(() => {
      Assert.That(call.FastMathFlags, Is.EqualTo(IrFastMathFlags.ApproxFunc));
      Assert.That(LlvmEmitter.Emit(function), Does.Contain("call afn double @llvm.sin.f64"));
    });
  }

  [Test]
  public void Reassociation_GivenPermission_ThenSerialFloatChainBecomesBalancedWithoutReorderingLeaves() {
    var a = new IrArgument(IrType.F80, 0, "a");
    var b = new IrArgument(IrType.F80, 1, "b");
    var c = new IrArgument(IrType.F80, 2, "c");
    var d = new IrArgument(IrType.F80, 3, "d");
    var function = Function(IrType.F80, a, b, c, d);
    var block = function.Entry!;
    var ab = block.Append(new IrBinary(IrBinaryOp.FAdd, a, b));
    var abc = block.Append(new IrBinary(IrBinaryOp.FAdd, ab, c));
    var abcd = block.Append(new IrBinary(IrBinaryOp.FAdd, abc, d));
    var ret = block.Append(new IrRet(abcd));

    Assert.That(FpFastMath.Run(function, IrFastMathFlags.Reassociate), Is.GreaterThan(0));

    var root = ret.Value as IrBinary;
    Assert.That(root, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(root!.Lhs, Is.InstanceOf<IrBinary>());
      Assert.That(root.Rhs, Is.InstanceOf<IrBinary>());
      var left = (IrBinary)root.Lhs;
      var right = (IrBinary)root.Rhs;
      Assert.That(left.Lhs, Is.SameAs(a));
      Assert.That(left.Rhs, Is.SameAs(b));
      Assert.That(right.Lhs, Is.SameAs(c));
      Assert.That(right.Rhs, Is.SameAs(d));
    });
  }

  [Test]
  public void CommonDenominator_GivenReciprocalPermission_ThenTwoDividesBecomeOneDivideAndTwoMultiplies() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var y = new IrArgument(IrType.F64, 1, "y");
    var d = new IrArgument(IrType.F64, 2, "d");
    var function = Function(IrType.F64, x, y, d);
    var block = function.Entry!;
    var xd = block.Append(new IrBinary(IrBinaryOp.FDiv, x, d));
    var yd = block.Append(new IrBinary(IrBinaryOp.FDiv, y, d));
    var sum = block.Append(new IrBinary(IrBinaryOp.FAdd, xd, yd));
    block.Append(new IrRet(sum));

    FpFastMath.Run(function, IrFastMathFlags.AllowReciprocal);

    Assert.Multiple(() => {
      Assert.That(function.AllInstructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.FDiv), Is.EqualTo(1));
      Assert.That(function.AllInstructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.FMul), Is.EqualTo(2));
      Assert.That(function.AllInstructions.OfType<IrBinary>().Where(binary => binary.Op == IrBinaryOp.FMul)
        .All(binary => (binary.FastMathFlags & IrFastMathFlags.AllowReciprocal) == 0), Is.True,
        "arcp belongs on the reciprocal division, not on the generated multiplications");
    });
  }

  [Test]
  public void Classification_GivenUnsignedIntegerConvertedToFloat_ThenNonNegativeComparisonFoldsStrictly() {
    var n = new IrArgument(IrType.U16, 0, "n");
    var function = Function(IrType.I1, n);
    var block = function.Entry!;
    var floating = block.Append(new IrCast(IrCastOp.UIToFP, n, IrType.F64));
    var comparison = block.Append(new IrCmp(IrCmpPred.Foge, floating, new IrConstantFloat(IrType.F64, 0)));
    var ret = block.Append(new IrRet(comparison));

    Assert.That(FpSimplify.Run(function), Is.EqualTo(1));

    Assert.That(ret.Value, Is.TypeOf<IrConstantInt>());
    Assert.That(((IrConstantInt)ret.Value!).Value, Is.EqualTo(1));
  }

  [Test]
  public void Classification_GivenUnconstrainedFloat_ThenSignComparisonDoesNotFold() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var function = Function(IrType.I1, x);
    var block = function.Entry!;
    var comparison = block.Append(new IrCmp(IrCmpPred.Foge, x, new IrConstantFloat(IrType.F64, 0)));
    block.Append(new IrRet(comparison));

    Assert.That(FpSimplify.Run(function), Is.Zero);
    Assert.That(comparison.Parent, Is.SameAs(block));
  }

  [Test]
  public void MixedPrecision_GivenFiniteF32OperandsMultipliedInF64ThenTruncated_ThenMultiplyNarrowsExactly() {
    var a = new IrArgument(IrType.I16, 0, "a");
    var b = new IrArgument(IrType.I16, 1, "b");
    var function = Function(IrType.F32, a, b);
    var block = function.Entry!;
    var af = block.Append(new IrCast(IrCastOp.SIToFP, a, IrType.F32));
    var bf = block.Append(new IrCast(IrCastOp.SIToFP, b, IrType.F32));
    var aw = block.Append(new IrCast(IrCastOp.FPExt, af, IrType.F64));
    var bw = block.Append(new IrCast(IrCastOp.FPExt, bf, IrType.F64));
    var wide = block.Append(new IrBinary(IrBinaryOp.FMul, aw, bw));
    var narrow = block.Append(new IrCast(IrCastOp.FPTrunc, wide, IrType.F32));
    var ret = block.Append(new IrRet(narrow));

    Assert.That(FpSimplify.Run(function), Is.EqualTo(1));

    Assert.That(ret.Value, Is.TypeOf<IrBinary>());
    var product = (IrBinary)ret.Value!;
    Assert.Multiple(() => {
      Assert.That(product.Op, Is.EqualTo(IrBinaryOp.FMul));
      Assert.That(product.Type, Is.EqualTo(IrType.F32));
      Assert.That(product.Lhs, Is.SameAs(af));
      Assert.That(product.Rhs, Is.SameAs(bf));
      Assert.That(wide.Parent, Is.Null);
      Assert.That(narrow.Parent, Is.Null);
    });
  }

  [Test]
  public void Clone_GivenFastMathFlags_ThenCloneRetainsTheNumericalContract() {
    var a = new IrArgument(IrType.F64, 0, "a");
    var b = new IrArgument(IrType.F64, 1, "b");
    var source = Function(IrType.F64, a, b);
    var sourceBlock = source.Entry!;
    var sum = sourceBlock.Append(new IrBinary(IrBinaryOp.FAdd, a, b) {
      FastMathFlags = IrFastMathFlags.AllowContract | IrFastMathFlags.NoNaNs,
    });
    sourceBlock.Append(new IrRet(sum));

    var destination = new IrFunction("g", IrType.F64,
      [new IrArgument(IrType.F64, 0), new IrArgument(IrType.F64, 1)]);
    var seed = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance) {
      [a] = destination.Parameters[0],
      [b] = destination.Parameters[1],
    };
    var blocks = IrCloner.Clone(destination, source.Blocks, seed, "clone.");
    var cloned = blocks[sourceBlock].Instructions.OfType<IrBinary>().Single();

    Assert.That(cloned.FastMathFlags, Is.EqualTo(sum.FastMathFlags));
  }

  [Test]
  public void DomainSpecialization_GivenUnsignedByteDomainAndTableCapability_ThenSinBecomesTypedLookup() {
    var module = new IrModule("test");
    var n = new IrArgument(IrType.U8, 0, "n");
    var function = module.AddFunction(new IrFunction("f", IrType.F64, [n]));
    var sin = module.AddFunction(new IrFunction("llvm.sin.f64", IrType.F64,
      [new IrArgument(IrType.F64, 0)]));
    var block = function.CreateBlock("entry");
    var x = block.Append(new IrCast(IrCastOp.UIToFP, n, IrType.F64));
    var call = block.Append(new IrCall(IrType.F64, sin, [x]));
    block.Append(new IrRet(call));

    Assert.That(FpDomainSpecialization.Run(module, allowLookupTables: true), Is.EqualTo(1));

    var table = module.Globals.Single(global => global.Name.StartsWith(".fplut.", StringComparison.Ordinal));
    Assert.Multiple(() => {
      Assert.That(table.ValueType, Is.EqualTo(IrType.F64));
      Assert.That(table.Count, Is.EqualTo(256));
      Assert.That(table.FloatingValues, Has.Length.EqualTo(256));
      Assert.That(function.AllInstructions.OfType<IrCall>(), Is.Empty);
      Assert.That(function.AllInstructions.OfType<IrLoad>().Single().Pointer, Is.InstanceOf<IrGep>());
    });
  }

  [Test]
  public void DomainSpecialization_GivenSameDiscreteDomainWithoutTableCapability_ThenGeneralCallRemains() {
    var module = new IrModule("test");
    var n = new IrArgument(IrType.U8, 0, "n");
    var function = module.AddFunction(new IrFunction("f", IrType.F64, [n]));
    var sin = module.AddFunction(new IrFunction("llvm.sin.f64", IrType.F64,
      [new IrArgument(IrType.F64, 0)]));
    var block = function.CreateBlock("entry");
    var x = block.Append(new IrCast(IrCastOp.UIToFP, n, IrType.F64));
    var call = block.Append(new IrCall(IrType.F64, sin, [x]));
    block.Append(new IrRet(call));

    Assert.That(FpDomainSpecialization.Run(module, allowLookupTables: false), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(module.Globals, Is.Empty);
      Assert.That(call.Parent, Is.SameAs(block));
    });
  }

  [Test]
  public void DomainSpecialization_GivenNarrowContinuousRangeWithoutTables_ThenSinUsesPolynomialKernel() {
    var module = new IrModule("test");
    var n = new IrArgument(IrType.U8, 0, "n");
    var function = module.AddFunction(new IrFunction("f", IrType.F64, [n]));
    var sin = module.AddFunction(new IrFunction("llvm.sin.f64", IrType.F64,
      [new IrArgument(IrType.F64, 0)]));
    var block = function.CreateBlock("entry");
    var x = block.Append(new IrCast(IrCastOp.UIToFP, n, IrType.F64));
    var scaled = block.Append(new IrBinary(IrBinaryOp.FDiv, x, new IrConstantFloat(IrType.F64, 1024.0)));
    var call = block.Append(new IrCall(IrType.F64, sin, [scaled]));
    var ret = block.Append(new IrRet(call));

    Assert.That(FpDomainSpecialization.Run(module, allowLookupTables: false), Is.EqualTo(1));

    Assert.Multiple(() => {
      Assert.That(call.Parent, Is.Null);
      Assert.That(ret.Value, Is.InstanceOf<IrBinary>());
      Assert.That(function.AllInstructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.FMul), Is.GreaterThan(1));
      Assert.That(function.AllInstructions.OfType<IrCall>(), Is.Empty);
    });
  }

  [Test]
  public void LlvmEmitter_GivenTypedFloatingTable_ThenInitializerRemainsTypedAndTargetIndependent() {
    var module = new IrModule("test");
    module.AddGlobal(new IrGlobalVariable(".fplut.test", IrType.F64) {
      FloatingValues = [0.0, 1.0],
      Count = 2,
      IsZeroInitialized = false,
    });

    var llvm = LlvmEmitter.Emit(module);

    Assert.That(llvm, Does.Contain("@.fplut.test = private constant [2 x double] [double 0x0000000000000000, double 0x3FF0000000000000]"));
  }
}
