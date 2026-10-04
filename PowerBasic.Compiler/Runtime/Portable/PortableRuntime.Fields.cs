using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Runtime.Portable;

/// <summary>
/// The rest of file I/O: FIELD and the record a bare GET/PUT moves through it, GET/PUT of a string,
/// SEEK, SETEOF, NAME, MKDIR/RMDIR/CHDIR, CURDIR$ and FILEATTR's handle - with a RANDOM file
/// positioned by its record length and a BINARY one by the byte, as the DOS runtime positions them.
/// </summary>
public static partial class PortableRuntime {

  /// <summary>How many FIELD windows a program may have registered at once, over all its files.</summary>
  private const int FieldSlots = 64;

  private sealed partial class Definer {

    private IrGlobalVariable? _modes, _recordLengths, _fieldFiles, _fieldWidths, _fieldOffsets, _fieldCells, _fieldCount;
    private IrFunction? _truncate, _rename, _mkdir, _rmdir, _chdir, _getcwd;

    private IrGlobalVariable Modes => this._modes ??= this.Shared(new IrGlobalVariable("rt.fileModes", this.Index) { Count = FileSlots });
    private IrGlobalVariable RecordLengths => this._recordLengths ??= this.Shared(new IrGlobalVariable("rt.fileRecordLengths", this.Index) { Count = FileSlots });
    private IrGlobalVariable FieldFiles => this._fieldFiles ??= this.Shared(new IrGlobalVariable("rt.fieldFiles", this.Index) { Count = FieldSlots });
    private IrGlobalVariable FieldWidths => this._fieldWidths ??= this.Shared(new IrGlobalVariable("rt.fieldWidths", this.Index) { Count = FieldSlots });
    private IrGlobalVariable FieldOffsets => this._fieldOffsets ??= this.Shared(new IrGlobalVariable("rt.fieldOffsets", this.Index) { Count = FieldSlots });
    private IrGlobalVariable FieldCells => this._fieldCells ??= this.Shared(new IrGlobalVariable("rt.fieldCells", IrType.Ptr) { Count = FieldSlots });
    private IrGlobalVariable FieldCount => this._fieldCount ??= this.Shared(new IrGlobalVariable("rt.fieldCount", this.Index));

    private IrFunction TruncateCall => this._truncate ??= this.Declare("sys_truncate", IrType.I32, IrType.I32, IrType.I32);
    private IrFunction RenameCall => this._rename ??= this.Declare("sys_rename", IrType.I32, IrType.Ptr, IrType.Ptr);
    private IrFunction MakeDirectoryCall => this._mkdir ??= this.Declare("sys_mkdir", IrType.I32, IrType.Ptr, IrType.I32);
    private IrFunction RemoveDirectoryCall => this._rmdir ??= this.Declare("sys_rmdir", IrType.I32, IrType.Ptr);
    private IrFunction ChangeDirectoryCall => this._chdir ??= this.Declare("sys_chdir", IrType.I32, IrType.Ptr);
    private IrFunction CurrentDirectoryCall => this._getcwd ??= this.Declare("sys_getcwd", IrType.I32, IrType.Ptr, IrType.I32);

    /// <summary>Whether this module positions records by number, so OPEN has to keep each file's mode and record length.</summary>
    private bool TracksRecords => module.FindFunction("rt_file_put") is not null || module.FindFunction("rt_file_get") is not null
      || module.FindFunction("rt_file_setpos") is not null || module.FindFunction("rt_field_add") is not null;

    private Action<IrWriter>? FieldRoutine(string name) => name switch {
      "rt_field_add" => this.FieldAdd,
      "rt_field_get" => w => this.FieldTransfer(w, write: false),
      "rt_field_put" => w => this.FieldTransfer(w, write: true),
      "rt_file_setpos" => w => {
        var slot = w.ToIndex(w.Function.Parameters[0]);
        var fd = w.B.Call(w.Index, this.FileDescriptor, slot);
        this.Position(w, slot, fd, w.ToIndex(w.Function.Parameters[1]), w.Ix(1));
        w.B.Ret();
      },
      // PUT of a string writes its bytes; GET of one fills it in place, as long as it already is
      "rt_file_put_raw" => w => {
        var (slot, handle) = (w.ToIndex(w.Function.Parameters[0]), w.Function.Parameters[1]);
        var fd = w.B.Call(w.Index, this.FileDescriptor, slot);
        var (bytes, length) = this.View(w, handle, w.Ix(1), this.Length(w, handle));
        w.If(w.Cmp(IrCmpPred.Sgt, length, w.Ix(0)), () => this.System(w, this.WriteCall, fd, bytes, length));
        w.B.Ret();
      },
      "rt_file_get_into" => w => {
        var (slot, handle) = (w.ToIndex(w.Function.Parameters[0]), w.Function.Parameters[1]);
        var fd = w.B.Call(w.Index, this.FileDescriptor, slot);
        var (bytes, length) = this.View(w, handle, w.Ix(1), this.Length(w, handle));
        this.ReadInto(w, slot, fd, bytes, length);
        w.B.Ret();
      },
      // SETEOF: the file ends where the program is in it
      "rt_file_seteof" => w => {
        var slot = w.ToIndex(w.Function.Parameters[0]);
        var fd = w.B.Call(w.Index, this.FileDescriptor, slot);
        var here = this.System(w, this.SeekCall, fd, w.Ix(0), w.Ix(1));
        var pending = w.Cmp(IrCmpPred.Ne, w.B.Load(w.Index, this.Cell(w, this.LookAhead, slot)), w.Ix(0));
        this.System(w, this.TruncateCall, fd, w.B.Sub(here, w.B.Select(pending, w.Ix(1), w.Ix(0))));
        w.B.Ret();
      },
      // FILEATTR(n, 2): the operating system's handle for the file
      "rt_file_handle" => w => {
        var fd = w.B.Call(w.Index, this.FileDescriptor, w.ToIndex(w.Function.Parameters[0]));
        w.B.Ret(w.FromIndex(fd, w.Function.ReturnType));
      },
      "rt_rename" => w => {
        var (from, to) = (w.Function.Parameters[0], w.Function.Parameters[1]);
        var oldPath = w.B.Call(IrType.Ptr, this.PathOf, from, w.Buffer(PathBytes));
        var newPath = w.B.Call(IrType.Ptr, this.PathOf, to, w.Buffer(PathBytes));
        // a name that is not there is error 53, as DOS reports it
        var renamed = this.System(w, this.RenameCall, oldPath, newPath);
        w.If(w.Cmp(IrCmpPred.Slt, renamed, w.Ix(0)), () => this.RaiseFileError(w, renamed, oldPath, missing: 53));
        this.Consume(w, from, to);
        w.B.Ret();
      },
      // a drive that makes no directories refuses as making one that exists is refused, with 75
      "rt_mkdir" => w => this.DirectoryCall(w, path => this.System(w, this.MakeDirectoryCall, path, w.Ix(0x1ED)), refused: 75),
      // a directory that is not there is 76, path not found, for RMDIR and CHDIR alike
      "rt_rmdir" => w => this.DirectoryCall(w, path => this.System(w, this.RemoveDirectoryCall, path)),
      "rt_chdir" => w => this.DirectoryCall(w, path => this.System(w, this.ChangeDirectoryCall, path)),
      "rt_curdir" => w => {
        var buffer = w.Buffer(PathBytes);
        var got = this.System(w, this.CurrentDirectoryCall, buffer, w.Ix(PathBytes));
        w.If(w.Cmp(IrCmpPred.Sle, got, w.Ix(0)), () => w.Return(new IrNullPtr()));
        w.B.Ret(this.Make(w, buffer, this.AsciizLength(w, buffer, w.Ix(PathBytes))));
      },
      _ => null,
    };

    /// <summary>MKDIR/RMDIR/CHDIR: the path to the system, and a refusal as genuine PBC 3.50 numbers it - one that exists 75, one that does not 76.</summary>
    private void DirectoryCall(IrWriter w, Func<IrValue, IrValue> call, int? refused = null) {
      var handle = w.Function.Parameters[0];
      var path = w.B.Call(IrType.Ptr, this.PathOf, handle, w.Buffer(PathBytes));
      var result = call(path);
      w.If(w.Cmp(IrCmpPred.Slt, result, w.Ix(0)), () => this.RaiseFileError(w, result, null, missing: 76, refused));
      this.Consume(w, handle);
      w.B.Ret();
    }

    /// <summary>
    /// Moves file <paramref name="slot"/> to record <paramref name="record"/>: a BINARY file to that
    /// byte, a RANDOM one to that record of its length, anything else by <paramref name="size"/>.
    /// </summary>
    private void Position(IrWriter w, IrValue slot, IrValue fd, IrValue record, IrValue size) {
      IrValue offset = w.B.Mul(w.B.Sub(record, w.Ix(1)), size);
      if (this.TracksRecords) {
        var mode = w.B.Load(w.Index, this.Cell(w, this.Modes, slot));
        var length = w.B.Load(w.Index, this.Cell(w, this.RecordLengths, slot));
        offset = w.B.Select(w.Cmp(IrCmpPred.Eq, mode, w.Ix(4)), w.B.Sub(record, w.Ix(1)),
          w.B.Select(w.Cmp(IrCmpPred.Eq, mode, w.Ix(3)), w.B.Mul(w.B.Sub(record, w.Ix(1)), length), offset));
      }
      this.System(w, this.SeekCall, fd, offset, w.Ix(0));
      w.B.Store(w.Ix(0), this.Cell(w, this.LookAhead, slot));
    }

    /// <summary>Reads <paramref name="length"/> bytes into <paramref name="bytes"/>, zeros past the end of the file.</summary>
    private void ReadInto(IrWriter w, IrValue slot, IrValue fd, IrValue bytes, IrValue length) {
      var i = w.Variable(w.Index, w.Ix(0));
      var reading = w.Variable(IrType.I1, IrBuilder.ConstBool(true));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), length), () => {
        w.If(reading.Get(), () => {
          var next = w.B.Call(w.Index, this.ReadByte, slot, fd);
          w.If(w.Cmp(IrCmpPred.Slt, next, w.Ix(0)), () => reading.Set(IrBuilder.ConstBool(false)),
            () => w.SetByte(bytes, i.Get(), w.B.Trunc(next, IrType.I8)));
        });
        w.If(w.B.Xor(reading.Get(), IrBuilder.ConstBool(true)), () => w.SetByte(bytes, i.Get(), w.I8(0)));
        i.Set(w.B.Add(i.Get(), w.Ix(1)));
      });
    }

    /// <summary>
    /// <c>FIELD #n, width AS name$</c>: the window registered - file, width, its offset in the record
    /// and the variable's handle cell - and the variable given a blank string of the width, which is
    /// what LSET and RSET then justify within. A window follows the previous one of the same file.
    /// </summary>
    private void FieldAdd(IrWriter w) {
      var (slot, width, cell) = (w.ToIndex(w.Function.Parameters[0]), w.ToIndex(w.Function.Parameters[1]), w.Function.Parameters[2]);
      var count = w.B.Load(w.Index, this.FieldCount);
      w.If(w.Cmp(IrCmpPred.Sge, count, w.Ix(FieldSlots)), () => w.B.Call(IrType.Void, this.ErrorFunction, w.I32(7)));
      var offset = w.Variable(w.Index, w.Ix(0));
      w.If(w.Cmp(IrCmpPred.Sgt, count, w.Ix(0)), () => {
        var last = w.B.Sub(count, w.Ix(1));
        w.If(w.Cmp(IrCmpPred.Eq, w.B.Load(w.Index, this.Cell(w, this.FieldFiles, last)), slot),
          () => offset.Set(w.B.Add(w.B.Load(w.Index, this.Cell(w, this.FieldOffsets, last)), w.B.Load(w.Index, this.Cell(w, this.FieldWidths, last)))));
      });
      w.B.Store(slot, this.Cell(w, this.FieldFiles, count));
      w.B.Store(width, this.Cell(w, this.FieldWidths, count));
      w.B.Store(offset.Get(), this.Cell(w, this.FieldOffsets, count));
      w.B.Store(cell, w.B.Gep(this.FieldCells, count, IrType.Ptr));
      w.B.Store(w.B.Add(count, w.Ix(1)), this.FieldCount);
      w.B.Call(IrType.Void, this.Release, w.B.Load(IrType.Ptr, cell));
      w.B.Store(this.Filled(w, width, w.I8(' ')), cell);
      w.B.Ret();
    }

    /// <summary>
    /// A bare GET or PUT: one record of the file's length between the file and its FIELD windows -
    /// read and scattered into the variables, or gathered out of them (blank-padded) and written.
    /// </summary>
    private void FieldTransfer(IrWriter w, bool write) {
      var slot = w.ToIndex(w.Function.Parameters[0]);
      var fd = w.B.Call(w.Index, this.FileDescriptor, slot);
      var length = w.B.Load(w.Index, this.Cell(w, this.RecordLengths, slot));
      var record = w.B.Call(IrType.Ptr, this.NewString, length);
      var bytes = Bytes(w, record);
      var count = w.B.Load(w.Index, this.FieldCount);
      void ForEachField(Action<IrValue, IrValue, IrValue> body) {
        var i = w.Variable(w.Index, w.Ix(0));
        w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), count), () => {
          w.If(w.Cmp(IrCmpPred.Eq, w.B.Load(w.Index, this.Cell(w, this.FieldFiles, i.Get())), slot), () => {
            var offset = w.B.Load(w.Index, this.Cell(w, this.FieldOffsets, i.Get()));
            var width = w.B.Load(w.Index, this.Cell(w, this.FieldWidths, i.Get()));
            // a window reaching past the record is cut at its end
            var room = w.B.Sub(length, offset);
            var fits = w.B.Select(w.Cmp(IrCmpPred.Slt, width, room), width, room);
            w.If(w.Cmp(IrCmpPred.Sgt, fits, w.Ix(0)),
              () => body(w.B.Load(IrType.Ptr, w.B.Gep(this.FieldCells, i.Get(), IrType.Ptr)), offset, fits));
          });
          i.Set(w.B.Add(i.Get(), w.Ix(1)));
        });
      }
      if (write) {
        var k = w.Variable(w.Index, w.Ix(0));
        w.While(() => w.Cmp(IrCmpPred.Slt, k.Get(), length), () => {
          w.SetByte(bytes, k.Get(), w.I8(' '));
          k.Set(w.B.Add(k.Get(), w.Ix(1)));
        });
        ForEachField((cell, offset, width) => {
          var handle = w.B.Load(IrType.Ptr, cell);
          var (source, sourceLength) = this.View(w, handle, w.Ix(1), this.Length(w, handle));
          var copy = w.B.Select(w.Cmp(IrCmpPred.Slt, sourceLength, width), sourceLength, width);
          w.B.Call(IrType.Void, this.CopyBytes, w.B.Gep(bytes, offset), source, copy);
        });
        w.If(w.Cmp(IrCmpPred.Sgt, length, w.Ix(0)), () => this.System(w, this.WriteCall, fd, bytes, length));
      } else {
        this.ReadInto(w, slot, fd, bytes, length);
        ForEachField((cell, offset, width) => {
          w.B.Call(IrType.Void, this.Release, w.B.Load(IrType.Ptr, cell));
          w.B.Store(this.Make(w, w.B.Gep(bytes, offset), width), cell);
        });
      }
      this.Consume(w, record);
      w.B.Ret();
    }
  }
}
