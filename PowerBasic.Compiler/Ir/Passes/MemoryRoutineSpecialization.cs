namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0339 — specializes small constant-size LLVM memory intrinsics into straight-line scalar accesses.
/// The pass is currently wired only from the x86-16 late backend, so a word is the widest access every
/// supported target can execute, including at an unaligned address. Larger transfers stay as intrinsics
/// so the target/runtime keeps the existing REP/MOVSD policy for medium and large copies.
/// </summary>
public static class MemoryRoutineSpecialization {

  private const int _MAX_MEMCPY_ACCESSES = 3;
  private const int _MAX_MEMSET_ACCESSES = 4;
  private const string _MEMCPY = "llvm.memcpy.p0.p0.i32";
  private const string _MEMSET = "llvm.memset.p0.i32";

  private readonly record struct Access(int Offset, IrType Type);

  /// <summary>Expands qualifying calls in <paramref name="fn"/>; returns the number specialized.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    if (fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;

    var specialized = 0;
    foreach (var call in fn.AllInstructions.OfType<IrCall>().ToList())
      if (TryMemcpy(call) || TryMemset(call))
        ++specialized;
    return specialized;
  }

  private static bool TryMemcpy(IrCall call) {
    if (call.Callee is not IrFunction { Name: _MEMCPY } || call.ArgCount != 4)
      return false;
    var args = call.Args.ToArray();
    if (!NonVolatile(args[3]) || !ConstantSize(args[2], out var size)
        || args[0] is IrFarPtr || args[1] is IrFarPtr
        || !TryAccessPlan(size, allowWords: true, _MAX_MEMCPY_ACCESSES, out var accesses))
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

  private static bool TryMemset(IrCall call) {
    if (call.Callee is not IrFunction { Name: _MEMSET } || call.ArgCount != 4)
      return false;
    var args = call.Args.ToArray();
    if (!NonVolatile(args[3]) || !ConstantSize(args[2], out var size) || args[0] is IrFarPtr
        || !args[1].Type.IsInteger || args[1].Type.Bits != 8)
      return false;

    // A constant byte can be splatted into a word for free at compile time. A dynamic byte cannot:
    // materializing value * 0x0101 would add arithmetic and pressure, especially disastrous on an 8086.
    var constantFill = args[1] is IrConstantInt;
    if (!TryAccessPlan(size, allowWords: constantFill, _MAX_MEMSET_ACCESSES, out var accesses))
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
  /// Builds the widest-first access plan, following the same profitability shape LLVM exposes through
  /// MaxStoresPerMemcpy/MaxStoresPerMemset: count scalar memory operations, not raw bytes. Three word
  /// copies cover six bytes while deliberately leaving the important seven/eight-byte UDT cases to the
  /// existing target-aware REP/MOVSD path; memset gets one extra access because it has no corresponding load.
  /// </summary>
  private static bool TryAccessPlan(int size, bool allowWords, int maxAccesses, out List<Access> accesses) {
    accesses = [];
    for (var offset = 0; offset < size;) {
      if (accesses.Count >= maxAccesses) {
        accesses = [];
        return false;
      }
      var type = allowWords && size - offset >= 2 ? IrType.I16 : IrType.I8;
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
