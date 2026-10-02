using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Runtime.Portable;

/// <summary>
/// Dynamic arrays: blocks from the same heap as strings, zeroed on allocation because BASIC's arrays
/// start zeroed, with running out as error 7. The IR types an array block as a far-heap pointer - on
/// DOS it lives outside the data segment - and on a flat machine that is simply a pointer, so these
/// routines cast between the two at their edges. The <c>_ptr</c> variants count elements of the
/// target's pointer width, which only the back end knows: an element-typed GEP says it.
/// </summary>
public static partial class PortableRuntime {

  private sealed partial class Definer {

    private IrFunction? _allocateArray;

    private Action<IrWriter>? ArrayRoutine(string name) => name switch {
      "rt_arr_alloc" => w => w.B.Ret(this.Far(w, w.B.Call(IrType.Ptr, this.AllocateArray, w.ToIndex(w.Function.Parameters[0]), IrBuilder.ConstBool(true)))),
      "rt_arr_alloc_nz" => w => w.B.Ret(this.Far(w, w.B.Call(IrType.Ptr, this.AllocateArray, w.ToIndex(w.Function.Parameters[0]), IrBuilder.ConstBool(false)))),
      "rt_arr_alloc_ptr" => w => w.B.Ret(this.Far(w, w.B.Call(IrType.Ptr, this.AllocateArray,
        this.PointerBytes(w, w.Function.Parameters[0]), IrBuilder.ConstBool(true)))),
      "rt_arr_realloc" => w => w.B.Ret(this.Reallocate(w, w.ToIndex(w.Function.Parameters[1]), w.ToIndex(w.Function.Parameters[2]))),
      "rt_arr_realloc_ptr" => w => w.B.Ret(this.Reallocate(w, this.PointerBytes(w, w.Function.Parameters[1]),
        this.PointerBytes(w, w.Function.Parameters[2]))),
      "rt_arr_free" or "rt_arr_free_ptr" => w => {
        w.B.Call(IrType.Void, this.Release, this.Near(w, w.Function.Parameters[0]));
        w.B.Ret();
      },
      "rt_mem_copy" => w => {
        w.B.Call(IrType.Void, this.CopyBytes, this.Near(w, w.Function.Parameters[0]), this.Near(w, w.Function.Parameters[1]),
          this.NonNegative(w, w.ToIndex(w.Function.Parameters[2])));
        w.B.Ret();
      },
      "rt_mem_compare" => this.MemoryCompare,
      _ => null,
    };

    /// <summary>The flat pointer as the declaration's pointer type, far-heap or not.</summary>
    private IrValue Far(IrWriter w, IrValue pointer)
      => pointer.Type.Equals(w.Function.ReturnType) ? pointer : w.B.Cast(IrCastOp.BitCast, pointer, w.Function.ReturnType);

    private IrValue Near(IrWriter w, IrValue pointer)
      => pointer.Type.Equals(IrType.Ptr) ? pointer : w.B.Cast(IrCastOp.BitCast, pointer, IrType.Ptr);

    private IrValue NonNegative(IrWriter w, IrValue count) => w.B.Select(w.Cmp(IrCmpPred.Slt, count, w.Ix(0)), w.Ix(0), count);

    /// <summary><c>count</c> target pointers, in bytes: the width of a pointer is the back end's to say.</summary>
    private IrValue PointerBytes(IrWriter w, IrValue count) {
      var end = w.B.Gep(new IrNullPtr(), this.NonNegative(w, w.ToIndex(count)), IrType.Ptr);
      return w.B.Cast(IrCastOp.PtrToInt, end, w.Index);
    }

    /// <summary><c>rt.allocateArray(bytes, zero)</c>: at least one byte, zeroed when asked; out of room is error 7.</summary>
    private IrFunction AllocateArray => this._allocateArray ??= this.Internal("rt.allocateArray", IrType.Ptr, [this.Index, IrType.I1], w => {
      var bytes = w.B.Select(w.Cmp(IrCmpPred.Slt, w.Function.Parameters[0], w.Ix(1)), w.Ix(1), w.Function.Parameters[0]);
      var block = w.B.Call(IrType.Ptr, this.Allocate, bytes, w.Ix(7));
      w.If(w.Function.Parameters[1], () => {
        var i = w.Variable(w.Index, w.Ix(0));
        w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), bytes), () => {
          w.SetByte(block, i.Get(), w.I8(0));
          i.Set(w.B.Add(i.Get(), w.Ix(1)));
        });
      });
      w.B.Ret(block);
    });

    /// <summary>REDIM PRESERVE: a new zeroed block, the kept bytes copied over, the old block released.</summary>
    private IrValue Reallocate(IrWriter w, IrValue oldBytes, IrValue newBytes) {
      var old = this.Near(w, w.Function.Parameters[0]);
      var fresh = w.B.Call(IrType.Ptr, this.AllocateArray, newBytes, IrBuilder.ConstBool(true));
      var keep = this.NonNegative(w, oldBytes);
      var want = this.NonNegative(w, newBytes);
      w.If(w.Cmp(IrCmpPred.Ne, old, new IrNullPtr()),
        () => w.B.Call(IrType.Void, this.CopyBytes, fresh, old, w.B.Select(w.Cmp(IrCmpPred.Sgt, keep, want), want, keep)));
      w.B.Call(IrType.Void, this.Release, old);
      return this.Far(w, fresh);
    }

    private void MemoryCompare(IrWriter w) {
      var (a, b) = (this.Near(w, w.Function.Parameters[0]), this.Near(w, w.Function.Parameters[1]));
      var count = this.NonNegative(w, w.ToIndex(w.Function.Parameters[2]));
      var i = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), count), () => {
        var (x, y) = (w.ByteAt(a, i.Get()), w.ByteAt(b, i.Get()));
        w.If(w.Cmp(IrCmpPred.Ne, x, y), () => w.Return(w.B.Select(w.Cmp(IrCmpPred.Ult, x, y),
          IrBuilder.ConstI32(-1), IrBuilder.ConstI32(1))));
        i.Set(w.B.Add(i.Get(), w.Ix(1)));
      });
      w.B.Ret(IrBuilder.ConstI32(0));
    }
  }
}
