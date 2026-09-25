using PowerBasic.Compiler.Ir;
using static PowerBasic.Compiler.Backend.Mos6502.M6502Op;
using Zp = PowerBasic.Compiler.Backend.Mos6502.Mos6502ZeroPage;

namespace PowerBasic.Compiler.Backend.Mos6502;

/// <summary>
/// Compiles an optimized IR module to 6502 machine code: the program's functions, the runtime
/// routines they use, and its data, laid out from a load address.
///
/// <para>
/// <b>Storage.</b> The 6502 has three 8-bit registers and a 256-byte stack, so values live in
/// memory. Every function gets a STATIC frame - one fixed address per argument, SSA value and local
/// - and the code addresses it absolutely, which is the fastest access the chip has. Recursion is
/// what makes a static frame wrong, and the call graph says exactly where it can happen: a function
/// in a cycle saves its own frame to a soft stack before a call back into the cycle and restores it
/// after. A call out of the cycle, and every call in a program without recursion, costs nothing.
/// </para>
///
/// <para>
/// <b>What it declines.</b> Floating point, strings the runtime would have to manage, inline
/// assembly, error trapping and indirect calls have no 6502 lowering yet; a module that uses one is
/// declined with the construct named, never compiled into something that does something else.
/// </para>
/// </summary>
public static partial class Mos6502Compiler {

  /// <summary>Compiles <paramref name="module"/> for code starting at <paramref name="origin"/>.</summary>
  public static Mos6502Assembler.Image? TryCompile(IrModule module, int origin, int memoryTop, out string? declined) {
    ArgumentNullException.ThrowIfNull(module);
    try {
      var image = new ModuleGenerator(module).Generate(origin);
      if (image.End > memoryTop) {
        declined = $"the program needs memory up to ${image.End:X4}, past the ${memoryTop:X4} available";
        return null;
      }
      declined = null;
      return image;
    } catch (DeclinedException exception) {
      declined = exception.Message;
      return null;
    }
  }

  private sealed class DeclinedException(string message) : Exception(message);

  private static DeclinedException Decline(string message) => new(message);

  /// <summary>The bytes a value of <paramref name="type"/> occupies.</summary>
  private static int SizeOf(IrType type) {
    if (type.IsFloat)
      throw Decline("floating point has no 6502 lowering yet");
    if (type.IsFarPointer)
      throw Decline("far pointers have no 6502 meaning");
    if (type.IsPointer)
      return 2;
    if (type.IsInteger)
      return type.Bits switch {
        1 or 8 => 1,
        16 => 2,
        32 => 4,
        64 => 8,
        _ => throw Decline($"{type.Bits}-bit integers have no 6502 lowering yet"),
      };
    if (type.IsVoid)
      return 0;
    throw Decline($"type {type} has no 6502 lowering");
  }

  /// <summary>What an IR value is at the machine level.</summary>
  private abstract record Operand;

  /// <summary>A constant: byte k is bits 8k..8k+7.</summary>
  private sealed record ConstantOperand(long Value) : Operand;

  /// <summary>A value stored in memory: byte k is at <see cref="Address"/> + k.</summary>
  private sealed record MemoryOperand(M6502Address Address) : Operand;

  /// <summary>A value that IS an address known at assembly time: a local, a global, a folded offset of one.</summary>
  private sealed record AddressOperand(M6502Address Address) : Operand;

  /// <summary>One function's static storage.</summary>
  private sealed class Frame(M6502Label start) {
    public M6502Label Start { get; } = start;
    public int Size { get; private set; }
    public Dictionary<IrValue, int> Offsets { get; } = [];

    public void Add(IrValue value, int bytes) {
      this.Offsets.Add(value, this.Size);
      this.Size += bytes;
    }

    public M6502Address AddressOf(IrValue value) => new(this.Start, this.Offsets[value]);
  }
}
