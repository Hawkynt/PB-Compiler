using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Backend;

/// <summary>The order in which argument groups are placed on the 16-bit x86 stack.</summary>
public enum X86StackArgumentOrder { LeftToRight, RightToLeft }

/// <summary>Which side restores SP after a 16-bit x86 stack call.</summary>
public enum X86StackCleanup { Caller, Callee }

/// <summary>The return-address width used by a 16-bit x86 call.</summary>
public enum X86CallDistance { Near, Far }

/// <summary>The BP-relative incoming-parameter layout of a stack-only x86-16 function definition.</summary>
public readonly record struct X86DefinitionStackLayout(int[] ParameterOffsets, int ParameterBytes,
    IReadOnlyList<Reg>? RegisterSpills = null) {

  /// <summary>
  /// The argument registers a register convention's prologue pushes, in parameter order, to give the
  /// leading parameters the negative offsets in <see cref="ParameterOffsets"/>. Empty for a stack ABI.
  /// </summary>
  public IReadOnlyList<Reg> Spills => this.RegisterSpills ?? [];
}

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
  /// <summary>The environment far pointer a pb36 closure call hands its callee - offset, then segment.</summary>
  private static readonly IReadOnlyList<Reg> _CLOSURE_ENV_REGISTERS = Array.AsReadOnly(new[] { Reg.BX, Reg.CX });
  private static readonly X86CallAbi _BASIC_CLOSURE = new(IrCallConvention.BasicClosure,
    X86StackArgumentOrder.LeftToRight, X86StackCleanup.Callee, X86CallDistance.Far, _CLOSURE_ENV_REGISTERS);

  /// <summary>Returns the compiler's near, real-mode DOS ABI for a source convention.</summary>
  public static X86CallAbi For(IrCallConvention convention) => convention switch {
    IrCallConvention.Basic => _BASIC,
    IrCallConvention.Pascal => _PASCAL,
    IrCallConvention.Cdecl => _CDECL,
    IrCallConvention.Stdcall => _STDCALL,
    IrCallConvention.Fastcall => _FASTCALL,
    IrCallConvention.Watcall => _WATCALL,
    IrCallConvention.BasicClosure => _BASIC_CLOSURE,
    _ => throw new ArgumentOutOfRangeException(nameof(convention), convention, null),
  };

  /// <summary>
  /// Derives the complete incoming stack layout of an IR function definition. This is deliberately
  /// definition-side: generated functions have no <c>ProcedureSymbol</c>, but their IR signature and
  /// <see cref="IrFunction.Convention"/> are sufficient for every ABI the routed backend supports. A
  /// register convention's leading word parameters get the negative offsets of the cells its prologue
  /// pushes them into (<see cref="X86DefinitionStackLayout.Spills"/>).
  /// </summary>
  public static bool TryDefinitionStackLayout(IrFunction function,
      out X86DefinitionStackLayout layout, out string? declineReason) {
    ArgumentNullException.ThrowIfNull(function);
    layout = default;
    declineReason = null;

    var abi = For(function.Convention);
    if (abi.Distance != X86CallDistance.Near) {
      declineReason = $"far definition ABI is not supported ({function.Convention})";
      return false;
    }
    var sizes = new int[function.Parameters.Count];
    for (var i = 0; i < sizes.Length; ++i)
      if (StackSlotSize(function.Parameters[i].Type) is not { } size) {
        declineReason = $"parameter {i} has no routed x86-16 ABI slot ({function.Parameters[i].Type})";
        return false;
      } else
        sizes[i] = size;

    // A register convention's leading parameters arrive in its argument registers and are pushed by
    // the prologue in parameter order, so parameter 0 lives at [BP-2], parameter 1 at [BP-4], ...
    // Each has to be one word - that is what one register holds.
    var registerCount = Math.Min(abi.ArgumentRegisters.Count, sizes.Length);
    var offsets = new int[sizes.Length];
    for (var i = 0; i < registerCount; ++i) {
      if (sizes[i] != 2) {
        declineReason = $"register parameter {i} is not one word ({function.Parameters[i].Type})";
        return false;
      }
      offsets[i] = -2 * (i + 1);
    }

    var offset = 4;
    var stack = Enumerable.Range(registerCount, sizes.Length - registerCount);
    IEnumerable<int> order = abi.StackArgumentOrder == X86StackArgumentOrder.RightToLeft ? stack : stack.Reverse();
    foreach (var index in order) {
      offsets[index] = offset;
      offset += sizes[index];
    }

    layout = new X86DefinitionStackLayout(offsets, offset - 4, [.. abi.ArgumentRegisters.Take(registerCount)]);
    return true;
  }

  /// <summary>Bytes one IR argument occupies in the routed 16-bit stack ABI, or null when unsupported.</summary>
  private static int? StackSlotSize(IrType type) => type switch {
    { IsPointer: true, AddressSpace: 0 } => 2,
    { IsInteger: true, Bits: 16 } => 2,
    { IsInteger: true, Bits: 32 } => 4,
    { IsIeeeFloat: true, Bits: 32 } => 4,
    { IsIeeeFloat: true, Bits: 64 } => 8,
    _ => null,
  };
}
