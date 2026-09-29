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
  PrintNewLine,
  PrintString,
  PrintI8, PrintI16, PrintI32, PrintU8, PrintU16, PrintU32,
  /// <summary>The comma separator: spaces to the next 14-column zone, at least one.</summary>
  PrintZone,
  /// <summary><c>TAB(n)</c>: a new line if the column is past n, then spaces up to it.</summary>
  PrintTab,
  /// <summary><c>SPC(n)</c>.</summary>
  PrintSpaces,
  /// <summary>The digits of the unsigned 32-bit <c>Arg</c>, no padding.</summary>
  PrintDigits,
  Multiply16, Multiply32,
  UnsignedDivide16, UnsignedDivide32, SignedDivide16, SignedDivide32,
  /// <summary><c>rt_error(code)</c>: reports a BASIC run-time error and ends the program.</summary>
  Error,
  /// <summary><c>rt_end(code)</c>.</summary>
  End,
  /// <summary>Copies <c>Temp</c> bytes from <c>(Ptr)</c> to <c>(Ptr2)</c>.</summary>
  CopyMemory,
  /// <summary>Pushes the frame at <c>Arg</c>, <c>Arg+2</c> bytes long, onto the soft stack.</summary>
  SaveFrame,
  /// <summary>Pops the frame at <c>Arg</c>, <c>Arg+2</c> bytes long, off the soft stack.</summary>
  RestoreFrame,
  /// <summary>Answers zero: a C64 has no I/O ports for <c>INP</c> to read.</summary>
  ReturnZero,
  /// <summary>Does nothing: <c>OUT</c> has no port to write.</summary>
  Nothing,
}

/// <summary>
/// The 6502 runtime, assembled into the program routine by routine as the code asks for them, so a
/// program that never divides carries no divider. Arguments arrive in <see cref="Zp.Arg"/>, laid out
/// one after another at their own widths; results leave in <see cref="Zp.Ret"/>, except division,
/// which leaves the quotient in <see cref="Zp.Arg"/> and the remainder in <see cref="Zp.Temp"/>.
/// Every routine may change A, X, Y and the scratch cells; none touches a program's own storage.
///
/// <para>
/// Output goes through the KERNAL's <c>CHROUT</c> at <c>$FFD2</c>. Start-up selects the upper- and
/// lower-case character set, where PETSCII keeps lower-case letters at <c>$41</c>-<c>$5A</c> and
/// capitals at <c>$C1</c>-<c>$DA</c>; <see cref="M6502Routine.PutChar"/> maps ASCII onto those, so
/// <c>PRINT "Hello"</c> reads as written.
/// </para>
/// </summary>
public sealed class Mos6502Runtime(Mos6502Assembler asm) {

  /// <summary>The KERNAL's character output.</summary>
  public static readonly M6502Address Chrout = M6502Address.Absolute(0xFFD2);

  /// <summary>Where recursion's frames go: the 4 KB under the I/O area, free on a C64.</summary>
  public const int SoftStackTop = 0xD000;
  public const int SoftStackBottom = 0xC000;

  /// <summary>The width of the C64's text screen, where output wraps.</summary>
  public const int ScreenColumns = 40;

  /// <summary>BASIC's print zone: the comma separator's tab stop.</summary>
  public const int ZoneWidth = 14;

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
  /// bytes from <paramref name="clearStart"/> first.
  /// </summary>
  public void EmitStartup(M6502Label main, M6502Address clearStart, int clearBytes) {
    asm.Bind(this.Routine(M6502Routine.Startup));
    this._emitted.Add(M6502Routine.Startup);
    var savedStack = asm.NewLabel("rt.savedStack");
    var savedZeroPage = asm.NewLabel("rt.savedZeroPage");
    asm.Emit(Tsx);
    asm.Memory(Stx, savedStack);
    this.CopyPageZero(from: M6502Address.Absolute(Zp.First), to: savedZeroPage);
    this.LoadWord(Zp.SoftStack, SoftStackTop);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, Zp.Column);
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
        asm.IndirectY(Sta, 0x02);
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
        asm.IndirectY(Sta, 0x02);
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
    this.CopyPageZero(from: savedZeroPage, to: M6502Address.Absolute(Zp.First));
    asm.Memory(Ldx, savedStack);
    asm.Emit(Txs);
    asm.Emit(Rts);
    // the saved state is initialised data: the clear above must not wipe it
    asm.Bind(savedStack);
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

  private void CopyPageZero(M6502Address from, M6502Address to) {
    var loop = asm.NewLabel("rt.copyZeroPage");
    asm.Immediate(Ldx, Zp.Last - Zp.First);
    asm.Bind(loop);
    asm.Memory(Lda, from, M6502Index.X);
    asm.Memory(Sta, to, M6502Index.X);
    asm.Emit(Dex);
    asm.Branch(Bpl, loop);
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
      case M6502Routine.PrintNewLine:
        asm.Immediate(Lda, 13);
        asm.Jump(this.Routine(M6502Routine.PutChar));
        break;
      case M6502Routine.PrintString: this.EmitPrintString(); break;
      case M6502Routine.PrintI8:
        this.Extend(Zp.Arg, from: 1, to: 4, signed: true);
        asm.Jump(this.Routine(M6502Routine.PrintI32));
        break;
      case M6502Routine.PrintI16:
        this.Extend(Zp.Arg, from: 2, to: 4, signed: true);
        asm.Jump(this.Routine(M6502Routine.PrintI32));
        break;
      case M6502Routine.PrintI32: this.EmitPrintSigned(); break;
      case M6502Routine.PrintU8:
        this.Extend(Zp.Arg, from: 1, to: 4, signed: false);
        asm.Jump(this.Routine(M6502Routine.PrintU32));
        break;
      case M6502Routine.PrintU16:
        this.Extend(Zp.Arg, from: 2, to: 4, signed: false);
        asm.Jump(this.Routine(M6502Routine.PrintU32));
        break;
      case M6502Routine.PrintU32:
        asm.Immediate(Lda, ' ');
        asm.Call(this.Routine(M6502Routine.PutChar));
        asm.Call(this.Routine(M6502Routine.PrintDigits));
        asm.Immediate(Lda, ' ');
        asm.Jump(this.Routine(M6502Routine.PutChar));
        break;
      case M6502Routine.PrintDigits: this.EmitPrintDigits(); break;
      case M6502Routine.PrintZone: this.EmitPrintZone(); break;
      case M6502Routine.PrintTab: this.EmitPrintTab(); break;
      case M6502Routine.PrintSpaces: this.EmitPrintSpaces(); break;
      case M6502Routine.Multiply16: this.EmitMultiply(2); break;
      case M6502Routine.Multiply32: this.EmitMultiply(4); break;
      case M6502Routine.UnsignedDivide16: this.EmitUnsignedDivide(2); break;
      case M6502Routine.UnsignedDivide32: this.EmitUnsignedDivide(4); break;
      case M6502Routine.SignedDivide16: this.EmitSignedDivide(2); break;
      case M6502Routine.SignedDivide32: this.EmitSignedDivide(4); break;
      case M6502Routine.Error: this.EmitError(); break;
      case M6502Routine.End: asm.Jump(this.Routine(M6502Routine.Exit)); break;
      case M6502Routine.CopyMemory: this.EmitCopyMemory(); break;
      case M6502Routine.SaveFrame: this.EmitSaveFrame(); break;
      case M6502Routine.RestoreFrame: this.EmitRestoreFrame(); break;
      case M6502Routine.ReturnZero:
        asm.Immediate(Lda, 0);
        for (var i = 0; i < 4; ++i)
          asm.Memory(Sta, Zp.Ret.Plus(i));
        asm.Emit(Rts);
        break;
      case M6502Routine.Nothing: asm.Emit(Rts); break;
      default:
        throw new InvalidOperationException($"runtime routine {routine} is emitted by the program, not on request");
    }
  }

  private void EmitPutChar() {
    // the column first: a carriage return starts a line, and the screen wraps at forty
    var translate = asm.NewLabel("rt.putChar.translate");
    var counted = asm.NewLabel("rt.putChar.counted");
    var output = asm.NewLabel("rt.putChar.out");
    var capital = asm.NewLabel("rt.putChar.capital");
    asm.Emit(Pha);
    asm.Immediate(Cmp, 13);
    asm.Branch(Bne, counted);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, Zp.Column);
    asm.Jump(translate);
    asm.Bind(counted);
    asm.Memory(Inc, Zp.Column);
    asm.Memory(Lda, Zp.Column);
    asm.Immediate(Cmp, ScreenColumns);
    asm.Branch(Bne, translate);
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, Zp.Column);
    asm.Bind(translate);
    asm.Emit(Pla);
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

  private void EmitPrintZone() {
    var loop = asm.NewLabel("rt.printZone.loop");
    var modulo = asm.NewLabel("rt.printZone.modulo");
    var remainder = asm.NewLabel("rt.printZone.remainder");
    asm.Bind(loop);
    asm.Immediate(Lda, ' ');
    asm.Call(this.Routine(M6502Routine.PutChar));
    asm.Memory(Lda, Zp.Column);
    asm.Bind(modulo);
    asm.Immediate(Cmp, ZoneWidth);
    asm.Branch(Bcc, remainder);
    asm.Immediate(Sbc, ZoneWidth);
    asm.Jump(modulo);
    asm.Bind(remainder);
    asm.Immediate(Cmp, 0);
    asm.Branch(Bne, loop);
    asm.Emit(Rts);
  }

  private void EmitPrintTab() {
    // Arg: the 1-based column, an i32; one below 1 counts as 1, one past the screen as its last
    var clamped = asm.NewLabel("rt.printTab.clamped");
    var inRange = asm.NewLabel("rt.printTab.inRange");
    var tooFar = asm.NewLabel("rt.printTab.tooFar");
    var back = asm.NewLabel("rt.printTab.back");
    var forward = asm.NewLabel("rt.printTab.forward");
    var done = asm.NewLabel("rt.printTab.done");
    asm.Memory(Lda, Zp.Arg.Plus(3));
    asm.Branch(Bmi, clamped);
    asm.Memory(Ora, Zp.Arg.Plus(2));
    asm.Memory(Ora, Zp.Arg.Plus(1));
    asm.Branch(Bne, tooFar);
    asm.Memory(Lda, Zp.Arg);
    asm.Branch(Beq, clamped);
    asm.Immediate(Cmp, ScreenColumns + 1);
    asm.Branch(Bcc, inRange);
    asm.Bind(tooFar);
    asm.Immediate(Lda, ScreenColumns);
    asm.Memory(Sta, Zp.Arg);
    asm.Jump(inRange);
    asm.Bind(clamped);
    asm.Immediate(Lda, 1);
    asm.Memory(Sta, Zp.Arg);
    asm.Bind(inRange);
    asm.Memory(Dec, Zp.Arg);
    asm.Bind(back);
    asm.Memory(Lda, Zp.Column);
    asm.Memory(Cmp, Zp.Arg);
    asm.Branch(Beq, done);
    asm.Branch(Bcc, forward);
    asm.Call(this.Routine(M6502Routine.PrintNewLine));
    asm.Jump(back);
    asm.Bind(forward);
    asm.Immediate(Lda, ' ');
    asm.Call(this.Routine(M6502Routine.PutChar));
    asm.Jump(back);
    asm.Bind(done);
    asm.Emit(Rts);
  }

  private void EmitPrintSpaces() {
    // Arg: the count, an i32; zero or less prints nothing
    var loop = asm.NewLabel("rt.printSpaces.loop");
    var noBorrow = asm.NewLabel("rt.printSpaces.noBorrow");
    var done = asm.NewLabel("rt.printSpaces.done");
    asm.Memory(Lda, Zp.Arg.Plus(3));
    asm.Branch(Bmi, done);
    asm.Bind(loop);
    asm.Memory(Lda, Zp.Arg);
    asm.Memory(Ora, Zp.Arg.Plus(1));
    asm.Branch(Beq, done);
    asm.Immediate(Lda, ' ');
    asm.Call(this.Routine(M6502Routine.PutChar));
    asm.Memory(Lda, Zp.Arg);
    asm.Branch(Bne, noBorrow);
    asm.Memory(Dec, Zp.Arg.Plus(1));
    asm.Bind(noBorrow);
    asm.Memory(Dec, Zp.Arg);
    asm.Jump(loop);
    asm.Bind(done);
    asm.Emit(Rts);
  }

  private void EmitPrintString() {
    // Arg: the address; Arg+2: the length (an i32 of which the low word counts)
    var loop = asm.NewLabel("rt.printString.loop");
    var done = asm.NewLabel("rt.printString.done");
    var noBorrow = asm.NewLabel("rt.printString.noBorrow");
    var noCarry = asm.NewLabel("rt.printString.noCarry");
    asm.Bind(loop);
    asm.Memory(Lda, Zp.Arg.Plus(2));
    asm.Memory(Ora, Zp.Arg.Plus(3));
    asm.Branch(Beq, done);
    asm.Immediate(Ldy, 0);
    asm.IndirectY(Lda, 0x06);
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

  private void EmitPrintSigned() {
    // BASIC's number: a sign slot (a space or the minus), the digits, a trailing space
    var positive = asm.NewLabel("rt.printSigned.positive");
    var digits = asm.NewLabel("rt.printSigned.digits");
    asm.Memory(Lda, Zp.Arg.Plus(3));
    asm.Branch(Bpl, positive);
    this.Negate(Zp.Arg, 4);
    asm.Immediate(Lda, '-');
    asm.Jump(digits);
    asm.Bind(positive);
    asm.Immediate(Lda, ' ');
    asm.Bind(digits);
    asm.Call(this.Routine(M6502Routine.PutChar));
    asm.Call(this.Routine(M6502Routine.PrintDigits));
    asm.Immediate(Lda, ' ');
    asm.Jump(this.Routine(M6502Routine.PutChar));
  }

  private void EmitPrintDigits() {
    // divide by ten until nothing is left, stacking the remainders; then print them back out
    var count = Zp.Temp.Plus(7);
    var divide = asm.NewLabel("rt.printDigits.divide");
    var bit = asm.NewLabel("rt.printDigits.bit");
    var smaller = asm.NewLabel("rt.printDigits.smaller");
    var print = asm.NewLabel("rt.printDigits.print");
    asm.Immediate(Lda, 0);
    asm.Memory(Sta, count);
    asm.Bind(divide);
    asm.Immediate(Ldx, 32);
    asm.Immediate(Lda, 0);
    asm.Bind(bit);
    asm.Memory(Asl, Zp.Arg);
    for (var i = 1; i < 4; ++i)
      asm.Memory(Rol, Zp.Arg.Plus(i));
    asm.EmitAccumulator(Rol);
    asm.Immediate(Cmp, 10);
    asm.Branch(Bcc, smaller);
    asm.Immediate(Sbc, 10);
    asm.Memory(Inc, Zp.Arg);
    asm.Bind(smaller);
    asm.Emit(Dex);
    asm.Branch(Bne, bit);
    asm.Emit(Pha);
    asm.Memory(Inc, count);
    asm.Memory(Lda, Zp.Arg);
    for (var i = 1; i < 4; ++i)
      asm.Memory(Ora, Zp.Arg.Plus(i));
    asm.Branch(Bne, divide);
    asm.Bind(print);
    asm.Emit(Pla);
    asm.Immediate(Ora, '0');
    asm.Call(this.Routine(M6502Routine.PutChar));
    asm.Memory(Dec, count);
    asm.Branch(Bne, print);
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
    asm.Call(this.Routine(bytes == 2 ? M6502Routine.UnsignedDivide16 : M6502Routine.UnsignedDivide32));
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

  private void EmitError() {
    // "Error n", a new line, and the program is over
    foreach (var character in "Error") {
      asm.Immediate(Lda, character);
      asm.Call(this.Routine(M6502Routine.PutChar));
    }
    asm.Call(this.Routine(M6502Routine.PrintI32));
    asm.Call(this.Routine(M6502Routine.PrintNewLine));
    asm.Jump(this.Routine(M6502Routine.Exit));
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
    asm.IndirectY(Lda, 0x02);
    asm.IndirectY(Sta, 0x04);
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
    asm.IndirectY(Lda, 0x02);
    asm.IndirectY(Sta, 0x04);
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
  private void Extend(M6502Address cell, int from, int to, bool signed) {
    if (signed) {
      // the sign bit into carry, then 0 + $FF + C: $00 for a set sign, $FF for a clear one; inverted
      asm.Memory(Lda, cell.Plus(from - 1));
      asm.EmitAccumulator(Asl);
      asm.Immediate(Lda, 0);
      asm.Immediate(Adc, 0xFF);
      asm.Immediate(Eor, 0xFF);
    } else {
      asm.Immediate(Lda, 0);
    }
    for (var i = from; i < to; ++i)
      asm.Memory(Sta, cell.Plus(i));
  }
}
