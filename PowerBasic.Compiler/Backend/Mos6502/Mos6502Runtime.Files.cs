using static PowerBasic.Compiler.Backend.Mos6502.M6502Op;
using Zp = PowerBasic.Compiler.Backend.Mos6502.Mos6502ZeroPage;

namespace PowerBasic.Compiler.Backend.Mos6502;

/// <summary>
/// Files on the 1541, device 8, through the KERNAL's channel I/O. A BASIC file is a sequential file
/// (<c>,S</c>) on a logical file of its own, 2 to 14, with the same secondary address; its name goes
/// to the drive in PETSCII capitals, so <c>"out.txt"</c> and <c>"OUT.TXT"</c> are the same file,
/// as they are on DOS. <c>OPEN</c> reads <c>name,S,R</c>, writes <c>@0:name,S,W</c> (replacing) and
/// appends <c>name,S,A</c> - creating the file when there is nothing to append to, as DOS does. A
/// drive that is not there, or a status of 20 or more on the command channel, fails the open, which
/// the portable runtime reports as error 57. A channel the KERNAL refuses (<c>CHKIN</c> or
/// <c>CHKOUT</c> with carry set) is never left pointing at the keyboard or the screen instead: a
/// read finds the end of the file and a write goes nowhere. The command channel stays open while any file is used;
/// the return to BASIC closes whatever the program left open, so nothing written is lost. Sequential
/// files cannot seek, so RANDOM and BINARY - the one mode that reads and writes - live in a RAM
/// cache while they are open (<c>Mos6502Runtime.FileCache.cs</c>).
/// </summary>
public sealed partial class Mos6502Runtime {

  private static readonly M6502Address Readst = M6502Address.Absolute(0xFFB7);
  private static readonly M6502Address Setlfs = M6502Address.Absolute(0xFFBA);
  private static readonly M6502Address Setnam = M6502Address.Absolute(0xFFBD);
  private static readonly M6502Address Open = M6502Address.Absolute(0xFFC0);
  private static readonly M6502Address Close = M6502Address.Absolute(0xFFC3);
  private static readonly M6502Address Chkin = M6502Address.Absolute(0xFFC6);
  private static readonly M6502Address Chkout = M6502Address.Absolute(0xFFC9);
  private static readonly M6502Address Clrchn = M6502Address.Absolute(0xFFCC);

  private const int Device = 8, CommandChannel = 15, FirstFile = 2, LastFile = 14;

  /// <summary>The longest name sent: <c>@0:</c>, sixteen characters and <c>,S,W</c>, with room to spare.</summary>
  private const int NameBytes = 32;

  /// <summary>The cells the file routines share, laid out in <see cref="M6502Routine.FileData"/>.</summary>
  private sealed record FileCells(M6502Label InUse, M6502Label AtEnd, M6502Label CommandOpen, M6502Label File,
    M6502Label NameLength, M6502Label Name, M6502Label Status);

  private FileCells? _fileCells;

  private FileCells Files {
    get {
      if (this._fileCells is null) {
        this.Routine(M6502Routine.FileData);
        this._fileCells = new(asm.NewLabel("rt.files.inUse"), asm.NewLabel("rt.files.atEnd"), asm.NewLabel("rt.files.commandOpen"),
          asm.NewLabel("rt.files.file"), asm.NewLabel("rt.files.nameLength"), asm.NewLabel("rt.files.name"),
          asm.NewLabel("rt.files.status"));
      }
      return this._fileCells;
    }
  }

  private bool EmitFile(M6502Routine routine) {
    switch (routine) {
      case M6502Routine.FileData: this.EmitFileData(); return true;
      case M6502Routine.FileOpen: this.EmitFileOpen(); return true;
      case M6502Routine.FileClose: this.EmitFileClose(); return true;
      case M6502Routine.FileRead: this.EmitFileRead(); return true;
      case M6502Routine.FileWrite: this.EmitFileWrite(); return true;
      case M6502Routine.FileUnlink: this.EmitFileUnlink(); return true;
      case M6502Routine.FileRename: this.EmitFileRename(); return true;
      case M6502Routine.FileTruncate: this.EmitFileTruncate(); return true;
      case M6502Routine.FileDirectoryRead: this.EmitDirectoryRead(); return true;
      case M6502Routine.FileCloseAll: this.EmitFileCloseAll(); return true;
      case M6502Routine.FileCommandChannel: this.EmitFileCommandChannel(); return true;
      case M6502Routine.FileStatus: this.EmitFileStatus(); return true;
      case M6502Routine.FileAppendPath: this.EmitFileAppendPath(); return true;
      case M6502Routine.FileOpenCached: this.EmitFileOpenCached(); return true;
      case M6502Routine.FileReadCached: this.EmitFileReadCached(); return true;
      case M6502Routine.FileWriteCached: this.EmitFileWriteCached(); return true;
      case M6502Routine.FileSeek: this.EmitFileSeek(); return true;
      case M6502Routine.FileFlushCache: this.EmitFileFlushCache(); return true;
      default: return false;
    }
  }

  private void EmitFileData() {
    // initialised, so the start-up's clear leaves it alone; the exit's close-all resets it for a second RUN
    var cells = this.Files;
    asm.Bind(cells.InUse);
    asm.Bytes(new byte[16]);
    asm.Bind(cells.AtEnd);
    asm.Bytes(new byte[16]);
    foreach (var cell in (ReadOnlySpan<M6502Label>)[cells.CommandOpen, cells.File, cells.NameLength, cells.Status]) {
      asm.Bind(cell);
      asm.Bytes([0]);
    }
    asm.Bind(cells.Name);
    asm.Bytes(new byte[NameBytes]);
    if (this.FileCache is not null)
      this.EmitCacheData();
  }

  /// <summary>Sets Ret to the byte in A, zero-extended.</summary>
  private void ReturnByte() {
    asm.Memory(Sta, Zp.Ret);
    asm.Immediate(Lda, 0);
    for (var i = 1; i < 4; ++i)
      asm.Memory(Sta, Zp.Ret.Plus(i));
  }

  private void ReturnFailure() {
    asm.Immediate(Lda, 0xFF);
    for (var i = 0; i < 4; ++i)
      asm.Memory(Sta, Zp.Ret.Plus(i));
  }

  /// <summary>Appends <paramref name="text"/> to the name being built.</summary>
  private void AppendText(string text) {
    var cells = this.Files;
    asm.Memory(Ldx, cells.NameLength);
    foreach (var character in text) {
      asm.Immediate(Lda, character);
      asm.Memory(Sta, cells.Name, M6502Index.X);
      asm.Emit(Inx);
    }
    asm.Memory(Stx, cells.NameLength);
  }

  /// <summary>Appends the NUL-terminated ASCII path at <c>(Arg)</c> to the name, in PETSCII capitals.</summary>
  private void EmitFileAppendPath() {
    var cells = this.Files;
    var loop = asm.NewLabel("rt.files.appendPath.loop");
    var store = asm.NewLabel("rt.files.appendPath.store");
    var done = asm.NewLabel("rt.files.appendPath.done");
    asm.Immediate(Ldy, 0);
    asm.Memory(Ldx, cells.NameLength);
    asm.Bind(loop);
    asm.Immediate(Cpx, NameBytes - 5);        // room for the ,S,W still to come
    asm.Branch(Bcs, done);
    asm.IndirectY(Lda, Zp.Arg);
    asm.Branch(Beq, done);
    asm.Immediate(Cmp, 'a');
    asm.Branch(Bcc, store);
    asm.Immediate(Cmp, 'z' + 1);
    asm.Branch(Bcs, store);
    asm.Immediate(M6502Op.And, 0xDF);
    asm.Bind(store);
    asm.Memory(Sta, cells.Name, M6502Index.X);
    asm.Emit(Inx);
    asm.Emit(Iny);
    asm.Jump(loop);
    asm.Bind(done);
    asm.Memory(Stx, cells.NameLength);
    asm.Emit(Rts);
  }

  /// <summary>Opens channel 15 to the drive, once; a drive that is not there leaves it shut.</summary>
  private void EmitFileCommandChannel() {
    var cells = this.Files;
    var done = asm.NewLabel("rt.files.commandChannel.done");
    asm.Memory(Lda, cells.CommandOpen);
    asm.Branch(Bne, done);
    asm.Immediate(Lda, CommandChannel);
    asm.Immediate(Ldx, Device);
    asm.Immediate(Ldy, CommandChannel);
    asm.Call(Setlfs);
    asm.Immediate(Lda, 0);
    asm.Call(Setnam);
    asm.Call(Open);
    asm.Branch(Bcs, done);
    asm.Memory(Inc, cells.CommandOpen);
    asm.Bind(done);
    asm.Emit(Rts);
  }

  /// <summary>
  /// Reads the drive's status line off channel 15, which also clears it: carry set for an error -
  /// a code of 20 or more, whose first digit is 2 or higher - or when there is no command channel.
  /// </summary>
  private void EmitFileStatus() {
    var cells = this.Files;
    var loop = asm.NewLabel("rt.files.status.loop");
    var done = asm.NewLabel("rt.files.status.done");
    var failed = asm.NewLabel("rt.files.status.failed");
    asm.Memory(Lda, cells.CommandOpen);
    asm.Branch(Beq, failed);
    asm.Immediate(Ldx, CommandChannel);
    asm.Call(Chkin);
    asm.Branch(Bcs, failed);
    asm.Call(Chrin);
    asm.Memory(Sta, cells.Status);
    asm.Bind(loop);
    asm.Call(Readst);
    asm.Branch(Bne, done);
    asm.Call(Chrin);
    asm.Immediate(Cmp, 13);
    asm.Branch(Bne, loop);
    asm.Bind(done);
    asm.Call(Clrchn);
    asm.Memory(Lda, cells.Status);
    asm.Immediate(Cmp, '2');
    asm.Emit(Rts);
    asm.Bind(failed);
    asm.Call(Clrchn);
    asm.Emit(Sec);
    asm.Emit(Rts);
  }

  /// <summary><c>sys_open(path, mode)</c>: path at Arg, mode at Arg+2 (0 read, 1 write, 2 append, 3 read and write).</summary>
  private void EmitFileOpen() {
    var cells = this.Files;
    var find = asm.NewLabel("rt.files.open.find");
    var found = asm.NewLabel("rt.files.open.found");
    var attempt = asm.NewLabel("rt.files.open.attempt");
    var noPrefix = asm.NewLabel("rt.files.open.noPrefix");
    var named = asm.NewLabel("rt.files.open.named");
    var opened = asm.NewLabel("rt.files.open.opened");
    var fail = asm.NewLabel("rt.files.open.fail");
    asm.Call(this.Routine(M6502Routine.FileCommandChannel));
    asm.Memory(Lda, Zp.Arg.Plus(2));
    asm.Immediate(Cmp, 4);
    var notDirectory = asm.NewLabel("rt.files.open.notDirectory");
    asm.Branch(Bne, notDirectory);
    asm.Jump(this.DirectoryOpen);
    asm.Bind(notDirectory);
    asm.Immediate(Cmp, this.FileCache is null ? 3 : 4);
    asm.Branch(Bcs, fail);
    asm.Immediate(Ldx, FirstFile);
    asm.Bind(find);
    asm.Memory(Lda, cells.InUse, M6502Index.X);
    asm.Branch(Beq, found);
    asm.Emit(Inx);
    asm.Immediate(Cpx, LastFile + 1);
    asm.Branch(Bne, find);
    asm.Jump(fail);
    asm.Bind(found);
    asm.Memory(Stx, cells.File);
    if (this.FileCache is not null) {
      var sequential = asm.NewLabel("rt.files.open.sequential");
      asm.Memory(Lda, Zp.Arg.Plus(2));
      asm.Immediate(Cmp, 3);
      asm.Branch(Bne, sequential);
      asm.Jump(this.Routine(M6502Routine.FileOpenCached));
      asm.Bind(sequential);
    }

    asm.Bind(attempt);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, cells.NameLength);
    asm.Memory(Lda, Zp.Arg.Plus(2));
    asm.Immediate(Cmp, 1);
    asm.Branch(Bne, noPrefix);
    this.AppendText("@0:");
    asm.Bind(noPrefix);
    asm.Call(this.Routine(M6502Routine.FileAppendPath));
    var suffixes = new[] { ",S,R", ",S,W", ",S,A" };
    for (var mode = 0; mode < suffixes.Length; ++mode) {
      var next = asm.NewLabel("rt.files.open.notMode");
      asm.Memory(Lda, Zp.Arg.Plus(2));
      asm.Immediate(Cmp, mode);
      asm.Branch(Bne, next);
      this.AppendText(suffixes[mode]);
      asm.Jump(named);
      asm.Bind(next);
    }
    asm.Bind(named);
    asm.Memory(Lda, cells.File);
    asm.Immediate(Ldx, Device);
    asm.Memory(Ldy, cells.File);
    asm.Call(Setlfs);
    asm.Memory(Lda, cells.NameLength);
    asm.ImmediateLow(Ldx, cells.Name);
    asm.ImmediateHigh(Ldy, cells.Name);
    asm.Call(Setnam);
    asm.Call(Open);
    asm.Branch(Bcs, fail);
    asm.Call(this.Routine(M6502Routine.FileStatus));
    asm.Branch(Bcc, opened);
    asm.Memory(Lda, cells.File);
    asm.Call(Close);
    // nothing to append to: DOS creates the file, so write a new one
    asm.Memory(Lda, Zp.Arg.Plus(2));
    asm.Immediate(Cmp, 2);
    asm.Branch(Bne, fail);
    asm.Immediate(Lda, 1);
    asm.Memory(Sta, Zp.Arg.Plus(2));
    asm.Jump(attempt);

    asm.Bind(opened);
    asm.Memory(Ldx, cells.File);
    asm.Immediate(Lda, 1);
    asm.Memory(Sta, cells.InUse, M6502Index.X);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, cells.AtEnd, M6502Index.X);
    asm.Emit(Txa);
    this.ReturnByte();
    asm.Emit(Rts);
    asm.Bind(fail);
    this.ReturnFailure();
    asm.Emit(Rts);
    if (this._pendingDirectoryOpen)
      this.EmitDirectoryOpen();
  }

  private M6502Label? _directoryOpen;

  /// <summary>
  /// <c>sys_open(path, 4)</c>: the drive's directory, which a 1541 hands out as the BASIC listing
  /// <c>LOAD "$",8</c> would - <c>"$"</c> on secondary address 0. The path is ignored, the drive having
  /// one flat directory; the listing's load address and its first line, the disk's own name, are read
  /// past here, so what is left is a line per file.
  /// </summary>
  private M6502Label DirectoryOpen {
    get {
      if (this._directoryOpen is { } existing)
        return existing;
      var label = asm.NewLabel("rt.files.directory.open");
      this._directoryOpen = label;
      this._pendingDirectoryOpen = true;
      return label;
    }
  }

  private bool _pendingDirectoryOpen;

  /// <summary>Emits the directory open when FileOpen asked for it; FileOpen's slot search has left the file number in <c>cells.File</c>.</summary>
  private void EmitDirectoryOpen() {
    var cells = this.Files;
    var fail = asm.NewLabel("rt.files.directory.fail");
    var header = asm.NewLabel("rt.files.directory.header");
    asm.Bind(this._directoryOpen!.Value);
    // the drive's one directory is "."; any other is a directory the 1541 cannot have
    asm.Immediate(Ldy, 0);
    asm.IndirectY(Lda, Zp.Arg);
    asm.Immediate(Cmp, (byte)'.');
    asm.Branch(Bne, fail);
    asm.Emit(Iny);
    asm.IndirectY(Lda, Zp.Arg);
    asm.Branch(Bne, fail);
    // FileOpen jumped here before its slot search: find a free logical file the same way
    var find = asm.NewLabel("rt.files.directory.find");
    var found = asm.NewLabel("rt.files.directory.found");
    asm.Immediate(Ldx, FirstFile);
    asm.Bind(find);
    asm.Memory(Lda, cells.InUse, M6502Index.X);
    asm.Branch(Beq, found);
    asm.Emit(Inx);
    asm.Immediate(Cpx, LastFile + 1);
    asm.Branch(Bne, find);
    asm.Jump(fail);
    asm.Bind(found);
    asm.Memory(Stx, cells.File);
    asm.Immediate(Lda, (byte)'$');
    asm.Memory(Sta, cells.Name);
    asm.Memory(Lda, cells.File);
    asm.Immediate(Ldx, Device);
    asm.Immediate(Ldy, 0);
    asm.Call(Setlfs);
    asm.Immediate(Lda, 1);
    asm.ImmediateLow(Ldx, cells.Name);
    asm.ImmediateHigh(Ldy, cells.Name);
    asm.Call(Setnam);
    asm.Call(Open);
    asm.Branch(Bcs, fail);
    asm.Memory(Ldx, cells.File);
    asm.Call(Chkin);
    asm.Branch(Bcs, fail);
    // the load address, the header line's link and number, and its text up to the zero
    for (var i = 0; i < 6; ++i)
      asm.Call(Chrin);
    asm.Bind(header);
    asm.Call(Chrin);
    asm.Immediate(Cmp, 0);
    asm.Branch(Bne, header);
    asm.Call(Clrchn);
    asm.Memory(Ldx, cells.File);
    asm.Immediate(Lda, 1);
    asm.Memory(Sta, cells.InUse, M6502Index.X);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, cells.AtEnd, M6502Index.X);
    asm.Emit(Txa);
    this.ReturnByte();
    asm.Emit(Rts);
    asm.Bind(fail);
    asm.Call(Clrchn);
    this.ReturnFailure();
    asm.Emit(Rts);
  }

  /// <summary>
  /// <c>sys_dirents(fd, buffer, length)</c> on the 1541: the next line of the listing as one
  /// getdents64 record - its length at byte 16, the regular-file type at 18, the name between the
  /// line's quotes from 19 with a zero after it - and the record's length; 0 at the end of the
  /// listing, which is the line with no quotes, "BLOCKS FREE.". fd at Arg, the buffer at Arg+4.
  /// </summary>
  private void EmitDirectoryRead() {
    var cells = this.Files;
    var end = asm.NewLabel("rt.files.dirents.end");
    var seek = asm.NewLabel("rt.files.dirents.seek");
    var name = asm.NewLabel("rt.files.dirents.name");
    var named = asm.NewLabel("rt.files.dirents.named");
    var rest = asm.NewLabel("rt.files.dirents.rest");
    var done = asm.NewLabel("rt.files.dirents.done");
    asm.Memory(Ldx, Zp.Arg);
    asm.Memory(Lda, cells.AtEnd, M6502Index.X);
    asm.Branch(Bne, end);
    asm.Call(Chkin);
    asm.Branch(Bcs, end);
    // the link: two zeros end the listing
    asm.Call(Chrin);
    asm.Memory(Sta, Zp.Temp);
    asm.Call(Chrin);
    asm.Memory(Ora, Zp.Temp);
    asm.Branch(Beq, end);
    asm.Call(Readst);
    asm.Immediate(M6502Op.And, 0x42);
    asm.Branch(Bne, end);
    asm.Call(Chrin);                                   // the block count, which DIR$ does not answer
    asm.Call(Chrin);
    asm.Bind(seek);                                     // to the opening quote, or the line's end
    asm.Call(Chrin);
    asm.Immediate(Cmp, 0);
    asm.Branch(Beq, end);
    asm.Immediate(Cmp, (byte)'"');
    asm.Branch(Bne, seek);
    asm.Immediate(Ldy, 19);
    asm.Bind(name);
    asm.Call(Chrin);
    asm.Immediate(Cmp, (byte)'"');
    asm.Branch(Beq, named);
    asm.IndirectY(Sta, Zp.Arg.Plus(4));
    asm.Emit(Iny);
    asm.Jump(name);
    asm.Bind(named);
    asm.Immediate(Lda, 0);
    asm.IndirectY(Sta, Zp.Arg.Plus(4));
    asm.Emit(Iny);
    asm.Emit(Tya);
    asm.Memory(Sta, Zp.Ret);
    asm.Immediate(Ldy, 16);
    asm.IndirectY(Sta, Zp.Arg.Plus(4));
    asm.Immediate(Lda, 0);
    asm.Emit(Iny);
    asm.IndirectY(Sta, Zp.Arg.Plus(4));
    asm.Immediate(Lda, 8);
    asm.Emit(Iny);
    asm.IndirectY(Sta, Zp.Arg.Plus(4));
    asm.Bind(rest);                                     // the file's type and the padding, to the line's zero
    asm.Call(Chrin);
    asm.Immediate(Cmp, 0);
    asm.Branch(Bne, rest);
    asm.Call(Clrchn);
    asm.Memory(Lda, Zp.Ret);
    this.ReturnByte();
    asm.Emit(Rts);
    asm.Bind(end);
    asm.Memory(Ldx, Zp.Arg);
    asm.Immediate(Lda, 1);
    asm.Memory(Sta, cells.AtEnd, M6502Index.X);
    asm.Call(Clrchn);
    asm.Immediate(Lda, 0);
    this.ReturnByte();
    asm.Bind(done);
    asm.Emit(Rts);
  }

  /// <summary><c>sys_close(fd)</c>: fd at Arg.</summary>
  private void EmitFileClose() {
    var cells = this.Files;
    var release = asm.NewLabel("rt.files.close.release");
    if (this.FileCache is not null) {
      var sequential = asm.NewLabel("rt.files.close.sequential");
      asm.Memory(Lda, Zp.Arg);
      asm.Memory(Cmp, this.Cache.File);
      asm.Branch(Bne, sequential);
      asm.Call(this.Routine(M6502Routine.FileFlushCache));
      asm.Jump(release);
      asm.Bind(sequential);
    }
    asm.Memory(Lda, Zp.Arg);
    asm.Call(Close);
    asm.Bind(release);
    asm.Memory(Ldx, Zp.Arg);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, cells.InUse, M6502Index.X);
    this.ReturnByte();
    asm.Emit(Rts);
  }

  /// <summary>
  /// <c>sys_read(fd, buffer, length)</c> as <see cref="M6502Routine.SystemRead"/> lays it out, for a
  /// file: bytes until the length or the end, which the status marks on the last byte - so a file
  /// remembers it has ended and answers 0 from then on. Descriptor 0 is the keyboard.
  /// </summary>
  private void EmitFileRead() {
    var cells = this.Files;
    var file = asm.NewLabel("rt.files.read.file");
    var loop = asm.NewLabel("rt.files.read.loop");
    var noCarry = asm.NewLabel("rt.files.read.noCarry");
    var counted = asm.NewLabel("rt.files.read.counted");
    var ended = asm.NewLabel("rt.files.read.ended");
    var done = asm.NewLabel("rt.files.read.done");
    asm.Memory(Lda, Zp.Arg);
    asm.Branch(Bne, file);
    asm.Jump(this.Routine(M6502Routine.SystemRead));
    asm.Bind(file);
    if (this.FileCache is not null)
      this.WhenCached(Zp.Arg, this.Routine(M6502Routine.FileReadCached));
    asm.Immediate(Lda, 0);
    for (var i = 0; i < 4; ++i)
      asm.Memory(Sta, Zp.Ret.Plus(i));
    asm.Memory(Ldx, Zp.Arg);
    asm.Memory(Lda, cells.AtEnd, M6502Index.X);
    asm.Branch(Bne, done);
    asm.Call(Chkin);
    asm.Branch(Bcs, ended);
    asm.Bind(loop);
    asm.Memory(Lda, Zp.Arg.Plus(6));
    asm.Memory(Ora, Zp.Arg.Plus(7));
    asm.Branch(Beq, done);
    asm.Call(Chrin);
    asm.Immediate(Ldy, 0);
    asm.IndirectY(Sta, Zp.Arg.Plus(4));
    asm.Call(Readst);
    asm.Memory(Sta, cells.Status);
    // a timeout or a missing device: the byte is not data
    asm.Immediate(M6502Op.And, 0x82);
    asm.Branch(Bne, ended);
    asm.Memory(Inc, Zp.Arg.Plus(4));
    asm.Branch(Bne, noCarry);
    asm.Memory(Inc, Zp.Arg.Plus(5));
    asm.Bind(noCarry);
    asm.Memory(Inc, Zp.Ret);
    asm.Branch(Bne, counted);
    asm.Memory(Inc, Zp.Ret.Plus(1));
    asm.Bind(counted);
    asm.Memory(Lda, Zp.Arg.Plus(6));
    var noBorrow = asm.NewLabel("rt.files.read.noBorrow");
    asm.Branch(Bne, noBorrow);
    asm.Memory(Dec, Zp.Arg.Plus(7));
    asm.Bind(noBorrow);
    asm.Memory(Dec, Zp.Arg.Plus(6));
    asm.Memory(Lda, cells.Status);
    asm.Immediate(M6502Op.And, 0x40);
    asm.Branch(Beq, loop);
    asm.Bind(ended);
    asm.Memory(Ldx, Zp.Arg);
    asm.Immediate(Lda, 1);
    asm.Memory(Sta, cells.AtEnd, M6502Index.X);
    asm.Bind(done);
    asm.Call(Clrchn);
    asm.Emit(Rts);
  }

  /// <summary>
  /// <c>sys_write(buffer, length)</c> as <see cref="M6502Routine.SystemWrite"/> lays it out, with the
  /// descriptor at Arg+6: descriptor 1 is the screen; a file gets the bytes as they are.
  /// </summary>
  private void EmitFileWrite() {
    var file = asm.NewLabel("rt.files.write.file");
    var loop = asm.NewLabel("rt.files.write.loop");
    var noCarry = asm.NewLabel("rt.files.write.noCarry");
    var noBorrow = asm.NewLabel("rt.files.write.noBorrow");
    var done = asm.NewLabel("rt.files.write.done");
    asm.Memory(Lda, Zp.Arg.Plus(6));
    asm.Immediate(Cmp, 1);
    asm.Branch(Bne, file);
    asm.Jump(this.Routine(M6502Routine.SystemWrite));
    asm.Bind(file);
    if (this.FileCache is not null)
      this.WhenCached(Zp.Arg.Plus(6), this.Routine(M6502Routine.FileWriteCached));
    asm.Memory(Ldx, Zp.Arg.Plus(6));
    asm.Call(Chkout);
    asm.Branch(Bcs, done);
    asm.Bind(loop);
    asm.Memory(Lda, Zp.Arg.Plus(2));
    asm.Memory(Ora, Zp.Arg.Plus(3));
    asm.Branch(Beq, done);
    asm.Immediate(Ldy, 0);
    asm.IndirectY(Lda, Zp.Arg);
    asm.Call(Chrout);
    asm.Memory(Inc, Zp.Arg);
    asm.Branch(Bne, noCarry);
    asm.Memory(Inc, Zp.Arg.Plus(1));
    asm.Bind(noCarry);
    asm.Memory(Lda, Zp.Arg.Plus(2));
    asm.Branch(Bne, noBorrow);
    asm.Memory(Dec, Zp.Arg.Plus(3));
    asm.Bind(noBorrow);
    asm.Memory(Dec, Zp.Arg.Plus(2));
    asm.Jump(loop);
    asm.Bind(done);
    asm.Call(Clrchn);
    asm.Emit(Rts);
  }

  /// <summary>
  /// <c>sys_rename(old, new)</c>: <c>R0:new=old</c> printed to the command channel, as CBM DOS spells
  /// a rename - the new name first. The paths arrive at <c>Arg</c> and <c>Arg+2</c>; the reply is read
  /// to clear the channel, and a refusal answers -1.
  /// </summary>
  private void EmitFileRename() {
    var cells = this.Files;
    var send = asm.NewLabel("rt.files.rename.send");
    var sent = asm.NewLabel("rt.files.rename.sent");
    var done = asm.NewLabel("rt.files.rename.done");
    var refused = asm.NewLabel("rt.files.rename.refused");
    asm.Call(this.Routine(M6502Routine.FileCommandChannel));
    asm.Memory(Lda, cells.CommandOpen);
    asm.Branch(Beq, refused);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, cells.NameLength);
    this.AppendText("R0:");
    // the new name, from Arg+2, through the one appender: it reads (Arg)
    asm.Memory(Lda, Zp.Arg);
    asm.Emit(Pha);
    asm.Memory(Lda, Zp.Arg.Plus(1));
    asm.Emit(Pha);
    asm.Memory(Lda, Zp.Arg.Plus(2));
    asm.Memory(Sta, Zp.Arg);
    asm.Memory(Lda, Zp.Arg.Plus(3));
    asm.Memory(Sta, Zp.Arg.Plus(1));
    asm.Call(this.Routine(M6502Routine.FileAppendPath));
    asm.Emit(Pla);
    asm.Memory(Sta, Zp.Arg.Plus(1));
    asm.Emit(Pla);
    asm.Memory(Sta, Zp.Arg);
    this.AppendText("=");
    asm.Call(this.Routine(M6502Routine.FileAppendPath));
    asm.Immediate(Ldx, CommandChannel);
    asm.Call(Chkout);
    var channelRefused = asm.NewLabel("rt.files.rename.channelRefused");
    asm.Branch(Bcs, channelRefused);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, cells.Status);
    asm.Bind(send);
    asm.Memory(Ldx, cells.Status);
    asm.Memory(Cpx, cells.NameLength);
    asm.Branch(Beq, sent);
    asm.Memory(Lda, cells.Name, M6502Index.X);
    asm.Call(Chrout);
    asm.Memory(Inc, cells.Status);
    asm.Jump(send);
    asm.Bind(sent);
    asm.Immediate(Lda, 13);
    asm.Call(Chrout);
    asm.Call(Clrchn);
    // 62 FILE NOT FOUND or 63 FILE EXISTS on the command channel: the rename did not happen
    asm.Call(this.Routine(M6502Routine.FileStatus));
    asm.Branch(Bcs, refused);
    asm.Jump(done);
    asm.Bind(channelRefused);
    asm.Call(Clrchn);
    asm.Bind(refused);
    this.ReturnFailure();
    asm.Emit(Rts);
    asm.Bind(done);
    asm.Immediate(Lda, 0);
    this.ReturnByte();
    asm.Emit(Rts);
  }

  /// <summary><c>sys_unlink(path)</c>: <c>S0:name</c> printed to the command channel, whose reply is read to clear it.</summary>
  private void EmitFileUnlink() {
    var cells = this.Files;
    var send = asm.NewLabel("rt.files.unlink.send");
    var sent = asm.NewLabel("rt.files.unlink.sent");
    var done = asm.NewLabel("rt.files.unlink.done");
    asm.Call(this.Routine(M6502Routine.FileCommandChannel));
    asm.Memory(Lda, cells.CommandOpen);
    asm.Branch(Beq, done);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, cells.NameLength);
    this.AppendText("S0:");
    asm.Call(this.Routine(M6502Routine.FileAppendPath));
    asm.Immediate(Ldx, CommandChannel);
    asm.Call(Chkout);
    var refused = asm.NewLabel("rt.files.unlink.refused");
    asm.Branch(Bcs, refused);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, cells.Status);            // the index of the next byte to send
    asm.Bind(send);
    asm.Memory(Ldx, cells.Status);
    asm.Memory(Cpx, cells.NameLength);
    asm.Branch(Beq, sent);
    asm.Memory(Lda, cells.Name, M6502Index.X);
    asm.Call(Chrout);
    asm.Memory(Inc, cells.Status);
    asm.Jump(send);
    asm.Bind(sent);
    asm.Immediate(Lda, 13);
    asm.Call(Chrout);
    asm.Call(Clrchn);
    // the reply is "01, FILES SCRATCHED,nn,00": nn of 00 means there was no such file, which DOS
    // reports as file not found - so the count after the second comma is read, not just the code
    var reply = asm.NewLabel("rt.files.unlink.reply");
    var counted = asm.NewLabel("rt.files.unlink.counted");
    var none = asm.NewLabel("rt.files.unlink.none");
    asm.Immediate(Ldx, CommandChannel);
    asm.Call(Chkin);
    asm.Branch(Bcs, refused);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, Zp.Temp);                   // commas seen
    asm.Memory(Sta, Zp.Temp.Plus(1));           // the count's digits, or'ed together less '0'
    asm.Bind(reply);
    asm.Call(Readst);
    asm.Branch(Bne, counted);
    asm.Call(Chrin);
    asm.Immediate(Cmp, 13);
    asm.Branch(Beq, counted);
    asm.Immediate(Cmp, (byte)',');
    var notComma = asm.NewLabel("rt.files.unlink.notComma");
    asm.Branch(Bne, notComma);
    asm.Memory(Inc, Zp.Temp);
    asm.Jump(reply);
    asm.Bind(notComma);
    asm.Memory(Ldx, Zp.Temp);
    asm.Immediate(Cpx, 2);
    asm.Branch(Bne, reply);
    asm.Emit(Sec);
    asm.Immediate(Sbc, (byte)'0');
    asm.Memory(Ora, Zp.Temp.Plus(1));
    asm.Memory(Sta, Zp.Temp.Plus(1));
    asm.Jump(reply);
    asm.Bind(counted);
    asm.Call(Clrchn);
    asm.Memory(Lda, Zp.Temp);
    asm.Immediate(Cmp, 2);
    asm.Branch(Bcc, done);                     // no count in the reply: nothing to judge by
    asm.Memory(Lda, Zp.Temp.Plus(1));
    asm.Branch(Beq, none);
    asm.Jump(done);
    asm.Bind(none);
    this.ReturnFailure();
    asm.Emit(Rts);
    asm.Bind(refused);
    asm.Call(Clrchn);
    asm.Bind(done);
    asm.Immediate(Lda, 0);
    this.ReturnByte();
    asm.Emit(Rts);
  }

  /// <summary>Closes every file still open, then the command channel: the return to BASIC loses nothing written.</summary>
  private void EmitFileCloseAll() {
    var cells = this.Files;
    var loop = asm.NewLabel("rt.files.closeAll.loop");
    var next = asm.NewLabel("rt.files.closeAll.next");
    var done = asm.NewLabel("rt.files.closeAll.done");
    asm.Call(Clrchn);
    asm.Immediate(Lda, FirstFile);
    asm.Memory(Sta, cells.File);
    asm.Bind(loop);
    asm.Memory(Ldx, cells.File);
    asm.Memory(Lda, cells.InUse, M6502Index.X);
    asm.Branch(Beq, next);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, cells.InUse, M6502Index.X);
    asm.Emit(Txa);
    if (this.FileCache is not null) {
      var sequential = asm.NewLabel("rt.files.closeAll.sequential");
      asm.Memory(Cmp, this.Cache.File);
      asm.Branch(Bne, sequential);
      asm.Call(this.Routine(M6502Routine.FileFlushCache));
      asm.Jump(next);
      asm.Bind(sequential);
    }
    asm.Call(Close);
    asm.Bind(next);
    asm.Memory(Inc, cells.File);
    asm.Memory(Lda, cells.File);
    asm.Immediate(Cmp, LastFile + 1);
    asm.Branch(Bne, loop);
    asm.Memory(Lda, cells.CommandOpen);
    asm.Branch(Beq, done);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, cells.CommandOpen);
    asm.Immediate(Lda, CommandChannel);
    asm.Call(Close);
    asm.Bind(done);
    asm.Emit(Rts);
  }
}
