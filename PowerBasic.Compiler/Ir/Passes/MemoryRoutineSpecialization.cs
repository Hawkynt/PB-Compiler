using PowerBasic.Compiler.CodeGen;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0339 — specializes small constant-size LLVM memory intrinsics into straight-line scalar accesses.
/// Profitability is target-driven: each CPU tier supplies its scalar width and its copy/fill store
/// budgets through <see cref="TargetCost"/>. The targetless overload deliberately preserves the
/// conservative 8086 baseline for standalone IR users and tests.
/// </summary>
public static class MemoryRoutineSpecialization {

  private const int _MAX_DYNAMIC_MEMSET_STORES = 4;
  private const string _MEMCPY = "llvm.memcpy.p0.p0.i32";
  private const string _MEMSET = "llvm.memset.p0.i32";

  private static readonly TargetCost _baseline = new(CpuTier.I8086, CostObjective.Balanced);

  private readonly record struct Access(int Offset, IrType Type);

  /// <summary>
  /// Expands qualifying calls using the conservative 8086 policy. Production target-aware callers
  /// should use <see cref="Run(IrFunction,TargetCost)"/>.
  /// </summary>
  public static int Run(IrFunction fn) => Run(fn, _baseline);

  /// <summary>Expands qualifying calls in <paramref name="fn"/> for <paramref name="cost"/>.</summary>
  public static int Run(IrFunction fn, TargetCost cost) {
    ArgumentNullException.ThrowIfNull(fn);
    ArgumentNullException.ThrowIfNull(cost);
    if (fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;

    var specialized = 0;
    foreach (var call in fn.AllInstructions.OfType<IrCall>().ToList())
      if (TryMemcpy(call, cost) || TryMemset(call, cost))
        ++specialized;
    return specialized;
  }

  private static bool TryMemcpy(IrCall call, TargetCost cost) {
    if (call.Callee is not IrFunction { Name: _MEMCPY } || call.ArgCount != 4)
      return false;
    var args = call.Args.ToArray();
    if (!NonVolatile(args[3]) || !ConstantSize(args[2], out var size)
        || args[0] is IrFarPtr || args[1] is IrFarPtr
        || !TryAccessPlan(size, cost.MemoryScalarBits, cost.MaxStoresPerMemcpy, out var accesses))
      return false;

    var block = call.Parent;
    if (block is null)
      return false;
    // Keep each load immediately paired with its store. memcpy already promises non-overlap, and this
    // ordering keeps only one scalar live at a time instead of turning a tiny copy into register pressure.
    foreach (var access in accesses) {
      var source = ByteAddress(block, call, args[1], access.Offset);
      var target = ByteAddress(block, call, args[0], access.Offset);
      var value = block.InsertBefore(new IrLoad(access.Type, source), call);
      block.InsertBefore(new IrStore(value, target), call);
    }
    call.EraseFromParent();
    return true;
  }

  private static bool TryMemset(IrCall call, TargetCost cost) {
    if (call.Callee is not IrFunction { Name: _MEMSET } || call.ArgCount != 4)
      return false;
    var args = call.Args.ToArray();
    if (!NonVolatile(args[3]) || !ConstantSize(args[2], out var size) || args[0] is IrFarPtr
        || !args[1].Type.IsInteger || args[1].Type.Bits != 8)
      return false;

    // A constant byte can be splatted into the target's widest scalar for free at compile time. A
    // dynamic byte cannot: manufacturing value * 0x01010101 adds arithmetic and pressure, especially
    // badly on the early targets, so that case deliberately remains byte-wise with the old four-store cap.
    var constantFill = args[1] is IrConstantInt;
    var width = constantFill ? cost.MemoryScalarBits : 8;
    var budget = constantFill ? cost.MaxStoresPerMemset : _MAX_DYNAMIC_MEMSET_STORES;
    if (!TryAccessPlan(size, width, budget, out var accesses))
      return false;

    var block = call.Parent;
    if (block is null)
      return false;
    foreach (var access in accesses)
      block.InsertBefore(new IrStore(FillValue(args[1], access.Type),
        ByteAddress(block, call, args[0], access.Offset)), call);
    call.EraseFromParent();
    return true;
  }

  /// <summary>
  /// Builds the widest-first plan required by the target's scalar width. The budget counts STORES,
  /// matching LLVM TargetLowering's MaxStoresPerMemcpy/MaxStoresPerMemset contract: consume as many
  /// largest legal accesses as possible, then use smaller accesses for the tail.
  /// </summary>
  private static bool TryAccessPlan(int size, int maxScalarBits, int maxAccesses, out List<Access> accesses) {
    accesses = [];
    for (var offset = 0; offset < size;) {
      if (accesses.Count >= maxAccesses) {
        accesses = [];
        return false;
      }

      var remaining = size - offset;
      var type = maxScalarBits >= 32 && remaining >= 4 ? IrType.I32
        : maxScalarBits >= 16 && remaining >= 2 ? IrType.I16
        : IrType.I8;
      accesses.Add(new Access(offset, type));
      offset += type.Bits / 8;
    }
    return true;
  }

  private static IrValue FillValue(IrValue fill, IrType type) {
    if (type.Bits == 8)
      return fill;

    var byteValue = ((IrConstantInt)fill).ZeroExtended & 0xff;
    var pattern = byteValue;
    for (var shift = 8; shift < type.Bits; shift += 8)
      pattern |= byteValue << shift;
    return new IrConstantInt(type, unchecked((long)pattern));
  }

  private static IrValue ByteAddress(IrBasicBlock block, IrCall anchor, IrValue pointer, int offset)
    => offset == 0
      ? pointer
      : block.InsertBefore(new IrGep(pointer, new IrConstantInt(IrType.I32, offset)), anchor);

  private static bool NonVolatile(IrValue value) => value is IrConstantInt { IsZero: true };

  private static bool ConstantSize(IrValue value, out int size) {
    size = 0;
    if (value is not IrConstantInt { Value: >= 0 and <= int.MaxValue } constant)
      return false;
    size = (int)constant.Value;
    return true;
  }
}
