using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>Production x86 emission boundary.</summary>
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
    if (function.HostedFunction is null)
      throw new NotSupportedException($"x86 machine function '{function.Source.Name}' contains an unlowered opcode or operand");
    if (!function.Target.Name.Equals("x86-16", StringComparison.OrdinalIgnoreCase))
      throw new NotSupportedException($"target '{function.Target.Name}' cannot be appended to a DOS image");

    var target = new X86TargetMachineEmitter(new X86InstructionEncoder(X86Mode.Bit16));
    // The target-owned address lowering currently materializes frame slots relative to BP.  Keep a
    // canonical frame until the target prologue itself owns frame-elision proofs; silently eliding it
    // here would turn valid stack-slot addresses into references to the caller's frame.
    var code = target.Emit(function.HostedFunction, preserveFramePointer: true,
      emitReturn: emitEpilogue is null);
    assembler.AppendMachineCode(code, symbol =>
      calleeLabel?.Invoke(symbol) ?? dataCellOf?.Invoke(symbol)?.Label);
    // The runtime exit sequence is target policy, not instruction selection.  It is appended only
    // after the target emitter has finished the function and therefore cannot reintroduce a legacy
    // body-emission fallback.
    emitEpilogue?.Invoke(assembler);
  }
}
