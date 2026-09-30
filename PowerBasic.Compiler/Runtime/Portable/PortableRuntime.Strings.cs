using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Runtime.Portable;

/// <summary>
/// Strings. A string handle points at a heap block holding a 32-bit length and then the bytes; a null
/// handle is the empty string, exactly like an unassigned BASIC string. Every routine here leaves its
/// argument handles alone and answers a new handle - the contract <c>runtime/pbc_rt.c</c> keeps - so
/// the only thing that frees a string is <c>rt_str_free</c>, which the lowering calls for every
/// temporary it is done with.
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
    private IrGlobalVariable HeapTop => this._heapTop ??= module.AddGlobal(new IrGlobalVariable("rt.heapTop", IrType.I32));
    private IrGlobalVariable FreeLists => this._freeLists ??= module.AddGlobal(new IrGlobalVariable("rt.freeLists", IrType.Ptr) { Count = SizeClasses });

    private IrValue FreeList(IrWriter w, IrValue sizeClass) => w.B.Gep(this.FreeLists, sizeClass, IrType.Ptr);

    /// <summary><c>rt.allocate(bytes)</c>: a block of at least that many bytes, from its class's free list or fresh.</summary>
    private IrFunction Allocate => this._allocate ??= this.Internal("rt.allocate", IrType.Ptr, [IrType.I32], w => {
      var needed = w.B.Add(w.Function.Parameters[0], w.I32(BlockHeader));
      var sizeClass = w.Variable(IrType.I32, w.I32(0));
      var block = w.Variable(IrType.I32, w.I32(16));
      w.While(() => w.Cmp(IrCmpPred.Slt, block.Get(), needed), () => {
        block.Set(w.B.Shl(block.Get(), w.I32(1)));
        sizeClass.Set(w.B.Add(sizeClass.Get(), w.I32(1)));
      });
      var list = this.FreeList(w, sizeClass.Get());
      var head = w.B.Load(IrType.Ptr, list);
      w.If(w.Cmp(IrCmpPred.Ne, head, new IrNullPtr()), () => {
        w.B.Store(w.B.Load(IrType.Ptr, head), list);
        w.Return(head);
      });
      var top = w.B.Load(IrType.I32, this.HeapTop);
      w.If(w.Cmp(IrCmpPred.Sgt, w.B.Add(top, block.Get()), w.I32(heapBytes)),
        () => w.B.Call(IrType.Void, this.ErrorFunction, w.I32(14)));
      w.B.Store(w.B.Add(top, block.Get()), this.HeapTop);
      var payload = w.B.Gep(this.Heap, w.B.Add(top, w.I32(BlockHeader)));
      w.B.Store(sizeClass.Get(), w.B.Gep(payload, w.I32(-BlockHeader)));
      w.B.Ret(payload);
    });

    /// <summary><c>rt.release(block)</c>: back onto its class's free list; null is nothing.</summary>
    private IrFunction Release => this._release ??= this.Internal("rt.release", IrType.Void, [IrType.Ptr], w => {
      var block = w.Function.Parameters[0];
      w.If(w.Cmp(IrCmpPred.Eq, block, new IrNullPtr()), () => w.Return());
      var list = this.FreeList(w, w.B.Load(IrType.I32, w.B.Gep(block, w.I32(-BlockHeader))));
      w.B.Store(w.B.Load(IrType.Ptr, list), block);
      w.B.Store(block, list);
      w.B.Ret();
    });

    private IrFunction ErrorFunction => module.FindFunction("rt_error")!;

    /// <summary>The length of a string handle, a null handle being empty.</summary>
    private IrValue Length(IrWriter w, IrValue handle) {
      var length = w.Variable(IrType.I32, w.I32(0));
      w.If(w.Cmp(IrCmpPred.Ne, handle, new IrNullPtr()), () => length.Set(w.B.Load(IrType.I32, handle)));
      return length.Get();
    }

    private static IrValue Bytes(IrWriter w, IrValue handle) => w.B.Gep(handle, w.I32(4));

    /// <summary><c>rt.newString(length)</c>: a string of that many (unset) bytes; a negative length is empty.</summary>
    private IrFunction NewString => this._newString ??= this.Internal("rt.newString", IrType.Ptr, [IrType.I32], w => {
      var length = w.Variable(IrType.I32, w.Function.Parameters[0]);
      w.If(w.Cmp(IrCmpPred.Slt, length.Get(), w.I32(0)), () => length.Set(w.I32(0)));
      var handle = w.B.Call(IrType.Ptr, this.Allocate, w.B.Add(length.Get(), w.I32(4)));
      w.B.Store(length.Get(), handle);
      w.B.Ret(handle);
    });

    /// <summary><c>rt.makeString(bytes, length)</c>: a string holding a copy of those bytes.</summary>
    private IrFunction MakeString => this._makeString ??= this.Internal("rt.makeString", IrType.Ptr, [IrType.Ptr, IrType.I32], w => {
      var (bytes, length) = (w.Function.Parameters[0], w.Function.Parameters[1]);
      var handle = w.B.Call(IrType.Ptr, this.NewString, length);
      w.B.Call(IrType.Void, this.CopyBytes, Bytes(w, handle), bytes, w.B.Load(IrType.I32, handle));
      w.B.Ret(handle);
    });

    private IrValue Make(IrWriter w, IrValue bytes, IrValue length) => w.B.Call(IrType.Ptr, this.MakeString, bytes, length);

    /// <summary>
    /// The window <c>(start, length)</c> of a string, clamped the way MID$ clamps it: a start below
    /// one is one, a window past the end is shortened, an impossible one is empty.
    /// </summary>
    private (IrValue Bytes, IrValue Length) View(IrWriter w, IrValue handle, IrValue start, IrValue length) {
      var total = this.Length(w, handle);
      var from = w.Variable(IrType.I32, start);
      var count = w.Variable(IrType.I32, length);
      w.If(w.Cmp(IrCmpPred.Slt, from.Get(), w.I32(1)), () => from.Set(w.I32(1)));
      w.If(w.B.Or(w.Cmp(IrCmpPred.Sgt, from.Get(), total), w.Cmp(IrCmpPred.Sle, count.Get(), w.I32(0))),
        () => {
          from.Set(w.I32(1));
          count.Set(w.I32(0));
        },
        () => {
          var rest = w.B.Add(w.B.Sub(total, from.Get()), w.I32(1));
          w.If(w.Cmp(IrCmpPred.Sgt, count.Get(), rest), () => count.Set(rest));
        });
      // a null handle has no bytes, but a zero-length view of it never reads them
      var bytes = w.Variable(IrType.Ptr, new IrNullPtr());
      w.If(w.Cmp(IrCmpPred.Ne, handle, new IrNullPtr()),
        () => bytes.Set(w.B.Gep(Bytes(w, handle), w.B.Sub(from.Get(), w.I32(1)))));
      return (bytes.Get(), count.Get());
    }

    private Action<IrWriter>? StringRoutine(string name) => name switch {
      "rt_str_const" or "rt_str_from_fixed" => w => w.B.Ret(this.Make(w, w.Function.Parameters[0], w.Function.Parameters[1])),
      "rt_str_dup" => w => {
        var handle = w.Function.Parameters[0];
        var (bytes, length) = this.View(w, handle, w.I32(1), this.Length(w, handle));
        w.B.Ret(this.Make(w, bytes, length));
      },
      "rt_str_free" => w => {
        w.B.Call(IrType.Void, this.Release, w.Function.Parameters[0]);
        w.B.Ret();
      },
      "rt_str_len" or "rt_str_len_borrow" => w => w.B.Ret(this.Length(w, w.Function.Parameters[0])),
      "rt_print_strvar" => w => {
        var handle = w.Function.Parameters[0];
        w.If(w.Cmp(IrCmpPred.Ne, handle, new IrNullPtr()),
          () => w.B.Call(IrType.Void, this.Out, Bytes(w, handle), w.B.Load(IrType.I32, handle)));
        w.B.Ret();
      },
      "rt_print_strview" => w => {
        var (bytes, length) = this.View(w, w.Function.Parameters[0], w.Function.Parameters[1], w.Function.Parameters[2]);
        w.If(w.Cmp(IrCmpPred.Sgt, length, w.I32(0)), () => w.B.Call(IrType.Void, this.Out, bytes, length));
        w.B.Ret();
      },
      "rt_str_concat" => w => w.B.Ret(this.Concatenate(w, w.Function.Parameters[0], w.Function.Parameters[1])),
      "rt_str_left" => w => {
        var handle = w.Function.Parameters[0];
        var (bytes, length) = this.View(w, handle, w.I32(1), w.Function.Parameters[1]);
        w.B.Ret(this.Make(w, bytes, length));
      },
      "rt_str_right" => w => {
        var handle = w.Function.Parameters[0];
        var total = this.Length(w, handle);
        var count = w.Variable(IrType.I32, w.Function.Parameters[1]);
        w.If(w.Cmp(IrCmpPred.Sgt, count.Get(), total), () => count.Set(total));
        var (bytes, length) = this.View(w, handle, w.B.Add(w.B.Sub(total, count.Get()), w.I32(1)), count.Get());
        w.B.Ret(this.Make(w, bytes, length));
      },
      "rt_str_mid" => w => {
        var (bytes, length) = this.View(w, w.Function.Parameters[0], w.Function.Parameters[1], w.Function.Parameters[2]);
        w.B.Ret(this.Make(w, bytes, length));
      },
      "rt_str_mid2" => w => {
        var handle = w.Function.Parameters[0];
        var (bytes, length) = this.View(w, handle, w.Function.Parameters[1], this.Length(w, handle));
        w.B.Ret(this.Make(w, bytes, length));
      },
      "rt_str_mid_assign" => this.MidAssign,
      "rt_str_compare" => w => w.B.Ret(this.Compare(w, w.Function.Parameters[0], w.Function.Parameters[1], equalityOnly: false)),
      "rt_str_compare_eq" => w => w.B.Ret(this.Compare(w, w.Function.Parameters[0], w.Function.Parameters[1], equalityOnly: true)),
      "rt_str_chr" => w => {
        var buffer = w.Buffer(1);
        w.B.Store(w.B.Trunc(w.Function.Parameters[0], IrType.I8), buffer);
        w.B.Ret(this.Make(w, buffer, w.I32(1)));
      },
      "rt_str_asc" => w => {
        // ASC("") is -1
        var handle = w.Function.Parameters[0];
        var result = w.Variable(IrType.I32, w.I32(-1));
        w.If(w.Cmp(IrCmpPred.Sgt, this.Length(w, handle), w.I32(0)),
          () => result.Set(w.B.ZExt(w.ByteAt(Bytes(w, handle), w.I32(0)), IrType.I32)));
        w.B.Ret(result.Get());
      },
      "rt_str_char_at" => w => {
        var (bytes, length) = this.View(w, w.Function.Parameters[0], w.Function.Parameters[1], w.I32(1));
        var result = w.Variable(IrType.I32, w.I32(0));
        w.If(w.Cmp(IrCmpPred.Sgt, length, w.I32(0)), () => result.Set(w.B.ZExt(w.ByteAt(bytes, w.I32(0)), IrType.I32)));
        w.B.Ret(result.Get());
      },
      "rt_str_space" => w => w.B.Ret(this.Filled(w, w.Function.Parameters[0], w.I8(' '))),
      "rt_str_string" => w => w.B.Ret(this.Filled(w, w.Function.Parameters[0], w.B.Trunc(w.Function.Parameters[1], IrType.I8))),
      "rt_str_string_s" => w => {
        var handle = w.Function.Parameters[1];
        var character = w.Variable(IrType.I8, w.I8(0));
        w.If(w.Cmp(IrCmpPred.Sgt, this.Length(w, handle), w.I32(0)), () => character.Set(w.ByteAt(Bytes(w, handle), w.I32(0))));
        w.B.Ret(this.Filled(w, w.Function.Parameters[0], character.Get()));
      },
      "rt_str_repeat" => this.Repeat,
      "rt_str_ucase" => w => w.B.Ret(this.MapCase(w, w.Function.Parameters[0], upper: true)),
      "rt_str_lcase" => w => w.B.Ret(this.MapCase(w, w.Function.Parameters[0], upper: false)),
      "rt_str_ltrim" or "rt_str_rtrim" => w => w.B.Ret(this.Trim(w, w.Function.Parameters[0], left: w.Function.Name == "rt_str_ltrim")),
      "rt_str_instr" => w => w.B.Ret(this.Find(w, w.I32(1), w.Function.Parameters[0], w.Function.Parameters[1])),
      "rt_str_instr_start" => w => w.B.Ret(this.Find(w, w.Function.Parameters[0], w.Function.Parameters[1], w.Function.Parameters[2])),
      "rt_str_to_fixed" => this.ToFixed,
      _ => null,
    };

    private IrValue Concatenate(IrWriter w, IrValue a, IrValue b) {
      var (aBytes, aLength) = this.View(w, a, w.I32(1), this.Length(w, a));
      var (bBytes, bLength) = this.View(w, b, w.I32(1), this.Length(w, b));
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
    private IrValue Compare(IrWriter w, IrValue a, IrValue b, bool equalityOnly) {
      var (aBytes, aLength) = this.View(w, a, w.I32(1), this.Length(w, a));
      var (bBytes, bLength) = this.View(w, b, w.I32(1), this.Length(w, b));
      var result = w.Variable(IrType.I32, w.I32(0));
      if (equalityOnly)
        w.If(w.Cmp(IrCmpPred.Ne, aLength, bLength), () => w.Return(w.I32(1)));
      var shorter = w.B.Select(w.Cmp(IrCmpPred.Slt, aLength, bLength), aLength, bLength);
      var i = w.Variable(IrType.I32, w.I32(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), shorter), () => {
        var x = w.ByteAt(aBytes, i.Get());
        var y = w.ByteAt(bBytes, i.Get());
        w.If(w.Cmp(IrCmpPred.Ne, x, y), () => w.Return(equalityOnly ? w.I32(1) : w.B.Select(w.Cmp(IrCmpPred.Ult, x, y), w.I32(-1), w.I32(1))));
        i.Set(w.B.Add(i.Get(), w.I32(1)));
      });
      if (!equalityOnly)
        w.If(w.Cmp(IrCmpPred.Ne, aLength, bLength),
          () => result.Set(w.B.Select(w.Cmp(IrCmpPred.Slt, aLength, bLength), w.I32(-1), w.I32(1))));
      return result.Get();
    }

    private IrValue Filled(IrWriter w, IrValue count, IrValue character) {
      var handle = w.B.Call(IrType.Ptr, this.NewString, count);
      var length = w.B.Load(IrType.I32, handle);
      var i = w.Variable(IrType.I32, w.I32(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), length), () => {
        w.SetByte(Bytes(w, handle), i.Get(), character);
        i.Set(w.B.Add(i.Get(), w.I32(1)));
      });
      return handle;
    }

    private void Repeat(IrWriter w) {
      var (count, source) = (w.Function.Parameters[0], w.Function.Parameters[1]);
      var times = w.Variable(IrType.I32, count);
      w.If(w.Cmp(IrCmpPred.Slt, times.Get(), w.I32(0)), () => times.Set(w.I32(0)));
      var (bytes, length) = this.View(w, source, w.I32(1), this.Length(w, source));
      var handle = w.B.Call(IrType.Ptr, this.NewString, w.B.Mul(times.Get(), length));
      var i = w.Variable(IrType.I32, w.I32(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), times.Get()), () => {
        w.B.Call(IrType.Void, this.CopyBytes, w.B.Gep(Bytes(w, handle), w.B.Mul(i.Get(), length)), bytes, length);
        i.Set(w.B.Add(i.Get(), w.I32(1)));
      });
      w.B.Ret(handle);
    }

    private IrValue MapCase(IrWriter w, IrValue source, bool upper) {
      var (bytes, length) = this.View(w, source, w.I32(1), this.Length(w, source));
      var handle = this.Make(w, bytes, length);
      var (from, to) = upper ? ('a', 'z') : ('A', 'Z');
      var i = w.Variable(IrType.I32, w.I32(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), length), () => {
        var character = w.ByteAt(Bytes(w, handle), i.Get());
        w.If(w.B.And(w.Cmp(IrCmpPred.Uge, character, w.I8(from)), w.Cmp(IrCmpPred.Ule, character, w.I8(to))),
          () => w.SetByte(Bytes(w, handle), i.Get(), w.B.Xor(character, w.I8(0x20))));
        i.Set(w.B.Add(i.Get(), w.I32(1)));
      });
      return handle;
    }

    private IrValue Trim(IrWriter w, IrValue source, bool left) {
      var (bytes, length) = this.View(w, source, w.I32(1), this.Length(w, source));
      var start = w.Variable(IrType.I32, w.I32(0));
      var end = w.Variable(IrType.I32, length);
      if (left)
        w.While(() => w.B.And(w.Cmp(IrCmpPred.Slt, start.Get(), end.Get()), w.Cmp(IrCmpPred.Eq, w.ByteAt(bytes, start.Get()), w.I8(' '))),
          () => start.Set(w.B.Add(start.Get(), w.I32(1))));
      else
        w.While(() => w.B.And(w.Cmp(IrCmpPred.Sgt, end.Get(), w.I32(0)), w.Cmp(IrCmpPred.Eq, w.ByteAt(bytes, w.B.Sub(end.Get(), w.I32(1))), w.I8(' '))),
          () => end.Set(w.B.Sub(end.Get(), w.I32(1))));
      return this.Make(w, w.B.Gep(bytes, start.Get()), w.B.Sub(end.Get(), start.Get()));
    }

    /// <summary>INSTR: the 1-based position of the needle at or after <paramref name="start"/>, or 0.</summary>
    private IrValue Find(IrWriter w, IrValue start, IrValue haystack, IrValue needle) {
      var from = w.Variable(IrType.I32, start);
      w.If(w.Cmp(IrCmpPred.Slt, from.Get(), w.I32(1)), () => from.Set(w.I32(1)));
      var (hayBytes, hayLength) = this.View(w, haystack, w.I32(1), this.Length(w, haystack));
      var (needleBytes, needleLength) = this.View(w, needle, w.I32(1), this.Length(w, needle));
      w.If(w.Cmp(IrCmpPred.Eq, needleLength, w.I32(0)),
        () => w.Return(w.B.Select(w.Cmp(IrCmpPred.Sle, from.Get(), hayLength), from.Get(), w.I32(0))));
      var i = w.Variable(IrType.I32, w.B.Sub(from.Get(), w.I32(1)));
      w.While(() => w.Cmp(IrCmpPred.Sle, w.B.Add(i.Get(), needleLength), hayLength), () => {
        var j = w.Variable(IrType.I32, w.I32(0));
        w.While(() => w.B.And(w.Cmp(IrCmpPred.Slt, j.Get(), needleLength),
            w.Cmp(IrCmpPred.Eq, w.ByteAt(hayBytes, w.B.Add(i.Get(), j.Get())), w.ByteAt(needleBytes, j.Get()))),
          () => j.Set(w.B.Add(j.Get(), w.I32(1))));
        w.If(w.Cmp(IrCmpPred.Eq, j.Get(), needleLength), () => w.Return(w.B.Add(i.Get(), w.I32(1))));
        i.Set(w.B.Add(i.Get(), w.I32(1)));
      });
      return w.I32(0);
    }

    /// <summary>MID$ as a statement: a copy of the target with the source written over it in place, never grown.</summary>
    private void MidAssign(IrWriter w) {
      var (target, start, length, source) = (w.Function.Parameters[0], w.Function.Parameters[1], w.Function.Parameters[2], w.Function.Parameters[3]);
      var (targetBytes, targetLength) = this.View(w, target, w.I32(1), this.Length(w, target));
      var (sourceBytes, sourceLength) = this.View(w, source, w.I32(1), this.Length(w, source));
      var result = this.Make(w, targetBytes, targetLength);
      var from = w.Variable(IrType.I32, start);
      w.If(w.Cmp(IrCmpPred.Slt, from.Get(), w.I32(1)), () => from.Set(w.I32(1)));
      var count = w.Variable(IrType.I32, length);
      w.If(w.B.Or(w.Cmp(IrCmpPred.Slt, count.Get(), w.I32(0)), w.Cmp(IrCmpPred.Sgt, count.Get(), sourceLength)),
        () => count.Set(sourceLength));
      var i = w.Variable(IrType.I32, w.I32(0));
      w.While(() => w.B.And(w.Cmp(IrCmpPred.Slt, i.Get(), count.Get()),
          w.Cmp(IrCmpPred.Slt, w.B.Add(w.B.Sub(from.Get(), w.I32(1)), i.Get()), targetLength)), () => {
        w.SetByte(Bytes(w, result), w.B.Add(w.B.Sub(from.Get(), w.I32(1)), i.Get()), w.ByteAt(sourceBytes, i.Get()));
        i.Set(w.B.Add(i.Get(), w.I32(1)));
      });
      w.B.Ret(result);
    }

    /// <summary>A string into a fixed-length field: copied, truncated, padded with spaces.</summary>
    private void ToFixed(IrWriter w) {
      var (destination, size, source) = (w.Function.Parameters[0], w.Function.Parameters[1], w.Function.Parameters[2]);
      var (bytes, length) = this.View(w, source, w.I32(1), this.Length(w, source));
      var copy = w.B.Select(w.Cmp(IrCmpPred.Slt, length, size), length, size);
      w.B.Call(IrType.Void, this.CopyBytes, destination, bytes, copy);
      var i = w.Variable(IrType.I32, copy);
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), size), () => {
        w.SetByte(destination, i.Get(), w.I8(' '));
        i.Set(w.B.Add(i.Get(), w.I32(1)));
      });
      w.B.Ret();
    }

    /// <summary>
    /// <c>rt_str_concat_n(count, a, b, ...)</c> takes its operands as C varargs, which IR cannot read.
    /// Each call is rewritten into the chain of pairwise concatenations it stands for, the
    /// intermediate results freed as soon as the next link has copied them.
    /// </summary>
    private void RewriteConcatenationChains() {
      if (module.FindFunction("rt_str_concat_n") is not { } chain)
        return;
      var pair = this.Declare("rt_str_concat", IrType.Ptr, IrType.Ptr, IrType.Ptr);
      var free = this.Declare("rt_str_free", IrType.Void, IrType.Ptr);
      foreach (var call in chain.Users.OfType<IrCall>().Where(call => call.Callee == chain).ToList()) {
        var block = call.Parent!;
        var operands = call.Args.Skip(1).ToList();
        IrValue result = operands.Count == 0 ? new IrNullPtr() : operands[0];
        if (operands.Count == 1)
          result = block.InsertBefore(new IrCall(IrType.Ptr, pair, [operands[0], new IrNullPtr()]), call);
        for (var i = 1; i < operands.Count; ++i) {
          var next = block.InsertBefore(new IrCall(IrType.Ptr, pair, [result, operands[i]]), call);
          if (i > 1)
            block.InsertBefore(new IrCall(IrType.Void, free, [result]), call);
          result = next;
        }
        call.ReplaceAllUsesWith(result);
        call.EraseFromParent();
      }
    }
  }
}
