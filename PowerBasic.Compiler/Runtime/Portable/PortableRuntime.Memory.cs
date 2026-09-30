using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Runtime.Portable;

public static partial class PortableRuntime {

  private sealed partial class Definer {

    /// <summary><c>llvm.memcpy(dst, src, n, volatile)</c>: the lowering's block copy, a byte at a time.</summary>
    private void MemoryCopy(IrWriter w) {
      var (destination, source, length) = (w.Function.Parameters[0], w.Function.Parameters[1], w.Function.Parameters[2]);
      w.B.Call(IrType.Void, this.CopyBytes, destination, source, length);
      w.B.Ret();
    }

    /// <summary><c>llvm.memset(dst, byte, n, volatile)</c>.</summary>
    private void MemorySet(IrWriter w) {
      var (destination, value, length) = (w.Function.Parameters[0], w.Function.Parameters[1], w.Function.Parameters[2]);
      var i = w.Variable(IrType.I32, w.I32(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), length), () => {
        w.SetByte(destination, i.Get(), value);
        i.Set(w.B.Add(i.Get(), w.I32(1)));
      });
      w.B.Ret();
    }

    private IrFunction? _copyBytes;

    /// <summary><c>rt.copy(dst, src, n)</c>: copies <c>n</c> bytes forwards.</summary>
    private IrFunction CopyBytes => this._copyBytes ??= this.Internal("rt.copy", IrType.Void, [IrType.Ptr, IrType.Ptr, IrType.I32], w => {
      var (destination, source, length) = (w.Function.Parameters[0], w.Function.Parameters[1], w.Function.Parameters[2]);
      var i = w.Variable(IrType.I32, w.I32(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), length), () => {
        w.SetByte(destination, i.Get(), w.ByteAt(source, i.Get()));
        i.Set(w.B.Add(i.Get(), w.I32(1)));
      });
      w.B.Ret();
    });
  }
}
