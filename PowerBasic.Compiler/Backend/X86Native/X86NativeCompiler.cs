using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Backend.X86Native;

/// <summary>
/// Compiles an optimized IR module - the program and the portable runtime defined into it - to i386
/// or x64 machine code, with no tool but its own assembler.
///
/// <para>
/// <b>Storage.</b> Every SSA value and local has a slot in its function's frame, addressed from the
/// frame pointer; arguments are written into an area the caller reserves on the stack, and a result
/// comes back through one static return area, which the caller reads before anything else can run.
/// Integers are computed in <c>eax</c>/<c>rax</c> with <c>ecx</c>/<c>edx</c> beside them; floats on
/// the x87 stack, loaded from and rounded back into the IEEE format their IR type names - the same
/// 80-bit arithmetic the DOS programs get.
/// </para>
///
/// <para>
/// <b>The operating system</b> is reached only through the two primitives the portable runtime is
/// written against: <c>sys_write(buffer, length)</c> and <c>sys_exit(code)</c>, each a Linux system
/// call emitted in place. Everything else - <c>PRINT</c>'s formatting, BASIC's errors - is the
/// portable runtime (<c>Runtime/Portable</c>), IR compiled here like the program.
/// </para>
/// </summary>
public static partial class X86NativeCompiler {

  /// <summary>
  /// The program's code and data, and its two ways in: <see cref="Start"/>, the executable's entry,
  /// and <see cref="Export"/>, <c>pb_main</c>, which a C program calls.
  /// </summary>
  public sealed record Program(X86Assembler Assembler, X86Label Start, X86Label Export);

  public static Program? TryCompile(IrModule module, X86Machine machine, out string? declined) {
    ArgumentNullException.ThrowIfNull(module);
    try {
      var program = new ModuleGenerator(module, machine).Generate();
      declined = null;
      return program;
    } catch (DeclinedException exception) {
      declined = exception.Message;
      return null;
    }
  }

  private sealed class DeclinedException(string message) : Exception(message);

  private static DeclinedException Decline(string message) => new(message);

  /// <summary>What an IR value is at the machine level.</summary>
  private abstract record Operand;

  /// <summary>An integer constant.</summary>
  private sealed record ConstantOperand(long Value) : Operand;

  /// <summary>A value stored in memory.</summary>
  private sealed record MemoryOperand(X86Mem Place) : Operand;

  /// <summary>A value that IS the address of some memory: a local, a global, a folded offset of one.</summary>
  private sealed record AddressOperand(X86Mem Place) : Operand;
}
