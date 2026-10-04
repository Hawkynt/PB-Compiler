using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Runtime.Portable;

/// <summary>
/// ARRAY SORT and ARRAY SCAN, driven from the parameter cells the lowering fills - the DOS runtime's
/// <c>rt_arpb</c> block, field by field. A descriptor here is just which of two arrays it names: the
/// key array is 1, a TAGARRAY 2, each with its data pointer and lower bound kept beside it. The sort
/// is the DOS runtime's insertion sort, so it is stable and a TAGARRAY's ties keep their order; a
/// string compare honours the FROM/TO character window without copying either string.
/// </summary>
public static partial class PortableRuntime {

  private sealed partial class Definer {

    private IrFunction? _numberCompare, _windowCompare, _swapBytes;
    private IrGlobalVariable? _sortData, _sortLower;

    /// <summary>The data pointer of the key array (slot 1) and of the TAGARRAY (slot 2).</summary>
    private IrGlobalVariable SortData => this._sortData ??= this.Shared(new IrGlobalVariable("rt.sortData", IrType.Ptr) { Count = 3 });
    private IrGlobalVariable SortLower => this._sortLower ??= this.Shared(new IrGlobalVariable("rt.sortLower", this.Index) { Count = 3 });

    private IrGlobalVariable Cell(string name, IrType type) => this.Shared(new IrGlobalVariable(name, type) { IsZeroInitialized = true });

    private IrValue Field(IrWriter w, string name) => w.ToIndex(w.B.Load(IrType.I16, this.Cell(name, IrType.I16)));
    private IrValue SmallField(IrWriter w, string name) => w.B.ZExt(w.B.Load(IrType.I8, this.Cell(name, IrType.I8)), this.Index);

    private Action<IrWriter>? SortRoutine(string name) => name switch {
      "rt_arr_desc" => w => this.Describe(w, 1),
      "rt_arr_tagdesc" => w => this.Describe(w, 2),
      "rt_array_sort_num" => w => {
        var size = this.SmallField(w, "rt_num_size");
        var tagged = w.Cmp(IrCmpPred.Ne, this.Field(w, "rt_num_tagdesc"), w.Ix(0));
        var tagSize = this.Field(w, "rt_num_tagsize");
        var descend = w.Cmp(IrCmpPred.Ne, this.SmallField(w, "rt_num_desc"), w.Ix(0));
        var keys = this.FirstElement(w, 1, size);
        var tags = this.FirstElement(w, 2, tagSize);
        this.InsertionSort(w, descend,
          (i, j) => w.B.Call(w.Index, this.NumberCompare, w.B.Gep(keys, w.B.Mul(i, size)), w.B.Gep(keys, w.B.Mul(j, size))),
          (i, j) => {
            w.B.Call(IrType.Void, this.SwapBytes, w.B.Gep(keys, w.B.Mul(i, size)), w.B.Gep(keys, w.B.Mul(j, size)), size);
            w.If(tagged, () => w.B.Call(IrType.Void, this.SwapBytes, w.B.Gep(tags, w.B.Mul(i, tagSize)), w.B.Gep(tags, w.B.Mul(j, tagSize)), tagSize));
          });
        w.B.Ret();
      },
      "rt_array_sort_str" => w => {
        var descend = w.Cmp(IrCmpPred.Ne, w.B.And(this.Field(w, "rt_arpb_flags"), w.Ix(1)), w.Ix(0));
        var cells = this.FirstString(w);
        IrValue At(IrValue i) => w.B.Gep(cells, i, IrType.Ptr);
        this.InsertionSort(w, descend,
          (i, j) => w.B.Call(w.Index, this.WindowCompare, w.B.Load(IrType.Ptr, At(i)), w.B.Load(IrType.Ptr, At(j))),
          (i, j) => {
            var (a, b) = (w.B.Load(IrType.Ptr, At(i)), w.B.Load(IrType.Ptr, At(j)));
            w.B.Store(b, At(i));
            w.B.Store(a, At(j));
          });
        w.B.Ret();
      },
      "rt_array_scan_num" => w => {
        var size = this.SmallField(w, "rt_num_size");
        var keys = this.FirstElement(w, 1, size);
        var match = this.Cell("rt_num_match", IrType.F80);
        this.Scan(w, this.SmallField(w, "rt_num_relop"), i => w.B.Call(w.Index, this.NumberCompare, w.B.Gep(keys, w.B.Mul(i, size)), match));
      },
      "rt_array_scan_str" => w => {
        var cells = this.FirstString(w);
        var match = w.B.Load(IrType.Ptr, this.Cell("rt_arpb_match", IrType.Ptr));
        var relop = w.B.And(w.B.Binary(IrBinaryOp.LShr, this.Field(w, "rt_arpb_flags"), w.Ix(8)), w.Ix(0xFF));
        this.Scan(w, relop, i => w.B.Call(w.Index, this.WindowCompare, w.B.Load(IrType.Ptr, w.B.Gep(cells, i, IrType.Ptr)), match));
      },
      _ => null,
    };

    /// <summary><c>rt_arr_desc(data, lower, size, count)</c>: the array remembered in its slot, the slot answered as the descriptor.</summary>
    private void Describe(IrWriter w, int slot) {
      w.B.Store(w.Function.Parameters[0], w.B.Gep(this.SortData, w.Ix(slot), IrType.Ptr));
      w.B.Store(w.ToIndex(w.Function.Parameters[1]), w.B.Gep(this.SortLower, w.Ix(slot), this.Index));
      w.B.Ret(IrBuilder.ConstInt(IrType.I16, slot));
    }

    /// <summary>The address of the start element in a slot's array: the start index is the key's, the bounds the slot's own.</summary>
    private IrValue FirstElement(IrWriter w, int slot, IrValue size) {
      var data = w.B.Load(IrType.Ptr, w.B.Gep(this.SortData, w.Ix(slot), IrType.Ptr));
      var lower = w.B.Load(this.Index, w.B.Gep(this.SortLower, w.Ix(slot), this.Index));
      return w.B.Gep(data, w.B.Mul(w.B.Sub(this.Field(w, "rt_arpb_start"), lower), size));
    }

    private IrValue FirstString(IrWriter w) {
      var data = w.B.Load(IrType.Ptr, w.B.Gep(this.SortData, w.Ix(1), IrType.Ptr));
      var lower = w.B.Load(this.Index, w.B.Gep(this.SortLower, w.Ix(1), this.Index));
      return w.B.Gep(data, w.B.Sub(this.Field(w, "rt_arpb_start"), lower), IrType.Ptr);
    }

    /// <summary>
    /// The DOS runtime's insertion sort over the block's count: each element walks down while it
    /// belongs before its neighbour - strictly, so equal elements never pass each other.
    /// </summary>
    private void InsertionSort(IrWriter w, IrValue descend, Func<IrValue, IrValue, IrValue> compare, Action<IrValue, IrValue> swap) {
      var count = this.Field(w, "rt_arpb_count");
      var i = w.Variable(w.Index, w.Ix(1));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), count), () => {
        var j = w.Variable(w.Index, i.Get());
        var walking = w.Variable(IrType.I1, IrBuilder.ConstBool(true));
        w.While(() => w.B.And(walking.Get(), w.Cmp(IrCmpPred.Sgt, j.Get(), w.Ix(0))), () => {
          var previous = w.B.Sub(j.Get(), w.Ix(1));
          var order = compare(previous, j.Get());
          var outOfOrder = w.B.Select(descend, w.Cmp(IrCmpPred.Slt, order, w.Ix(0)), w.Cmp(IrCmpPred.Sgt, order, w.Ix(0)));
          w.If(outOfOrder, () => {
            swap(previous, j.Get());
            j.Set(previous);
          }, () => walking.Set(IrBuilder.ConstBool(false)));
        });
        i.Set(w.B.Add(i.Get(), w.Ix(1)));
      });
    }

    /// <summary>The 1-based position, from the start element, of the first element the relation holds for; 0 for none.</summary>
    private void Scan(IrWriter w, IrValue relop, Func<IrValue, IrValue> compare) {
      var count = this.Field(w, "rt_arpb_count");
      var i = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), count), () => {
        var order = compare(i.Get());
        var zero = w.Ix(0);
        // 0 = / 1 <> / 2 < / 3 <= / 4 > / 5 >=
        var holds = w.B.Select(w.Cmp(IrCmpPred.Eq, relop, w.Ix(0)), w.Cmp(IrCmpPred.Eq, order, zero),
          w.B.Select(w.Cmp(IrCmpPred.Eq, relop, w.Ix(1)), w.Cmp(IrCmpPred.Ne, order, zero),
          w.B.Select(w.Cmp(IrCmpPred.Eq, relop, w.Ix(2)), w.Cmp(IrCmpPred.Slt, order, zero),
          w.B.Select(w.Cmp(IrCmpPred.Eq, relop, w.Ix(3)), w.Cmp(IrCmpPred.Sle, order, zero),
          w.B.Select(w.Cmp(IrCmpPred.Eq, relop, w.Ix(4)), w.Cmp(IrCmpPred.Sgt, order, zero),
            w.Cmp(IrCmpPred.Sge, order, zero))))));
        w.If(holds, () => w.Return(w.FromIndex(w.B.Add(i.Get(), w.Ix(1)), IrType.I16)));
        i.Set(w.B.Add(i.Get(), w.Ix(1)));
      });
      w.B.Ret(IrBuilder.ConstInt(IrType.I16, 0));
    }

    /// <summary>
    /// <c>rt.numberCompare(a, b)</c>: -1, 0 or 1 for two elements of the block's numeric kind. An
    /// integer narrower than its load width is unsigned, as the DOS runtime's FILD table says.
    /// </summary>
    private IrFunction NumberCompare => this._numberCompare ??= this.Internal("rt.numberCompare", this.Index, [IrType.Ptr, IrType.Ptr], w => {
      var (a, b) = (w.Function.Parameters[0], w.Function.Parameters[1]);
      var size = this.SmallField(w, "rt_num_size");
      IrValue Order(IrCmpPred less, IrCmpPred greater, IrValue x, IrValue y)
        => w.B.Select(w.Cmp(less, x, y), w.Ix(-1), w.B.Select(w.Cmp(greater, x, y), w.Ix(1), w.Ix(0)));
      w.If(w.Cmp(IrCmpPred.Eq, this.SmallField(w, "rt_num_kind"), w.Ix(2)), () => {
        IrValue Real(IrValue p, IrType type) => type.Equals(IrType.F80) ? w.B.Load(type, p) : w.B.Cast(IrCastOp.FPExt, w.B.Load(type, p), IrType.F80);
        foreach (var (bytes, type) in ((int, IrType)[])[(4, IrType.F32), (8, IrType.F64)])
          w.If(w.Cmp(IrCmpPred.Eq, size, w.Ix(bytes)), () => w.Return(Order(IrCmpPred.Folt, IrCmpPred.Fogt, Real(a, type), Real(b, type))));
        w.Return(Order(IrCmpPred.Folt, IrCmpPred.Fogt, Real(a, IrType.F80), Real(b, IrType.F80)));
      });
      var signed = w.Cmp(IrCmpPred.Eq, this.SmallField(w, "rt_num_load"), size);
      foreach (var (bytes, type) in ((int, IrType)[])[(1, IrType.I8), (2, IrType.I16), (4, IrType.I32)])
        w.If(w.Cmp(IrCmpPred.Eq, size, w.Ix(bytes)), () => {
          var (x, y) = (w.B.Load(type, a), w.B.Load(type, b));
          w.If(signed, () => w.Return(Order(IrCmpPred.Slt, IrCmpPred.Sgt, x, y)));
          w.Return(Order(IrCmpPred.Ult, IrCmpPred.Ugt, x, y));
        });
      var (p, q) = (w.B.Load(IrType.I64, a), w.B.Load(IrType.I64, b));
      w.B.Ret(Order(IrCmpPred.Slt, IrCmpPred.Sgt, p, q));
    });

    /// <summary>
    /// <c>rt.windowCompare(element, other)</c>: the two strings compared over the FROM/TO window - the
    /// element's always, the other's too unless flag bit 1 says the other is a scan's match.
    /// </summary>
    private IrFunction WindowCompare => this._windowCompare ??= this.Internal("rt.windowCompare", this.Index, [IrType.Ptr, IrType.Ptr], w => {
      var (a, b) = (w.Function.Parameters[0], w.Function.Parameters[1]);
      var from = this.SignedField(w, "rt_arpb_from");
      var to = this.SignedField(w, "rt_arpb_to");
      var skip = w.B.Select(w.Cmp(IrCmpPred.Slt, from, w.Ix(1)), w.Ix(0), w.B.Sub(from, w.Ix(1)));
      var span = w.B.Add(w.B.Sub(to, from), w.Ix(1));
      var width = w.B.Select(w.Cmp(IrCmpPred.Slt, span, w.Ix(0)), w.Ix(0), span);
      (IrValue Bytes, IrValue Length) Window(IrValue handle, bool clamp) {
        var length = this.Length(w, handle);
        var bytes = w.Variable(IrType.Ptr, new IrNullPtr());
        w.If(w.Cmp(IrCmpPred.Ne, handle, new IrNullPtr()), () => bytes.Set(Bytes(w, handle)));
        if (!clamp)
          return (bytes.Get(), length);
        var cut = w.B.Select(w.Cmp(IrCmpPred.Sgt, skip, length), length, skip);
        var rest = w.B.Sub(length, cut);
        return (w.B.Select(w.Cmp(IrCmpPred.Eq, rest, w.Ix(0)), bytes.Get(), w.B.Gep(bytes.Get(), cut)),
          w.B.Select(w.Cmp(IrCmpPred.Sgt, rest, width), width, rest));
      }
      var (left, leftLength) = Window(a, clamp: true);
      var matchSide = w.Cmp(IrCmpPred.Ne, w.B.And(this.Field(w, "rt_arpb_flags"), w.Ix(2)), w.Ix(0));
      var (whole, wholeLength) = Window(b, clamp: false);
      var (clamped, clampedLength) = Window(b, clamp: true);
      var right = w.B.Select(matchSide, whole, clamped);
      var rightLength = w.B.Select(matchSide, wholeLength, clampedLength);
      var shorter = w.B.Select(w.Cmp(IrCmpPred.Slt, leftLength, rightLength), leftLength, rightLength);
      var i = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), shorter), () => {
        var (x, y) = (w.ByteAt(left, i.Get()), w.ByteAt(right, i.Get()));
        w.If(w.Cmp(IrCmpPred.Ne, x, y), () => w.Return(w.B.Select(w.Cmp(IrCmpPred.Ult, x, y), w.Ix(-1), w.Ix(1))));
        i.Set(w.B.Add(i.Get(), w.Ix(1)));
      });
      w.B.Ret(w.B.Select(w.Cmp(IrCmpPred.Slt, leftLength, rightLength), w.Ix(-1),
        w.B.Select(w.Cmp(IrCmpPred.Sgt, leftLength, rightLength), w.Ix(1), w.Ix(0))));
    });

    private IrValue SignedField(IrWriter w, string name) => w.ToIndex(w.B.Load(IrType.I16, this.Cell(name, IrType.I16)));

    private IrFunction SwapBytes => this._swapBytes ??= this.Internal("rt.swapBytes", IrType.Void, [IrType.Ptr, IrType.Ptr, this.Index], w => {
      var (a, b, count) = (w.Function.Parameters[0], w.Function.Parameters[1], w.Function.Parameters[2]);
      var i = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), count), () => {
        var (x, y) = (w.ByteAt(a, i.Get()), w.ByteAt(b, i.Get()));
        w.SetByte(a, i.Get(), y);
        w.SetByte(b, i.Get(), x);
        i.Set(w.B.Add(i.Get(), w.Ix(1)));
      });
      w.B.Ret();
    });
  }
}
