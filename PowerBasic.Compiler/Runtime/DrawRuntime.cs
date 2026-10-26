using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Runtime.Portable;

namespace PowerBasic.Compiler.Runtime;

/// <summary>The DOS DRAW interpreter and its shared, inlineable command operations, expressed in IR.</summary>
public static partial class DrawRuntime {
  public static int Define(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var names = new[] { "rt_draw", "rt_draw_resume", "rt_draw_move", "rt_draw_begin", "rt_draw_begin_owned" };
    if (!names.Any(name => module.FindFunction(name) is { IsDeclaration: true, HasNoUsers: false }))
      return 0;
    return new Definer(module).Run();
  }

  private sealed partial class Definer(IrModule module) {
    private int _defined;

    private sealed record Context(IrWriter W, IrValue Handle, IrValue Length, IrWriter.Local Position,
      IrWriter.Local Metadata, IrWriter.Local Error, IrWriter.Local Flags);

    internal int Run() {
      var entry = module.FindFunction("rt_draw");
      var resume = module.FindFunction("rt_draw_resume");
      var needParser = entry is { IsDeclaration: true, HasNoUsers: false }
        || resume is { IsDeclaration: true, HasNoUsers: false };
      this.DefineBegin();
      this.DefineOwnedBegin();
      if (needParser || module.FindFunction("rt_draw_move") is { IsDeclaration: true, HasNoUsers: false })
        this.DefineMove();
      if (needParser) {
        this.DefineNext();
        this.DefineNumber();
        this.DefineEntry(entry, resume);
      }
      var errors = IrVerifier.Verify(module);
      if (errors.Count != 0)
        throw new InvalidOperationException("DRAW runtime built invalid IR: " + string.Join("; ", errors));
      return this._defined;
    }

    private IrFunction Declare(string name, IrType result, params IrType[] parameters) {
      if (module.FindFunction(name) is { } existing) {
        if (existing.ReturnType != result || existing.Convention != IrCallConvention.Basic
            || !existing.Parameters.Select(parameter => parameter.Type).SequenceEqual(parameters))
          throw new InvalidOperationException($"DRAW runtime ABI collision: {name}");
        return existing;
      }
      return module.AddFunction(new IrFunction(name, result,
        parameters.Select((type, index) => new IrArgument(type, index))));
    }

    private IrGlobalVariable Cell(string name) {
      if (module.FindGlobal(name) is { } existing) {
        if (existing.ValueType != IrType.I16 || existing.Count != 1)
          throw new InvalidOperationException($"DRAW runtime storage collision: {name}");
        return existing;
      }
      return module.AddGlobal(new IrGlobalVariable(name, IrType.I16));
    }

    private IrWriter Writer(IrFunction function) {
      if (!function.IsDeclaration)
        throw new InvalidOperationException($"DRAW runtime definition collision: {function.Name}");
      ++this._defined;
      return new IrWriter(function, IrType.I16);
    }

    private IrValue Call(IrWriter w, string name, IrType result, params IrValue[] arguments)
      => w.B.Call(result, this.Declare(name, result, arguments.Select(argument => argument.Type).ToArray()), arguments);

    private IrValue Load(IrWriter w, string name) => w.B.Load(IrType.I16, this.Cell(name));
    private void Store(IrWriter w, string name, IrValue value) => w.B.Store(value, this.Cell(name));

    private IrValue IsGraphics(IrWriter w) {
      var mode = this.Call(w, "rt_graphics_mode", IrType.I16);
      return w.B.And(w.Cmp(IrCmpPred.Uge, mode, w.Ix(4)), w.Cmp(IrCmpPred.Ne, mode, w.Ix(7)));
    }

    private void DefineBegin() {
      var function = this.Declare("rt_draw_begin", IrType.Void);
      if (!function.IsDeclaration)
        return;
      var w = this.Writer(function);
      w.If(w.Cmp(IrCmpPred.Eq, this.IsGraphics(w), IrBuilder.ConstBool(false)),
        () => this.Call(w, "rt_error", IrType.Void, w.I32(5)));
      w.B.Ret();
    }

    private void DefineWrapper(IrFunction function, IrFunction parser, IrValue start) {
      var wrapper = this.Writer(function);
      var error = wrapper.B.Call(IrType.I16, parser, function.Parameters[0], start);
      wrapper.If(wrapper.Cmp(IrCmpPred.Ne, error, wrapper.Ix(0)),
        () => this.Call(wrapper, "rt_error", IrType.Void, wrapper.B.ZExt(error, IrType.I32)));
      wrapper.B.Ret();
    }

    private void DefineOwnedBegin() {
      var function = module.FindFunction("rt_draw_begin_owned");
      if (function is not { IsDeclaration: true, HasNoUsers: false })
        return;
      function = this.Declare("rt_draw_begin_owned", IrType.Void, IrType.Ptr);
      var w = this.Writer(function);
      w.If(w.Cmp(IrCmpPred.Eq, this.IsGraphics(w), IrBuilder.ConstBool(false)), () => {
        this.Call(w, "rt_str_free", IrType.Void, function.Parameters[0]);
        this.Call(w, "rt_error", IrType.Void, w.I32(5));
      });
      w.B.Ret();
    }

    private void DefineEntry(IrFunction? function, IrFunction? resume) {
      var parser = this.Declare("rt_draw_parse", IrType.I16, IrType.Ptr, IrType.I16);
      if (function is { IsDeclaration: true, HasNoUsers: false })
        this.DefineWrapper(function, parser, new IrConstantInt(IrType.I16, 0));
      if (resume is { IsDeclaration: true, HasNoUsers: false })
        this.DefineWrapper(resume, parser, resume.Parameters[1]);
      var w = this.Writer(parser);
      var handle = parser.Parameters[0];
      var length = w.ToIndex(this.Call(w, "rt_str_len_borrow", IrType.I32, handle));
      var context = new Context(w, handle, length, w.Variable(IrType.I16, parser.Parameters[1]),
        w.Variable(IrType.I16, w.Ix(0)), w.Variable(IrType.I16, w.Ix(0)), w.Variable(IrType.I16, w.Ix(0)));
      w.If(w.Cmp(IrCmpPred.Eq, this.IsGraphics(w), IrBuilder.ConstBool(false)), () => context.Error.Set(w.Ix(5)));
      var character = w.Variable(IrType.I16, w.Ix(-1));
      w.While(() => w.Cmp(IrCmpPred.Eq, context.Error.Get(), w.Ix(0)), () => {
        character.Set(this.Next(context));
        w.If(w.Cmp(IrCmpPred.Eq, character.Get(), w.Ix(-1)), () => {
          this.Call(w, "rt_str_free", IrType.Void, handle);
          w.Return(w.Ix(0));
        });
        this.EmitCommand(context, this.Upper(w, character.Get()));
      });
      this.Call(w, "rt_str_free", IrType.Void, handle);
      w.B.Ret(context.Error.Get());
    }

    private IrValue Upper(IrWriter w, IrValue character) => w.B.Select(
      w.B.And(w.Cmp(IrCmpPred.Uge, character, w.Ix('a')), w.Cmp(IrCmpPred.Ule, character, w.Ix('z'))),
      w.B.Sub(character, w.Ix('a' - 'A')), character);

    private IrValue Next(Context context) => this.Call(context.W, "rt_draw_next", IrType.I16,
      context.Handle, context.Length, context.Position.Slot);

    private IrValue Number(Context context, bool required) {
      var w = context.W;
      var number = this.Call(w, "rt_draw_number", IrType.I16, context.Handle, context.Length,
        context.Position.Slot, context.Metadata.Slot, context.Error.Slot);
      var present = w.Cmp(IrCmpPred.Ne, w.B.And(context.Metadata.Get(), w.Ix(1)), w.Ix(0));
      if (required)
        w.If(w.Cmp(IrCmpPred.Eq, present, IrBuilder.ConstBool(false)), () => context.Error.Set(w.Ix(5)));
      return required ? number : w.B.Select(present, number, w.Ix(1));
    }

    private void WhenValid(Context context, Action action)
      => context.W.If(context.W.Cmp(IrCmpPred.Eq, context.Error.Get(), context.W.Ix(0)), action);
  }
}
