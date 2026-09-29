using PowerBasic.Compiler.Ir;
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
    private Mos6502Runtime _runtime = null!;
    private M6502Label _staging;
    private int _stagingBytes;

    public Mos6502Assembler.Image Generate(int origin) {
      this._runtime = new(this._asm);
      var defined = module.Functions.Where(function => !function.IsDeclaration).ToList();
      var main = defined.FirstOrDefault(function => function.Name == "main")
        ?? throw Decline("the module has no main program");
      foreach (var function in defined)
        Validate(function);

      foreach (var function in defined)
        this._entries.Add(function, this._asm.NewLabel(function.Name));
      this.FindRecursion(defined);
      foreach (var function in defined)
        this._frames.Add(function, LayOut(function, this._asm.NewLabel(function.Name + ".frame")));
      foreach (var global in module.Globals.OfType<IrGlobalVariable>())
        this._globals.Add(global, this._asm.NewLabel(global.Name));
      this._staging = this._asm.NewLabel("staging");
      this._stagingBytes = defined.Select(function => function.Parameters.Sum(parameter => SizeOf(parameter.Type)))
        .DefaultIfEmpty(0).Max();

      var uninitialized = this._asm.NewLabel("uninitialized");
      this._runtime.EmitStartup(this._entries[main], uninitialized, this.UninitializedBytes());
      foreach (var function in defined)
        new FunctionGenerator(this, function).Generate();
      this._runtime.EmitRequested();
      foreach (var (global, label) in this._globals.Where(pair => !IsUninitialized(pair.Key))) {
        this._asm.Bind(label);
        this._asm.Bytes(InitialBytes(global));
      }

      this._asm.BeginUninitialized();
      this._asm.Bind(uninitialized);
      foreach (var (function, frame) in this._frames) {
        this._asm.Bind(frame.Start);
        this._asm.Reserve(frame.Size);
      }
      foreach (var (global, label) in this._globals.Where(pair => IsUninitialized(pair.Key))) {
        this._asm.Bind(label);
        this._asm.Reserve(SizeOf(global.ValueType) * global.Count);
      }
      this._asm.Bind(this._staging);
      this._asm.Reserve(this._stagingBytes);
      return this._asm.Assemble(origin);
    }

    private int UninitializedBytes()
      => this._frames.Values.Sum(frame => frame.Size)
        + this._globals.Keys.Where(IsUninitialized).Sum(global => SizeOf(global.ValueType) * global.Count)
        + this._stagingBytes;

    private static bool IsUninitialized(IrGlobalVariable global) => global.Bytes is null;

    private static byte[] InitialBytes(IrGlobalVariable global) {
      if (global.FloatingValues is not null)
        throw Decline($"global '{global.Name}' holds floating-point values, which have no 6502 lowering yet");
      var size = SizeOf(global.ValueType) * global.Count;
      var bytes = new byte[Math.Max(size, global.Bytes!.Length)];
      global.Bytes.CopyTo(bytes, 0);
      return bytes;
    }

    /// <summary>Refuses, up front and by name, what this back end cannot lower.</summary>
    private static void Validate(IrFunction function) {
      if (function.HasInlineAsm)
        throw Decline($"'{function.Name}' contains inline assembly, which is x86 text");
      if (function.HasErrorHandler)
        throw Decline($"'{function.Name}' traps errors (ON ERROR), which has no 6502 lowering yet");
      _ = SizeOf(function.ReturnType);
      foreach (var parameter in function.Parameters)
        _ = SizeOf(parameter.Type);
    }

    /// <summary>Gives every argument, local and SSA value of <paramref name="function"/> its fixed address.</summary>
    private static Frame LayOut(IrFunction function, M6502Label start) {
      var frame = new Frame(start);
      foreach (var parameter in function.Parameters)
        frame.Add(parameter, SizeOf(parameter.Type));
      foreach (var instruction in function.Blocks.SelectMany(block => block.Instructions)) {
        if (instruction is IrAlloca alloca)
          frame.Add(alloca, SizeOf(alloca.Allocated) * alloca.Count);
        else if (!instruction.Type.IsVoid && (instruction is IrPhi || !instruction.HasNoUsers))
          frame.Add(instruction, SizeOf(instruction.Type));
      }
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
