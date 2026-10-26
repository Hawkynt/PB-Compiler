using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Runtime.Portable;

namespace PowerBasic.Compiler.Runtime;

public static partial class DrawRuntime {
  private sealed partial class Definer {
    private void EmitCommand(Context context, IrValue character) {
      var w = context.W;
      var done = w.Block("command.done");
      var invalid = w.Block("command.invalid");
      var dispatch = w.B.Switch(character, invalid);
      var commands = new Dictionary<char, Action> {
        ['B'] = () => context.Flags.Set(w.B.Or(context.Flags.Get(), w.Ix(1))),
        ['N'] = () => context.Flags.Set(w.B.Or(context.Flags.Get(), w.Ix(2))),
        ['C'] = () => this.EmitState(context, 'C'),
        ['S'] = () => this.EmitState(context, 'S'),
        ['A'] = () => this.EmitState(context, 'A'),
        ['T'] = () => this.EmitTurn(context),
        ['M'] = () => this.EmitMoveTo(context),
        ['P'] = () => this.EmitPaint(context),
        ['X'] = () => this.EmitExecute(context),
      };
      foreach (var token in "UDLREFGH") {
        var movement = token;
        commands[token] = () => this.EmitMovement(context, movement);
      }
      foreach (var (token, emit) in commands) {
        var block = w.Block("command." + token);
        dispatch.AddCase(token, block);
        w.B.Position(block);
        emit();
        w.B.Br(done);
      }
      dispatch.AddCase(';', done);
      w.B.Position(invalid);
      context.Error.Set(w.Ix(5));
      w.B.Br(done);
      w.B.Position(done);
    }

    private void EmitState(Context context, char command) {
      var w = context.W;
      var number = this.Number(context, required: true);
      if (command != 'C') {
        var minimum = command == 'S' ? 1 : 0;
        var maximum = command == 'S' ? 255 : 3;
        w.If(w.B.Or(w.Cmp(IrCmpPred.Slt, number, w.Ix(minimum)),
          w.Cmp(IrCmpPred.Sgt, number, w.Ix(maximum))), () => context.Error.Set(w.Ix(5)));
      }
      this.WhenValid(context, () => {
        if (command == 'C')
          this.Store(w, "rt_draw_color", w.B.Add(w.B.ZExt(w.B.Trunc(number, IrType.I8), IrType.I16), w.Ix(1)));
        else if (command == 'S')
          this.Store(w, "rt_draw_scale", number);
        else
          this.Store(w, "rt_draw_angle", w.B.Mul(number, w.Ix(90)));
      });
    }

    private void EmitTurn(Context context) {
      var w = context.W;
      w.If(w.Cmp(IrCmpPred.Ne, this.Upper(w, this.Next(context)), w.Ix('A')),
        () => context.Error.Set(w.Ix(5)));
      var degrees = this.Number(context, required: true);
      w.If(w.B.Or(w.Cmp(IrCmpPred.Slt, degrees, w.Ix(-360)),
        w.Cmp(IrCmpPred.Sgt, degrees, w.Ix(360))), () => context.Error.Set(w.Ix(5)));
      this.WhenValid(context, () => {
        var remainder = w.B.Binary(IrBinaryOp.SRem, degrees, w.Ix(360));
        this.Store(w, "rt_draw_angle", w.B.Select(w.Cmp(IrCmpPred.Slt, remainder, w.Ix(0)),
          w.B.Add(remainder, w.Ix(360)), remainder));
      });
    }

    private void EmitMovement(Context context, char command) {
      var w = context.W;
      var number = this.Number(context, required: false);
      var (x, y) = command switch {
        'U' => (0, -1), 'D' => (0, 1), 'L' => (-1, 0), 'R' => (1, 0),
        'E' => (1, -1), 'F' => (1, 1), 'G' => (-1, 1), _ => (-1, -1),
      };
      IrValue Component(int direction) => direction switch {
        0 => w.Ix(0), 1 => number, _ => w.B.Sub(w.Ix(0), number),
      };
      this.WhenValid(context, () => this.Move(context, w.Ix(0), Component(x), Component(y)));
    }

    private void EmitMoveTo(Context context) {
      var w = context.W;
      var x = this.Number(context, required: true);
      var relative = w.Cmp(IrCmpPred.Ne, w.B.And(context.Metadata.Get(), w.Ix(2)), w.Ix(0));
      w.If(w.Cmp(IrCmpPred.Ne, this.Next(context), w.Ix(',')), () => context.Error.Set(w.Ix(5)));
      var y = this.Number(context, required: true);
      this.WhenValid(context, () => this.Move(context, w.B.Select(relative, w.Ix(0), w.Ix(1)), x, y));
    }

    private void Move(Context context, IrValue kind, IrValue x, IrValue y) {
      this.Call(context.W, "rt_draw_move", IrType.Void, kind, x, y, context.Flags.Get());
      context.Flags.Set(context.W.Ix(0));
    }

    private void EmitPaint(Context context) {
      var w = context.W;
      var color = this.Number(context, required: true);
      w.If(w.Cmp(IrCmpPred.Ne, this.Next(context), w.Ix(',')), () => context.Error.Set(w.Ix(5)));
      var border = this.Number(context, required: true);
      this.WhenValid(context, () => {
        var kept = this.Load(w, "rt_gcolor");
        this.Store(w, "rt_gcolor", color);
        this.Store(w, "rt_gpbord", border);
        context.Error.Set(this.Call(w, "rt_paint_status", IrType.I16));
        this.Store(w, "rt_gcolor", kept);
      });
    }

    private void EmitExecute(Context context) {
      var w = context.W;
      var handle = this.Reference(w, context.Handle, context.Length, context.Position.Slot,
        context.Error.Slot, isString: true);
      this.WhenValid(context, () => {
        var owned = this.Call(w, "rt_str_dup", IrType.Ptr, handle);
        var error = this.Call(w, "rt_draw_parse", IrType.I16, owned, w.Ix(0));
        context.Error.Set(error);
      });
    }
  }
}
