using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Runtime.Portable;

/// <summary>
/// DIR$: the files a mask names, one per call. <c>DIR$(mask$)</c> starts a search and answers the
/// first match, a bare <c>DIR$</c> the next, and the empty string says there are no more. It stands
/// on <c>sys_open(path, 4)</c>, which opens a directory, and <c>sys_dirents(fd, buffer, length)</c>,
/// which fills the buffer with entries laid out as Linux's <c>getdents64</c> lays them out - the
/// record's length at byte 16, its type at 18 and the NUL-terminated name from 19 - and answers how
/// many bytes it wrote, 0 at the end. The mask matches as DOS matches it, without regard to case:
/// <c>*</c> any run of characters, <c>?</c> any one. Directories are left out unless the attribute
/// asks for them (16), as FindFirst leaves them out.
/// </summary>
public static partial class PortableRuntime {

  /// <summary>Room for the directory entries one <c>sys_dirents</c> call returns - Linux packs many, a 1541 one at a time.</summary>
  private const int DirectoryBufferBytes = 512;
  private const int MaskBytes = 80;

  private sealed partial class Definer {

    private IrFunction? _dirents, _maskMatches;
    private IrGlobalVariable? _dirFd, _dirBuffer, _dirLength, _dirPosition, _dirMask, _dirMaskLength, _dirAttribute;

    private IrFunction DirentsCall => this._dirents ??= this.Declare("sys_dirents", IrType.I32, IrType.I32, IrType.Ptr, IrType.I32);

    /// <summary>The open search's descriptor plus one - 0 when none is open.</summary>
    private IrGlobalVariable DirFd => this._dirFd ??= this.Shared(new IrGlobalVariable("rt.dir.fd", this.Index) { IsZeroInitialized = true });
    private IrGlobalVariable DirBuffer => this._dirBuffer ??= this.Shared(new IrGlobalVariable("rt.dir.buffer", IrType.I8) { Count = DirectoryBufferBytes, IsZeroInitialized = true });
    private IrGlobalVariable DirLength => this._dirLength ??= this.Shared(new IrGlobalVariable("rt.dir.length", this.Index) { IsZeroInitialized = true });
    private IrGlobalVariable DirPosition => this._dirPosition ??= this.Shared(new IrGlobalVariable("rt.dir.position", this.Index) { IsZeroInitialized = true });
    private IrGlobalVariable DirMask => this._dirMask ??= this.Shared(new IrGlobalVariable("rt.dir.mask", IrType.I8) { Count = MaskBytes, IsZeroInitialized = true });
    private IrGlobalVariable DirMaskLength => this._dirMaskLength ??= this.Shared(new IrGlobalVariable("rt.dir.maskLength", this.Index) { IsZeroInitialized = true });
    private IrGlobalVariable DirAttribute => this._dirAttribute ??= this.Shared(new IrGlobalVariable("rt.dir.attribute", this.Index) { IsZeroInitialized = true });

    private Action<IrWriter>? DirectoryRoutine(string name) => name switch {
      "rt_dir" => this.Dir,
      _ => null,
    };

    private void Dir(IrWriter w) {
      var (mask, attribute) = (w.Function.Parameters[0], w.Function.Parameters[1]);
      w.If(w.Cmp(IrCmpPred.Ne, mask, new IrNullPtr()), () => {
        this.CloseSearch(w);
        // the directory is everything up to the last separator, the mask what follows it
        var (bytes, length) = this.View(w, mask, w.Ix(1), this.Length(w, mask));
        var split = w.Variable(w.Index, w.Ix(0));
        var i = w.Variable(w.Index, w.Ix(0));
        w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), length), () => {
          var c = w.ByteAt(bytes, i.Get());
          w.If(w.B.Or(w.Cmp(IrCmpPred.Eq, c, w.I8('/')), w.B.Or(w.Cmp(IrCmpPred.Eq, c, w.I8('\\')), w.Cmp(IrCmpPred.Eq, c, w.I8(':')))),
            () => split.Set(w.B.Add(i.Get(), w.Ix(1))));
          i.Set(w.B.Add(i.Get(), w.Ix(1)));
        });
        var maskLength = w.B.Sub(length, split.Get());
        var kept = w.B.Select(w.Cmp(IrCmpPred.Sgt, maskLength, w.Ix(MaskBytes)), w.Ix(MaskBytes), maskLength);
        w.B.Call(IrType.Void, this.CopyBytes, this.DirMask, w.B.Gep(bytes, split.Get()), kept);
        w.B.Store(kept, this.DirMaskLength);
        w.B.Store(w.ToIndex(attribute), this.DirAttribute);
        var path = w.Buffer(PathBytes);
        var directoryLength = w.B.Select(w.Cmp(IrCmpPred.Sgt, split.Get(), w.Ix(PathBytes - 2)), w.Ix(PathBytes - 2), split.Get());
        w.If(w.Cmp(IrCmpPred.Eq, directoryLength, w.Ix(0)), () => {
          w.SetByte(path, w.Ix(0), w.I8('.'));
          w.SetByte(path, w.Ix(1), w.I8(0));
        }, () => {
          w.B.Call(IrType.Void, this.CopyBytes, path, bytes, directoryLength);
          w.SetByte(path, directoryLength, w.I8(0));
        });
        this.Consume(w, mask);
        var fd = w.B.Call(IrType.I32, this.OpenCall, path, w.I32(4));
        w.If(w.Cmp(IrCmpPred.Sge, fd, w.I32(0)), () => {
          w.B.Store(w.B.Add(w.ToIndex(fd), w.Ix(1)), this.DirFd);
          w.B.Store(w.Ix(0), this.DirLength);
          w.B.Store(w.Ix(0), this.DirPosition);
        });
      });
      w.If(w.Cmp(IrCmpPred.Eq, w.B.Load(this.Index, this.DirFd), w.Ix(0)), () => w.Return(new IrNullPtr()));
      // the next entry the mask takes, refilling the buffer as it runs out
      var searching = w.Variable(IrType.I1, IrBuilder.ConstBool(true));
      w.While(() => searching.Get(), () => {
        w.If(w.Cmp(IrCmpPred.Sge, w.B.Load(this.Index, this.DirPosition), w.B.Load(this.Index, this.DirLength)), () => {
          var fd = w.FromIndex(w.B.Sub(w.B.Load(this.Index, this.DirFd), w.Ix(1)), IrType.I32);
          var got = w.B.Call(IrType.I32, this.DirentsCall, fd, this.DirBuffer, w.I32(DirectoryBufferBytes));
          w.If(w.Cmp(IrCmpPred.Sle, got, w.I32(0)), () => {
            this.CloseSearch(w);
            w.Return(new IrNullPtr());
          });
          w.B.Store(w.ToIndex(got), this.DirLength);
          w.B.Store(w.Ix(0), this.DirPosition);
        });
        var record = w.B.Gep(this.DirBuffer, w.B.Load(this.Index, this.DirPosition));
        var rawLength = w.B.Load(IrType.I16, w.B.Gep(record, w.Ix(16)));
        IrValue recordLength = this.Index.Bits > 16 ? w.B.ZExt(rawLength, this.Index) : rawLength;
        w.B.Store(w.B.Add(w.B.Load(this.Index, this.DirPosition), recordLength), this.DirPosition);
        var entry = w.B.Gep(record, w.Ix(19));
        var nameLength = w.Variable(w.Index, w.Ix(0));
        w.While(() => w.Cmp(IrCmpPred.Ne, w.ByteAt(entry, nameLength.Get()), w.I8(0)), () => nameLength.Set(w.B.Add(nameLength.Get(), w.Ix(1))));
        // . and .. are never a match; a directory only when the attribute asks for directories
        var dot = w.Cmp(IrCmpPred.Eq, w.ByteAt(entry, w.Ix(0)), w.I8('.'));
        var dots = w.B.And(dot, w.B.Or(w.Cmp(IrCmpPred.Eq, nameLength.Get(), w.Ix(1)),
          w.B.And(w.Cmp(IrCmpPred.Eq, nameLength.Get(), w.Ix(2)), w.Cmp(IrCmpPred.Eq, w.ByteAt(entry, w.Ix(1)), w.I8('.')))));
        var isDirectory = w.Cmp(IrCmpPred.Eq, w.ByteAt(record, w.Ix(18)), w.I8(4));
        var wantsDirectories = w.Cmp(IrCmpPred.Ne, w.B.And(w.B.Load(this.Index, this.DirAttribute), w.Ix(16)), w.Ix(0));
        var eligible = w.B.And(w.Cmp(IrCmpPred.Eq, dots, IrBuilder.ConstBool(false)), w.B.Or(wantsDirectories, w.Cmp(IrCmpPred.Eq, isDirectory, IrBuilder.ConstBool(false))));
        w.If(eligible, () => w.If(w.B.Call(IrType.I1, this.MaskMatches, entry, nameLength.Get()),
          () => w.Return(this.Make(w, entry, nameLength.Get()))));
      });
      w.B.Ret(new IrNullPtr());
    }

    private void CloseSearch(IrWriter w) {
      var open = w.B.Load(this.Index, this.DirFd);
      w.If(w.Cmp(IrCmpPred.Ne, open, w.Ix(0)), () => {
        w.B.Call(IrType.I32, this.CloseCall, w.FromIndex(w.B.Sub(open, w.Ix(1)), IrType.I32));
        w.B.Store(w.Ix(0), this.DirFd);
      });
    }

    /// <summary>
    /// <c>rt.maskMatches(name, length)</c>: whether the search's mask takes the name, letters of
    /// either case alike. A <c>*</c> takes any run - backtracking to the last one when a later part
    /// fails - and a <c>?</c> any one character.
    /// </summary>
    private IrFunction MaskMatches => this._maskMatches ??= this.Internal("rt.maskMatches", IrType.I1, [IrType.Ptr, this.Index], w => {
      var (name, length) = (w.Function.Parameters[0], w.Function.Parameters[1]);
      var maskLength = w.B.Load(this.Index, this.DirMaskLength);
      IrValue Fold(IrValue c) => w.B.Select(w.B.And(w.Cmp(IrCmpPred.Uge, c, w.I8('a')), w.Cmp(IrCmpPred.Ule, c, w.I8('z'))),
        w.B.Sub(c, w.I8(32)), c);
      var n = w.Variable(w.Index, w.Ix(0));
      var m = w.Variable(w.Index, w.Ix(0));
      var star = w.Variable(w.Index, w.Ix(-1));
      var resume = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, n.Get(), length), () => {
        var inMask = w.Cmp(IrCmpPred.Slt, m.Get(), maskLength);
        var c = w.ByteAt(this.DirMask, w.B.Select(inMask, m.Get(), w.Ix(0)));
        w.If(w.B.And(inMask, w.Cmp(IrCmpPred.Eq, c, w.I8('*'))), () => {
          star.Set(m.Get());
          resume.Set(n.Get());
          m.Set(w.B.Add(m.Get(), w.Ix(1)));
        }, () => w.If(w.B.And(inMask, w.B.Or(w.Cmp(IrCmpPred.Eq, c, w.I8('?')),
            w.Cmp(IrCmpPred.Eq, Fold(c), Fold(w.ByteAt(name, n.Get()))))), () => {
          m.Set(w.B.Add(m.Get(), w.Ix(1)));
          n.Set(w.B.Add(n.Get(), w.Ix(1)));
        }, () => {
          // no match here: let the last star take one more character, or fail
          w.If(w.Cmp(IrCmpPred.Slt, star.Get(), w.Ix(0)), () => w.Return(IrBuilder.ConstBool(false)));
          resume.Set(w.B.Add(resume.Get(), w.Ix(1)));
          n.Set(resume.Get());
          m.Set(w.B.Add(star.Get(), w.Ix(1)));
        }));
      });
      // what is left of the mask must be stars - or ".*", so that "*.*" takes a name without a dot
      w.While(() => w.Cmp(IrCmpPred.Slt, m.Get(), maskLength), () => {
        var c = w.ByteAt(this.DirMask, m.Get());
        var dotStar = w.B.And(w.Cmp(IrCmpPred.Eq, c, w.I8('.')), w.B.And(w.Cmp(IrCmpPred.Slt, w.B.Add(m.Get(), w.Ix(1)), maskLength),
          w.Cmp(IrCmpPred.Eq, w.ByteAt(this.DirMask, w.B.Select(w.Cmp(IrCmpPred.Slt, w.B.Add(m.Get(), w.Ix(1)), maskLength), w.B.Add(m.Get(), w.Ix(1)), w.Ix(0))), w.I8('*'))));
        w.If(w.Cmp(IrCmpPred.Eq, c, w.I8('*')), () => m.Set(w.B.Add(m.Get(), w.Ix(1))),
          () => w.If(dotStar, () => m.Set(w.B.Add(m.Get(), w.Ix(2))), () => w.Return(IrBuilder.ConstBool(false))));
      });
      w.B.Ret(IrBuilder.ConstBool(true));
    });
  }
}
