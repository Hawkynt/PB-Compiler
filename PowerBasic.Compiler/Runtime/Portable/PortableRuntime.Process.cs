using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Runtime.Portable;

/// <summary>
/// What the program was started with: <c>COMMAND$</c>, the command line after the program's name,
/// and <c>ENVIRON$(name$)</c>, one variable of the environment. Both stand on two primitives that
/// answer a NUL-terminated string or a null pointer past the last: <c>sys_arg(i)</c>, the i-th word
/// of the command line with 0 the program itself, and <c>sys_env(i)</c>, the i-th NAME=VALUE.
/// </summary>
public static partial class PortableRuntime {

  private sealed partial class Definer {

    private IrFunction? _arg, _env;

    private IrFunction ArgCall => this._arg ??= this.Declare("sys_arg", IrType.Ptr, IrType.I32);
    private IrFunction EnvCall => this._env ??= this.Declare("sys_env", IrType.Ptr, IrType.I32);

    private Action<IrWriter>? ProcessRoutine(string name) => name switch {
      // DOS hands a program its command tail as typed; here it is the words after the name, a space apart
      "rt_command" => w => {
        var text = w.Variable(IrType.Ptr, new IrNullPtr());
        var i = w.Variable(IrType.I32, w.I32(1));
        var more = w.Variable(IrType.I1, IrBuilder.ConstBool(true));
        w.While(() => more.Get(), () => {
          var word = w.B.Call(IrType.Ptr, this.ArgCall, i.Get());
          w.If(w.Cmp(IrCmpPred.Eq, word, new IrNullPtr()), () => more.Set(IrBuilder.ConstBool(false)), () => {
            w.If(w.Cmp(IrCmpPred.Sgt, i.Get(), w.I32(1)), () => {
              var space = w.Buffer(1);
              w.SetByte(space, w.Ix(0), w.I8(' '));
              this.Append(w, text, this.Make(w, space, w.Ix(1)));
            });
            this.Append(w, text, this.Make(w, word, this.CLength(w, word)));
            i.Set(w.B.Add(i.Get(), w.I32(1)));
          });
        });
        w.B.Ret(text.Get());
      },
      "rt_environ" => w => {
        var wanted = w.Function.Parameters[0];
        var (bytes, length) = this.View(w, wanted, w.Ix(1), this.Length(w, wanted));
        var i = w.Variable(IrType.I32, w.I32(0));
        var more = w.Variable(IrType.I1, IrBuilder.ConstBool(true));
        var answer = w.Variable(IrType.Ptr, new IrNullPtr());
        w.While(() => more.Get(), () => {
          var entry = w.B.Call(IrType.Ptr, this.EnvCall, i.Get());
          w.If(w.Cmp(IrCmpPred.Eq, entry, new IrNullPtr()), () => more.Set(IrBuilder.ConstBool(false)), () => {
            // NAME=VALUE whose name is the one asked for, letters of either case alike, as DOS names are
            var k = w.Variable(w.Index, w.Ix(0));
            var same = w.Variable(IrType.I1, IrBuilder.ConstBool(true));
            w.While(() => w.B.And(same.Get(), w.Cmp(IrCmpPred.Slt, k.Get(), length)), () => {
              same.Set(w.Cmp(IrCmpPred.Eq, this.UpperByte(w, w.ByteAt(entry, k.Get())), this.UpperByte(w, w.ByteAt(bytes, k.Get()))));
              k.Set(w.B.Add(k.Get(), w.Ix(1)));
            });
            w.If(w.B.And(same.Get(), w.Cmp(IrCmpPred.Eq, w.ByteAt(entry, length), w.I8('='))), () => {
              var value = w.B.Gep(entry, w.B.Add(length, w.Ix(1)));
              answer.Set(this.Make(w, value, this.CLength(w, value)));
              more.Set(IrBuilder.ConstBool(false));
            });
            i.Set(w.B.Add(i.Get(), w.I32(1)));
          });
        });
        this.Consume(w, wanted);
        w.B.Ret(answer.Get());
      },
      _ => null,
    };

    private void Append(IrWriter w, IrWriter.Local text, IrValue piece) {
      var previous = text.Get();
      text.Set(this.Concatenate(w, previous, piece));
      this.Consume(w, previous, piece);
    }

    private IrValue CLength(IrWriter w, IrValue text) {
      var n = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.Cmp(IrCmpPred.Ne, w.ByteAt(text, n.Get()), w.I8(0)), () => n.Set(w.B.Add(n.Get(), w.Ix(1))));
      return n.Get();
    }

    private IrValue UpperByte(IrWriter w, IrValue c)
      => w.B.Select(w.B.And(w.Cmp(IrCmpPred.Uge, c, w.I8('a')), w.Cmp(IrCmpPred.Ule, c, w.I8('z'))), w.B.Sub(c, w.I8(32)), c);
  }
}
