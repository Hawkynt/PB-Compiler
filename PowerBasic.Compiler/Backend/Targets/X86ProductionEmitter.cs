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
    if (function.HostedFunction is null) {
      var shape = string.Join(", ", function.Function.AllInstructions
        .Select((instruction, index) => $"#{index}:{instruction.Opcode}({string.Join("|", instruction.Operands.Select(operand => operand.GetType().Name))})")
        .Distinct()
        .Take(64));
      throw new NotSupportedException($"x86 machine function '{function.Source.Name}' contains an unlowered opcode or operand: " +
        $"{function.HostedLoweringError ?? shape}");
    }
    var mode = function.Target.Name.ToLowerInvariant() switch {
      "x86-16" => X86Mode.Bit16,
      "x86-32" => X86Mode.Bit32,
      "x86-64" => X86Mode.Bit64,
      _ => throw new NotSupportedException($"target '{function.Target.Name}' is not an x86 hosted target")
    };
    var target = new X86TargetMachineEmitter(new X86InstructionEncoder(mode));
    var hosted = function.HostedFunction;

    // A DOS module body has no caller. Its IR terminators are still ordinary RETs because the
    // target-neutral function model does not know that "main returns" means "terminate the process".
    // Encoding those RETs literally makes execution pop an address from the PSP/user stack and wander
    // into runtime/data bytes. Funnel every target RET to one local end label instead; the artifact
    // policy hook emitted immediately after the machine body performs the actual DOS exit.
    if (emitEpilogue is not null) {
      const string exitLabel = "__pb_module_exit";
      var instructions = hosted.Instructions
        .Select(instruction => instruction.Opcode == X86TargetOpcode.Ret
          ? new X86TargetInstruction(X86TargetOpcode.Jmp, [], Symbol: exitLabel)
          : instruction)
        .ToArray();
      var labels = new Dictionary<string, int>(hosted.LabelInstructionIndices, StringComparer.Ordinal) {
        [exitLabel] = instructions.Length,
      };
      hosted = new X86TargetMachineFunction(
        hosted.Mode,
        hosted.Abi,
        instructions,
        labels,
        hosted.FrameSizeBytes,
        hosted.ArgumentLocations);
    }

    // The target-owned address lowering currently materializes frame slots relative to BP. Keep a
    // canonical frame until the target prologue itself owns frame-elision proofs; silently eliding it
    // here would turn valid stack-slot addresses into references to the caller's frame.
    var code = target.Emit(hosted, preserveFramePointer: true,
      emitReturn: emitEpilogue is null);
    assembler.AppendMachineCode(code, symbol =>
      calleeLabel?.Invoke(symbol) ?? dataCellOf?.Invoke(symbol)?.Label);
    // The runtime exit sequence is artifact policy, not instruction selection. It is appended only
    // after all machine returns have converged on the local end label above.
    emitEpilogue?.Invoke(assembler);
  }
}
