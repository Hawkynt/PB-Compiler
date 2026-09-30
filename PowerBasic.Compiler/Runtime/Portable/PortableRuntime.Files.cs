using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Runtime.Portable;

/// <summary>
/// Files and the console's input, on the system primitives a back end supplies:
/// <c>sys_read(fd, buffer, length)</c>, <c>sys_open(path, mode)</c> - mode 0 read, 1 create and
/// truncate, 2 append, 3 read and write, creating - <c>sys_close</c>, <c>sys_seek(fd, offset,
/// whence)</c> and <c>sys_unlink</c>.
///
/// <para>
/// BASIC numbers its files 1 to 15, so the runtime keeps a table indexed by that number, as the DOS
/// runtime does: the descriptor (plus one, so a zeroed table means all closed), the column its comma
/// zones count from, and one byte of look-ahead - <c>EOF</c> is true only once the last byte has been
/// read, so it has to peek. Slot 0 is the console's input. The error numbers are the DOS runtime's,
/// which are genuine PowerBASIC's: 52 bad file number, 55 already open, 57 for an open that fails -
/// not <c>runtime/pbc_rt.c</c>'s 53 - and 67 too many files; <c>KILL</c> of a missing file is
/// ignored, as the DOS runtime ignores it.
/// </para>
/// </summary>
public static partial class PortableRuntime {

  /// <summary>File numbers run 1 to 15; slot 0 is the console's input.</summary>
  private const int FileSlots = 16;

  /// <summary>The longest path, and the longest line or field, a read gathers.</summary>
  private const int PathBytes = 260;
  private const int LineBytes = 1024;

  private sealed partial class Definer {

    private IrFunction? _read, _open, _close, _seek, _unlink;
    private IrFunction? _fileDescriptor, _readByte, _getField, _fileOut, _pathOf, _val;
    private IrGlobalVariable? _descriptors, _columns, _lookAhead;

    private IrFunction ReadCall => this._read ??= this.Declare("sys_read", IrType.I32, IrType.I32, IrType.Ptr, IrType.I32);
    private IrFunction OpenCall => this._open ??= this.Declare("sys_open", IrType.I32, IrType.Ptr, IrType.I32);
    private IrFunction CloseCall => this._close ??= this.Declare("sys_close", IrType.I32, IrType.I32);
    private IrFunction SeekCall => this._seek ??= this.Declare("sys_seek", IrType.I32, IrType.I32, IrType.I32, IrType.I32);
    private IrFunction UnlinkCall => this._unlink ??= this.Declare("sys_unlink", IrType.I32, IrType.Ptr);

    private IrGlobalVariable Descriptors => this._descriptors ??= module.AddGlobal(new IrGlobalVariable("rt.fileDescriptors", this.Index) { Count = FileSlots });
    private IrGlobalVariable Columns => this._columns ??= module.AddGlobal(new IrGlobalVariable("rt.fileColumns", this.Index) { Count = FileSlots });
    private IrGlobalVariable LookAhead => this._lookAhead ??= module.AddGlobal(new IrGlobalVariable("rt.fileLookAhead", this.Index) { Count = FileSlots });

    private IrValue Cell(IrWriter w, IrGlobalVariable table, IrValue slot) => w.B.Gep(table, slot, w.Index);

    private Action<IrWriter>? FileRoutine(string name) => name switch {
      "rt_input_prompt" => w => {
        w.B.Call(IrType.Void, this.Out, w.Function.Parameters[0], w.Function.Parameters[1]);
        w.B.Ret();
      },
      "rt_input_str" => w => w.B.Ret(w.B.Call(IrType.Ptr, this.GetField, w.Ix(0), w.Ix(0), IrBuilder.ConstBool(false))),
      "rt_input_line" => w => w.B.Ret(w.B.Call(IrType.Ptr, this.GetField, w.Ix(0), w.Ix(0), IrBuilder.ConstBool(true))),
      "rt_input_i8" or "rt_input_u8" or "rt_input_i16" or "rt_input_u16" or "rt_input_i32" or "rt_input_u32" or "rt_input_i64"
        or "rt_input_single" or "rt_input_double" or "rt_input_ext" => w => this.InputNumber(w, w.Ix(0), w.Ix(0)),
      "rt_finput_i8" or "rt_finput_u8" or "rt_finput_i16" or "rt_finput_u16" or "rt_finput_i32" or "rt_finput_u32"
        or "rt_finput_i64" or "rt_finput_single" or "rt_finput_double" or "rt_finput_ext" => w => {
          var slot = w.ToIndex(w.Function.Parameters[0]);
          this.InputNumber(w, slot, w.B.Call(w.Index, this.FileDescriptor, slot));
        },
      "rt_file_open" => this.FileOpen,
      "rt_file_close" => w => {
        this.CloseSlot(w, w.ToIndex(w.Function.Parameters[0]));
        w.B.Ret();
      },
      "rt_file_close_all" => w => {
        var n = w.Variable(w.Index, w.Ix(1));
        w.While(() => w.Cmp(IrCmpPred.Slt, n.Get(), w.Ix(FileSlots)), () => {
          this.CloseSlot(w, n.Get());
          n.Set(w.B.Add(n.Get(), w.Ix(1)));
        });
        w.B.Ret();
      },
      "rt_freefile" => w => {
        var n = w.Variable(w.Index, w.Ix(1));
        w.While(() => w.Cmp(IrCmpPred.Slt, n.Get(), w.Ix(FileSlots)), () => {
          w.If(w.Cmp(IrCmpPred.Eq, w.B.Load(w.Index, this.Cell(w, this.Descriptors, n.Get())), w.Ix(0)),
            () => w.Return(w.FromIndex(n.Get(), w.Function.ReturnType)));
          n.Set(w.B.Add(n.Get(), w.Ix(1)));
        });
        w.B.Call(IrType.Void, this.ErrorFunction, w.Ix(67));
        w.B.Ret(IrBuilder.ConstInt(w.Function.ReturnType, 0));
      },
      "rt_eof" => w => {
        var slot = w.ToIndex(w.Function.Parameters[0]);
        var fd = w.B.Call(w.Index, this.FileDescriptor, slot);
        var next = w.B.Call(w.Index, this.ReadByte, slot, fd);
        w.If(w.Cmp(IrCmpPred.Slt, next, w.Ix(0)), () => w.Return(IrBuilder.ConstInt(w.Function.ReturnType, -1)));
        w.B.Store(w.B.Add(next, w.Ix(1)), this.Cell(w, this.LookAhead, slot));
        w.B.Ret(IrBuilder.ConstInt(w.Function.ReturnType, 0));
      },
      "rt_kill" => w => {
        var path = w.B.Call(IrType.Ptr, this.PathOf, w.Function.Parameters[0], w.Buffer(PathBytes));
        this.System(w, this.UnlinkCall, path);
        this.Consume(w, w.Function.Parameters[0]);
        w.B.Ret();
      },
      "rt_fprint_str" => w => {
        w.B.Call(IrType.Void, this.FileOut, w.ToIndex(w.Function.Parameters[0]), w.Function.Parameters[1], w.ToIndex(w.Function.Parameters[2]));
        w.B.Ret();
      },
      "rt_fprint_strvar" or "rt_fput_str" => w => {
        var (n, handle) = (w.ToIndex(w.Function.Parameters[0]), w.Function.Parameters[1]);
        var (bytes, length) = this.View(w, handle, w.Ix(1), this.Length(w, handle));
        w.If(w.Cmp(IrCmpPred.Sgt, length, w.Ix(0)), () => w.B.Call(IrType.Void, this.FileOut, n, bytes, length));
        this.Consume(w, handle);
        w.B.Ret();
      },
      "rt_fprint_strview" => w => {
        var (bytes, length) = this.View(w, w.Function.Parameters[1], w.ToIndex(w.Function.Parameters[2]), w.ToIndex(w.Function.Parameters[3]));
        w.If(w.Cmp(IrCmpPred.Sgt, length, w.Ix(0)),
          () => w.B.Call(IrType.Void, this.FileOut, w.ToIndex(w.Function.Parameters[0]), bytes, length));
        w.B.Ret();
      },
      "rt_fprint_nl" => w => {
        var newLine = w.Buffer(1);
        w.B.Store(w.I8('\n'), newLine);
        w.B.Call(IrType.Void, this.FileOut, w.ToIndex(w.Function.Parameters[0]), newLine, w.Ix(1));
        w.B.Ret();
      },
      "rt_fprint_comma" => this.FilePrintZone,
      "rt_fprint_i8" or "rt_fprint_i16" or "rt_fprint_i32" or "rt_fprint_i64" => w => this.FilePrintNumber(w, this.FileWidened(w, signed: true), this.FormatSigned),
      "rt_fprint_u8" or "rt_fprint_u16" or "rt_fprint_u32" => w => this.FilePrintNumber(w, this.FileWidened(w, signed: false), this.FormatUnsigned),
      "rt_fprint_single" => w => this.FilePrintNumber(w, w.Function.Parameters[1], this.FormatFloat(7)),
      "rt_fprint_double" => w => this.FilePrintNumber(w, w.Function.Parameters[1], this.FormatFloat(15)),
      // an EXT prints as a DOUBLE does: the DOS runtime has one formatter for both
      "rt_fprint_ext" => w => this.FilePrintNumber(w, w.Function.Parameters[1], this.FormatFloat(15)),
      "rt_fprint_tab" => this.FilePrintTab,
      "rt_finput_line" => w => {
        var slot = w.ToIndex(w.Function.Parameters[0]);
        w.B.Ret(w.B.Call(IrType.Ptr, this.GetField, slot, w.B.Call(w.Index, this.FileDescriptor, slot), IrBuilder.ConstBool(true)));
      },
      "rt_fget_str" => this.GetBytes,
      "rt_file_put" => w => this.RecordTransfer(w, write: true),
      "rt_file_get" => w => this.RecordTransfer(w, write: false),
      "rt_file_length" => w => {
        var fd = w.B.Call(w.Index, this.FileDescriptor, w.ToIndex(w.Function.Parameters[0]));
        var here = this.System(w, this.SeekCall, fd, w.Ix(0), w.Ix(1));
        var end = this.System(w, this.SeekCall, fd, w.Ix(0), w.Ix(2));
        this.System(w, this.SeekCall, fd, here, w.Ix(0));
        w.B.Ret(w.FromIndex(end, w.Function.ReturnType));
      },
      "rt_file_pos" => w => {
        // the look-ahead byte has been read from the file but not yet by the program
        var slot = w.ToIndex(w.Function.Parameters[0]);
        var fd = w.B.Call(w.Index, this.FileDescriptor, slot);
        var here = this.System(w, this.SeekCall, fd, w.Ix(0), w.Ix(1));
        var pending = w.Cmp(IrCmpPred.Ne, w.B.Load(w.Index, this.Cell(w, this.LookAhead, slot)), w.Ix(0));
        w.B.Ret(w.FromIndex(w.B.Sub(here, w.B.Select(pending, w.Ix(1), w.Ix(0))), w.Function.ReturnType));
      },
      "rt_file_seek" => w => {
        var slot = w.ToIndex(w.Function.Parameters[0]);
        var fd = w.B.Call(w.Index, this.FileDescriptor, slot);
        var position = w.ToIndex(w.Function.Parameters[1]);
        this.System(w, this.SeekCall, fd, w.B.Select(w.Cmp(IrCmpPred.Slt, position, w.Ix(0)), w.Ix(0), position), w.Ix(0));
        w.B.Store(w.Ix(0), this.Cell(w, this.LookAhead, slot));
        w.B.Ret();
      },
      _ => null,
    };

    /// <summary><c>rt.fileDescriptor(n)</c>: the open file's descriptor, or error 52.</summary>
    private IrFunction FileDescriptor => this._fileDescriptor ??= this.Internal("rt.fileDescriptor", this.Index, [this.Index], w => {
      var n = w.Function.Parameters[0];
      w.If(w.B.Or(w.Cmp(IrCmpPred.Slt, n, w.Ix(1)), w.Cmp(IrCmpPred.Sge, n, w.Ix(FileSlots))),
        () => w.B.Call(IrType.Void, this.ErrorFunction, w.Ix(52)));
      var stored = w.B.Load(w.Index, this.Cell(w, this.Descriptors, n));
      w.If(w.Cmp(IrCmpPred.Eq, stored, w.Ix(0)), () => w.B.Call(IrType.Void, this.ErrorFunction, w.Ix(52)));
      w.B.Ret(w.B.Sub(stored, w.Ix(1)));
    });

    /// <summary><c>rt.readByte(slot, fd)</c>: the look-ahead byte if one is waiting, else the next byte read; -1 at the end.</summary>
    private IrFunction ReadByte => this._readByte ??= this.Internal("rt.readByte", this.Index, [this.Index, this.Index], w => {
      var (slot, fd) = (w.Function.Parameters[0], w.Function.Parameters[1]);
      var waiting = this.Cell(w, this.LookAhead, slot);
      var pending = w.B.Load(w.Index, waiting);
      w.If(w.Cmp(IrCmpPred.Ne, pending, w.Ix(0)), () => {
        w.B.Store(w.Ix(0), waiting);
        w.Return(w.B.Sub(pending, w.Ix(1)));
      });
      var buffer = w.Buffer(1);
      var got = this.System(w, this.ReadCall, fd, buffer, w.Ix(1));
      w.If(w.Cmp(IrCmpPred.Sle, got, w.Ix(0)), () => w.Return(w.Ix(-1)));
      w.B.Ret(w.B.ZExt(w.B.Load(IrType.I8, buffer), w.Index));
    });

    /// <summary>
    /// <c>rt.getField(slot, fd, wholeLine)</c>: bytes up to the end of the line - or, for an INPUT
    /// field, up to a comma - with carriage returns dropped; a field is trimmed of the blanks around
    /// it, a whole line is not.
    /// </summary>
    private IrFunction GetField => this._getField ??= this.Internal("rt.getField", IrType.Ptr, [this.Index, this.Index, IrType.I1], w => {
      var (slot, fd, wholeLine) = (w.Function.Parameters[0], w.Function.Parameters[1], w.Function.Parameters[2]);
      var text = w.Buffer(LineBytes);
      var length = w.Variable(w.Index, w.Ix(0));
      var reading = w.Variable(IrType.I1, IrBuilder.ConstBool(true));
      w.While(() => reading.Get(), () => {
        var next = w.B.Call(w.Index, this.ReadByte, slot, fd);
        var ends = w.B.Or(w.Cmp(IrCmpPred.Slt, next, w.Ix(0)),
          w.B.Or(w.Cmp(IrCmpPred.Eq, next, w.Ix('\n')),
            w.B.And(w.B.Xor(wholeLine, IrBuilder.ConstBool(true)), w.Cmp(IrCmpPred.Eq, next, w.Ix(',')))));
        w.If(ends, () => reading.Set(IrBuilder.ConstBool(false)),
          () => w.If(w.B.And(w.Cmp(IrCmpPred.Ne, next, w.Ix('\r')), w.Cmp(IrCmpPred.Slt, length.Get(), w.Ix(LineBytes))), () => {
            w.SetByte(text, length.Get(), w.B.Trunc(next, IrType.I8));
            length.Set(w.B.Add(length.Get(), w.Ix(1)));
          }));
      });
      var start = w.Variable(w.Index, w.Ix(0));
      var end = w.Variable(w.Index, length.Get());
      w.If(w.B.Xor(wholeLine, IrBuilder.ConstBool(true)), () => {
        IrValue Blank(IrValue character) => w.B.Or(w.Cmp(IrCmpPred.Eq, character, w.I8(' ')), w.Cmp(IrCmpPred.Eq, character, w.I8('\t')));
        w.While(() => w.B.And(w.Cmp(IrCmpPred.Slt, start.Get(), end.Get()), Blank(w.B.Select(w.Cmp(IrCmpPred.Slt, start.Get(), end.Get()),
            w.ByteAt(text, start.Get()), w.I8(0)))), () => start.Set(w.B.Add(start.Get(), w.Ix(1))));
        w.While(() => w.B.And(w.Cmp(IrCmpPred.Sgt, end.Get(), start.Get()), Blank(w.B.Select(w.Cmp(IrCmpPred.Sgt, end.Get(), start.Get()),
            w.ByteAt(text, w.B.Sub(end.Get(), w.Ix(1))), w.I8(0)))), () => end.Set(w.B.Sub(end.Get(), w.Ix(1))));
      });
      w.B.Ret(this.Make(w, w.B.Gep(text, start.Get()), w.B.Sub(end.Get(), start.Get())));
    });

    /// <summary><c>rt.val(handle)</c>: VAL, as a function other routines can call.</summary>
    private IrFunction ValFunction => this._val ??= this.Internal("rt.val", IrType.F64, [IrType.Ptr], this.Val);

    /// <summary>INPUT of a number: one field, read as VAL reads it, converted as a C cast converts (truncating).</summary>
    /// <summary>An INPUT field from <paramref name="slot"/>'s descriptor <paramref name="fd"/>, as the entry's number type.</summary>
    private void InputNumber(IrWriter w, IrValue slot, IrValue fd) {
      var field = w.B.Call(IrType.Ptr, this.GetField, slot, fd, IrBuilder.ConstBool(false));
      var value = w.B.Call(IrType.F64, this.ValFunction, field);
      var type = w.Function.ReturnType;
      IrValue result = type.IsFloat
        ? type.Bits == 64 ? value : w.B.Cast(type.Bits > 64 ? IrCastOp.FPExt : IrCastOp.FPTrunc, value, type)
        : w.B.Cast(w.Function.Name.StartsWith("rt_input_u", StringComparison.Ordinal)
          || w.Function.Name.StartsWith("rt_finput_u", StringComparison.Ordinal) ? IrCastOp.FPToUI : IrCastOp.FPToSI, value, type);
      w.B.Ret(result);
    }

    /// <summary><c>rt.pathOf(handle, buffer)</c>: the string's bytes, NUL-terminated, in the buffer.</summary>
    private IrFunction PathOf => this._pathOf ??= this.Internal("rt.pathOf", IrType.Ptr, [IrType.Ptr, IrType.Ptr], w => {
      var (handle, buffer) = (w.Function.Parameters[0], w.Function.Parameters[1]);
      var (bytes, length) = this.View(w, handle, w.Ix(1), this.Length(w, handle));
      var copy = w.B.Select(w.Cmp(IrCmpPred.Sgt, length, w.Ix(PathBytes - 1)), w.Ix(PathBytes - 1), length);
      w.B.Call(IrType.Void, this.CopyBytes, buffer, bytes, copy);
      w.SetByte(buffer, copy, w.I8(0));
      w.B.Ret(buffer);
    });

    private void FileOpen(IrWriter w) {
      var (n, name, mode) = (w.ToIndex(w.Function.Parameters[0]), w.Function.Parameters[1], w.ToIndex(w.Function.Parameters[2]));
      w.If(w.B.Or(w.Cmp(IrCmpPred.Slt, n, w.Ix(1)), w.Cmp(IrCmpPred.Sge, n, w.Ix(FileSlots))),
        () => w.B.Call(IrType.Void, this.ErrorFunction, w.Ix(52)));
      w.If(w.Cmp(IrCmpPred.Ne, w.B.Load(w.Index, this.Cell(w, this.Descriptors, n)), w.Ix(0)),
        () => w.B.Call(IrType.Void, this.ErrorFunction, w.Ix(55)));
      var path = w.B.Call(IrType.Ptr, this.PathOf, name, w.Buffer(PathBytes));
      // INPUT, OUTPUT, APPEND as they are; RANDOM and BINARY read and write, created when absent
      var access = w.B.Select(w.Cmp(IrCmpPred.Sge, mode, w.Ix(3)), w.Ix(3), mode);
      var fd = this.System(w, this.OpenCall, path, access);
      // a failed open is ERR 57, whatever DOS said - the genuine runtime maps every file failure there
      w.If(w.Cmp(IrCmpPred.Slt, fd, w.Ix(0)), () => w.B.Call(IrType.Void, this.ErrorFunction, w.Ix(57)));
      w.B.Store(w.B.Add(fd, w.Ix(1)), this.Cell(w, this.Descriptors, n));
      w.B.Store(w.Ix(0), this.Cell(w, this.Columns, n));
      w.B.Store(w.Ix(0), this.Cell(w, this.LookAhead, n));
      this.Consume(w, name);
      w.B.Ret();
    }

    private void CloseSlot(IrWriter w, IrValue n) {
      w.If(w.B.And(w.Cmp(IrCmpPred.Sge, n, w.Ix(1)), w.Cmp(IrCmpPred.Slt, n, w.Ix(FileSlots))), () => {
        var cell = this.Cell(w, this.Descriptors, n);
        var stored = w.B.Load(w.Index, cell);
        w.If(w.Cmp(IrCmpPred.Ne, stored, w.Ix(0)), () => {
          this.System(w, this.CloseCall, w.B.Sub(stored, w.Ix(1)));
          w.B.Store(w.Ix(0), cell);
          w.B.Store(w.Ix(0), this.Cell(w, this.Columns, n));
          w.B.Store(w.Ix(0), this.Cell(w, this.LookAhead, n));
        });
      });
    }

    /// <summary><c>rt.fileOut(n, bytes, length)</c>: writes to file n and keeps its column.</summary>
    private IrFunction FileOut => this._fileOut ??= this.Internal("rt.fileOut", IrType.Void, [this.Index, IrType.Ptr, this.Index], w => {
      var (n, bytes, length) = (w.Function.Parameters[0], w.Function.Parameters[1], w.Function.Parameters[2]);
      var fd = w.B.Call(w.Index, this.FileDescriptor, n);
      var column = this.Cell(w, this.Columns, n);
      var i = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), length), () => {
        w.If(w.Cmp(IrCmpPred.Eq, w.ByteAt(bytes, i.Get()), w.I8('\n')),
          () => w.B.Store(w.Ix(0), column),
          () => w.B.Store(w.B.Add(w.B.Load(w.Index, column), w.Ix(1)), column));
        i.Set(w.B.Add(i.Get(), w.Ix(1)));
      });
      this.System(w, this.WriteCall, fd, bytes, length);
      w.B.Ret();
    });

    private IrFunction WriteCall => this.Write;

    private void FilePrintZone(IrWriter w) {
      var n = w.ToIndex(w.Function.Parameters[0]);
      var space = w.Buffer(1);
      w.B.Store(w.I8(' '), space);
      var loop = w.Block("zone");
      var done = w.Block("zoned");
      w.B.Br(loop);
      w.B.Position(loop);
      w.B.Call(IrType.Void, this.FileOut, n, space, w.Ix(1));
      var column = w.B.Load(w.Index, this.Cell(w, this.Columns, n));
      w.B.CondBr(w.Cmp(IrCmpPred.Ne, w.B.Binary(IrBinaryOp.SRem, column, w.Ix(ZoneWidth)), w.Ix(0)), loop, done);
      w.B.Position(done);
      w.B.Ret();
    }

    /// <summary><c>TAB(n)</c> in a file: spaces up to column <c>n</c>, a new line first when the column is already past it.</summary>
    private void FilePrintTab(IrWriter w) {
      var n = w.ToIndex(w.Function.Parameters[0]);
      var target = w.Variable(w.Index, w.ToIndex(w.Function.Parameters[1]));
      w.If(w.Cmp(IrCmpPred.Slt, target.Get(), w.Ix(1)), () => target.Set(w.Ix(1)));
      var column = this.Cell(w, this.Columns, n);
      var character = w.Buffer(1);
      w.If(w.Cmp(IrCmpPred.Sgt, w.B.Load(w.Index, column), w.B.Sub(target.Get(), w.Ix(1))), () => {
        w.B.Store(w.I8('\n'), character);
        w.B.Call(IrType.Void, this.FileOut, n, character, w.Ix(1));
      });
      w.B.Store(w.I8(' '), character);
      w.While(() => w.Cmp(IrCmpPred.Slt, w.B.Load(w.Index, column), w.B.Sub(target.Get(), w.Ix(1))),
        () => w.B.Call(IrType.Void, this.FileOut, n, character, w.Ix(1)));
      w.B.Ret();
    }

    private IrValue FileWidened(IrWriter w, bool signed) {
      var value = w.Function.Parameters[1];
      return value.Type.Bits == 64 ? value : signed ? w.B.SExt(value, IrType.I64) : w.B.ZExt(value, IrType.I64);
    }

    private void FilePrintNumber(IrWriter w, IrValue value, IrFunction format) {
      var buffer = w.Buffer(NumberText);
      var length = w.B.Call(w.Index, format, value, buffer);
      w.SetByte(buffer, length, w.I8(' '));
      w.B.Call(IrType.Void, this.FileOut, w.ToIndex(w.Function.Parameters[0]), buffer, w.B.Add(length, w.Ix(1)));
      w.B.Ret();
    }

    /// <summary>GET$ #n, count: up to that many bytes, fewer at the end of the file.</summary>
    private void GetBytes(IrWriter w) {
      var slot = w.ToIndex(w.Function.Parameters[0]);
      var count = this.NonNegative(w, w.ToIndex(w.Function.Parameters[1]));
      var fd = w.B.Call(w.Index, this.FileDescriptor, slot);
      var handle = w.B.Call(IrType.Ptr, this.NewString, count);
      var got = w.Variable(w.Index, w.Ix(0));
      var reading = w.Variable(IrType.I1, IrBuilder.ConstBool(true));
      w.While(() => w.B.And(reading.Get(), w.Cmp(IrCmpPred.Slt, got.Get(), count)), () => {
        var next = w.B.Call(w.Index, this.ReadByte, slot, fd);
        w.If(w.Cmp(IrCmpPred.Slt, next, w.Ix(0)), () => reading.Set(IrBuilder.ConstBool(false)), () => {
          w.SetByte(Bytes(w, handle), got.Get(), w.B.Trunc(next, IrType.I8));
          got.Set(w.B.Add(got.Get(), w.Ix(1)));
        });
      });
      w.B.Store(got.Get(), handle);
      w.B.Ret(handle);
    }

    /// <summary>
    /// PUT/GET #n, record, variable: one fixed-size value at a 1-based record, or where the file is
    /// for record 0. A short read leaves zeros, not stale bytes.
    /// </summary>
    private void RecordTransfer(IrWriter w, bool write) {
      var (slot, record, value, size) = (w.ToIndex(w.Function.Parameters[0]), w.ToIndex(w.Function.Parameters[1]),
        w.Function.Parameters[2], this.NonNegative(w, w.ToIndex(w.Function.Parameters[3])));
      var fd = w.B.Call(w.Index, this.FileDescriptor, slot);
      w.If(w.Cmp(IrCmpPred.Sgt, record, w.Ix(0)), () => {
        this.System(w, this.SeekCall, fd, w.B.Mul(w.B.Sub(record, w.Ix(1)), size), w.Ix(0));
        w.B.Store(w.Ix(0), this.Cell(w, this.LookAhead, slot));
      });
      if (write) {
        this.System(w, this.WriteCall, fd, value, size);
        w.B.Ret();
        return;
      }
      var i = w.Variable(w.Index, w.Ix(0));
      var reading = w.Variable(IrType.I1, IrBuilder.ConstBool(true));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), size), () => {
        w.If(reading.Get(), () => {
          var next = w.B.Call(w.Index, this.ReadByte, slot, fd);
          w.If(w.Cmp(IrCmpPred.Slt, next, w.Ix(0)), () => reading.Set(IrBuilder.ConstBool(false)),
            () => w.SetByte(value, i.Get(), w.B.Trunc(next, IrType.I8)));
        });
        w.If(w.B.Xor(reading.Get(), IrBuilder.ConstBool(true)), () => w.SetByte(value, i.Get(), w.I8(0)));
        i.Set(w.B.Add(i.Get(), w.Ix(1)));
      });
      w.B.Ret();
    }
  }
}
