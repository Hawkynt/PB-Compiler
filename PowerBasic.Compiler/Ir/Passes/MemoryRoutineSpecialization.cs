namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0339 — specializes tiny constant-size LLVM memory intrinsics into straight-line byte accesses.
/// Medium and large transfers stay as intrinsics so the target-specific selector/runtime keeps its
/// existing widened REP and alignment policy.
/// </summary>
public static class MemoryRoutineSpecialization {

  private const int _MAX_INLINE_BYTES = 8;
  private const string _MEMCPY = "llvm.memcpy.p0.p0.i32";
  private const string _MEMSET = "llvm.memset.p0.i32";

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
    if (!NonVolatile(args[3]) || !TinySize(args[2], out var size)
        || args[0] is IrFarPtr || args[1] is IrFarPtr)
      return false;

    var block = call.Parent;
    if (block is null)
      return false;
    for (var i = 0; i < size; ++i) {
      var source = ByteAddress(block, call, args[1], i);
      var target = ByteAddress(block, call, args[0], i);
      var value = block.InsertBefore(new IrLoad(IrType.I8, source), call);
      block.InsertBefore(new IrStore(value, target), call);
    }
    call.EraseFromParent();
    return true;
  }

  private static bool TryMemset(IrCall call) {
    if (call.Callee is not IrFunction { Name: _MEMSET } || call.ArgCount != 4)
      return false;
    var args = call.Args.ToArray();
    if (!NonVolatile(args[3]) || !TinySize(args[2], out var size) || args[0] is IrFarPtr
        || !args[1].Type.IsInteger || args[1].Type.Bits != 8)
      return false;

    var block = call.Parent;
    if (block is null)
      return false;
    for (var i = 0; i < size; ++i)
      block.InsertBefore(new IrStore(args[1], ByteAddress(block, call, args[0], i)), call);
    call.EraseFromParent();
    return true;
  }

  private static IrValue ByteAddress(IrBasicBlock block, IrCall anchor, IrValue pointer, int offset)
    => offset == 0
      ? pointer
      : block.InsertBefore(new IrGep(pointer, new IrConstantInt(IrType.I32, offset)), anchor);

  private static bool NonVolatile(IrValue value) => value is IrConstantInt { IsZero: true };

  private static bool TinySize(IrValue value, out int size) {
    size = 0;
    if (value is not IrConstantInt constant || constant.Value is < 0 or > _MAX_INLINE_BYTES)
      return false;
    size = (int)constant.Value;
    return true;
  }
}
