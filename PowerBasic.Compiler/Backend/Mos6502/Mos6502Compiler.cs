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
/// <b>Floating point</b> is the runtime's soft float: each operation unpacks its operands, works in
/// 72 bits and rounds once into the result's IEEE format. <b>Everything else the program calls</b> -
/// PRINT, strings, BASIC's errors - is the portable runtime, IR compiled here like the program; its
/// output primitive <c>sys_write</c> is the KERNAL's <c>CHROUT</c>.
/// </para>
///
/// <para>
/// <b>What it declines.</b> Math functions, inline assembly, error trapping and indirect calls have
/// no 6502 lowering yet; a module that uses one is declined with the construct named, never compiled
/// into something that does something else.
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
    } catch (M6502ImageTooLargeException exception) {
      declined = exception.Message;
      return null;
    }
  }

  private sealed class DeclinedException(string message) : Exception(message);

  private static DeclinedException Decline(string message) => new(message);

  /// <summary>The bytes a value of <paramref name="type"/> occupies.</summary>
  private static int SizeOf(IrType type) {
    if (type.IsFloat) {
      if (type.IsMbf)
        throw Decline("Microsoft Binary Format floats have no 6502 lowering yet");
      return type.Bits switch { 32 => 4, 64 => 8, 80 => 10, _ => throw Decline($"{type} has no 6502 lowering") };
    }
    // a far-heap pointer is DOS's; the 6502 has one 64 KB space
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

  /// <summary>
  /// One function's storage: each value's offset and size within the frame, and where the frame
  /// sits in the program-wide overlay (<see cref="Base"/>) - placed by the module, which also says
  /// how an overlay offset becomes an address.
  /// </summary>
  private sealed class Frame {
    public int Size { get; private set; }
    public Dictionary<IrValue, (int Offset, int Size)> Slots { get; } = [];
    public int Base { get; set; }

    /// <summary>The key of the slot where a procedure with a handler keeps its caller's.</summary>
    public IrValue SavedHandler { get; } = new IrUndef(IrType.I64);

    /// <summary>A frame that recursion saves and restores must be one contiguous block of RAM.</summary>
    public bool Contiguous { get; init; }

    public Func<int, int, bool, M6502Address> Place { get; init; } = null!;

    public void Add(IrValue value, int bytes) {
      this.Slots.Add(value, (this.Size, bytes));
      this.Size += bytes;
    }

    /// <summary>A slot at a chosen offset - one a value no longer live has given up - growing the frame to cover it.</summary>
    public void AddAt(IrValue value, int offset, int bytes) {
      this.Slots.Add(value, (offset, bytes));
      this.Size = Math.Max(this.Size, offset + bytes);
    }

    public bool Holds(IrValue value) => this.Slots.ContainsKey(value);

    public M6502Address AddressOf(IrValue value) {
      var (offset, size) = this.Slots[value];
      return this.Place(this.Base + offset, size, this.Contiguous);
    }

    /// <summary>The first byte of a contiguous frame.</summary>
    public M6502Address Start => this.Place(this.Base, this.Size, true);
  }
}
