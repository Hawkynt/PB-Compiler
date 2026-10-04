using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Runtime.Portable;

/// <summary>
/// <c>REG</c> and <c>CALL INTERRUPT</c> on a machine with no DOS and no BIOS: the register buffer is
/// kept as DOS's is - 0 the flags, then AX, BX, CX, DX, SI, DI, BP, DS and ES - and an interrupt is
/// answered by doing what the service would have done, for the services a BASIC program asks for most:
/// <list type="bullet">
/// <item>INT 21h: <c>AH=01h/07h/08h</c> a key (01h echoed), <c>02h</c> a character out, <c>0Bh</c>
/// whether a key waits, <c>2Ah</c> the date, <c>2Ch</c> the time, <c>30h</c> the version - 5.0, the
/// DOS the oracles run - and <c>4Ch</c> the end with a return code;</item>
/// <item>INT 10h: <c>AH=00h</c> a mode set as a clear screen, <c>02h</c> the cursor placed, <c>03h</c>
/// read back, <c>0Eh</c> a character out;</item>
/// <item>INT 16h: <c>AH=00h</c> a key, waited for, <c>01h</c> whether one waits (ZF clear when one does);</item>
/// <item>INT 1Ah: <c>AH=00h</c> the timer ticks since midnight, 18.2 a second.</item>
/// </list>
/// Any other service leaves the registers as they were and sets the carry, as DOS refuses a function
/// it does not have.
/// </summary>
public static partial class PortableRuntime {

  private sealed partial class Definer {

    private IrGlobalVariable? _registers;

    private IrGlobalVariable Registers => this._registers ??= this.Shared(new IrGlobalVariable("rt.regs", IrType.I16) { Count = 10, IsZeroInitialized = true });

    private Action<IrWriter>? InterruptRoutine(string name) => name switch {
      "rt_reg_set" => w => {
        var n = w.ToIndex(w.Function.Parameters[0]);
        w.If(w.B.And(w.Cmp(IrCmpPred.Sge, n, w.Ix(0)), w.Cmp(IrCmpPred.Slt, n, w.Ix(10))),
          () => w.B.Store(w.Function.Parameters[1], w.B.Gep(this.Registers, n, IrType.I16)));
        w.B.Ret();
      },
      "rt_reg_get" => w => {
        var n = w.ToIndex(w.Function.Parameters[0]);
        w.If(w.B.Or(w.Cmp(IrCmpPred.Slt, n, w.Ix(0)), w.Cmp(IrCmpPred.Sge, n, w.Ix(10))), () => w.Return(IrBuilder.ConstInt(IrType.I16, 0)));
        w.B.Ret(w.B.Load(IrType.I16, w.B.Gep(this.Registers, n, IrType.I16)));
      },
      "rt_interrupt" => this.Interrupt,
      _ => null,
    };

    private const int Flags = 0, Ax = 1, Bx = 2, Cx = 3, Dx = 4;

    private IrValue Register(IrWriter w, int n) => w.B.ZExt(w.B.Load(IrType.I16, w.B.Gep(this.Registers, w.Ix(n), IrType.I16)), IrType.I32);

    private void SetRegister(IrWriter w, int n, IrValue value)
      => w.B.Store(w.B.Trunc(value, IrType.I16), w.B.Gep(this.Registers, w.Ix(n), IrType.I16));

    /// <summary>A register's low byte replaced, the high one kept: AL of AX, DL of DX.</summary>
    private void SetLow(IrWriter w, int n, IrValue value)
      => this.SetRegister(w, n, w.B.Or(w.B.And(this.Register(w, n), w.I32(0xFF00)), w.B.And(value, w.I32(0xFF))));

    private void SetCarry(IrWriter w, bool set) {
      var flags = this.Register(w, Flags);
      this.SetRegister(w, Flags, set ? w.B.Or(flags, w.I32(1)) : w.B.And(flags, w.I32(~1 & 0xFFFF)));
    }

    private void Interrupt(IrWriter w) {
      var vector = w.B.And(w.B.ZExt(w.Function.Parameters[0], IrType.I32), w.I32(0xFF));
      var ah = w.B.Binary(IrBinaryOp.LShr, this.Register(w, Ax), w.I32(8));
      void Service(int number, int function, Action body)
        => w.If(w.B.And(w.Cmp(IrCmpPred.Eq, vector, w.I32(number)), w.Cmp(IrCmpPred.Eq, ah, w.I32(function))), () => {
          this.SetCarry(w, false);
          body();
          w.Return();
        });
      void Out(IrValue character) {
        var buffer = w.Buffer(1);
        w.B.Store(w.B.Trunc(character, IrType.I8), buffer);
        w.B.Call(IrType.Void, this.Out, buffer, w.Ix(1));
      }
      IrValue Key() => this.ReadKey(w, wait: true);

      // INT 21h
      Service(0x21, 0x01, () => {
        var key = Key();
        Out(key);
        this.SetLow(w, Ax, key);
      });
      Service(0x21, 0x02, () => Out(w.B.And(this.Register(w, Dx), w.I32(0xFF))));
      Service(0x21, 0x07, () => this.SetLow(w, Ax, Key()));
      Service(0x21, 0x08, () => this.SetLow(w, Ax, Key()));
      Service(0x21, 0x0B, () => this.SetLow(w, Ax, w.B.Select(this.KeyWaiting(w), w.I32(0xFF), w.I32(0))));
      Service(0x21, 0x2C, () => {
        var (_, hundredths) = this.Now(w);
        var seconds = w.B.Binary(IrBinaryOp.SDiv, hundredths, w.I32(100));
        var hour = w.B.Binary(IrBinaryOp.SDiv, seconds, w.I32(3600));
        var minute = w.B.Binary(IrBinaryOp.SRem, w.B.Binary(IrBinaryOp.SDiv, seconds, w.I32(60)), w.I32(60));
        this.SetRegister(w, Cx, w.B.Or(w.B.Shl(hour, w.I32(8)), minute));
        this.SetRegister(w, Dx, w.B.Or(w.B.Shl(w.B.Binary(IrBinaryOp.SRem, seconds, w.I32(60)), w.I32(8)),
          w.B.Binary(IrBinaryOp.SRem, hundredths, w.I32(100))));
      });
      Service(0x21, 0x2A, () => {
        var (days, _) = this.Now(w);
        var (year, month, day) = this.CivilDate(w, days);
        this.SetRegister(w, Cx, year);
        this.SetRegister(w, Dx, w.B.Or(w.B.Shl(month, w.I32(8)), day));
        // 1 January 1970 was a Thursday, day 4 of DOS's week
        this.SetLow(w, Ax, w.B.Binary(IrBinaryOp.SRem, w.B.Add(days, w.I32(4)), w.I32(7)));
      });
      Service(0x21, 0x30, () => this.SetRegister(w, Ax, w.I32(0x0005)));
      Service(0x21, 0x4C, () => {
        w.B.Call(IrType.Void, this.Exit, w.B.And(this.Register(w, Ax), w.I32(0xFF)));
      });
      // INT 10h
      Service(0x10, 0x00, () => {
        w.B.Call(IrType.I32, this.ClearCall);
        w.B.Store(w.I32(0), this.Row);
        this.SetColumn(w, w.Ix(0));
      });
      Service(0x10, 0x02, () => {
        var position = this.Register(w, Dx);
        var row = w.B.Binary(IrBinaryOp.LShr, position, w.I32(8));
        var column = w.B.And(position, w.I32(0xFF));
        w.B.Store(row, this.Row);
        this.SetColumn(w, w.ToIndex(column));
        w.B.Call(IrType.I32, this.LocateCall, w.B.Add(row, w.I32(1)), w.B.Add(column, w.I32(1)));
      });
      Service(0x10, 0x03, () => this.SetRegister(w, Dx,
        w.B.Or(w.B.Shl(w.B.Load(IrType.I32, this.Row), w.I32(8)), w.FromIndex(this.GetColumn(w), IrType.I32))));
      Service(0x10, 0x0E, () => Out(w.B.And(this.Register(w, Ax), w.I32(0xFF))));
      // INT 16h
      Service(0x16, 0x00, () => this.SetRegister(w, Ax, Key()));
      Service(0x16, 0x01, () => {
        var waiting = this.KeyWaiting(w);
        var flags = w.B.And(this.Register(w, Flags), w.I32(~0x40 & 0xFFFF));
        this.SetRegister(w, Flags, w.B.Select(waiting, flags, w.B.Or(flags, w.I32(0x40))));
        w.If(waiting, () => this.SetRegister(w, Ax, w.B.Sub(w.B.Load(IrType.I32, this.PendingKey), w.I32(1))));
      });
      // INT 1Ah
      Service(0x1A, 0x00, () => {
        var (_, hundredths) = this.Now(w);
        var ticks = w.B.Binary(IrBinaryOp.SDiv, w.B.Mul(hundredths, w.I32(182)), w.I32(1000));
        this.SetRegister(w, Cx, w.B.Binary(IrBinaryOp.LShr, ticks, w.I32(16)));
        this.SetRegister(w, Dx, ticks);
        this.SetLow(w, Ax, w.I32(0));
      });
      this.SetCarry(w, true);
      w.B.Ret();
    }
  }
}
