using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Runtime.Portable;

/// <summary>
/// Strings. A string handle points at a heap block holding a 32-bit length and then the bytes; a null
/// handle is the empty string, exactly like an unassigned BASIC string.
///
/// <para>
/// <b>Ownership is the DOS runtime's</b> (<c>Backend/RuntimeAbi.cs</c> states it entry by entry): a
/// routine CONSUMES the handles it is given, because the lowering hands it an owned temporary -
/// every read of a variable goes through <c>rt_str_dup</c>. The exceptions borrow: <c>rt_str_dup</c>
/// itself, <c>rt_str_len_borrow</c>, <c>rt_print_strview</c> and an append's source. <c>MID$</c> as a
/// statement and <c>UCASE$</c>/<c>LCASE$</c> edit their target in place and hand the same handle
/// back. <c>runtime/pbc_rt.c</c> never frees at all, which is safe on a host with memory to spare and
/// a leak on a machine with 8 KB of heap.
/// </para>
///
/// <para>
/// The heap is a static region handed out in power-of-two blocks from sixteen bytes up, one free list
/// per size: a freed block is reused whole by the next request of its class, so a program that keeps
/// building and dropping strings of similar sizes runs in constant space. Running out is BASIC's
/// error 14, out of string space.
/// </para>
/// </summary>
public static partial class PortableRuntime {

  /// <summary>How many block sizes the heap has: 16 bytes to 16 << 27.</summary>
  private const int SizeClasses = 28;

  /// <summary>The bytes before a block's payload: its size class, kept at eight for alignment.</summary>
  private const int BlockHeader = 8;

  private sealed partial class Definer {

    private IrGlobalVariable? _heap, _heapTop, _freeLists;
    private IrFunction? _allocate, _release, _newString, _makeString;

    private IrGlobalVariable Heap => this._heap ??= module.AddGlobal(new IrGlobalVariable("rt.heap", IrType.I8) { Count = heapBytes });
    private IrGlobalVariable HeapTop => this._heapTop ??= module.AddGlobal(new IrGlobalVariable("rt.heapTop", this.Index));
    private IrGlobalVariable FreeLists => this._freeLists ??= module.AddGlobal(new IrGlobalVariable("rt.freeLists", IrType.Ptr) { Count = SizeClasses });

    private IrValue FreeList(IrWriter w, IrValue sizeClass) => w.B.Gep(this.FreeLists, sizeClass, IrType.Ptr);

    /// <summary>
    /// <c>rt.allocate(bytes, error)</c>: a block of at least that many bytes, from its class's free
    /// list or fresh; running out raises <c>error</c> - 14 for a string, 7 for an array.
    /// </summary>
    private IrFunction Allocate => this._allocate ??= this.Internal("rt.allocate", IrType.Ptr, [this.Index, this.Index], w => {
      var needed = w.B.Add(w.Function.Parameters[0], w.Ix(BlockHeader));
      var sizeClass = w.Variable(w.Index, w.Ix(0));
      var block = w.Variable(w.Index, w.Ix(16));
      w.While(() => w.Cmp(IrCmpPred.Slt, block.Get(), needed), () => {
        block.Set(w.B.Shl(block.Get(), w.Ix(1)));
        sizeClass.Set(w.B.Add(sizeClass.Get(), w.Ix(1)));
      });
      var list = this.FreeList(w, sizeClass.Get());
      var head = w.B.Load(IrType.Ptr, list);
      w.If(w.Cmp(IrCmpPred.Ne, head, new IrNullPtr()), () => {
        w.B.Store(w.B.Load(IrType.Ptr, head), list);
        w.Return(head);
      });
      var top = w.B.Load(w.Index, this.HeapTop);
      w.If(w.Cmp(IrCmpPred.Sgt, w.B.Add(top, block.Get()), w.Ix(heapBytes)),
        () => w.B.Call(IrType.Void, this.ErrorFunction, w.FromIndex(w.Function.Parameters[1], IrType.I32)));
      w.B.Store(w.B.Add(top, block.Get()), this.HeapTop);
      var payload = w.B.Gep(this.Heap, w.B.Add(top, w.Ix(BlockHeader)));
      w.B.Store(sizeClass.Get(), w.B.Gep(payload, w.Ix(-BlockHeader)));
      w.B.Ret(payload);
    });

    /// <summary><c>rt.release(block)</c>: back onto its class's free list; null is nothing.</summary>
    private IrFunction Release => this._release ??= this.Internal("rt.release", IrType.Void, [IrType.Ptr], w => {
      var block = w.Function.Parameters[0];
      w.If(w.Cmp(IrCmpPred.Eq, block, new IrNullPtr()), () => w.Return());
      var list = this.FreeList(w, w.B.Load(w.Index, w.B.Gep(block, w.Ix(-BlockHeader))));
      w.B.Store(w.B.Load(IrType.Ptr, list), block);
      w.B.Store(block, list);
      w.B.Ret();
    });

    private IrFunction ErrorFunction => module.FindFunction("rt_error")!;

    /// <summary>The length of a string handle, a null handle being empty.</summary>
    private IrValue Length(IrWriter w, IrValue handle) {
      var length = w.Variable(w.Index, w.Ix(0));
      w.If(w.Cmp(IrCmpPred.Ne, handle, new IrNullPtr()), () => length.Set(w.B.Load(w.Index, handle)));
      return length.Get();
    }

    private static IrValue Bytes(IrWriter w, IrValue handle) => w.B.Gep(handle, w.Ix(4));

    /// <summary><c>rt.newString(length)</c>: a string of that many (unset) bytes; a negative length is empty.</summary>
    private IrFunction NewString => this._newString ??= this.Internal("rt.newString", IrType.Ptr, [this.Index], w => {
      var length = w.Variable(w.Index, w.Function.Parameters[0]);
      w.If(w.Cmp(IrCmpPred.Slt, length.Get(), w.Ix(0)), () => length.Set(w.Ix(0)));
      var handle = w.B.Call(IrType.Ptr, this.Allocate, w.B.Add(length.Get(), w.Ix(4)), w.Ix(14));
      w.B.Store(length.Get(), handle);
      w.B.Ret(handle);
    });

    /// <summary><c>rt.makeString(bytes, length)</c>: a string holding a copy of those bytes.</summary>
    private IrFunction MakeString => this._makeString ??= this.Internal("rt.makeString", IrType.Ptr, [IrType.Ptr, this.Index], w => {
      var (bytes, length) = (w.Function.Parameters[0], w.Function.Parameters[1]);
      var handle = w.B.Call(IrType.Ptr, this.NewString, length);
      w.B.Call(IrType.Void, this.CopyBytes, Bytes(w, handle), bytes, w.B.Load(w.Index, handle));
      w.B.Ret(handle);
    });

    private IrValue Make(IrWriter w, IrValue bytes, IrValue length) => w.B.Call(IrType.Ptr, this.MakeString, bytes, length);

    /// <summary>
    /// The window <c>(start, length)</c> of a string, clamped the way MID$ clamps it: a start below
    /// one is one, a window past the end is shortened, an impossible one is empty.
    /// </summary>
    private (IrValue Bytes, IrValue Length) View(IrWriter w, IrValue handle, IrValue start, IrValue length) {
      var total = this.Length(w, handle);
      var from = w.Variable(w.Index, start);
      var count = w.Variable(w.Index, length);
      w.If(w.Cmp(IrCmpPred.Slt, from.Get(), w.Ix(1)), () => from.Set(w.Ix(1)));
      w.If(w.B.Or(w.Cmp(IrCmpPred.Sgt, from.Get(), total), w.Cmp(IrCmpPred.Sle, count.Get(), w.Ix(0))),
        () => {
          from.Set(w.Ix(1));
          count.Set(w.Ix(0));
        },
        () => {
          var rest = w.B.Add(w.B.Sub(total, from.Get()), w.Ix(1));
          w.If(w.Cmp(IrCmpPred.Sgt, count.Get(), rest), () => count.Set(rest));
        });
      // a null handle has no bytes, but a zero-length view of it never reads them
      var bytes = w.Variable(IrType.Ptr, new IrNullPtr());
      w.If(w.Cmp(IrCmpPred.Ne, handle, new IrNullPtr()),
        () => bytes.Set(w.B.Gep(Bytes(w, handle), w.B.Sub(from.Get(), w.Ix(1)))));
      return (bytes.Get(), count.Get());
    }

    private Action<IrWriter>? StringRoutine(string name) => name switch {
      "rt_str_const" or "rt_str_from_fixed" => w => w.B.Ret(this.Make(w, w.Function.Parameters[0], w.ToIndex(w.Function.Parameters[1]))),
      "rt_str_dup" => w => {
        var handle = w.Function.Parameters[0];
        var (bytes, length) = this.View(w, handle, w.Ix(1), this.Length(w, handle));
        w.B.Ret(this.Make(w, bytes, length));
      },
      "rt_str_free" => w => {
        w.B.Call(IrType.Void, this.Release, w.Function.Parameters[0]);
        w.B.Ret();
      },
      "rt_str_len_borrow" => w => w.B.Ret(w.FromIndex(this.Length(w, w.Function.Parameters[0]), w.Function.ReturnType)),
      "rt_str_len" => w => this.ReturnConsuming(w, this.Length(w, w.Function.Parameters[0]), w.Function.Parameters[0]),
      "rt_print_strvar" => w => {
        var handle = w.Function.Parameters[0];
        w.If(w.Cmp(IrCmpPred.Ne, handle, new IrNullPtr()),
          () => w.B.Call(IrType.Void, this.Out, Bytes(w, handle), w.B.Load(w.Index, handle)));
        this.Consume(w, handle);
        w.B.Ret();
      },
      "rt_print_strview" => w => {
        var (bytes, length) = this.View(w, w.Function.Parameters[0], w.ToIndex(w.Function.Parameters[1]), w.ToIndex(w.Function.Parameters[2]));
        w.If(w.Cmp(IrCmpPred.Sgt, length, w.Ix(0)), () => w.B.Call(IrType.Void, this.Out, bytes, length));
        w.B.Ret();
      },
      "rt_str_concat" => w => {
        var result = this.Concatenate(w, w.Function.Parameters[0], w.Function.Parameters[1]);
        this.Consume(w, w.Function.Parameters[0], w.Function.Parameters[1]);
        w.B.Ret(result);
      },
      "rt_str_append_var" => w => {
        // the target is consumed, the source borrowed
        var result = this.Concatenate(w, w.Function.Parameters[0], w.Function.Parameters[1]);
        this.Consume(w, w.Function.Parameters[0]);
        w.B.Ret(result);
      },
      "rt_str_append_lit" => w => {
        var literal = this.Make(w, w.Function.Parameters[1], w.ToIndex(w.Function.Parameters[2]));
        var result = this.Concatenate(w, w.Function.Parameters[0], literal);
        this.Consume(w, w.Function.Parameters[0], literal);
        w.B.Ret(result);
      },
      "rt_str_left" => w => {
        var handle = w.Function.Parameters[0];
        var (bytes, length) = this.View(w, handle, w.Ix(1), w.ToIndex(w.Function.Parameters[1]));
        this.ReturnConsuming(w, this.Make(w, bytes, length), handle);
      },
      "rt_str_right" => w => {
        var handle = w.Function.Parameters[0];
        var total = this.Length(w, handle);
        var count = w.Variable(w.Index, w.ToIndex(w.Function.Parameters[1]));
        w.If(w.Cmp(IrCmpPred.Sgt, count.Get(), total), () => count.Set(total));
        var (bytes, length) = this.View(w, handle, w.B.Add(w.B.Sub(total, count.Get()), w.Ix(1)), count.Get());
        this.ReturnConsuming(w, this.Make(w, bytes, length), handle);
      },
      "rt_str_mid" => w => {
        var handle = w.Function.Parameters[0];
        var (bytes, length) = this.View(w, handle, w.ToIndex(w.Function.Parameters[1]), w.ToIndex(w.Function.Parameters[2]));
        this.ReturnConsuming(w, this.Make(w, bytes, length), handle);
      },
      "rt_str_mid2" => w => {
        var handle = w.Function.Parameters[0];
        var (bytes, length) = this.View(w, handle, w.ToIndex(w.Function.Parameters[1]), this.Length(w, handle));
        this.ReturnConsuming(w, this.Make(w, bytes, length), handle);
      },
      "rt_str_mid_assign" => this.MidAssign,
      "rt_str_compare" or "rt_str_compare_eq" => w => {
        var (a, b) = (w.Function.Parameters[0], w.Function.Parameters[1]);
        var result = w.B.Call(w.Index, w.Function.Name == "rt_str_compare_eq" ? this.CompareEqual : this.CompareOrder, a, b);
        this.ReturnConsuming(w, result, a, b);
      },
      "rt_str_chr" => w => {
        var buffer = w.Buffer(1);
        w.B.Store(w.B.Trunc(w.Function.Parameters[0], IrType.I8), buffer);
        w.B.Ret(this.Make(w, buffer, w.Ix(1)));
      },
      "rt_str_asc" => w => {
        // ASC("") is -1
        var handle = w.Function.Parameters[0];
        var result = w.Variable(w.Index, w.Ix(-1));
        w.If(w.Cmp(IrCmpPred.Sgt, this.Length(w, handle), w.Ix(0)),
          () => result.Set(w.B.ZExt(w.ByteAt(Bytes(w, handle), w.Ix(0)), w.Index)));
        this.ReturnConsuming(w, result.Get(), handle);
      },
      "rt_str_char_at" => w => {
        var handle = w.Function.Parameters[0];
        var (bytes, length) = this.View(w, handle, w.ToIndex(w.Function.Parameters[1]), w.Ix(1));
        var result = w.Variable(w.Index, w.Ix(0));
        w.If(w.Cmp(IrCmpPred.Sgt, length, w.Ix(0)), () => result.Set(w.B.ZExt(w.ByteAt(bytes, w.Ix(0)), w.Index)));
        this.ReturnConsuming(w, result.Get(), handle);
      },
      "rt_str_space" => w => w.B.Ret(this.Filled(w, w.ToIndex(w.Function.Parameters[0]), w.I8(' '))),
      "rt_str_string" => w => w.B.Ret(this.Filled(w, w.ToIndex(w.Function.Parameters[0]), w.B.Trunc(w.Function.Parameters[1], IrType.I8))),
      "rt_str_string_s" => w => {
        var handle = w.Function.Parameters[1];
        var character = w.Variable(IrType.I8, w.I8(0));
        w.If(w.Cmp(IrCmpPred.Sgt, this.Length(w, handle), w.Ix(0)), () => character.Set(w.ByteAt(Bytes(w, handle), w.Ix(0))));
        this.ReturnConsuming(w, this.Filled(w, w.ToIndex(w.Function.Parameters[0]), character.Get()), handle);
      },
      "rt_str_repeat" => this.Repeat,
      "rt_str_ucase" => w => w.B.Ret(this.MapCase(w, w.Function.Parameters[0], upper: true)),
      "rt_str_lcase" => w => w.B.Ret(this.MapCase(w, w.Function.Parameters[0], upper: false)),
      "rt_str_ltrim" or "rt_str_rtrim" => w => {
        var handle = w.Function.Parameters[0];
        this.ReturnConsuming(w, this.Trim(w, handle, left: w.Function.Name == "rt_str_ltrim"), handle);
      },
      "rt_str_instr" => w => {
        var (haystack, needle) = (w.Function.Parameters[0], w.Function.Parameters[1]);
        this.ReturnConsuming(w, w.B.Call(w.Index, this.FindFunction, w.Ix(1), haystack, needle), haystack, needle);
      },
      "rt_str_instr_start" => w => {
        var (haystack, needle) = (w.Function.Parameters[1], w.Function.Parameters[2]);
        this.ReturnConsuming(w, w.B.Call(w.Index, this.FindFunction, w.ToIndex(w.Function.Parameters[0]), haystack, needle), haystack, needle);
      },
      "rt_str_to_fixed" => this.ToFixed,
      // INSTR of a literal needle (ConstantInstrSpecialization): the needle arrives as bytes; the
      // Horspool variant's skip table only speeds the search up, so the plain search answers both
      "rt_instr_short" or "rt_instr_horspool" => w => {
        var (start, haystack) = (w.ToIndex(w.Function.Parameters[0]), w.Function.Parameters[1]);
        var needle = this.Make(w, w.Function.Parameters[2], w.ToIndex(w.Function.Parameters[3]));
        this.ReturnConsuming(w, w.B.Call(w.Index, this.FindFunction, start, haystack, needle), haystack, needle);
      },
      _ => null,
    };

    private IrValue Concatenate(IrWriter w, IrValue a, IrValue b) {
      var (aBytes, aLength) = this.View(w, a, w.Ix(1), this.Length(w, a));
      var (bBytes, bLength) = this.View(w, b, w.Ix(1), this.Length(w, b));
      var handle = w.B.Call(IrType.Ptr, this.NewString, w.B.Add(aLength, bLength));
      var bytes = Bytes(w, handle);
      w.B.Call(IrType.Void, this.CopyBytes, bytes, aBytes, aLength);
      w.B.Call(IrType.Void, this.CopyBytes, w.B.Gep(bytes, aLength), bBytes, bLength);
      return handle;
    }

    /// <summary>
    /// <c>-1</c>, <c>0</c> or <c>1</c> by bytes then length; with <paramref name="equalityOnly"/>,
    /// <c>0</c> for equal and <c>1</c> for not, deciding unequal lengths without reading a byte.
    /// </summary>
    private IrFunction? _compareOrder, _compareEqual, _find;

    private IrFunction CompareOrder => this._compareOrder ??= this.Internal("rt.compareOrder", this.Index, [IrType.Ptr, IrType.Ptr],
      w => w.B.Ret(this.Compare(w, w.Function.Parameters[0], w.Function.Parameters[1], equalityOnly: false)));

    private IrFunction CompareEqual => this._compareEqual ??= this.Internal("rt.compareEqual", this.Index, [IrType.Ptr, IrType.Ptr],
      w => w.B.Ret(this.Compare(w, w.Function.Parameters[0], w.Function.Parameters[1], equalityOnly: true)));

    private IrFunction FindFunction => this._find ??= this.Internal("rt.find", this.Index, [this.Index, IrType.Ptr, IrType.Ptr],
      w => w.B.Ret(this.Find(w, w.Function.Parameters[0], w.Function.Parameters[1], w.Function.Parameters[2])));

    /// <summary>Releases handles this routine was given to consume.</summary>
    private void Consume(IrWriter w, params IrValue[] handles) {
      foreach (var handle in handles)
        w.B.Call(IrType.Void, this.Release, handle);
    }

    /// <summary>Releases the consumed handles and returns - an integer answer as the entry's declared type.</summary>
    private void ReturnConsuming(IrWriter w, IrValue result, params IrValue[] handles) {
      this.Consume(w, handles);
      w.B.Ret(result.Type.IsInteger && w.Function.ReturnType.IsInteger && !result.Type.IsBool
        ? w.FromIndex(result, w.Function.ReturnType) : result);
    }

    private IrValue Compare(IrWriter w, IrValue a, IrValue b, bool equalityOnly) {
      var (aBytes, aLength) = this.View(w, a, w.Ix(1), this.Length(w, a));
      var (bBytes, bLength) = this.View(w, b, w.Ix(1), this.Length(w, b));
      var result = w.Variable(w.Index, w.Ix(0));
      if (equalityOnly)
        w.If(w.Cmp(IrCmpPred.Ne, aLength, bLength), () => w.Return(w.Ix(1)));
      var shorter = w.B.Select(w.Cmp(IrCmpPred.Slt, aLength, bLength), aLength, bLength);
      var i = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), shorter), () => {
        var x = w.ByteAt(aBytes, i.Get());
        var y = w.ByteAt(bBytes, i.Get());
        w.If(w.Cmp(IrCmpPred.Ne, x, y), () => w.Return(equalityOnly ? w.Ix(1) : w.B.Select(w.Cmp(IrCmpPred.Ult, x, y), w.Ix(-1), w.Ix(1))));
        i.Set(w.B.Add(i.Get(), w.Ix(1)));
      });
      if (!equalityOnly)
        w.If(w.Cmp(IrCmpPred.Ne, aLength, bLength),
          () => result.Set(w.B.Select(w.Cmp(IrCmpPred.Slt, aLength, bLength), w.Ix(-1), w.Ix(1))));
      return result.Get();
    }

    private IrValue Filled(IrWriter w, IrValue count, IrValue character) {
      var handle = w.B.Call(IrType.Ptr, this.NewString, count);
      var length = w.B.Load(w.Index, handle);
      var i = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), length), () => {
        w.SetByte(Bytes(w, handle), i.Get(), character);
        i.Set(w.B.Add(i.Get(), w.Ix(1)));
      });
      return handle;
    }

    private void Repeat(IrWriter w) {
      var (count, source) = (w.ToIndex(w.Function.Parameters[0]), w.Function.Parameters[1]);
      var times = w.Variable(w.Index, count);
      w.If(w.Cmp(IrCmpPred.Slt, times.Get(), w.Ix(0)), () => times.Set(w.Ix(0)));
      var (bytes, length) = this.View(w, source, w.Ix(1), this.Length(w, source));
      var handle = w.B.Call(IrType.Ptr, this.NewString, w.B.Mul(times.Get(), length));
      var i = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), times.Get()), () => {
        w.B.Call(IrType.Void, this.CopyBytes, w.B.Gep(Bytes(w, handle), w.B.Mul(i.Get(), length)), bytes, length);
        i.Set(w.B.Add(i.Get(), w.Ix(1)));
      });
      this.ReturnConsuming(w, handle, source);
    }

    /// <summary>UCASE$/LCASE$: the handle's own bytes changed in place, and the handle handed back.</summary>
    private IrValue MapCase(IrWriter w, IrValue handle, bool upper) {
      var (bytes, length) = this.View(w, handle, w.Ix(1), this.Length(w, handle));
      var (from, to) = upper ? ('a', 'z') : ('A', 'Z');
      var i = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), length), () => {
        var character = w.ByteAt(bytes, i.Get());
        w.If(w.B.And(w.Cmp(IrCmpPred.Uge, character, w.I8(from)), w.Cmp(IrCmpPred.Ule, character, w.I8(to))),
          () => w.SetByte(bytes, i.Get(), w.B.Xor(character, w.I8(0x20))));
        i.Set(w.B.Add(i.Get(), w.Ix(1)));
      });
      return handle;
    }

    private IrValue Trim(IrWriter w, IrValue source, bool left) {
      var (bytes, length) = this.View(w, source, w.Ix(1), this.Length(w, source));
      var start = w.Variable(w.Index, w.Ix(0));
      var end = w.Variable(w.Index, length);
      if (left)
        w.While(() => w.B.And(w.Cmp(IrCmpPred.Slt, start.Get(), end.Get()), w.Cmp(IrCmpPred.Eq, w.ByteAt(bytes, start.Get()), w.I8(' '))),
          () => start.Set(w.B.Add(start.Get(), w.Ix(1))));
      else
        w.While(() => w.B.And(w.Cmp(IrCmpPred.Sgt, end.Get(), w.Ix(0)), w.Cmp(IrCmpPred.Eq, w.ByteAt(bytes, w.B.Sub(end.Get(), w.Ix(1))), w.I8(' '))),
          () => end.Set(w.B.Sub(end.Get(), w.Ix(1))));
      return this.Make(w, w.B.Gep(bytes, start.Get()), w.B.Sub(end.Get(), start.Get()));
    }

    /// <summary>INSTR: the 1-based position of the needle at or after <paramref name="start"/>, or 0.</summary>
    private IrValue Find(IrWriter w, IrValue start, IrValue haystack, IrValue needle) {
      var from = w.Variable(w.Index, start);
      w.If(w.Cmp(IrCmpPred.Slt, from.Get(), w.Ix(1)), () => from.Set(w.Ix(1)));
      var (hayBytes, hayLength) = this.View(w, haystack, w.Ix(1), this.Length(w, haystack));
      var (needleBytes, needleLength) = this.View(w, needle, w.Ix(1), this.Length(w, needle));
      w.If(w.Cmp(IrCmpPred.Eq, needleLength, w.Ix(0)),
        () => w.Return(w.B.Select(w.Cmp(IrCmpPred.Sle, from.Get(), hayLength), from.Get(), w.Ix(0))));
      var i = w.Variable(w.Index, w.B.Sub(from.Get(), w.Ix(1)));
      w.While(() => w.Cmp(IrCmpPred.Sle, w.B.Add(i.Get(), needleLength), hayLength), () => {
        var j = w.Variable(w.Index, w.Ix(0));
        w.While(() => w.B.And(w.Cmp(IrCmpPred.Slt, j.Get(), needleLength),
            w.Cmp(IrCmpPred.Eq, w.ByteAt(hayBytes, w.B.Add(i.Get(), j.Get())), w.ByteAt(needleBytes, j.Get()))),
          () => j.Set(w.B.Add(j.Get(), w.Ix(1))));
        w.If(w.Cmp(IrCmpPred.Eq, j.Get(), needleLength), () => w.Return(w.B.Add(i.Get(), w.Ix(1))));
        i.Set(w.B.Add(i.Get(), w.Ix(1)));
      });
      return w.Ix(0);
    }

    /// <summary>
    /// MID$ as a statement: the source written over the target's bytes in place - never growing it -
    /// the source consumed, and the target's own handle handed back.
    /// </summary>
    private void MidAssign(IrWriter w) {
      var (target, start, length, source) = (w.Function.Parameters[0], w.ToIndex(w.Function.Parameters[1]),
        w.ToIndex(w.Function.Parameters[2]), w.Function.Parameters[3]);
      var (targetBytes, targetLength) = this.View(w, target, w.Ix(1), this.Length(w, target));
      var (sourceBytes, sourceLength) = this.View(w, source, w.Ix(1), this.Length(w, source));
      var from = w.Variable(w.Index, start);
      w.If(w.Cmp(IrCmpPred.Slt, from.Get(), w.Ix(1)), () => from.Set(w.Ix(1)));
      var count = w.Variable(w.Index, length);
      w.If(w.B.Or(w.Cmp(IrCmpPred.Slt, count.Get(), w.Ix(0)), w.Cmp(IrCmpPred.Sgt, count.Get(), sourceLength)),
        () => count.Set(sourceLength));
      var i = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.B.And(w.Cmp(IrCmpPred.Slt, i.Get(), count.Get()),
          w.Cmp(IrCmpPred.Slt, w.B.Add(w.B.Sub(from.Get(), w.Ix(1)), i.Get()), targetLength)), () => {
        w.SetByte(targetBytes, w.B.Add(w.B.Sub(from.Get(), w.Ix(1)), i.Get()), w.ByteAt(sourceBytes, i.Get()));
        i.Set(w.B.Add(i.Get(), w.Ix(1)));
      });
      this.ReturnConsuming(w, target, source);
    }

    /// <summary>A string into a fixed-length field: copied, truncated, padded with spaces.</summary>
    private void ToFixed(IrWriter w) {
      var (destination, size, source) = (w.Function.Parameters[0], w.ToIndex(w.Function.Parameters[1]), w.Function.Parameters[2]);
      var (bytes, length) = this.View(w, source, w.Ix(1), this.Length(w, source));
      var copy = w.B.Select(w.Cmp(IrCmpPred.Slt, length, size), length, size);
      w.B.Call(IrType.Void, this.CopyBytes, destination, bytes, copy);
      var i = w.Variable(w.Index, copy);
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), size), () => {
        w.SetByte(destination, i.Get(), w.I8(' '));
        i.Set(w.B.Add(i.Get(), w.Ix(1)));
      });
      this.Consume(w, source);
      w.B.Ret();
    }

    /// <summary>
    /// <c>rt_str_concat_n(count, a, b, ...)</c> takes its operands as C varargs, which IR cannot read.
    /// Each call is rewritten into the chain of pairwise concatenations it stands for; since a
    /// concatenation consumes both operands, every intermediate result is released by the next link,
    /// exactly as the chain consumed its operands.
    /// </summary>
    private void RewriteConcatenationChains() {
      if (module.FindFunction("rt_str_concat_n") is not { } chain)
        return;
      var pair = this.Declare("rt_str_concat", IrType.Ptr, IrType.Ptr, IrType.Ptr);
      foreach (var call in chain.Users.OfType<IrCall>().Where(call => call.Callee == chain).ToList()) {
        var block = call.Parent!;
        var operands = call.Args.Skip(1).ToList();
        IrValue result = operands.Count == 0 ? new IrNullPtr() : operands[0];
        if (operands.Count == 1)
          result = block.InsertBefore(new IrCall(IrType.Ptr, pair, [operands[0], new IrNullPtr()]), call);
        for (var i = 1; i < operands.Count; ++i)
          result = block.InsertBefore(new IrCall(IrType.Ptr, pair, [result, operands[i]]), call);
        call.ReplaceAllUsesWith(result);
        call.EraseFromParent();
      }
    }
  }
}
