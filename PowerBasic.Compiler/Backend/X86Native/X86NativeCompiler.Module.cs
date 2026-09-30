using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Backend.X86Native;

public static partial class X86NativeCompiler {

  /// <summary>The whole-program half: sizes, frames, globals, constants and the entry point.</summary>
  private sealed partial class ModuleGenerator(IrModule module, X86Machine machine) {

    private readonly X86Assembler _asm = new(machine);
    private readonly Dictionary<IrFunction, X86Label> _entries = [];
    private readonly Dictionary<IrFunction, Frame> _frames = [];
    private readonly Dictionary<IrGlobalVariable, X86Label> _globals = [];
    private readonly Dictionary<string, X86Label> _constants = [];
    private X86Label _returnArea;
    private X86Label _staging;
    private int _stagingBytes = 16;
    private X86Label _controlWord;

    /// <summary>The portable runtime's <c>rt_error</c>, through which BASIC's run-time errors are raised.</summary>
    private IrFunction? ErrorFunction
      => module.FindFunction("rt_error") is { IsDeclaration: false } error ? error : null;

    private X86Width Word => machine.Word();
    private int WordBytes => machine.Word().Bytes();

    public Program Generate() {
      var defined = module.Functions.Where(function => !function.IsDeclaration).ToList();
      var main = defined.FirstOrDefault(function => function.Name == "main")
        ?? throw Decline("the module has no main program");
      foreach (var function in defined) {
        if (function.HasInlineAsm)
          throw Decline($"'{function.Name}' contains inline assembly, which is 16-bit DOS text");
        if (function.HasErrorHandler)
          throw Decline($"'{function.Name}' traps errors (ON ERROR), which has no native lowering yet");
        this._entries.Add(function, this._asm.NewLabel(function.Name));
      }
      foreach (var function in defined)
        this._frames.Add(function, this.LayOut(function));
      foreach (var global in module.Globals)
        this._globals.Add(global, this._asm.NewLabel(global.Name));
      this._returnArea = this._asm.NewLabel("pb.return");
      this._staging = this._asm.NewLabel("pb.staging");
      this._controlWord = this._asm.NewLabel("pb.controlWord");

      // _start: the program, then exit(0) - the stack pointer as the kernel left it
      var start = this._asm.NewLabel("_start");
      this._asm.Bind(start);
      this._asm.Call(this._entries[main]);
      this.EmitExit(null);

      // pb_main: the program for a C caller, which expects EBX, ESI, EDI and EBP kept
      var export = this._asm.NewLabel("pb_main");
      this._asm.Bind(export);
      foreach (var register in (X86Reg[])[X86Reg.Bx, X86Reg.Si, X86Reg.Di, X86Reg.Bp])
        this._asm.Push(register);
      this._asm.Call(this._entries[main]);
      foreach (var register in (X86Reg[])[X86Reg.Bp, X86Reg.Di, X86Reg.Si, X86Reg.Bx])
        this._asm.Pop(register);
      this._asm.Ret();

      foreach (var function in defined)
        new FunctionGenerator(this, function).Generate();

      foreach (var (global, label) in this._globals) {
        var size = this.SizeOf(global.ValueType) * global.Count;
        if (global.Bytes is null && global.FloatingValues is null) {
          this._asm.Reserve(label, Math.Max(size, 1), 16);
          continue;
        }
        var bytes = new byte[Math.Max(size, global.Bytes?.Length ?? 0)];
        if (global.FloatingValues is { } floats)
          for (var i = 0; i < floats.Length; ++i)
            FloatBytes(global.ValueType, floats[i]).CopyTo(bytes, i * this.SizeOf(global.ValueType));
        else
          global.Bytes!.CopyTo(bytes, 0);
        this._asm.DataBytes(label, bytes, 16);
      }
      this._asm.Reserve(this._returnArea, 16, 16);
      this._asm.Reserve(this._staging, this._stagingBytes, 16);
      this._asm.Reserve(this._controlWord, 4, 4);
      return new(this._asm, start, export);
    }

    /// <summary><c>exit(code)</c>: the code from memory, or zero.</summary>
    private void EmitExit(X86Mem? code) {
      if (machine == X86Machine.Amd64) {
        if (code is { } place)
          this._asm.Mov(X86Width.Dword, X86Reg.Di, place);
        else
          this._asm.Alu(X86Alu.Xor, X86Width.Dword, X86Reg.Di, X86Reg.Di);
        this._asm.MovImmediate(X86Width.Dword, X86Reg.Ax, 231);   // exit_group
      } else {
        if (code is { } place)
          this._asm.Mov(X86Width.Dword, X86Reg.Bx, place);
        else
          this._asm.Alu(X86Alu.Xor, X86Width.Dword, X86Reg.Bx, X86Reg.Bx);
        this._asm.MovImmediate(X86Width.Dword, X86Reg.Ax, 1);     // exit
      }
      this._asm.SystemCall();
    }

    /// <summary>The bytes a value of <paramref name="type"/> occupies in memory.</summary>
    public int SizeOf(IrType type) {
      if (type.IsFloat) {
        if (type.IsMbf)
          throw Decline("Microsoft Binary Format floats have no native lowering yet");
        return type.Bits switch { 32 => 4, 64 => 8, 80 => 10, _ => throw Decline($"{type} has no native lowering") };
      }
      if (type.IsFarPointer)
        throw Decline("far pointers have no meaning on a flat 32- or 64-bit target");
      if (type.IsPointer)
        return this.WordBytes;
      if (type.IsInteger)
        return type.Bits switch {
          1 or 8 => 1,
          16 => 2,
          32 => 4,
          64 => 8,
          _ => throw Decline($"{type.Bits}-bit integers have no native lowering yet"),
        };
      if (type.IsVoid)
        return 0;
      throw Decline($"type {type} has no native lowering");
    }

    private static X87Format FormatOf(IrType type) => type.Bits switch {
      32 => X87Format.Single,
      64 => X87Format.Double,
      _ => X87Format.Extended,
    };

    private static byte[] FloatBytes(IrType type, double value) => type.Bits switch {
      32 => BitConverter.GetBytes((float)value),
      64 => BitConverter.GetBytes(value),
      _ => Float80Bytes(IrFloat80.FromDouble(value)),
    };

    private static byte[] Float80Bytes(IrFloat80 value) {
      var bytes = new byte[10];
      BitConverter.GetBytes(value.Significand).CopyTo(bytes, 0);
      BitConverter.GetBytes(value.SignExponent).CopyTo(bytes, 8);
      return bytes;
    }

    /// <summary>A float constant, in its format, in the data section - one copy per distinct bit pattern.</summary>
    private X86Label Constant(IrConstantFloat constant) {
      if (constant.Type.IsMbf)
        throw Decline("Microsoft Binary Format floats have no native lowering yet");
      var key = $"{constant.Type.Bits}:{constant.BitPatternKey()}";
      if (this._constants.TryGetValue(key, out var label))
        return label;
      label = this._asm.NewLabel("pb.float." + key);
      var bytes = constant.Type.Bits == 80 ? Float80Bytes(constant.Float80) : FloatBytes(constant.Type, constant.Value);
      this._asm.DataBytes(label, bytes, 16);
      this._constants.Add(key, label);
      return label;
    }

    /// <summary>
    /// A function's frame: arguments above the frame pointer, in the area the caller filled; every
    /// local and SSA value below it, each aligned to its own size.
    /// </summary>
    private Frame LayOut(IrFunction function) {
      var frame = new Frame();
      var above = 2 * this.WordBytes;
      foreach (var parameter in function.Parameters) {
        frame.Places.Add(parameter, above);
        above += this.ArgumentSlot(parameter.Type);
      }
      var below = 0;
      void Add(IrValue value, int bytes) {
        var alignment = bytes >= 8 ? 8 : bytes >= 4 ? 4 : bytes >= 2 ? 2 : 1;
        below = (below + bytes + alignment - 1) / alignment * alignment;
        frame.Places.Add(value, -below);
      }
      foreach (var instruction in function.Blocks.SelectMany(block => block.Instructions)) {
        if (instruction is IrAlloca alloca)
          Add(alloca, Math.Max(1, this.SizeOf(alloca.Allocated) * alloca.Count));
        else if (!instruction.Type.IsVoid && (instruction is IrPhi || !instruction.HasNoUsers))
          Add(instruction, this.SizeOf(instruction.Type));
      }
      // scratch for conversions through memory
      Add(frame.Scratch, 16);
      frame.Size = (below + 15) / 16 * 16;
      return frame;
    }

    /// <summary>The stack bytes an argument of <paramref name="type"/> takes: its size, rounded up to a word.</summary>
    private int ArgumentSlot(IrType type) {
      var size = this.SizeOf(type);
      return (size + this.WordBytes - 1) / this.WordBytes * this.WordBytes;
    }

    /// <summary>One function's storage, as displacements from the frame pointer.</summary>
    private sealed class Frame {
      public Dictionary<IrValue, int> Places { get; } = [];
      public IrValue Scratch { get; } = new IrUndef(IrType.I64);
      public int Size { get; set; }
    }
  }
}
