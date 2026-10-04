using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Runtime.Portable;

/// <summary>
/// The string functions beyond the basic set: the set and substring searches (TALLY, EXTRACT$,
/// REMOVE$, REPLACE, INSTR ANY, VERIFY), MIN$/MAX$, the MK$/CV conversions between numbers and
/// their bytes, ASCIIZ buffers, ASC as a statement, and LSET/RSET. Each follows the DOS runtime's
/// contract in <c>Backend/RuntimeAbi.cs</c> - what it consumes, what it answers for an empty or
/// out-of-range argument - so a program prints the same on every target.
/// </summary>
public static partial class PortableRuntime {

  private sealed partial class Definer {

    private Action<IrWriter>? TextRoutine(string name) => name switch {
      "rt_str_asc_set" => this.AscSet,
      "rt_asciiz_len" => w => w.B.Ret(w.FromIndex(this.AsciizLength(w, w.Function.Parameters[0], w.ToIndex(w.Function.Parameters[1])), w.Function.ReturnType)),
      "rt_asciiz_load" => w => {
        var bytes = w.Function.Parameters[0];
        w.B.Ret(this.Make(w, bytes, this.AsciizLength(w, bytes, w.ToIndex(w.Function.Parameters[1]))));
      },
      "rt_asciiz_store" => this.AsciizStore,
      "rt_str_mkbyt" => w => this.NumberBytes(w, w.B.Trunc(w.Function.Parameters[0], IrType.I8)),
      "rt_str_mki" or "rt_str_mkl" or "rt_str_mkdwd" or "rt_str_mks" or "rt_str_mkd" => w => this.NumberBytes(w, w.Function.Parameters[0]),
      "rt_str_cvbyt" => w => this.BytesNumber(w, IrType.I8),
      "rt_str_cvi" or "rt_str_cvwrd" => w => this.BytesNumber(w, IrType.I16),
      "rt_str_cvl" or "rt_str_cvdwd" => w => this.BytesNumber(w, IrType.I32),
      "rt_str_cvs" => w => this.BytesNumber(w, IrType.F32),
      "rt_str_cvd" or "rt_str_cve" => w => this.BytesNumber(w, IrType.F64),
      "rt_str_min" or "rt_str_max" => w => {
        var (a, b) = (w.Function.Parameters[0], w.Function.Parameters[1]);
        var order = w.B.Call(w.Index, this.CompareOrder, a, b);
        // a tie keeps the left operand, as the numeric MIN/MAX do
        var takeLeft = w.Function.Name == "rt_str_min" ? w.Cmp(IrCmpPred.Sle, order, w.Ix(0)) : w.Cmp(IrCmpPred.Sge, order, w.Ix(0));
        w.B.Call(IrType.Void, this.Release, w.B.Select(takeLeft, b, a));
        w.B.Ret(w.B.Select(takeLeft, a, b));
      },
      "rt_str_tally" or "rt_str_tally_any" => w => {
        var (main, match) = (w.Function.Parameters[0], w.Function.Parameters[1]);
        var count = w.Function.Name == "rt_str_tally_any" ? this.CountMembers(w, main, match) : this.CountOccurrences(w, main, match);
        this.ReturnConsuming(w, count, main, match);
      },
      "rt_str_extract" or "rt_str_extract_any" => w => {
        var (main, match) = (w.Function.Parameters[0], w.Function.Parameters[1]);
        var at = w.Function.Name == "rt_str_extract_any"
          ? this.ScanSet(w, main, match, w.Ix(1), member: true)
          : w.B.Call(w.Index, this.FindFunction, w.Ix(1), main, match);
        // the part before the first match, or all of it when there is none
        var length = w.B.Select(w.Cmp(IrCmpPred.Eq, at, w.Ix(0)), this.Length(w, main), w.B.Sub(at, w.Ix(1)));
        var (bytes, kept) = this.View(w, main, w.Ix(1), length);
        this.ReturnConsuming(w, this.Make(w, bytes, kept), main, match);
      },
      "rt_str_scanset" or "rt_str_verify" => w => {
        var (haystack, set) = (w.Function.Parameters[0], w.Function.Parameters[1]);
        var at = this.ScanSet(w, haystack, set, w.ToIndex(w.Function.Parameters[2]), member: w.Function.Name == "rt_str_scanset");
        this.ReturnConsuming(w, at, haystack, set);
      },
      "rt_str_remove" => w => {
        var (main, match) = (w.Function.Parameters[0], w.Function.Parameters[1]);
        this.ReturnConsuming(w, this.Substitute(w, main, match, null), main, match);
      },
      "rt_str_replace" => w => {
        var (subject, find, with) = (w.Function.Parameters[0], w.Function.Parameters[1], w.Function.Parameters[2]);
        this.ReturnConsuming(w, this.Substitute(w, subject, find, with), subject, find, with);
      },
      "rt_str_ucase_ascii" => w => w.B.Ret(this.MapCase(w, w.Function.Parameters[0], upper: true)),
      "rt_str_lcase_ascii" => w => w.B.Ret(this.MapCase(w, w.Function.Parameters[0], upper: false)),
      // an unsigned QUAD prints as a QUAD does, through the DOUBLE formatter
      "rt_str_from_u64" => w => this.NumberString(w, w.B.Cast(IrCastOp.UIToFP, w.Function.Parameters[0], IrType.F80), this.FormatFloat(15)),
      "rt_str_to_fixed_r" => this.ToFixedRight,
      "rt_str_justify" => this.Justify,
      // STRPTR names the characters; on a flat target the low word of their address is all a word holds
      "rt_str_ptr" => w => {
        var handle = w.Function.Parameters[0];
        var address = w.Variable(IrType.I32, w.I32(0));
        w.If(w.Cmp(IrCmpPred.Ne, handle, new IrNullPtr()), () => address.Set(w.B.Cast(IrCastOp.PtrToInt, Bytes(w, handle), IrType.I32)));
        w.B.Ret(w.B.Trunc(address.Get(), IrType.I16));
      },
      _ => null,
    };

    /// <summary><c>ASC(s$, n) = code</c>: the byte poked in place when n is inside the string; the handle comes back untouched.</summary>
    private void AscSet(IrWriter w) {
      var handle = w.Function.Parameters[0];
      var position = w.ToIndex(w.Function.Parameters[1]);
      w.If(w.B.And(w.Cmp(IrCmpPred.Sge, position, w.Ix(1)), w.Cmp(IrCmpPred.Sle, position, this.Length(w, handle))),
        () => w.SetByte(Bytes(w, handle), w.B.Sub(position, w.Ix(1)), w.B.Trunc(w.Function.Parameters[2], IrType.I8)));
      w.B.Ret(handle);
    }

    /// <summary>The bytes before the NUL, or the whole capacity when there is none.</summary>
    private IrValue AsciizLength(IrWriter w, IrValue bytes, IrValue capacity) {
      var length = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.B.And(w.Cmp(IrCmpPred.Slt, length.Get(), capacity),
          w.Cmp(IrCmpPred.Ne, w.ByteAt(bytes, w.B.Select(w.Cmp(IrCmpPred.Slt, length.Get(), capacity), length.Get(), w.Ix(0))), w.I8(0))),
        () => length.Set(w.B.Add(length.Get(), w.Ix(1))));
      return length.Get();
    }

    /// <summary>ASCIIZ store: at most capacity - 1 bytes and always the NUL after them; consumes the handle.</summary>
    private void AsciizStore(IrWriter w) {
      var (destination, capacity, source) = (w.Function.Parameters[0], w.ToIndex(w.Function.Parameters[1]), w.Function.Parameters[2]);
      w.If(w.Cmp(IrCmpPred.Sle, capacity, w.Ix(0)), () => {
        this.Consume(w, source);
        w.Return();
      });
      var (bytes, length) = this.View(w, source, w.Ix(1), this.Length(w, source));
      var room = w.B.Sub(capacity, w.Ix(1));
      var copy = w.B.Select(w.Cmp(IrCmpPred.Slt, length, room), length, room);
      w.B.Call(IrType.Void, this.CopyBytes, destination, bytes, copy);
      w.SetByte(destination, copy, w.I8(0));
      this.Consume(w, source);
      w.B.Ret();
    }

    /// <summary>MKI$ and its family: the value's own bytes, little-endian, as a string.</summary>
    private void NumberBytes(IrWriter w, IrValue value) {
      var bytes = value.Type.Bits / 8;
      var buffer = w.Buffer(bytes);
      w.B.Store(value, buffer);
      w.B.Ret(this.Make(w, buffer, w.Ix(bytes)));
    }

    /// <summary>CVI and its family: the first bytes of the string as the number, zeros standing in for any it lacks; consumes.</summary>
    private void BytesNumber(IrWriter w, IrType type) {
      var handle = w.Function.Parameters[0];
      var size = type.Bits / 8;
      var buffer = w.Buffer(8);
      for (var i = 0; i < 8; ++i)
        w.SetByte(buffer, w.Ix(i), w.I8(0));
      var (bytes, length) = this.View(w, handle, w.Ix(1), w.Ix(size));
      w.B.Call(IrType.Void, this.CopyBytes, buffer, bytes, length);
      IrValue value = w.B.Load(type, buffer);
      var result = w.Function.ReturnType;
      if (!Equals(value.Type, result))
        value = result.IsFloat
          ? w.B.Cast(result.Bits > type.Bits ? IrCastOp.FPExt : IrCastOp.FPTrunc, value, result)
          : w.B.Cast(result.Bits > type.Bits ? IrCastOp.ZExt : IrCastOp.Trunc, value, result);
      this.Consume(w, handle);
      w.B.Ret(value);
    }

    /// <summary>Whether <paramref name="character"/> is one of the bytes of <paramref name="set"/>.</summary>
    private IrValue InSet(IrWriter w, IrValue character, IrValue setBytes, IrValue setLength) {
      var found = w.Variable(IrType.I1, IrBuilder.ConstBool(false));
      var k = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.B.And(w.B.Xor(found.Get(), IrBuilder.ConstBool(true)), w.Cmp(IrCmpPred.Slt, k.Get(), setLength)), () => {
        w.If(w.Cmp(IrCmpPred.Eq, w.ByteAt(setBytes, k.Get()), character), () => found.Set(IrBuilder.ConstBool(true)));
        k.Set(w.B.Add(k.Get(), w.Ix(1)));
      });
      return found.Get();
    }

    /// <summary>
    /// INSTR ANY / VERIFY: the first position at or after <paramref name="start"/> whose byte is
    /// (<paramref name="member"/>) or is not in the set; 0 when there is none.
    /// </summary>
    private IrValue ScanSet(IrWriter w, IrValue haystack, IrValue set, IrValue start, bool member) {
      var (bytes, length) = this.View(w, haystack, w.Ix(1), this.Length(w, haystack));
      var (setBytes, setLength) = this.View(w, set, w.Ix(1), this.Length(w, set));
      var at = w.Variable(w.Index, w.B.Select(w.Cmp(IrCmpPred.Slt, start, w.Ix(1)), w.Ix(1), start));
      var found = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.B.And(w.Cmp(IrCmpPred.Eq, found.Get(), w.Ix(0)), w.Cmp(IrCmpPred.Sle, at.Get(), length)), () => {
        var inSet = this.InSet(w, w.ByteAt(bytes, w.B.Sub(at.Get(), w.Ix(1))), setBytes, setLength);
        w.If(member ? inSet : w.B.Xor(inSet, IrBuilder.ConstBool(true)), () => found.Set(at.Get()));
        at.Set(w.B.Add(at.Get(), w.Ix(1)));
      });
      return found.Get();
    }

    /// <summary>TALLY ANY: how many bytes of the string are in the set.</summary>
    private IrValue CountMembers(IrWriter w, IrValue main, IrValue set) {
      var (bytes, length) = this.View(w, main, w.Ix(1), this.Length(w, main));
      var (setBytes, setLength) = this.View(w, set, w.Ix(1), this.Length(w, set));
      var count = w.Variable(w.Index, w.Ix(0));
      var i = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), length), () => {
        w.If(this.InSet(w, w.ByteAt(bytes, i.Get()), setBytes, setLength), () => count.Set(w.B.Add(count.Get(), w.Ix(1))));
        i.Set(w.B.Add(i.Get(), w.Ix(1)));
      });
      return count.Get();
    }

    /// <summary>TALLY: non-overlapping occurrences of the match; an empty match occurs nowhere.</summary>
    private IrValue CountOccurrences(IrWriter w, IrValue main, IrValue match) {
      var count = w.Variable(w.Index, w.Ix(0));
      var matchLength = this.Length(w, match);
      w.If(w.Cmp(IrCmpPred.Sgt, matchLength, w.Ix(0)), () => {
        var from = w.Variable(w.Index, w.Ix(1));
        var searching = w.Variable(IrType.I1, IrBuilder.ConstBool(true));
        w.While(() => searching.Get(), () => {
          var at = w.B.Call(w.Index, this.FindFunction, from.Get(), main, match);
          w.If(w.Cmp(IrCmpPred.Eq, at, w.Ix(0)), () => searching.Set(IrBuilder.ConstBool(false)), () => {
            count.Set(w.B.Add(count.Get(), w.Ix(1)));
            from.Set(w.B.Add(at, matchLength));
          });
        });
      });
      return count.Get();
    }

    /// <summary>
    /// REMOVE$ (<paramref name="with"/> null) and REPLACE: every non-overlapping occurrence of
    /// <paramref name="find"/>, left to right, cut out or replaced. An empty find matches nothing.
    /// Built in two passes - count, then copy - so the result is allocated once at its exact length.
    /// </summary>
    private IrValue Substitute(IrWriter w, IrValue subject, IrValue find, IrValue? with) {
      var (bytes, length) = this.View(w, subject, w.Ix(1), this.Length(w, subject));
      var findLength = this.Length(w, find);
      var (withBytes, withLength) = with is null ? (new IrNullPtr(), w.Ix(0)) : this.View(w, with, w.Ix(1), this.Length(w, with));
      var result = w.Variable(IrType.Ptr, new IrNullPtr());
      w.If(w.Cmp(IrCmpPred.Eq, findLength, w.Ix(0)), () => result.Set(this.Make(w, bytes, length)), () => {
        var count = this.CountOccurrences(w, subject, find);
        var total = w.B.Add(length, w.B.Mul(count, w.B.Sub(withLength, findLength)));
        var handle = w.B.Call(IrType.Ptr, this.NewString, total);
        var output = Bytes(w, handle);
        var written = w.Variable(w.Index, w.Ix(0));
        var from = w.Variable(w.Index, w.Ix(1));
        var copying = w.Variable(IrType.I1, IrBuilder.ConstBool(true));
        w.While(() => copying.Get(), () => {
          var at = w.B.Call(w.Index, this.FindFunction, from.Get(), subject, find);
          // the stretch before the match (or the rest of the subject when there is none)
          var end = w.B.Select(w.Cmp(IrCmpPred.Eq, at, w.Ix(0)), w.B.Add(length, w.Ix(1)), at);
          var stretch = w.B.Sub(end, from.Get());
          w.B.Call(IrType.Void, this.CopyBytes, w.B.Gep(output, written.Get()), w.B.Gep(bytes, w.B.Sub(from.Get(), w.Ix(1))), stretch);
          written.Set(w.B.Add(written.Get(), stretch));
          w.If(w.Cmp(IrCmpPred.Eq, at, w.Ix(0)), () => copying.Set(IrBuilder.ConstBool(false)), () => {
            w.B.Call(IrType.Void, this.CopyBytes, w.B.Gep(output, written.Get()), withBytes, withLength);
            written.Set(w.B.Add(written.Get(), withLength));
            from.Set(w.B.Add(at, findLength));
          });
        });
        result.Set(handle);
      });
      return result.Get();
    }

    /// <summary>RSET into a fixed string: the field blanked, then the value right-justified (its leftmost bytes when it is longer); consumes.</summary>
    private void ToFixedRight(IrWriter w) {
      var (destination, size, source) = (w.Function.Parameters[0], w.ToIndex(w.Function.Parameters[1]), w.Function.Parameters[2]);
      var (bytes, length) = this.View(w, source, w.Ix(1), this.Length(w, source));
      var copy = w.B.Select(w.Cmp(IrCmpPred.Slt, length, size), length, size);
      var pad = w.B.Sub(size, copy);
      var i = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), pad), () => {
        w.SetByte(destination, i.Get(), w.I8(' '));
        i.Set(w.B.Add(i.Get(), w.Ix(1)));
      });
      w.B.Call(IrType.Void, this.CopyBytes, w.B.Gep(destination, pad), bytes, copy);
      this.Consume(w, source);
      w.B.Ret();
    }

    /// <summary>
    /// LSET/RSET into a dynamic string: the target keeps its handle and length, and the value is
    /// written into it left- (flag 0) or right-justified (flag 1), blank-padded, its leftmost bytes
    /// when it is longer. The value is consumed; the target is not.
    /// </summary>
    private void Justify(IrWriter w) {
      var (target, source, flag) = (w.Function.Parameters[0], w.Function.Parameters[1], w.Function.Parameters[2]);
      var size = this.Length(w, target);
      w.If(w.Cmp(IrCmpPred.Sgt, size, w.Ix(0)), () => {
        var destination = Bytes(w, target);
        var (bytes, length) = this.View(w, source, w.Ix(1), this.Length(w, source));
        var copy = w.B.Select(w.Cmp(IrCmpPred.Slt, length, size), length, size);
        var right = w.Cmp(IrCmpPred.Ne, flag, IrBuilder.ConstInt(flag.Type, 0));
        var offset = w.B.Select(right, w.B.Sub(size, copy), w.Ix(0));
        var i = w.Variable(w.Index, w.Ix(0));
        w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), size), () => {
          w.SetByte(destination, i.Get(), w.I8(' '));
          i.Set(w.B.Add(i.Get(), w.Ix(1)));
        });
        w.B.Call(IrType.Void, this.CopyBytes, w.B.Gep(destination, offset), bytes, copy);
      });
      this.Consume(w, source);
      w.B.Ret();
    }
  }
}
