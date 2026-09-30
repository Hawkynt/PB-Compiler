using static PowerBasic.Compiler.Backend.Mos6502.M6502Op;
using Zp = PowerBasic.Compiler.Backend.Mos6502.Mos6502ZeroPage;

namespace PowerBasic.Compiler.Backend.Mos6502;

/// <summary>The routines of the 6502 runtime.</summary>
public enum M6502Routine {
  /// <summary>Program entry: saves what BASIC owns, clears storage, calls <c>main</c>, falls into <see cref="Exit"/>.</summary>
  Startup,
  /// <summary>Restores BASIC's page zero and stack and returns to it, from any depth.</summary>
  Exit,
  /// <summary>Writes the ASCII character in A, translated to PETSCII.</summary>
  PutChar,
  /// <summary><c>sys_write(bytes, length)</c>: the portable runtime's output, a new line as a carriage return.</summary>
  SystemWrite,
  /// <summary><c>sys_read(fd, buffer, length)</c>: one byte of console input through <c>CHRIN</c>; other descriptors are at their end.</summary>
  SystemRead,
  Multiply16, Multiply32, Multiply64,
  UnsignedDivide16, UnsignedDivide32, UnsignedDivide64, SignedDivide16, SignedDivide32, SignedDivide64,
  /// <summary>BASIC's run-time error in Arg, handed to the portable runtime's <c>rt_error</c>.</summary>
  Error,
  /// <summary>Copies <c>Temp</c> bytes from <c>(Ptr)</c> to <c>(Ptr2)</c>.</summary>
  CopyMemory,
  /// <summary>Pushes the frame at <c>Arg</c>, <c>Arg+2</c> bytes long, onto the soft stack.</summary>
  SaveFrame,
  /// <summary>Pops the frame at <c>Arg</c>, <c>Arg+2</c> bytes long, off the soft stack.</summary>
  RestoreFrame,

  // files on the 1541: Mos6502Runtime.Files.cs
  FileData, FileOpen, FileClose, FileRead, FileWrite, FileUnlink, FileCloseAll, FileCommandChannel, FileStatus,
  FileAppendPath,

  // floating point: Mos6502Runtime.Float.cs
  UnpackSingleA, UnpackSingleB, UnpackDoubleA, UnpackDoubleB, UnpackExtendedA, UnpackExtendedB,
  PackSingle, PackDouble, PackExtended, NormalizeA,
  FloatAdd, FloatSubtract, FloatMultiply, FloatDivide, FloatCompare,
  FloatFromSigned, FloatFromUnsigned,
  FloatToSignedTruncate, FloatToSignedRound, FloatToUnsignedTruncate, FloatToUnsignedRound,
  CopyBToA, SwapFloats, ShiftRightStickyB,
}

/// <summary>
/// The 6502's own runtime: what a portable runtime written in IR cannot be, because it is the
/// machine. Start-up and the return to BASIC, character output through the KERNAL's <c>CHROUT</c>
/// (the portable runtime's <c>sys_write</c>), multiplication and division at 16, 32 and 64 bits,
/// the soft-float routines (<c>Mos6502Runtime.Float.cs</c>), and saving frames for recursion. It is
/// assembled routine by routine as the code asks for them, so a program that never divides carries
/// no divider. Arguments arrive in <see cref="Zp.Arg"/>; results leave in <see cref="Zp.Ret"/>,
/// except division, which leaves the quotient in <see cref="Zp.Arg"/> and the remainder in
/// <see cref="Zp.Temp"/>. Every routine may change A, X, Y and the scratch cells; none touches a
/// program's own storage.
///
/// <para>
/// Start-up selects the upper- and lower-case character set, where PETSCII keeps lower-case letters
/// at <c>$41</c>-<c>$5A</c> and capitals at <c>$C1</c>-<c>$DA</c>; <see cref="M6502Routine.PutChar"/>
/// maps ASCII onto those, so <c>PRINT "Hello"</c> reads as written.
/// </para>
/// </summary>
public sealed partial class Mos6502Runtime(Mos6502Assembler asm) {

  /// <summary>The KERNAL's character output.</summary>
  public static readonly M6502Address Chrout = M6502Address.Absolute(0xFFD2);

  /// <summary>The KERNAL's character input: a line from the screen editor, a carriage return at its end.</summary>
  public static readonly M6502Address Chrin = M6502Address.Absolute(0xFFCF);

  /// <summary>The processor port: bit 0 maps the BASIC ROM in at <c>$A000</c>, or the RAM beneath it out of sight.</summary>
  private static readonly M6502Address ProcessorPort = M6502Address.Absolute(0x0001);

  /// <summary>Where recursion's frames go: the 4 KB under the I/O area, free on a C64.</summary>
  public const int SoftStackTop = 0xD000;
  public const int SoftStackBottom = 0xC000;

  private readonly Dictionary<M6502Routine, M6502Label> _labels = [];
  private readonly Queue<M6502Routine> _pending = new();
  private readonly HashSet<M6502Routine> _emitted = [];

  /// <summary>The routine's entry, which will be assembled into the program.</summary>
  public M6502Label Routine(M6502Routine routine) {
    if (this._labels.TryGetValue(routine, out var label))
      return label;
    label = asm.NewLabel("rt." + routine);
    this._labels.Add(routine, label);
    this._pending.Enqueue(routine);
    return label;
  }

  /// <summary>
  /// Assembles the start-up code: it calls <paramref name="main"/> and clears <paramref name="clearBytes"/>
  /// bytes from <paramref name="clearStart"/> first. A program that opens files (<paramref name="closeFiles"/>)
  /// closes whatever it left open on the way back to BASIC.
  /// </summary>
  public void EmitStartup(M6502Label main, M6502Address clearStart, int clearBytes, bool closeFiles = false) {
    asm.Bind(this.Routine(M6502Routine.Startup));
    this._emitted.Add(M6502Routine.Startup);
    var savedStack = asm.NewLabel("rt.savedStack");
    var savedPort = asm.NewLabel("rt.savedPort");
    var savedZeroPage = asm.NewLabel("rt.savedZeroPage");
    asm.Emit(Tsx);
    asm.Memory(Stx, savedStack);
    // the BASIC ROM out, the RAM under it in: the program may reach $C000
    asm.Memory(Lda, ProcessorPort);
    asm.Memory(Sta, savedPort);
    asm.Immediate(M6502Op.And, 0xFE);
    asm.Memory(Sta, ProcessorPort);
    this.CopyPageZero(from: M6502Address.Absolute(Zp.First), to: savedZeroPage);
    this.LoadWord(Zp.SoftStack, SoftStackTop);
    if (clearBytes > 0) {
      asm.ImmediateLow(Lda, clearStart);
      asm.Memory(Sta, Zp.Ptr);
      asm.ImmediateHigh(Lda, clearStart);
      asm.Memory(Sta, Zp.Ptr.Plus(1));
      asm.Immediate(Lda, 0);
      asm.Emit(Tay);
      var partial = asm.NewLabel("rt.clearPartial");
      if (clearBytes >= 256) {
        var page = asm.NewLabel("rt.clearPage");
        asm.Immediate(Ldx, clearBytes >> 8);
        asm.Bind(page);
        asm.IndirectY(Sta, Zp.Ptr);
        asm.Emit(Iny);
        asm.Branch(Bne, page);
        asm.Memory(Inc, Zp.Ptr.Plus(1));
        asm.Emit(Dex);
        asm.Branch(Bne, page);
      }
      asm.Bind(partial);
      if ((clearBytes & 0xFF) != 0) {
        var rest = asm.NewLabel("rt.clearRest");
        asm.Immediate(Ldx, clearBytes & 0xFF);
        asm.Bind(rest);
        asm.IndirectY(Sta, Zp.Ptr);
        asm.Emit(Iny);
        asm.Emit(Dex);
        asm.Branch(Bne, rest);
      }
    }
    asm.Immediate(Lda, 0x0E);
    asm.Call(Chrout);
    asm.Call(main);

    asm.Bind(this.Routine(M6502Routine.Exit));
    this._emitted.Add(M6502Routine.Exit);
    if (closeFiles)
      asm.Call(this.Routine(M6502Routine.FileCloseAll));
    this.CopyPageZero(from: savedZeroPage, to: M6502Address.Absolute(Zp.First));
    asm.Memory(Lda, savedPort);
    asm.Memory(Sta, ProcessorPort);
    asm.Memory(Ldx, savedStack);
    asm.Emit(Txs);
    asm.Emit(Rts);
    // the saved state is initialised data: the clear above must not wipe it
    asm.Bind(savedStack);
    asm.Bytes([0]);
    asm.Bind(savedPort);
    asm.Bytes([0]);
    asm.Bind(savedZeroPage);
    asm.Bytes(new byte[Zp.Last - Zp.First + 1]);
  }

  /// <summary>Assembles every routine asked for so far, and the ones those ask for.</summary>
  public void EmitRequested() {
    while (this._pending.TryDequeue(out var routine)) {
      if (!this._emitted.Add(routine))
        continue;
      asm.Bind(this._labels[routine]);
      this.Emit(routine);
    }
  }

  /// <summary>
  /// Copies BASIC's page-zero range, counting X up: the range is longer than 128 bytes, past where a
  /// DEX/BPL loop would stop.
  /// </summary>
  private void CopyPageZero(M6502Address from, M6502Address to) {
    var loop = asm.NewLabel("rt.copyZeroPage");
    asm.Immediate(Ldx, 0);
    asm.Bind(loop);
    asm.Memory(Lda, from, M6502Index.X);
    asm.Memory(Sta, to, M6502Index.X);
    asm.Emit(Inx);
    asm.Immediate(Cpx, Zp.Last - Zp.First + 1);
    asm.Branch(Bne, loop);
  }

  private void LoadWord(M6502Address cell, int value) {
    asm.Immediate(Lda, value & 0xFF);
    asm.Memory(Sta, cell);
    asm.Immediate(Lda, (value >> 8) & 0xFF);
    asm.Memory(Sta, cell.Plus(1));
  }

  private void Emit(M6502Routine routine) {
    switch (routine) {
      case M6502Routine.PutChar: this.EmitPutChar(); break;
      case M6502Routine.SystemWrite: this.EmitSystemWrite(); break;
      case M6502Routine.SystemRead: this.EmitSystemRead(); break;
      case M6502Routine.Multiply16: this.EmitMultiply(2); break;
      case M6502Routine.Multiply32: this.EmitMultiply(4); break;
      case M6502Routine.Multiply64: this.EmitMultiply(8); break;
      case M6502Routine.UnsignedDivide16: this.EmitUnsignedDivide(2); break;
      case M6502Routine.UnsignedDivide32: this.EmitUnsignedDivide(4); break;
      case M6502Routine.UnsignedDivide64: this.EmitUnsignedDivide(8); break;
      case M6502Routine.SignedDivide16: this.EmitSignedDivide(2); break;
      case M6502Routine.SignedDivide32: this.EmitSignedDivide(4); break;
      case M6502Routine.SignedDivide64: this.EmitSignedDivide(8); break;
      case M6502Routine.Error: this.EmitError(); break;
      case M6502Routine.CopyMemory: this.EmitCopyMemory(); break;
      case M6502Routine.SaveFrame: this.EmitSaveFrame(); break;
      case M6502Routine.RestoreFrame: this.EmitRestoreFrame(); break;
      case var floating when this.EmitFloat(floating): break;
      case var file when this.EmitFile(file): break;
      default:
        throw new InvalidOperationException($"runtime routine {routine} is emitted by the program, not on request");
    }
  }

  private void EmitPutChar() {
    var output = asm.NewLabel("rt.putChar.out");
    var capital = asm.NewLabel("rt.putChar.capital");
    asm.Immediate(Cmp, 'A');
    asm.Branch(Bcc, output);
    asm.Immediate(Cmp, 'Z' + 1);
    asm.Branch(Bcc, capital);
    asm.Immediate(Cmp, 'a');
    asm.Branch(Bcc, output);
    asm.Immediate(Cmp, 'z' + 1);
    asm.Branch(Bcs, output);
    asm.Immediate(M6502Op.And, 0xDF);
    asm.Jump(Chrout);
    asm.Bind(capital);
    asm.Immediate(Ora, 0x80);
    asm.Bind(output);
    asm.Jump(Chrout);
  }

  private void EmitSystemRead() {
    // Arg: the descriptor (4 bytes), Arg+4: the buffer, Arg+6: the length. Only the console reads,
    // one byte at a time - which is all the portable runtime asks for - PETSCII back to ASCII
    var console = asm.NewLabel("rt.systemRead.console");
    var noReturn = asm.NewLabel("rt.systemRead.notReturn");
    var capital = asm.NewLabel("rt.systemRead.capital");
    var store = asm.NewLabel("rt.systemRead.store");
    asm.Memory(Lda, Zp.Arg);
    for (var i = 1; i < 4; ++i)
      asm.Memory(Ora, Zp.Arg.Plus(i));
    asm.Branch(Beq, console);
    asm.Immediate(Lda, 0xFF);
    for (var i = 0; i < 4; ++i)
      asm.Memory(Sta, Zp.Ret.Plus(i));
    asm.Emit(Rts);
    asm.Bind(console);
    asm.Call(Chrin);
    asm.Immediate(Cmp, 13);
    asm.Branch(Bne, noReturn);
    asm.Immediate(Lda, '\n');
    asm.Jump(store);
    asm.Bind(noReturn);
    asm.Immediate(Cmp, 0xC1);
    asm.Branch(Bcs, capital);
    asm.Immediate(Cmp, 0x41);
    asm.Branch(Bcc, store);
    asm.Immediate(Cmp, 0x5B);
    asm.Branch(Bcs, store);
    asm.Immediate(Ora, 0x20);
    asm.Jump(store);
    asm.Bind(capital);
    asm.Immediate(Cmp, 0xDB);
    asm.Branch(Bcs, store);
    asm.Immediate(M6502Op.And, 0x7F);
    asm.Bind(store);
    asm.Immediate(Ldy, 0);
    asm.IndirectY(Sta, Zp.Arg.Plus(4));
    asm.Immediate(Lda, 1);
    asm.Memory(Sta, Zp.Ret);
    asm.Immediate(Lda, 0);
    for (var i = 1; i < 4; ++i)
      asm.Memory(Sta, Zp.Ret.Plus(i));
    asm.Emit(Rts);
  }

  private void EmitSystemWrite() {
    // Arg: the address; Arg+2: the length (an i32 of which the low word counts)
    var loop = asm.NewLabel("rt.systemWrite.loop");
    var done = asm.NewLabel("rt.systemWrite.done");
    var character = asm.NewLabel("rt.systemWrite.character");
    var noBorrow = asm.NewLabel("rt.systemWrite.noBorrow");
    var noCarry = asm.NewLabel("rt.systemWrite.noCarry");
    asm.Bind(loop);
    asm.Memory(Lda, Zp.Arg.Plus(2));
    asm.Memory(Ora, Zp.Arg.Plus(3));
    asm.Branch(Beq, done);
    asm.Immediate(Ldy, 0);
    asm.IndirectY(Lda, Zp.Arg);
    asm.Immediate(Cmp, '\n');
    asm.Branch(Bne, character);
    asm.Immediate(Lda, 13);
    asm.Bind(character);
    asm.Call(this.Routine(M6502Routine.PutChar));
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
    asm.Emit(Rts);
  }


  private void EmitMultiply(int bytes) {
    // shift-and-add from the multiplier's top bit: product = product * 2 + (bit ? multiplicand : 0)
    var loop = asm.NewLabel($"rt.multiply{bytes * 8}.loop");
    var skip = asm.NewLabel($"rt.multiply{bytes * 8}.skip");
    asm.Immediate(Lda, 0);
    for (var i = 0; i < bytes; ++i)
      asm.Memory(Sta, Zp.Ret.Plus(i));
    asm.Immediate(Ldx, bytes * 8);
    asm.Bind(loop);
    this.ShiftLeft(Zp.Ret, bytes);
    this.ShiftLeft(Zp.Arg, bytes);
    asm.Branch(Bcc, skip);
    asm.Emit(Clc);
    for (var i = 0; i < bytes; ++i) {
      asm.Memory(Lda, Zp.Ret.Plus(i));
      asm.Memory(Adc, Zp.ArgB.Plus(i));
      asm.Memory(Sta, Zp.Ret.Plus(i));
    }
    asm.Bind(skip);
    asm.Emit(Dex);
    asm.Branch(Bne, loop);
    asm.Emit(Rts);
  }

  private void EmitUnsignedDivide(int bytes) {
    // restoring division: Arg / ArgB -> quotient in Arg, remainder in Temp; a zero divisor is error 11
    var loop = asm.NewLabel($"rt.udivide{bytes * 8}.loop");
    var skip = asm.NewLabel($"rt.udivide{bytes * 8}.skip");
    var divisible = asm.NewLabel($"rt.udivide{bytes * 8}.divisible");
    asm.Memory(Lda, Zp.ArgB);
    for (var i = 1; i < bytes; ++i)
      asm.Memory(Ora, Zp.ArgB.Plus(i));
    asm.Branch(Bne, divisible);
    this.RaiseError(11);
    asm.Bind(divisible);
    asm.Immediate(Lda, 0);
    for (var i = 0; i < bytes; ++i)
      asm.Memory(Sta, Zp.Temp.Plus(i));
    asm.Immediate(Ldx, bytes * 8);
    asm.Bind(loop);
    this.ShiftLeft(Zp.Arg, bytes);
    for (var i = 0; i < bytes; ++i)
      asm.Memory(Rol, Zp.Temp.Plus(i));
    asm.Emit(Sec);
    for (var i = 0; i < bytes; ++i) {
      asm.Memory(Lda, Zp.Temp.Plus(i));
      asm.Memory(Sbc, Zp.ArgB.Plus(i));
      asm.Memory(Sta, Zp.Temp2.Plus(i));
    }
    asm.Branch(Bcc, skip);
    for (var i = 0; i < bytes; ++i) {
      asm.Memory(Lda, Zp.Temp2.Plus(i));
      asm.Memory(Sta, Zp.Temp.Plus(i));
    }
    asm.Memory(Inc, Zp.Arg);
    asm.Bind(skip);
    asm.Emit(Dex);
    asm.Branch(Bne, loop);
    asm.Emit(Rts);
  }

  private void EmitSignedDivide(int bytes) {
    // BASIC truncates toward zero: the quotient takes the sign of the operands' product, the
    // remainder the dividend's - so divide the magnitudes and put the signs back
    var top = bytes - 1;
    var dividendPositive = asm.NewLabel($"rt.sdivide{bytes * 8}.dividendPositive");
    var divisorPositive = asm.NewLabel($"rt.sdivide{bytes * 8}.divisorPositive");
    var quotientPositive = asm.NewLabel($"rt.sdivide{bytes * 8}.quotientPositive");
    var remainderPositive = asm.NewLabel($"rt.sdivide{bytes * 8}.remainderPositive");
    asm.Memory(Lda, Zp.Arg.Plus(top));
    asm.Memory(Sta, Zp.RemainderSign);
    asm.Memory(Eor, Zp.ArgB.Plus(top));
    asm.Memory(Sta, Zp.QuotientSign);
    asm.Memory(Lda, Zp.Arg.Plus(top));
    asm.Branch(Bpl, dividendPositive);
    this.Negate(Zp.Arg, bytes);
    asm.Bind(dividendPositive);
    asm.Memory(Lda, Zp.ArgB.Plus(top));
    asm.Branch(Bpl, divisorPositive);
    this.Negate(Zp.ArgB, bytes);
    asm.Bind(divisorPositive);
    asm.Call(this.Routine(bytes switch { 2 => M6502Routine.UnsignedDivide16, 4 => M6502Routine.UnsignedDivide32, _ => M6502Routine.UnsignedDivide64 }));
    asm.Memory(Lda, Zp.QuotientSign);
    asm.Branch(Bpl, quotientPositive);
    this.Negate(Zp.Arg, bytes);
    asm.Bind(quotientPositive);
    asm.Memory(Lda, Zp.RemainderSign);
    asm.Branch(Bpl, remainderPositive);
    this.Negate(Zp.Temp, bytes);
    asm.Bind(remainderPositive);
    asm.Emit(Rts);
  }

  /// <summary>
  /// Where the portable runtime's <c>rt_error</c> starts and where its one argument lives: its
  /// frame is static, so passing the code is a store into that cell.
  /// </summary>
  public (M6502Label Entry, M6502Address Argument)? ErrorFunction { get; set; }

  private void EmitError() {
    var (entry, argument) = this.ErrorFunction
      ?? throw new InvalidOperationException("a runtime routine raises an error, but the program has no rt_error");
    for (var i = 0; i < 4; ++i) {
      asm.Memory(Lda, Zp.Arg.Plus(i));
      asm.Memory(Sta, argument.Plus(i));
    }
    asm.Jump(entry);
  }


  private void RaiseError(int code) {
    asm.Immediate(Lda, code);
    asm.Memory(Sta, Zp.Arg);
    asm.Immediate(Lda, 0);
    for (var i = 1; i < 4; ++i)
      asm.Memory(Sta, Zp.Arg.Plus(i));
    asm.Jump(this.Routine(M6502Routine.Error));
  }

  private void EmitCopyMemory() {
    var page = asm.NewLabel("rt.copy.page");
    var partial = asm.NewLabel("rt.copy.partial");
    var rest = asm.NewLabel("rt.copy.rest");
    var done = asm.NewLabel("rt.copy.done");
    asm.Immediate(Ldy, 0);
    asm.Memory(Ldx, Zp.Temp.Plus(1));
    asm.Branch(Beq, partial);
    asm.Bind(page);
    asm.IndirectY(Lda, Zp.Ptr);
    asm.IndirectY(Sta, Zp.Ptr2);
    asm.Emit(Iny);
    asm.Branch(Bne, page);
    asm.Memory(Inc, Zp.Ptr.Plus(1));
    asm.Memory(Inc, Zp.Ptr2.Plus(1));
    asm.Emit(Dex);
    asm.Branch(Bne, page);
    asm.Bind(partial);
    asm.Memory(Ldx, Zp.Temp);
    asm.Branch(Beq, done);
    asm.Bind(rest);
    asm.IndirectY(Lda, Zp.Ptr);
    asm.IndirectY(Sta, Zp.Ptr2);
    asm.Emit(Iny);
    asm.Emit(Dex);
    asm.Branch(Bne, rest);
    asm.Bind(done);
    asm.Emit(Rts);
  }

  private void EmitSaveFrame() {
    // SoftStack -= size; copy the frame there. Running into the bottom is error 7, out of memory.
    var fits = asm.NewLabel("rt.saveFrame.fits");
    asm.Emit(Sec);
    asm.Memory(Lda, Zp.SoftStack);
    asm.Memory(Sbc, Zp.Arg.Plus(2));
    asm.Memory(Sta, Zp.SoftStack);
    asm.Memory(Sta, Zp.Ptr2);
    asm.Memory(Lda, Zp.SoftStack.Plus(1));
    asm.Memory(Sbc, Zp.Arg.Plus(3));
    asm.Memory(Sta, Zp.SoftStack.Plus(1));
    asm.Memory(Sta, Zp.Ptr2.Plus(1));
    asm.Immediate(Cmp, SoftStackBottom >> 8);
    asm.Branch(Bcs, fits);
    this.RaiseError(7);
    asm.Bind(fits);
    this.CopyArguments(frameTo: Zp.Ptr);
    asm.Jump(this.Routine(M6502Routine.CopyMemory));
  }

  private void EmitRestoreFrame() {
    asm.Memory(Lda, Zp.SoftStack);
    asm.Memory(Sta, Zp.Ptr);
    asm.Memory(Lda, Zp.SoftStack.Plus(1));
    asm.Memory(Sta, Zp.Ptr.Plus(1));
    this.CopyArguments(frameTo: Zp.Ptr2);
    asm.Call(this.Routine(M6502Routine.CopyMemory));
    asm.Emit(Clc);
    asm.Memory(Lda, Zp.SoftStack);
    asm.Memory(Adc, Zp.Arg.Plus(2));
    asm.Memory(Sta, Zp.SoftStack);
    asm.Memory(Lda, Zp.SoftStack.Plus(1));
    asm.Memory(Adc, Zp.Arg.Plus(3));
    asm.Memory(Sta, Zp.SoftStack.Plus(1));
    asm.Emit(Rts);
  }

  /// <summary>The frame address from <c>Arg</c> into <paramref name="frameTo"/>, its size into the copy count.</summary>
  private void CopyArguments(M6502Address frameTo) {
    for (var i = 0; i < 2; ++i) {
      asm.Memory(Lda, Zp.Arg.Plus(i));
      asm.Memory(Sta, frameTo.Plus(i));
      asm.Memory(Lda, Zp.Arg.Plus(2 + i));
      asm.Memory(Sta, Zp.Temp.Plus(i));
    }
  }

  private void ShiftLeft(M6502Address cell, int bytes) {
    asm.Memory(Asl, cell);
    for (var i = 1; i < bytes; ++i)
      asm.Memory(Rol, cell.Plus(i));
  }

  /// <summary>Two's-complement negation in place.</summary>
  private void Negate(M6502Address cell, int bytes) {
    asm.Emit(Sec);
    for (var i = 0; i < bytes; ++i) {
      asm.Immediate(Lda, 0);
      asm.Memory(Sbc, cell.Plus(i));
      asm.Memory(Sta, cell.Plus(i));
    }
  }

  /// <summary>Widens the value at <paramref name="cell"/> in place.</summary>
}
