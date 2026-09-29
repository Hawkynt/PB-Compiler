using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>
/// Production x86-16 emission: the selected and allocated machine function, encoded by
/// <see cref="MachineEmitter"/>, which owns the PowerBASIC stack ABI, frame layout, far and data
/// operands, runtime symbol resolution and verbatim inline assembly.
/// </summary>
public static class X86ProductionEmitter {
  public static void EmitFunction(
      Assembler assembler,
      IrMachineFunction function,
      int[] parameterOffsets,
      int calleeCleanupBytes,
      Func<string, Label?>? calleeLabel,
      Func<string, Mem?>? dataCellOf,
      Action<Assembler>? emitEpilogue = null,
      bool alignLoops = false,
      bool allowFrameElision = false,
      IReadOnlyList<Reg>? registerSpills = null,
      Func<string, IAsmSymbolResolver, bool>? emitInlineAsm = null) {
    ArgumentNullException.ThrowIfNull(assembler);
    ArgumentNullException.ThrowIfNull(function);
    MachineEmitter.EmitFunction(
      assembler,
      function.Function,
      function.Allocation,
      parameterOffsets,
      calleeCleanupBytes,
      calleeLabel,
      dataCellOf,
      emitEpilogue,
      alignLoops,
      allowFrameElision,
      registerSpills,
      emitInlineAsm);
  }
}
