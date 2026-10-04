using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Runtime.Portable;
using static PowerBasic.Compiler.Backend.Mos6502.M6502Op;
using Zp = PowerBasic.Compiler.Backend.Mos6502.Mos6502ZeroPage;

namespace PowerBasic.Compiler.Backend.Mos6502;

public static partial class Mos6502Compiler {

  /// <summary>The whole-program half: what exists, where it lives, what can recurse.</summary>
  private sealed partial class ModuleGenerator(IrModule module) {

    private readonly Mos6502Assembler _asm = new();
    private readonly Dictionary<IrFunction, M6502Label> _entries = [];
    private readonly Dictionary<IrFunction, Frame> _frames = [];
    private readonly Dictionary<IrGlobalVariable, M6502Label> _globals = [];
    private readonly Dictionary<IrFunction, int> _cycleOf = [];
    private readonly HashSet<IrFunction> _recursive = [];
    private readonly List<List<IrFunction>> _cyclesCalleesFirst = [];
    private M6502Label _overlay;

    /// <summary>
    /// ON ERROR's state as the DOS runtime keeps it, reserved only in a program that arms a handler:
    /// the handler - nought when disarmed, <see cref="_resumeNextStub"/> for ON ERROR RESUME NEXT -
    /// with the soft stack and the stack pointer it runs on (the triple TRY saves and restores as
    /// <c>rt_onerr</c>, <c>rt_onerr_bp</c> and <c>rt_onerr_sp</c>, so the stack pointer's cell is
    /// padded to an address's two bytes), the current statement's restart and resume addresses, and
    /// the pair a fault latched from them.
    /// </summary>
    private M6502Label _errorHandler, _errorSoftStack, _errorStack, _statementStart, _statementNext, _faultStart, _faultNext,
      _resumeNextStub;

    /// <summary>EXIT FAR's unwind point: where to land, and the soft stack and stack pointer to have back.</summary>
    private M6502Label _exitFarTarget, _exitFarSoftStack, _exitFarStack;

    private bool _trapsErrors, _exitsFar;

    private (M6502Label Cell, int Bytes)[] ErrorCells => [(this._errorHandler, 2), (this._errorSoftStack, 2),
      (this._errorStack, 2), (this._statementStart, 2), (this._statementNext, 2), (this._faultStart, 2), (this._faultNext, 2)];

    private (M6502Label Cell, int Bytes)[] ExitFarCells => [(this._exitFarTarget, 2), (this._exitFarSoftStack, 2), (this._exitFarStack, 1)];

    /// <summary>
    /// A jump to the address a cell holds, as <c>RTI</c> takes it - status, then the address itself -
    /// rather than <c>JMP (cell)</c>, which on an NMOS 6502 fetches the high byte from the wrong page
    /// when the cell's low byte sits at <c>$xxFF</c>, somewhere a RAM cell can land.
    /// </summary>
    private static void JumpThrough(Mos6502Assembler asm, M6502Address cell) {
      asm.Memory(Lda, cell.Plus(1));
      asm.Emit(Pha);
      asm.Memory(Lda, cell);
      asm.Emit(Pha);
      asm.Emit(Php);
      asm.Emit(Rti);
    }

    /// <summary>The RAM behind the frame overlay: as deep as the deepest chain of calls.</summary>
    private int OverlayBytes => this._frames.Values.Select(frame => frame.Base + frame.Size).DefaultIfEmpty(0).Max();

    private static int WindowBytes => Mos6502ZeroPage.FrameWindowEnd - Mos6502ZeroPage.FrameWindowStart;

    /// <summary>
    /// An overlay offset as an address: in the page-zero window when the whole slot fits there and
    /// the frame need not be contiguous, else in the overlay's RAM at the same offset. A slot in page
    /// zero is reached with two-byte instructions instead of three.
    /// </summary>
    private M6502Address Place(int offset, int size, bool contiguous)
      => !contiguous && offset + size <= WindowBytes
        ? M6502Address.Absolute(Mos6502ZeroPage.FrameWindowStart + offset)
        : new M6502Address(this._overlay, offset);

    /// <summary>
    /// The frame overlay. Two functions that are never active at the same time can share memory, and
    /// the call graph says which: a function's frame is placed above the frames of everything it calls,
    /// so a caller never overlaps a callee, and functions on different branches of the call tree share.
    /// Placing callees first - Tarjan's order - puts the innermost functions, the ones in the tightest
    /// loops, at the bottom: in the page-zero window. The members of one recursive cycle are laid side
    /// by side; each saves its own frame before a call back into the cycle, so a re-entry finds it.
    /// </summary>
    private void PlaceFrames() {
      this._overlay = this._asm.NewLabel("frames");
      foreach (var cycle in this._cyclesCalleesFirst) {
        var members = cycle.Where(this._frames.ContainsKey).ToList();
        var floor = members
          .SelectMany(member => member.Blocks.SelectMany(block => block.Instructions).OfType<IrCall>())
          .Select(call => call.Callee).OfType<IrFunction>()
          .Where(callee => this._frames.ContainsKey(callee) && !cycle.Contains(callee))
          .Select(callee => this._frames[callee].Base + this._frames[callee].Size)
          .DefaultIfEmpty(0).Max();
        foreach (var member in members) {
          this._frames[member].Base = floor;
          floor += this._frames[member].Size;
        }
      }
    }
    private Mos6502Runtime _runtime = null!;
    private M6502Label _staging;
    private int _stagingBytes;
    private readonly Dictionary<string, (M6502Label Label, byte[] Bytes)> _constants = [];

    /// <summary>Whether the program opens files: its reads then go through the file routines, and its exit closes them.</summary>
    private bool UsesFiles => module.FindFunction("sys_open") is not null;

    /// <summary>
    /// Whether the program may open a RANDOM or BINARY file, and so needs the file cache: it seeks,
    /// or it opens a file in a mode that is not a constant INPUT, OUTPUT or APPEND - the mode is
    /// <c>rt_file_open</c>'s third argument, and <c>sys_open</c>'s second once that is inlined.
    /// </summary>
    private bool Seeks => module.FindFunction("sys_seek") is not null
      || module.Functions.Where(function => !function.IsDeclaration && function.Name != "rt_file_open")
        .SelectMany(function => function.Blocks).SelectMany(block => block.Instructions).OfType<IrCall>()
        .Any(call => call.Callee is IrFunction { Name: "rt_file_open" or "sys_open" } callee
          && (callee.Name == "sys_open"
            ? call.Args.ElementAt(1) is not IrConstantInt { Value: (>= 0 and < 3) or 4 }   // sys_open's 4 is a directory
            : call.Args.ElementAt(2) is not IrConstantInt { Value: >= 0 and < 3 }));

    /// <summary>The program's ERR cell, when it reads ERR at all.</summary>
    private M6502Address? ErrorCode
      => module.Globals.FirstOrDefault(global => global.Name == "rt_err") is { } err ? this._globals[err] : null;

    /// <summary>A float constant in its IEEE format, in the data - one copy per bit pattern.</summary>
    private M6502Label Constant(IrConstantFloat constant) {
      if (constant.Type.IsMbf)
        throw Decline("Microsoft Binary Format floats have no 6502 lowering yet");
      var key = $"{constant.Type.Bits}:{constant.BitPatternKey()}";
      if (!this._constants.TryGetValue(key, out var entry)) {
        byte[] bytes;
        if (constant.Type.Bits == 80) {
          bytes = new byte[10];
          BitConverter.GetBytes(constant.Float80.Significand).CopyTo(bytes, 0);
          BitConverter.GetBytes(constant.Float80.SignExponent).CopyTo(bytes, 8);
        } else {
          bytes = constant.Type.Bits == 32 ? BitConverter.GetBytes((float)constant.Value) : BitConverter.GetBytes(constant.Value);
        }
        entry = (this._asm.NewLabel("float." + key), bytes);
        this._constants.Add(key, entry);
      }
      return entry.Label;
    }

    public Mos6502Assembler.Image Generate(int origin) {
      this._runtime = new(this._asm);
      if (this.Seeks)
        this._runtime.FileCache = this._asm.NewLabel("fileCache");
      // the portable runtime's block copy is the runtime's own CopyMemory here: its IR body is not needed
      var defined = module.Functions.Where(function => !function.IsDeclaration && function.Name != PortableRuntime.NativeCopy).ToList();
      var main = defined.FirstOrDefault(function => function.Name == "main")
        ?? throw Decline("the module has no main program");
      foreach (var function in defined)
        Validate(function);

      foreach (var function in defined)
        this._entries.Add(function, this._asm.NewLabel(function.Name));
      this.FindRecursion(defined);
      foreach (var function in defined)
        this._frames.Add(function, this.LayOut(function));
      this.PlaceFrames();
      foreach (var global in module.Globals.OfType<IrGlobalVariable>())
        this._globals.Add(global, this._asm.NewLabel(global.Name));
      this._staging = this._asm.NewLabel("staging");
      this._trapsErrors = defined.Any(function => function.HasErrorHandler);
      this._exitsFar = module.FindFunction("rt_efar_arm") is not null;
      this._resumeNextStub = this._asm.NewLabel("resumeNext");
      this._exitFarTarget = this._asm.NewLabel("exitFarTarget");
      this._exitFarSoftStack = this._asm.NewLabel("exitFarSoftStack");
      this._exitFarStack = this._asm.NewLabel("exitFarStack");
      this._errorStack = this._asm.NewLabel("errorStack");
      this._errorSoftStack = this._asm.NewLabel("errorSoftStack");
      this._errorHandler = this._asm.NewLabel("errorHandler");
      this._statementStart = this._asm.NewLabel("statementStart");
      this._statementNext = this._asm.NewLabel("statementNext");
      this._faultStart = this._asm.NewLabel("faultStart");
      this._faultNext = this._asm.NewLabel("faultNext");
      this._stagingBytes = defined.Select(function => function.Parameters.Sum(parameter => SizeOf(parameter.Type)))
        .DefaultIfEmpty(0).Max();

      if (module.FindFunction("rt_error") is { IsDeclaration: false } error)
        this._runtime.ErrorFunction = (this._entries[error], this._frames[error].AddressOf(error.Parameters[0]));
      var uninitialized = this._asm.NewLabel("uninitialized");
      this._runtime.EmitStartup(this._entries[main], uninitialized, this.UninitializedBytes(), closeFiles: this.UsesFiles);
      foreach (var function in defined)
        new FunctionGenerator(this, function).Generate();
      if (this._trapsErrors) {
        // ON ERROR RESUME NEXT arms this as its handler: a fault goes straight on with the next statement
        this._asm.Bind(this._resumeNextStub);
        JumpThrough(this._asm, this._faultNext);
      }
      this._runtime.EmitRequested();
      foreach (var (label, bytes) in this._constants.Values) {
        this._asm.Bind(label);
        this._asm.Bytes(bytes);
      }
      foreach (var (global, label) in this._globals.Where(pair => !IsUninitialized(pair.Key))) {
        this._asm.Bind(label);
        this._asm.Bytes(InitialBytes(global));
      }

      this._asm.BeginUninitialized();
      this._asm.Bind(uninitialized);
      this._asm.Bind(this._overlay);
      this._asm.Reserve(this.OverlayBytes);
      foreach (var (global, label) in this._globals.Where(pair => IsUninitialized(pair.Key))) {
        this._asm.Bind(label);
        this._asm.Reserve(SizeOf(global.ValueType) * global.Count);
      }
      this._asm.Bind(this._staging);
      this._asm.Reserve(this._stagingBytes);
      if (this._runtime.FileCache is { } cache) {
        this._asm.Bind(cache);
        this._asm.Reserve(Mos6502Runtime.FileCacheBytes);
      }
      foreach (var (cell, bytes) in (this._trapsErrors ? this.ErrorCells : []).Concat(this._exitsFar ? this.ExitFarCells : [])) {
        this._asm.Bind(cell);
        this._asm.Reserve(bytes);
      }
      return this._asm.Assemble(origin);
    }

    private int UninitializedBytes()
      => this.OverlayBytes + (this._trapsErrors ? this.ErrorCells.Sum(cell => cell.Bytes) : 0)
        + (this._exitsFar ? this.ExitFarCells.Sum(cell => cell.Bytes) : 0)
        + (this.Seeks ? Mos6502Runtime.FileCacheBytes : 0)
        + this._globals.Keys.Where(IsUninitialized).Sum(global => SizeOf(global.ValueType) * global.Count)
        + this._stagingBytes;

    private static bool IsUninitialized(IrGlobalVariable global) => global.Bytes is null && global.FloatingValues is null;

    private static byte[] InitialBytes(IrGlobalVariable global) {
      var size = SizeOf(global.ValueType) * global.Count;
      var bytes = new byte[Math.Max(size, global.Bytes?.Length ?? 0)];
      if (global.FloatingValues is { } floats) {
        if (global.ValueType.Bits == 80)
          throw Decline($"global '{global.Name}' holds 80-bit float values given as doubles");
        for (var i = 0; i < floats.Length; ++i)
          (global.ValueType.Bits == 32 ? BitConverter.GetBytes((float)floats[i]) : BitConverter.GetBytes(floats[i]))
            .CopyTo(bytes, i * SizeOf(global.ValueType));
      } else {
        global.Bytes!.CopyTo(bytes, 0);
      }
      return bytes;
    }

    /// <summary>Refuses, up front and by name, what this back end cannot lower.</summary>
    private static void Validate(IrFunction function) {
      if (function.HasInlineAsm)
        throw Decline($"'{function.Name}' contains inline assembly, which is x86 text");
      _ = SizeOf(function.ReturnType);
      foreach (var parameter in function.Parameters)
        _ = SizeOf(parameter.Type);
    }

    /// <summary>
    /// Gives every argument, local and SSA value of <paramref name="function"/> its place in the frame.
    /// Arguments, locals and the saved handler each keep a place of their own; an SSA value shares one
    /// with values whose lives do not overlap its own (<see cref="ValueLiveRanges"/>), so a frame is as big
    /// as the most that is alive at once rather than the sum of everything - and more of it fits in
    /// the page-zero window, where an access is a byte shorter.
    /// </summary>
    private Frame LayOut(IrFunction function) {
      var frame = new Frame { Contiguous = this._recursive.Contains(function), Place = this.Place };
      foreach (var parameter in function.Parameters)
        frame.Add(parameter, SizeOf(parameter.Type));
      var values = new List<IrInstruction>();
      foreach (var instruction in function.Blocks.SelectMany(block => block.Instructions)) {
        if (instruction is IrAlloca alloca)
          frame.Add(alloca, SizeOf(alloca.Allocated) * alloca.Count);
        else if (!instruction.Type.IsVoid && (instruction is IrPhi || !instruction.HasNoUsers))
          values.Add(instruction);
      }
      // a procedure that arms a handler keeps its caller's here, to put back when it returns
      if (function.HasErrorHandler && function.Name != "main")
        frame.Add(frame.SavedHandler, 6);
      // a function with a handler can resume anywhere, which no live range accounts for
      if (function.HasErrorHandler) {
        foreach (var value in values)
          frame.Add(value, SizeOf(value.Type));
        return frame;
      }
      var ranges = ValueLiveRanges.Of(function, values);
      // a cast that keeps a value's low bytes reads them where they already are: it takes its source's
      // cell, whose life then lasts as long as either's
      var aliases = new Dictionary<IrInstruction, IrValue>(ReferenceEqualityComparer.Instance);
      var slotted = new HashSet<IrValue>(values, ReferenceEqualityComparer.Instance);
      IrValue RootOf(IrValue value) => value is IrInstruction aliased && aliases.TryGetValue(aliased, out var root) ? root : value;
      foreach (var value in values)
        if (value is IrCast { Op: IrCastOp.Trunc or IrCastOp.BitCast or IrCastOp.PtrToInt or IrCastOp.IntToPtr } cast
            && SizeOf(cast.Type) <= SizeOf(cast.Value.Type) && RootOf(cast.Value) is var source
            && (source is IrArgument || source is IrInstruction and not (IrPhi or IrGep or IrAlloca) && slotted.Contains(source))) {
          aliases[cast] = source;
          if (source is IrInstruction held)
            ranges[held] = (Math.Min(ranges[held].Start, ranges[cast].Start), Math.Max(ranges[held].End, ranges[cast].End));
        }
      var floor = frame.Size;
      var active = new List<(int End, int Offset, int Bytes)>();
      foreach (var value in values.Where(value => !aliases.ContainsKey(value)).OrderBy(value => ranges[value].Start)) {
        var (start, end) = ranges[value];
        active.RemoveAll(slot => slot.End < start);
        var bytes = SizeOf(value.Type);
        // the lowest offset no live slot overlaps
        var offset = floor;
        foreach (var slot in active.OrderBy(slot => slot.Offset))
          if (offset + bytes > slot.Offset && slot.Offset + slot.Bytes > offset)
            offset = slot.Offset + slot.Bytes;
        while (active.Any(slot => offset + bytes > slot.Offset && slot.Offset + slot.Bytes > offset))
          offset = active.Where(slot => offset + bytes > slot.Offset && slot.Offset + slot.Bytes > offset).Max(slot => slot.Offset + slot.Bytes);
        frame.AddAt(value, offset, bytes);
        active.Add((end, offset, bytes));
      }
      foreach (var (cast, source) in aliases)
        frame.AddAt(cast, frame.Slots[source].Offset, SizeOf(cast.Type));
      return frame;
    }

    /// <summary>Tarjan's strongly connected components over the direct-call graph.</summary>
    private void FindRecursion(List<IrFunction> defined) {
      var index = new Dictionary<IrFunction, int>();
      var low = new Dictionary<IrFunction, int>();
      var stack = new Stack<IrFunction>();
      var onStack = new HashSet<IrFunction>();
      var next = 0;
      var cycle = 0;

      IEnumerable<IrFunction> Callees(IrFunction function)
        => function.Blocks.SelectMany(block => block.Instructions).OfType<IrCall>()
          .Select(call => call.Callee).OfType<IrFunction>().Where(callee => !callee.IsDeclaration).Distinct();

      void Visit(IrFunction function) {
        index[function] = low[function] = next++;
        stack.Push(function);
        onStack.Add(function);
        foreach (var callee in Callees(function)) {
          if (!index.ContainsKey(callee)) {
            Visit(callee);
            low[function] = Math.Min(low[function], low[callee]);
          } else if (onStack.Contains(callee)) {
            low[function] = Math.Min(low[function], index[callee]);
          }
        }
        if (low[function] != index[function])
          return;
        var members = new List<IrFunction>();
        IrFunction member;
        do {
          member = stack.Pop();
          onStack.Remove(member);
          members.Add(member);
          this._cycleOf[member] = cycle;
        } while (member != function);
        if (members.Count > 1 || Callees(function).Contains(function))
          this._recursive.UnionWith(members);
        this._cyclesCalleesFirst.Add(members);
        ++cycle;
      }

      foreach (var function in defined)
        if (!index.ContainsKey(function))
          Visit(function);
    }

    /// <summary>Whether a call from <paramref name="caller"/> to <paramref name="callee"/> can re-enter the caller.</summary>
    private bool Reenters(IrFunction caller, IrFunction callee)
      => this._recursive.Contains(caller) && this._cycleOf[caller] == this._cycleOf[callee];
  }
}
