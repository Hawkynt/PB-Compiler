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
  /// <remarks>
  /// Before the middle end (<paramref name="cleanUp"/> false) the entries <see cref="IsRewrittenByMiddleEnd"/>
  /// names are left declared: the passes that specialize them must still find calls to rewrite, not
  /// a body the inliner has already spread into the caller. The definition after the middle end
  /// gives them - and whatever those passes call instead - their bodies.
  /// </remarks>
  public static void Define(IrModule module, int heapBytes, bool cleanUp = true, int indexBits = 32, bool softMath = false) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentOutOfRangeException.ThrowIfLessThan(heapBytes, 256);
    if (indexBits is not (16 or 32))
      throw new ArgumentOutOfRangeException(nameof(indexBits), indexBits, "the runtime's index is 16 or 32 bits");
    new Definer(module, heapBytes, cleanUp, indexBits == 16 ? IrType.I16 : IrType.I32, softMath).Run();
  }

  /// <summary>
  /// The runtime entries a middle-end pass rewrites by name: the INSTR searches a constant needle
  /// specializes (<c>ConstantInstrSpecialization</c>), the number printers a constant argument turns
  /// into a literal print (<c>ConstantNumericPrint</c>), and the string producers and printers a
  /// bounded temporary moves to the stack through (<c>StringStackPromotion</c>).
  /// </summary>
  public static bool IsRewrittenByMiddleEnd(string name)
    => name is "rt_str_instr" or "rt_str_instr_start"
         or "rt_print_str" or "rt_print_strvar" or "rt_fprint_str" or "rt_fprint_strvar"
         or "rt_str_chr" or "rt_str_const" or "rt_str_concat" or "rt_str_concat_n" or "rt_str_append_lit"
       || (name.StartsWith("rt_print_", StringComparison.Ordinal) || name.StartsWith("rt_fprint_", StringComparison.Ordinal))
          && name[(name.IndexOf("print_", StringComparison.Ordinal) + "print_".Length)..]
            is "i16" or "i32" or "i64" or "u8" or "u16" or "u32" or "single" or "double" or "ext";

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
      this.InitialiseInternalVariables();
      foreach (var function in module.Functions.Where(function => function.IsDeclaration).ToList())
        if (cleanUp || !IsRewrittenByMiddleEnd(function.Name))
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
      var body = this.StringRoutine(function.Name) ?? this.TextRoutine(function.Name) ?? this.UsingRoutine(function.Name) ?? this.ClockRoutine(function.Name) ?? this.FieldRoutine(function.Name) ?? this.InternalRoutine(function.Name) ?? this.NumberRoutine(function.Name) ?? this.ArrayRoutine(function.Name) ?? this.SortRoutine(function.Name) ?? this.DirectoryRoutine(function.Name) ?? this.ProcessRoutine(function.Name) ?? this.InterruptRoutine(function.Name) ?? this.CoalescedRoutine(function)
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
        // one flat address space: the segment half of a code pointer is nothing
        // $ERROR STACK ON: a procedure entered with the stack nearly gone is error 201, out of stack space
        "rt_stack_probe" => w => {
          w.If(w.Cmp(IrCmpPred.Ne, w.B.Call(IrType.I32, this.Declare("sys_stack_low", IrType.I32), []), w.I32(0)),
            () => w.B.Call(IrType.Void, this.ErrorFunction, w.I32(201)));
          w.B.Ret();
        },
        // DEF SEG and the memory it names: a segment and an offset, which the back end turns into
        // whatever address the target has for them (FarPointerFlattening)
        "rt_defseg_reset" => w => {
          w.B.Store(IrBuilder.ConstInt(IrType.I16, 0), this.DefaultSegment);
          w.B.Ret();
        },
        "rt_peek" => w => w.B.Ret(w.B.ZExt(w.B.Load(IrType.I8, this.Segmented(w, w.Function.Parameters[0])), w.Function.ReturnType)),
        "rt_peeki" or "rt_peekl" => w => w.B.Ret(w.B.Load(w.Function.ReturnType, this.Segmented(w, w.Function.Parameters[0]))),
        "rt_poke" => w => {
          w.B.Store(w.B.Trunc(w.Function.Parameters[1], IrType.I8), this.Segmented(w, w.Function.Parameters[0]));
          w.B.Ret();
        },
        "rt_poke_str" => w => {
          var text = w.Function.Parameters[1];
          var (bytes, length) = this.View(w, text, w.Ix(1), this.Length(w, text));
          var i = w.Variable(w.Index, w.Ix(0));
          w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), length), () => {
            var at = w.B.Add(w.Function.Parameters[0], w.FromIndex(i.Get(), IrType.I16));
            w.B.Store(w.ByteAt(bytes, i.Get()), this.Segmented(w, at));
            i.Set(w.B.Add(i.Get(), w.Ix(1)));
          });
          this.Consume(w, text);
          w.B.Ret();
        },
        // FRE(-11), the EMS a VIRTUAL array draws on: here every array draws on the one heap, so it is
        // the heap's room never yet handed out
        "rt_ems_fre" => w => w.B.Ret(w.FromIndex(w.B.Sub(w.Ix(heapBytes), w.B.Load(w.Index, this.HeapTop)), IrType.I32)),
        "rt_codeseg" => w => w.B.Ret(IrBuilder.ConstInt(w.Function.ReturnType, 0)),
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

    /// <summary>
    /// The entries string-allocation coalescing routes a region through. Its preflight is the DOS
    /// heap's - a reservation this heap has no use for - so the region's markers do nothing and each
    /// coalesced producer is its ordinary self; a borrowing one copies the source it was lent first,
    /// since the ordinary producer consumes what it is given.
    /// </summary>
    private Action<IrWriter>? CoalescedRoutine(IrFunction function) {
      if (function.Name is "rt_str_coalesce_begin" or "rt_str_coalesce_end")
        return w => w.B.Ret();
      if (!function.Name.EndsWith("_coalesced", StringComparison.Ordinal))
        return null;
      var borrows = function.Name.EndsWith("_borrow_coalesced", StringComparison.Ordinal);
      var plainName = function.Name[..^(borrows ? "_borrow_coalesced" : "_coalesced").Length];
      return w => {
        var plain = this.Declare(plainName, function.ReturnType, [.. function.Parameters.Select(p => p.Type)]);
        if (plain.IsDeclaration)
          this.DefineIfKnown(plain);
        var arguments = w.Function.Parameters.Cast<IrValue>().ToArray();
        if (borrows)
          arguments[0] = w.B.Call(IrType.Ptr, this.Declare("rt_str_dup", IrType.Ptr, IrType.Ptr) is var dup && dup.IsDeclaration
            ? this.Defined(dup) : dup, arguments[0]);
        w.B.Ret(w.B.Call(function.ReturnType, plain, arguments));
      };
    }

    private IrGlobalVariable DefaultSegment => this.Shared(new IrGlobalVariable("rt_defseg", IrType.I16) { IsZeroInitialized = true });

    /// <summary>The byte at DEF SEG:<paramref name="offset"/>.</summary>
    private IrValue Segmented(IrWriter w, IrValue offset) => w.B.FarPtr(w.B.Load(IrType.I16, this.DefaultSegment), offset);

    private IrFunction Defined(IrFunction function) {
      this.DefineIfKnown(function);
      return function;
    }

    private void Build(IrFunction function, Action<IrWriter> body) {
      var writer = new IrWriter(function, this.Index);
      body(writer);
      // defined before the middle end, it may still be called by the entries defined after it
      function.MayGainCallers = !cleanUp;
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
    /// <summary>
    /// The runtime's state cell named by <paramref name="cell"/>, shared with every earlier definition
    /// in the module. The runtime is defined twice - its early entries before the middle end, the rest
    /// after it - and each definition is a fresh writer: a second cell of the same name would give the
    /// two halves two file tables or two heaps, so an OPEN inlined into main wrote a descriptor that the
    /// later PRINT # never saw.
    /// </summary>
    private IrGlobalVariable Shared(IrGlobalVariable cell) => module.FindGlobal(cell.Name) ?? module.AddGlobal(cell);

    /// <summary>
    /// The console's print column, 0-based: <c>rt_col</c>, the cell the lowering reads POS from and
    /// STDOUT resets, so that this runtime and the program agree on where the cursor is.
    /// </summary>
    private IrGlobalVariable Column => this._column ??= module.FindGlobal("rt_col")
      ?? module.AddGlobal(new IrGlobalVariable("rt_col", IrType.I16) { IsZeroInitialized = true });

    private IrValue GetColumn(IrWriter w) => w.ToIndex(w.B.Load(IrType.I16, this.Column));
    private void SetColumn(IrWriter w, IrValue column) => w.B.Store(w.FromIndex(column, IrType.I16), this.Column);

    /// <summary><c>rt.out(buffer, length)</c>: writes, and keeps the column.</summary>
    private IrFunction Out => this._out ??= this.Internal("rt.out", IrType.Void, [IrType.Ptr, this.Index], w => {
      var (buffer, length) = (w.Function.Parameters[0], w.Function.Parameters[1]);
      // while a USING$ is being built, the text is the string's, not the screen's
      if (this.CapturesOutput)
        w.If(w.Cmp(IrCmpPred.Ne, w.B.Load(w.Index, this.Capturing), w.Ix(0)), () => {
          this.Capture(w, buffer, length);
          w.Return();
        });
      var i = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), length), () => {
        var column = this.GetColumn(w);
        w.If(w.Cmp(IrCmpPred.Eq, w.ByteAt(buffer, i.Get()), w.I8('\n')),
          () => {
            this.SetColumn(w, w.Ix(0));
            if (this.TracksRow)
              w.If(w.Cmp(IrCmpPred.Slt, w.B.Load(IrType.I32, this.Row), w.I32(24)),
                () => w.B.Store(w.B.Add(w.B.Load(IrType.I32, this.Row), w.I32(1)), this.Row));
          },
          () => this.SetColumn(w, w.B.Add(column, w.Ix(1))));
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
      var column = this.GetColumn(w);
      w.B.CondBr(w.Cmp(IrCmpPred.Ne, w.B.Binary(IrBinaryOp.SRem, column, w.Ix(ZoneWidth)), w.Ix(0)), loop, done);
      w.B.Position(done);
      w.B.Ret();
    }

    private void PrintTab(IrWriter w) {
      var target = w.Variable(w.Index, w.ToIndex(w.Function.Parameters[0]));
      w.If(w.Cmp(IrCmpPred.Slt, target.Get(), w.Ix(1)), () => target.Set(w.Ix(1)));
      w.While(() => w.Cmp(IrCmpPred.Sgt, this.GetColumn(w), w.B.Sub(target.Get(), w.Ix(1))),
        () => this.OutByte(w, w.I8('\n')));
      w.While(() => w.Cmp(IrCmpPred.Slt, this.GetColumn(w), w.B.Sub(target.Get(), w.Ix(1))),
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
