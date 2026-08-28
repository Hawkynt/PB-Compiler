using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Backend;

/// <summary>The order in which argument groups are placed on the 16-bit x86 stack.</summary>
public enum X86StackArgumentOrder { LeftToRight, RightToLeft }

/// <summary>Which side restores SP after a 16-bit x86 stack call.</summary>
public enum X86StackCleanup { Caller, Callee }

/// <summary>The return-address width used by a 16-bit x86 call.</summary>
public enum X86CallDistance { Near, Far }

/// <summary>
/// The concrete x86-16 rules selected from a source-level calling-convention identity. Register
/// lists describe the compiler's existing DOS convention in argument order; remaining arguments
/// use <see cref="StackArgumentOrder"/>. Far variants will be separate descriptors once the source
/// language and memory model can distinguish them.
/// </summary>
public sealed record X86CallAbi(
    IrCallConvention Convention,
    X86StackArgumentOrder StackArgumentOrder,
    X86StackCleanup StackCleanup,
    X86CallDistance Distance,
    IReadOnlyList<Reg> ArgumentRegisters) {

  private static readonly IReadOnlyList<Reg> _NO_REGISTERS = Array.AsReadOnly(Array.Empty<Reg>());
  private static readonly IReadOnlyList<Reg> _FASTCALL_REGISTERS =
    Array.AsReadOnly(new[] { Reg.AX, Reg.DX, Reg.BX });
  private static readonly IReadOnlyList<Reg> _WATCALL_REGISTERS =
    Array.AsReadOnly(new[] { Reg.AX, Reg.DX, Reg.BX, Reg.CX });
  private static readonly X86CallAbi _BASIC = new(IrCallConvention.Basic,
    X86StackArgumentOrder.LeftToRight, X86StackCleanup.Callee, X86CallDistance.Near, _NO_REGISTERS);
  private static readonly X86CallAbi _PASCAL = new(IrCallConvention.Pascal,
    X86StackArgumentOrder.LeftToRight, X86StackCleanup.Callee, X86CallDistance.Near, _NO_REGISTERS);
  private static readonly X86CallAbi _CDECL = new(IrCallConvention.Cdecl,
    X86StackArgumentOrder.RightToLeft, X86StackCleanup.Caller, X86CallDistance.Near, _NO_REGISTERS);
  private static readonly X86CallAbi _STDCALL = new(IrCallConvention.Stdcall,
    X86StackArgumentOrder.RightToLeft, X86StackCleanup.Callee, X86CallDistance.Near, _NO_REGISTERS);
  private static readonly X86CallAbi _FASTCALL = new(IrCallConvention.Fastcall,
    X86StackArgumentOrder.LeftToRight, X86StackCleanup.Callee, X86CallDistance.Near, _FASTCALL_REGISTERS);
  private static readonly X86CallAbi _WATCALL = new(IrCallConvention.Watcall,
    X86StackArgumentOrder.RightToLeft, X86StackCleanup.Callee, X86CallDistance.Near, _WATCALL_REGISTERS);

  /// <summary>Returns the compiler's near, real-mode DOS ABI for a source convention.</summary>
  public static X86CallAbi For(IrCallConvention convention) => convention switch {
    IrCallConvention.Basic => _BASIC,
    IrCallConvention.Pascal => _PASCAL,
    IrCallConvention.Cdecl => _CDECL,
    IrCallConvention.Stdcall => _STDCALL,
    IrCallConvention.Fastcall => _FASTCALL,
    IrCallConvention.Watcall => _WATCALL,
    _ => throw new ArgumentOutOfRangeException(nameof(convention), convention, null),
  };
}
