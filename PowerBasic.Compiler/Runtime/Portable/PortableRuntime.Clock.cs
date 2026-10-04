using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Runtime.Portable;

/// <summary>
/// The clock and the keyboard: TIMER, TIME$, DATE$, RANDOMIZE with no seed, DELAY and SLEEP, INKEY$,
/// INSTAT and INPUT$(n). They stand on three system primitives each back end provides:
/// <c>sys_clock(out)</c> writes the days since 1 January 1970 and the hundredths of a second since
/// midnight as two LONGs; <c>sys_sleep(hundredths)</c> waits; <c>sys_key(wait)</c> answers the next
/// key typed - or -1 at once when <c>wait</c> is zero and nothing is waiting.
/// </summary>
public static partial class PortableRuntime {

  private sealed partial class Definer {

    private IrFunction? _clock, _sleep, _key;

    private IrFunction ClockCall => this._clock ??= this.Declare("sys_clock", IrType.I32, IrType.Ptr);
    private IrFunction SleepCall => this._sleep ??= this.Declare("sys_sleep", IrType.I32, IrType.I32);
    private IrFunction KeyCall => this._key ??= this.Declare("sys_key", IrType.I32, IrType.I32);

    private Action<IrWriter>? ClockRoutine(string name) => name switch {
      "rt_timer" => w => {
        var (_, hundredths) = this.Now(w);
        w.B.Ret(w.B.Binary(IrBinaryOp.FDiv, w.B.Cast(IrCastOp.SIToFP, hundredths, IrType.F64), new IrConstantFloat(IrType.F64, 100)));
      },
      "rt_time_str" => w => {
        // HH:MM:SS
        var (_, hundredths) = this.Now(w);
        var seconds = w.B.Binary(IrBinaryOp.SDiv, hundredths, w.I32(100));
        var text = w.Buffer(8);
        this.TwoDigits(w, text, 0, w.B.Binary(IrBinaryOp.SDiv, seconds, w.I32(3600)));
        w.SetByte(text, w.Ix(2), w.I8(':'));
        this.TwoDigits(w, text, 3, w.B.Binary(IrBinaryOp.SRem, w.B.Binary(IrBinaryOp.SDiv, seconds, w.I32(60)), w.I32(60)));
        w.SetByte(text, w.Ix(5), w.I8(':'));
        this.TwoDigits(w, text, 6, w.B.Binary(IrBinaryOp.SRem, seconds, w.I32(60)));
        w.B.Ret(this.Make(w, text, w.Ix(8)));
      },
      "rt_date_str" => this.DateString,
      "rt_randomize" => w => {
        // a seed from the clock, as the DOS runtime takes one from the BIOS tick counter
        var (days, hundredths) = this.Now(w);
        w.B.Store(w.B.Xor(w.B.Mul(days, w.I32(8640000)), hundredths), this.Seed);
        w.B.Ret();
      },
      "rt_delay" => w => {
        var hundredths = w.B.Cast(IrCastOp.FPToSIRound, w.B.Binary(IrBinaryOp.FMul, w.Function.Parameters[0], new IrConstantFloat(IrType.F64, 100)), IrType.I32);
        w.If(w.Cmp(IrCmpPred.Sgt, hundredths, w.I32(0)), () => w.B.Call(IrType.I32, this.SleepCall, hundredths));
        w.B.Ret();
      },
      // SLEEP with no count: until a key is pressed, and the key is used up
      "rt_sleep_key" => w => {
        this.ReadKey(w, wait: true);
        w.B.Ret();
      },
      "rt_inkey" => w => {
        var key = this.ReadKey(w, wait: false);
        w.If(w.Cmp(IrCmpPred.Slt, key, w.I32(0)), () => w.Return(new IrNullPtr()));
        var buffer = w.Buffer(1);
        w.B.Store(w.B.Trunc(key, IrType.I8), buffer);
        w.B.Ret(this.Make(w, buffer, w.Ix(1)));
      },
      // INSTAT: -1 when a key is waiting. Asking takes it, so it is held for the next INKEY$ to find
      "rt_instat" => w => w.B.Ret(w.B.Trunc(w.B.Select(this.KeyWaiting(w), w.I32(-1), w.I32(0)), IrType.I16)),
      // the screen: the runtime keeps the cursor's row beside its column, and a back end moves the
      // real one - an ANSI terminal's, the C64's screen editor's - where there is one to move
      "rt_cls" => w => {
        w.B.Call(IrType.I32, this.ClearCall);
        w.B.Store(w.I32(0), this.Row);
        this.SetColumn(w, w.Ix(0));
        w.B.Ret();
      },
      "rt_locate" => w => {
        // a row or column of 0 is the one LOCATE left out, which stays where it is
        var row = w.B.SExt(w.Function.Parameters[0], IrType.I32);
        var column = w.B.SExt(w.Function.Parameters[1], IrType.I32);
        w.If(w.Cmp(IrCmpPred.Sgt, row, w.I32(0)), () => w.B.Store(w.B.Sub(row, w.I32(1)), this.Row));
        w.If(w.Cmp(IrCmpPred.Sgt, column, w.I32(0)), () => this.SetColumn(w, w.ToIndex(w.B.Sub(column, w.I32(1)))));
        w.B.Call(IrType.I32, this.LocateCall, w.B.Add(w.B.Load(IrType.I32, this.Row), w.I32(1)),
          w.B.Add(w.FromIndex(this.GetColumn(w), IrType.I32), w.I32(1)));
        w.B.Ret();
      },
      // CONSIN / CONSOUT: -1 when standard input or output is the console rather than a file or a pipe
      "rt_consin" or "rt_consout" => w => w.B.Ret(w.B.Trunc(w.B.Select(
        w.Cmp(IrCmpPred.Ne, w.B.Call(IrType.I32, this.ConsoleCall, w.I32(name == "rt_consin" ? 0 : 1)), w.I32(0)),
        w.I32(-1), w.I32(0)), IrType.I16)),
      "rt_csrlin" => w => w.B.Ret(w.B.Trunc(w.B.Add(w.B.Load(IrType.I32, this.Row), w.I32(1)), IrType.I16)),
      "rt_key_input" => w => {
        // INPUT$(n): n keys, waiting for each
        var count = w.ToIndex(w.Function.Parameters[0]);
        var handle = w.B.Call(IrType.Ptr, this.NewString, count);
        var i = w.Variable(w.Index, w.Ix(0));
        w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), w.B.Load(w.Index, handle)), () => {
          w.SetByte(Bytes(w, handle), i.Get(), w.B.Trunc(this.ReadKey(w, wait: true), IrType.I8));
          i.Set(w.B.Add(i.Get(), w.Ix(1)));
        });
        w.B.Ret(handle);
      },
      _ => null,
    };

    private IrFunction? _clear, _locate;
    private IrGlobalVariable? _row;

    private IrFunction ClearCall => this._clear ??= this.Declare("sys_cls", IrType.I32);
    /// <summary><c>sys_console(fd)</c>: non-zero when the descriptor is a terminal.</summary>
    private IrFunction ConsoleCall => this._console ??= this.Declare("sys_console", IrType.I32, IrType.I32);
    private IrFunction? _console;

    private IrFunction LocateCall => this._locate ??= this.Declare("sys_locate", IrType.I32, IrType.I32, IrType.I32);

    /// <summary>The cursor's row, 0-based; a line end moves it down, the bottom of a 25-line screen scrolls.</summary>
    private IrGlobalVariable Row => this._row ??= this.Shared(new IrGlobalVariable("rt.row", IrType.I32));

    /// <summary>Whether this module asks where the cursor is, so console output keeps the row at all.</summary>
    private bool TracksRow => module.FindFunction("rt_csrlin") is not null || module.FindFunction("rt_locate") is not null;

    private IrGlobalVariable? _pendingKey;

    /// <summary>A key INSTAT saw and took, one more than its code, kept for the next read; 0 for none.</summary>
    private IrGlobalVariable PendingKey => this._pendingKey ??= this.Shared(new IrGlobalVariable("rt.pendingKey", IrType.I32));

    /// <summary>The next key - one INSTAT already took, if it did - or -1 when none is waiting and the caller will not wait.</summary>
    private IrValue ReadKey(IrWriter w, bool wait) {
      var key = w.Variable(IrType.I32, w.B.Sub(w.B.Load(IrType.I32, this.PendingKey), w.I32(1)));
      w.B.Store(w.I32(0), this.PendingKey);
      w.If(w.Cmp(IrCmpPred.Slt, key.Get(), w.I32(0)), () => key.Set(w.B.Call(IrType.I32, this.KeyCall, w.I32(wait ? 1 : 0))));
      return key.Get();
    }

    private IrValue KeyWaiting(IrWriter w) {
      w.If(w.Cmp(IrCmpPred.Eq, w.B.Load(IrType.I32, this.PendingKey), w.I32(0)), () => {
        var key = w.B.Call(IrType.I32, this.KeyCall, w.I32(0));
        w.If(w.Cmp(IrCmpPred.Sge, key, w.I32(0)), () => w.B.Store(w.B.Add(key, w.I32(1)), this.PendingKey));
      });
      return w.Cmp(IrCmpPred.Ne, w.B.Load(IrType.I32, this.PendingKey), w.I32(0));
    }

    /// <summary>The clock: days since 1970 and hundredths since midnight.</summary>
    private (IrValue Days, IrValue Hundredths) Now(IrWriter w) {
      var buffer = w.Buffer(8);
      w.B.Call(IrType.I32, this.ClockCall, buffer);
      return (w.B.Load(IrType.I32, buffer), w.B.Load(IrType.I32, w.B.Gep(buffer, w.Ix(4))));
    }

    private void TwoDigits(IrWriter w, IrValue text, int at, IrValue value) {
      w.SetByte(text, w.Ix(at), w.B.Add(w.B.Trunc(w.B.Binary(IrBinaryOp.SDiv, value, w.I32(10)), IrType.I8), w.I8('0')));
      w.SetByte(text, w.Ix(at + 1), w.B.Add(w.B.Trunc(w.B.Binary(IrBinaryOp.SRem, value, w.I32(10)), IrType.I8), w.I8('0')));
    }

    /// <summary>DATE$ as MM-DD-YYYY: the civil date of a day count, by Howard Hinnant's days-to-civil algorithm.</summary>
    private void DateString(IrWriter w) {
      var (days, _) = this.Now(w);
      var (year, month, day) = this.CivilDate(w, days);
      IrValue Div(IrValue a, int b) => w.B.Binary(IrBinaryOp.SDiv, a, w.I32(b));
      var text = w.Buffer(10);
      this.TwoDigits(w, text, 0, month);
      w.SetByte(text, w.Ix(2), w.I8('-'));
      this.TwoDigits(w, text, 3, day);
      w.SetByte(text, w.Ix(5), w.I8('-'));
      this.TwoDigits(w, text, 6, Div(year, 100));
      this.TwoDigits(w, text, 8, w.B.Binary(IrBinaryOp.SRem, year, w.I32(100)));
      w.B.Ret(this.Make(w, text, w.Ix(10)));
    }

    /// <summary>The year, month and day of a count of days since 1970, by Howard Hinnant's days-to-civil algorithm.</summary>
    private (IrValue Year, IrValue Month, IrValue Day) CivilDate(IrWriter w, IrValue days) {
      IrValue Div(IrValue a, int b) => w.B.Binary(IrBinaryOp.SDiv, a, w.I32(b));
      var z = w.B.Add(days, w.I32(719468));
      var era = Div(z, 146097);                                    // days are never before 1970, so z is positive
      var dayOfEra = w.B.Sub(z, w.B.Mul(era, w.I32(146097)));
      var yearOfEra = Div(w.B.Sub(w.B.Add(w.B.Sub(dayOfEra, Div(dayOfEra, 1460)), Div(dayOfEra, 36524)), Div(dayOfEra, 146096)), 365);
      var year = w.B.Add(yearOfEra, w.B.Mul(era, w.I32(400)));
      var dayOfYear = w.B.Sub(dayOfEra, w.B.Sub(w.B.Add(w.B.Mul(yearOfEra, w.I32(365)), Div(yearOfEra, 4)), Div(yearOfEra, 100)));
      var monthPrime = Div(w.B.Add(w.B.Mul(dayOfYear, w.I32(5)), w.I32(2)), 153);
      var day = w.B.Add(w.B.Sub(dayOfYear, Div(w.B.Add(w.B.Mul(monthPrime, w.I32(153)), w.I32(2)), 5)), w.I32(1));
      var month = w.B.Select(w.Cmp(IrCmpPred.Slt, monthPrime, w.I32(10)), w.B.Add(monthPrime, w.I32(3)), w.B.Sub(monthPrime, w.I32(9)));
      year = w.B.Add(year, w.B.Select(w.Cmp(IrCmpPred.Sle, month, w.I32(2)), w.I32(1), w.I32(0)));
      return (year, month, day);
    }
  }
}
