using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>
/// The production x86 emission boundary.  Code generation must not know which concrete
/// instruction encoder is used for a target; it hands the allocated machine product back to
/// the target contract and this facade owns the final target emission decision.
///
/// The DOS image writer still needs the assembler-backed x86-16 spelling (labels, segment
/// fixups, inline assembly and runtime calls are DOS-image concerns).  Hosted x86-32/x86-64
/// products use <see cref="X86TargetMachineEmitter"/> and are never sent through that DOS
/// spelling.  Keeping the distinction here prevents callers from accidentally selecting an
/// emitter based on source-path choreography.
/// </summary>
public static class X86ProductionEmitter {
  public static void EmitFunction(
      Assembler assembler,
      IrMachineFunction function,
      IReadOnlyDictionary<int, Reg> allocation,
      IReadOnlyList<int> parameterOffsets,
      int calleeCleanupBytes,
      Func<string, Label?> calleeLabel,
      Func<string, int, (Label Label, int Offset)> dataCellOf,
      Action<Assembler>? emitEpilogue = null,
      bool alignLoops = false,
      bool allowFrameElision = false,
      IReadOnlyList<Reg>? registerSpills = null,
      Action<string, IReadOnlyList<string>>? emitInlineAsm = null) {
    ArgumentNullException.ThrowIfNull(assembler);
    ArgumentNullException.ThrowIfNull(function);
    // x86-16 is the DOS executable target.  Its final product contains target-owned labels,
    // segment relocations and inline-assembly blocks, so it is intentionally emitted by the
    // target facade rather than by CodeGenerator itself.
    if (function.Target.Name.Equals("x86-16", StringComparison.OrdinalIgnoreCase)) {
      MachineEmitter.EmitFunction(assembler, function, allocation, parameterOffsets,
        calleeCleanupBytes, calleeLabel, dataCellOf, emitEpilogue, alignLoops,
        allowFrameElision, registerSpills, emitInlineAsm);
      return;
    }

    throw new NotSupportedException(
      $"the hosted x86 emitter cannot append '{function.Target.Name}' machine code to a DOS image");
  }
}
