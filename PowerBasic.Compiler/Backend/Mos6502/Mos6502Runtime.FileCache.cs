using static PowerBasic.Compiler.Backend.Mos6502.M6502Op;
using Zp = PowerBasic.Compiler.Backend.Mos6502.Mos6502ZeroPage;

namespace PowerBasic.Compiler.Backend.Mos6502;

/// <summary>
/// RANDOM and BINARY files - the mode that reads, writes and seeks - on a drive whose files only go
/// forwards: the file lives in RAM while it is open. <c>OPEN</c> reads all of it into the cache (a
/// file that is not there starts empty, and is created, as DOS creates it); reads, writes and seeks
/// work on the cache; <c>CLOSE</c>, or the return to BASIC, writes it back whole with <c>@0:</c>
/// when anything changed. The cache is <see cref="FileCacheBytes"/> long and holds one file at a
/// time: a second such file fails to open, one too long to fit fails to open, and a write past the
/// end of the cache is error 61, disk full.
/// </summary>
public sealed partial class Mos6502Runtime {

  /// <summary>The largest RANDOM or BINARY file a C64 program can open.</summary>
  public const int FileCacheBytes = 4096;

  /// <summary>
  /// The cache's <see cref="FileCacheBytes"/> of uninitialised storage, which the program reserves
  /// when it seeks at all; without it, RANDOM and BINARY files fail to open.
  /// </summary>
  public M6502Label? FileCache { get; set; }

  /// <summary>The cache's state: the logical file it holds (0 for none), its size, the position, whether it changed, and its name.</summary>
  private sealed record CacheCells(M6502Label File, M6502Label Size, M6502Label Position, M6502Label Dirty,
    M6502Label NameLength, M6502Label Name);

  private CacheCells? _cacheCells;

  private CacheCells Cache => this._cacheCells ??= new(asm.NewLabel("rt.cache.file"), asm.NewLabel("rt.cache.size"),
    asm.NewLabel("rt.cache.position"), asm.NewLabel("rt.cache.dirty"), asm.NewLabel("rt.cache.nameLength"),
    asm.NewLabel("rt.cache.name"));

  private M6502Label CacheBuffer => this.FileCache ?? throw new InvalidOperationException("the file cache was not reserved");

  private void EmitCacheData() {
    var cells = this.Cache;
    foreach (var (cell, bytes) in (ReadOnlySpan<(M6502Label, int)>)[(cells.File, 1), (cells.Size, 2), (cells.Position, 2),
        (cells.Dirty, 1), (cells.NameLength, 1), (cells.Name, NameBytes)]) {
      asm.Bind(cell);
      asm.Bytes(new byte[bytes]);
    }
  }

  /// <summary>Jumps to <paramref name="target"/> when the descriptor in <paramref name="fd"/> is the cached file.</summary>
  private void WhenCached(M6502Address fd, M6502Label target) {
    if (this.FileCache is null)
      return;
    var other = asm.NewLabel("rt.cache.notCached");
    asm.Memory(Lda, fd);
    asm.Memory(Cmp, this.Cache.File);
    asm.Branch(Bne, other);
    asm.Jump(target);
    asm.Bind(other);
  }

  /// <summary><paramref name="pointer"/> = the cache's address plus the 16-bit <paramref name="offset"/>.</summary>
  private void CacheAddress(M6502Address pointer, M6502Address offset) {
    asm.Emit(Clc);
    asm.ImmediateLow(Lda, this.CacheBuffer);
    asm.Memory(Adc, offset);
    asm.Memory(Sta, pointer);
    asm.ImmediateHigh(Lda, this.CacheBuffer);
    asm.Memory(Adc, offset.Plus(1));
    asm.Memory(Sta, pointer.Plus(1));
  }

  /// <summary>Increments the 16-bit cell at <paramref name="cell"/>.</summary>
  private void Increment16(M6502Address cell) {
    var done = asm.NewLabel("rt.cache.incremented");
    asm.Memory(Inc, cell);
    asm.Branch(Bne, done);
    asm.Memory(Inc, cell.Plus(1));
    asm.Bind(done);
  }

  private void Copy16(M6502Address from, M6502Address to) {
    for (var i = 0; i < 2; ++i) {
      asm.Memory(Lda, from.Plus(i));
      asm.Memory(Sta, to.Plus(i));
    }
  }

  /// <summary>Sets Ret to the 16-bit cell at <paramref name="cell"/>, zero-extended.</summary>
  private void Return16(M6502Address cell) {
    this.Copy16(cell, Zp.Ret);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, Zp.Ret.Plus(2));
    asm.Memory(Sta, Zp.Ret.Plus(3));
  }

  /// <summary>
  /// <c>sys_open(path, 3)</c>, from <see cref="M6502Routine.FileOpen"/> with the free logical file in
  /// its <c>File</c> cell: reads the whole file into the cache.
  /// </summary>
  private void EmitFileOpenCached() {
    var files = this.Files;
    var cache = this.Cache;
    var copyName = asm.NewLabel("rt.cache.open.copyName");
    var named = asm.NewLabel("rt.cache.open.named");
    var load = asm.NewLabel("rt.cache.open.load");
    var loaded = asm.NewLabel("rt.cache.open.loaded");
    var missing = asm.NewLabel("rt.cache.open.missing");
    var tooLong = asm.NewLabel("rt.cache.open.tooLong");
    var ready = asm.NewLabel("rt.cache.open.ready");
    var fail = asm.NewLabel("rt.cache.open.fail");
    asm.Memory(Lda, cache.File);
    asm.Branch(Bne, fail);
    // the bare name, kept for writing the file back
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, files.NameLength);
    asm.Call(this.Routine(M6502Routine.FileAppendPath));
    asm.Memory(Ldx, files.NameLength);
    asm.Memory(Stx, cache.NameLength);
    asm.Bind(copyName);
    asm.Emit(Dex);
    asm.Branch(Bmi, named);
    asm.Memory(Lda, files.Name, M6502Index.X);
    asm.Memory(Sta, cache.Name, M6502Index.X);
    asm.Jump(copyName);
    asm.Bind(named);
    this.AppendText(",S,R");
    this.OpenNamed(files.File);
    asm.Branch(Bcs, fail);
    asm.Call(this.Routine(M6502Routine.FileStatus));
    asm.Branch(Bcs, missing);

    asm.Immediate(Lda, 0);
    asm.Memory(Sta, cache.Size);
    asm.Memory(Sta, cache.Size.Plus(1));
    asm.Memory(Ldx, files.File);
    asm.Call(Chkin);
    asm.Branch(Bcs, loaded);
    asm.Bind(load);
    asm.Call(Chrin);
    asm.Emit(Pha);
    this.CacheAddress(Zp.Ptr, cache.Size);
    asm.Emit(Pla);
    asm.Immediate(Ldy, 0);
    asm.IndirectY(Sta, Zp.Ptr);
    asm.Call(Readst);
    asm.Memory(Sta, files.Status);
    asm.Immediate(M6502Op.And, 0x82);
    asm.Branch(Bne, loaded);
    this.Increment16(cache.Size);
    asm.Memory(Lda, files.Status);
    asm.Immediate(M6502Op.And, 0x40);
    asm.Branch(Bne, loaded);
    asm.Memory(Lda, cache.Size.Plus(1));
    asm.Immediate(Cmp, FileCacheBytes >> 8);
    asm.Branch(Beq, tooLong);
    asm.Jump(load);
    asm.Bind(loaded);
    asm.Call(Clrchn);
    asm.Memory(Lda, files.File);
    asm.Call(Close);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, cache.Dirty);
    asm.Jump(ready);

    asm.Bind(missing);
    // DOS creates a RANDOM or BINARY file that is not there: an empty one, to be written at the close
    asm.Memory(Lda, files.File);
    asm.Call(Close);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, cache.Size);
    asm.Memory(Sta, cache.Size.Plus(1));
    asm.Immediate(Lda, 1);
    asm.Memory(Sta, cache.Dirty);

    asm.Bind(ready);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, cache.Position);
    asm.Memory(Sta, cache.Position.Plus(1));
    asm.Memory(Ldx, files.File);
    asm.Memory(Stx, cache.File);
    asm.Immediate(Lda, 1);
    asm.Memory(Sta, files.InUse, M6502Index.X);
    asm.Emit(Txa);
    this.ReturnByte();
    asm.Emit(Rts);

    asm.Bind(tooLong);
    asm.Call(Clrchn);
    asm.Memory(Lda, files.File);
    asm.Call(Close);
    asm.Bind(fail);
    this.ReturnFailure();
    asm.Emit(Rts);
  }

  /// <summary><c>SETLFS file, 8, file</c>, <c>SETNAM</c> with the name built so far, and <c>OPEN</c>: carry set when it fails.</summary>
  private void OpenNamed(M6502Address file) {
    var files = this.Files;
    asm.Memory(Lda, file);
    asm.Immediate(Ldx, Device);
    asm.Memory(Ldy, file);
    asm.Call(Setlfs);
    asm.Memory(Lda, files.NameLength);
    asm.ImmediateLow(Ldx, files.Name);
    asm.ImmediateHigh(Ldy, files.Name);
    asm.Call(Setnam);
    asm.Call(Open);
  }

  /// <summary><c>sys_read</c> on the cached file: up to the length, from the position, never past the end.</summary>
  private void EmitFileReadCached() {
    var cache = this.Cache;
    var some = asm.NewLabel("rt.cache.read.some");
    var counted = asm.NewLabel("rt.cache.read.counted");
    // Temp = size - position, or nothing when the position is at or past the end
    asm.Emit(Sec);
    asm.Memory(Lda, cache.Size);
    asm.Memory(Sbc, cache.Position);
    asm.Memory(Sta, Zp.Temp);
    asm.Memory(Lda, cache.Size.Plus(1));
    asm.Memory(Sbc, cache.Position.Plus(1));
    asm.Memory(Sta, Zp.Temp.Plus(1));
    asm.Branch(Bcs, some);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, Zp.Temp);
    asm.Memory(Sta, Zp.Temp.Plus(1));
    asm.Bind(some);
    // no more than was asked for (a length past 16 bits asks for everything)
    asm.Memory(Lda, Zp.Arg.Plus(8));
    asm.Memory(Ora, Zp.Arg.Plus(9));
    asm.Branch(Bne, counted);
    asm.Memory(Lda, Zp.Arg.Plus(6));
    asm.Memory(Cmp, Zp.Temp);
    asm.Memory(Lda, Zp.Arg.Plus(7));
    asm.Memory(Sbc, Zp.Temp.Plus(1));
    asm.Branch(Bcs, counted);
    this.Copy16(Zp.Arg.Plus(6), Zp.Temp);
    asm.Bind(counted);
    this.Return16(Zp.Temp);
    this.CacheAddress(Zp.Ptr, cache.Position);
    this.Copy16(Zp.Arg.Plus(4), Zp.Ptr2);
    asm.Emit(Clc);
    asm.Memory(Lda, cache.Position);
    asm.Memory(Adc, Zp.Temp);
    asm.Memory(Sta, cache.Position);
    asm.Memory(Lda, cache.Position.Plus(1));
    asm.Memory(Adc, Zp.Temp.Plus(1));
    asm.Memory(Sta, cache.Position.Plus(1));
    asm.Jump(this.Routine(M6502Routine.CopyMemory));
  }

  /// <summary>
  /// <c>sys_write</c> on the cached file: the bytes at the position, a gap before it - left by a
  /// seek past the end - filled with zeros, and the file as long as the furthest byte written.
  /// </summary>
  private void EmitFileWriteCached() {
    var cache = this.Cache;
    var fill = asm.NewLabel("rt.cache.write.fill");
    var filled = asm.NewLabel("rt.cache.write.filled");
    var full = asm.NewLabel("rt.cache.write.full");
    var longer = asm.NewLabel("rt.cache.write.longer");
    var done = asm.NewLabel("rt.cache.write.done");
    // Temp2 = position + length, which must stay within the cache
    asm.Memory(Lda, Zp.Arg.Plus(4));
    asm.Memory(Ora, Zp.Arg.Plus(5));
    asm.Branch(Bne, full);
    asm.Emit(Clc);
    asm.Memory(Lda, cache.Position);
    asm.Memory(Adc, Zp.Arg.Plus(2));
    asm.Memory(Sta, Zp.Temp2);
    asm.Memory(Lda, cache.Position.Plus(1));
    asm.Memory(Adc, Zp.Arg.Plus(3));
    asm.Memory(Sta, Zp.Temp2.Plus(1));
    asm.Branch(Bcs, full);
    asm.Immediate(Lda, 0);
    asm.Memory(Cmp, Zp.Temp2);
    asm.Immediate(Lda, FileCacheBytes >> 8);
    asm.Memory(Sbc, Zp.Temp2.Plus(1));
    asm.Branch(Bcc, full);
    // zeros from the end up to the position
    asm.Bind(fill);
    asm.Memory(Lda, cache.Size);
    asm.Memory(Cmp, cache.Position);
    asm.Memory(Lda, cache.Size.Plus(1));
    asm.Memory(Sbc, cache.Position.Plus(1));
    asm.Branch(Bcs, filled);
    this.CacheAddress(Zp.Ptr, cache.Size);
    asm.Immediate(Lda, 0);
    asm.Emit(Tay);
    asm.IndirectY(Sta, Zp.Ptr);
    this.Increment16(cache.Size);
    asm.Jump(fill);
    asm.Bind(filled);
    this.Copy16(Zp.Arg, Zp.Ptr);
    this.CacheAddress(Zp.Ptr2, cache.Position);
    this.Copy16(Zp.Arg.Plus(2), Zp.Temp);
    asm.Call(this.Routine(M6502Routine.CopyMemory));
    this.Copy16(Zp.Temp2, cache.Position);
    asm.Immediate(Lda, 1);
    asm.Memory(Sta, cache.Dirty);
    asm.Memory(Lda, cache.Size);
    asm.Memory(Cmp, Zp.Temp2);
    asm.Memory(Lda, cache.Size.Plus(1));
    asm.Memory(Sbc, Zp.Temp2.Plus(1));
    asm.Branch(Bcc, longer);
    asm.Jump(done);
    asm.Bind(longer);
    this.Copy16(Zp.Temp2, cache.Size);
    asm.Bind(done);
    asm.Emit(Rts);
    asm.Bind(full);
    this.RaiseError(61);
  }

  /// <summary>
  /// <c>sys_seek(fd, offset, whence)</c>: fd at Arg, the offset's low word at Arg+2, whence (0 the
  /// start, 1 the position, 2 the end) at Arg+4; answers the new position, or -1 for a file that
  /// cannot seek. A position before the start is the start.
  /// </summary>
  private void EmitFileSeek() {
    var cache = this.Cache;
    var fromPosition = asm.NewLabel("rt.cache.seek.fromPosition");
    var fromEnd = asm.NewLabel("rt.cache.seek.fromEnd");
    var add = asm.NewLabel("rt.cache.seek.add");
    var placed = asm.NewLabel("rt.cache.seek.placed");
    var fail = asm.NewLabel("rt.cache.seek.fail");
    asm.Memory(Lda, cache.File);
    asm.Branch(Beq, fail);
    asm.Memory(Cmp, Zp.Arg);
    asm.Branch(Bne, fail);
    asm.Memory(Lda, Zp.Arg.Plus(4));
    asm.Immediate(Cmp, 1);
    asm.Branch(Beq, fromPosition);
    asm.Immediate(Cmp, 2);
    asm.Branch(Beq, fromEnd);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, Zp.Temp);
    asm.Memory(Sta, Zp.Temp.Plus(1));
    asm.Jump(add);
    asm.Bind(fromPosition);
    this.Copy16(cache.Position, Zp.Temp);
    asm.Jump(add);
    asm.Bind(fromEnd);
    this.Copy16(cache.Size, Zp.Temp);
    asm.Bind(add);
    asm.Emit(Clc);
    asm.Memory(Lda, Zp.Temp);
    asm.Memory(Adc, Zp.Arg.Plus(2));
    asm.Memory(Sta, cache.Position);
    asm.Memory(Lda, Zp.Temp.Plus(1));
    asm.Memory(Adc, Zp.Arg.Plus(3));
    asm.Memory(Sta, cache.Position.Plus(1));
    asm.Branch(Bpl, placed);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, cache.Position);
    asm.Memory(Sta, cache.Position.Plus(1));
    asm.Bind(placed);
    this.Return16(cache.Position);
    asm.Emit(Rts);
    asm.Bind(fail);
    this.ReturnFailure();
    asm.Emit(Rts);
  }

  /// <summary>Writes the cached file back whole when it changed, and lets it go.</summary>
  private void EmitFileFlushCache() {
    var files = this.Files;
    var cache = this.Cache;
    var copyName = asm.NewLabel("rt.cache.flush.copyName");
    var named = asm.NewLabel("rt.cache.flush.named");
    var loop = asm.NewLabel("rt.cache.flush.loop");
    var written = asm.NewLabel("rt.cache.flush.written");
    var shut = asm.NewLabel("rt.cache.flush.shut");
    var done = asm.NewLabel("rt.cache.flush.done");
    asm.Memory(Lda, cache.Dirty);
    asm.Branch(Beq, done);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, files.NameLength);
    this.AppendText("@0:");
    asm.Immediate(Ldy, 0);
    asm.Memory(Ldx, files.NameLength);
    asm.Bind(copyName);
    asm.Memory(Cpy, cache.NameLength);
    asm.Branch(Beq, named);
    asm.Memory(Lda, cache.Name, M6502Index.Y);
    asm.Memory(Sta, files.Name, M6502Index.X);
    asm.Emit(Inx);
    asm.Emit(Iny);
    asm.Jump(copyName);
    asm.Bind(named);
    asm.Memory(Stx, files.NameLength);
    this.AppendText(",S,W");
    this.OpenNamed(cache.File);
    asm.Branch(Bcs, done);
    asm.Call(this.Routine(M6502Routine.FileStatus));
    asm.Branch(Bcs, shut);
    asm.Memory(Ldx, cache.File);
    asm.Call(Chkout);
    asm.Branch(Bcs, shut);
    asm.ImmediateLow(Lda, this.CacheBuffer);
    asm.Memory(Sta, Zp.Ptr);
    asm.ImmediateHigh(Lda, this.CacheBuffer);
    asm.Memory(Sta, Zp.Ptr.Plus(1));
    this.Copy16(cache.Size, Zp.Temp);
    asm.Bind(loop);
    asm.Memory(Lda, Zp.Temp);
    asm.Memory(Ora, Zp.Temp.Plus(1));
    asm.Branch(Beq, written);
    asm.Immediate(Ldy, 0);
    asm.IndirectY(Lda, Zp.Ptr);
    asm.Call(Chrout);
    this.Increment16(Zp.Ptr);
    asm.Memory(Lda, Zp.Temp);
    var noBorrow = asm.NewLabel("rt.cache.flush.noBorrow");
    asm.Branch(Bne, noBorrow);
    asm.Memory(Dec, Zp.Temp.Plus(1));
    asm.Bind(noBorrow);
    asm.Memory(Dec, Zp.Temp);
    asm.Jump(loop);
    asm.Bind(written);
    asm.Bind(shut);
    asm.Call(Clrchn);
    asm.Memory(Lda, cache.File);
    asm.Call(Close);
    asm.Bind(done);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, cache.File);
    asm.Memory(Sta, cache.Dirty);
    asm.Emit(Rts);
  }
}
