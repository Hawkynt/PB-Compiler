using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Runtime.Portable;

/// <summary>
/// The runtime for native targets that are not DOS, written once as IR and compiled by each back end
/// like the program itself. It defines the <c>rt_*</c> functions a module declares - the same ABI
/// <c>runtime/pbc_rt.h</c> documents - on top of a handful of primitives a back end emits for its
/// operating system: <c>sys_write(fd, buffer, length)</c> and <c>sys_exit(code)</c>, and for files
/// and input <c>sys_read</c>, <c>sys_open</c>, <c>sys_close</c>, <c>sys_seek</c> and
/// <c>sys_unlink</c> (<c>PortableRuntime.Files.cs</c>).
///
/// <para>
/// Only what the module calls is defined, so a program that never prints a float carries no float
/// printer. A routine it asks for that this runtime does not have stays a declaration, and the back
/// end declines it by name.
/// </para>
/// </summary>
public static partial class PortableRuntime {

  /// <summary>
  /// The runtime's block copy, <c>(dst, src, n)</c>: a back end that has a faster one of its own may
  /// lower calls to it natively and leave this body out.
  /// </summary>
  public const string NativeCopy = "rt.copy";

  /// <summary>BASIC's print zone: the comma separator's tab stop.</summary>
  private const int ZoneWidth = 14;

  /// <summary>
  /// Defines, in <paramref name="module"/>, every runtime function it declares that this runtime
  /// implements; strings live in a heap of <paramref name="heapBytes"/>. Before the middle end the
  /// new functions are left in alloca form for it to promote; after it, <paramref name="cleanUp"/>
  /// promotes and tidies them here. <paramref name="softMath"/> also defines the math intrinsics
  /// (<c>llvm.sqrt.f64</c> and its kin) for a target with no floating-point hardware to lower them to.
  /// </summary>
  public static void Define(IrModule module, int heapBytes, bool cleanUp = true, int indexBits = 32, bool softMath = false) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentOutOfRangeException.ThrowIfLessThan(heapBytes, 256);
    if (indexBits is not (16 or 32))
      throw new ArgumentOutOfRangeException(nameof(indexBits), indexBits, "the runtime's index is 16 or 32 bits");
    new Definer(module, heapBytes, cleanUp, indexBits == 16 ? IrType.I16 : IrType.I32, softMath).Run();
  }

  private sealed partial class Definer(IrModule module, int heapBytes, bool cleanUp, IrType index, bool softMath) {

    /// <summary>The runtime's integer for lengths, sizes and counters; see <see cref="IrWriter.Index"/>.</summary>
    private IrType Index => index;


    private IrFunction? _write, _exit, _out;
    private IrGlobalVariable? _column;
    private readonly List<IrFunction> _defined = [];

    public void Run() {
      // rt_error is always there: a back end raises BASIC's errors through it
      this.Declare("rt_error", IrType.Void, IrType.I32);
      this.RewriteConcatenationChains();
      foreach (var function in module.Functions.Where(function => function.IsDeclaration).ToList())
        this.DefineIfKnown(function);
      if (cleanUp)
        foreach (var function in this._defined) {
          Mem2Reg.Run(function);
          SimplifyCfg.Run(function);
          Dce.Run(function);
        }
      var errors = IrVerifier.Verify(module);
      if (errors.Count != 0)
        throw new InvalidOperationException("the portable runtime built invalid IR: " + string.Join("; ", errors));
    }

    /// <summary>
    /// Calls a system primitive: its integers are the ABI's <c>i32</c>s, so the runtime's indexes are
    /// widened going in and the answer narrowed coming back.
    /// </summary>
    private IrValue System(IrWriter w, IrFunction primitive, params IrValue[] arguments) {
      var converted = arguments.Select((argument, i) => primitive.Parameters[i].Type.IsInteger
        ? w.FromIndex(argument, primitive.Parameters[i].Type) : argument).ToArray();
      var result = w.B.Call(primitive.ReturnType, primitive, converted);
      return primitive.ReturnType.IsInteger ? w.ToIndex(result) : result;
    }

    private IrFunction Declare(string name, IrType returnType, params IrType[] parameters)
      => module.FindFunction(name)
        ?? module.AddFunction(new IrFunction(name, returnType, parameters.Select((type, i) => new IrArgument(type, i))));

    private void DefineIfKnown(IrFunction function) {
      var body = this.StringRoutine(function.Name) ?? this.NumberRoutine(function.Name) ?? this.ArrayRoutine(function.Name)
        ?? this.FileRoutine(function.Name) ?? this.MathRoutine(function.Name)
        ?? (Action<IrWriter>?)(function.Name switch {
        "rt_print_str" => w => {
          w.B.Call(IrType.Void, this.Out, w.Function.Parameters[0], w.ToIndex(w.Function.Parameters[1]));
          w.B.Ret();
        },
        "rt_print_nl" => w => this.PrintByte(w, '\n'),
        "rt_print_comma" or "rt_print_zone" => this.PrintZone,
        "rt_print_tab" => this.PrintTab,
        "rt_print_spc" => this.PrintSpaces,
        "rt_error" => this.Error,
        "rt_end" => w => {
          this.System(w, this.Exit, w.Function.Parameters[0]);
          w.B.Ret();
        },
        "rt_inp" => w => w.B.Ret(IrBuilder.ConstInt(w.Function.ReturnType, 0)),
        "rt_outp" => w => w.B.Ret(),
        "llvm.memcpy.p0.p0.i32" => this.MemoryCopy,
        "llvm.memset.p0.i32" => this.MemorySet,
        _ => null,
      });
      if (body is null)
        return;
      this.Build(function, body);
    }

    private void Build(IrFunction function, Action<IrWriter> body) {
      var writer = new IrWriter(function, this.Index);
      body(writer);
      this._defined.Add(function);
    }

    /// <summary>A runtime-internal function, defined on first use.</summary>
    private IrFunction Internal(string name, IrType returnType, IrType[] parameters, Action<IrWriter> body) {
      if (module.FindFunction(name) is { } existing)
        return existing;
      var function = module.AddFunction(new IrFunction(name, returnType, parameters.Select((type, i) => new IrArgument(type, i))));
      this.Build(function, body);
      return function;
    }

    /// <summary><c>sys_write(fd, buffer, length)</c>: the bytes written, or a negative error.</summary>
    private IrFunction Write => this._write ??= this.Declare("sys_write", IrType.I32, IrType.I32, IrType.Ptr, IrType.I32);
    private IrFunction Exit => this._exit ??= this.Declare("sys_exit", IrType.Void, IrType.I32);

    /// <summary>The output column, counted from zero, that zones and TAB measure from.</summary>
    private IrGlobalVariable Column => this._column ??= module.AddGlobal(new IrGlobalVariable("rt.column", this.Index));

    /// <summary><c>rt.out(buffer, length)</c>: writes, and keeps the column.</summary>
    private IrFunction Out => this._out ??= this.Internal("rt.out", IrType.Void, [IrType.Ptr, this.Index], w => {
      var (buffer, length) = (w.Function.Parameters[0], w.Function.Parameters[1]);
      var i = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), length), () => {
        var column = w.B.Load(w.Index, this.Column);
        w.If(w.Cmp(IrCmpPred.Eq, w.ByteAt(buffer, i.Get()), w.I8('\n')),
          () => w.B.Store(w.Ix(0), this.Column),
          () => w.B.Store(w.B.Add(column, w.Ix(1)), this.Column));
        i.Set(w.B.Add(i.Get(), w.Ix(1)));
      });
      this.System(w, this.Write, w.Ix(1), buffer, length);
      w.B.Ret();
    });

    private void OutByte(IrWriter w, IrValue character) {
      var buffer = w.Buffer(1);
      w.B.Store(character, buffer);
      w.B.Call(IrType.Void, this.Out, buffer, w.Ix(1));
    }

    private void PrintByte(IrWriter w, char character) {
      this.OutByte(w, w.I8(character));
      w.B.Ret();
    }

    private void PrintZone(IrWriter w) {
      // at least one space, then on to the next multiple of fourteen
      var loop = w.Block("zone");
      var done = w.Block("zoned");
      w.B.Br(loop);
      w.B.Position(loop);
      this.OutByte(w, w.I8(' '));
      var column = w.B.Load(w.Index, this.Column);
      w.B.CondBr(w.Cmp(IrCmpPred.Ne, w.B.Binary(IrBinaryOp.SRem, column, w.Ix(ZoneWidth)), w.Ix(0)), loop, done);
      w.B.Position(done);
      w.B.Ret();
    }

    private void PrintTab(IrWriter w) {
      var target = w.Variable(w.Index, w.ToIndex(w.Function.Parameters[0]));
      w.If(w.Cmp(IrCmpPred.Slt, target.Get(), w.Ix(1)), () => target.Set(w.Ix(1)));
      w.While(() => w.Cmp(IrCmpPred.Sgt, w.B.Load(w.Index, this.Column), w.B.Sub(target.Get(), w.Ix(1))),
        () => this.OutByte(w, w.I8('\n')));
      w.While(() => w.Cmp(IrCmpPred.Slt, w.B.Load(w.Index, this.Column), w.B.Sub(target.Get(), w.Ix(1))),
        () => this.OutByte(w, w.I8(' ')));
      w.B.Ret();
    }

    private void PrintSpaces(IrWriter w) {
      var count = w.Variable(w.Index, w.ToIndex(w.Function.Parameters[0]));
      w.While(() => w.Cmp(IrCmpPred.Sgt, count.Get(), w.Ix(0)), () => {
        this.OutByte(w, w.I8(' '));
        count.Set(w.B.Sub(count.Get(), w.Ix(1)));
      });
      w.B.Ret();
    }

    private void Error(IrWriter w) {
      // an armed ON ERROR takes the error first: sys_trap does not come back when it does
      w.B.Call(IrType.Void, this.Declare("sys_trap", IrType.Void, IrType.I32), w.Function.Parameters[0]);
      // otherwise "Error n", a new line, and the program is over
      var text = w.Buffer(5);
      for (var i = 0; i < 5; ++i)
        w.SetByte(text, w.Ix(i), w.I8("Error"[i]));
      w.B.Call(IrType.Void, this.Out, text, w.Ix(5));
      var number = w.Buffer(NumberText);
      var length = w.B.Call(w.Index, this.FormatSigned, w.B.SExt(w.Function.Parameters[0], IrType.I64), number);
      w.B.Call(IrType.Void, this.Out, number, length);
      this.OutByte(w, w.I8(' '));
      this.OutByte(w, w.I8('\n'));
      this.System(w, this.Exit, w.Ix(3));
      w.B.Ret();
    }

  }
}
