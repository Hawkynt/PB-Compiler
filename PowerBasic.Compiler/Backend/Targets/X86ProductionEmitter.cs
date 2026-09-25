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
    // DOS x86-16 production is emitted from the selected/allocated MFunction. This is the stable
    // machine-IR emitter, not the retired syntax/body emitter: all source semantics have already
    // crossed Bound AST -> HIR/SSA -> Low IR -> Machine IR before this point. It remains the canonical
    // 16-bit emission stage because it owns the PowerBASIC stack ABI, frame layout, far/data operands,
    // runtime symbol resolution and ISA-policy virtualization. The newer hosted byte encoder remains
    // available for the other x86 modes until it reaches behavioral parity for 16-bit DOS.
    if (function.Target.Name.Equals("x86-16", StringComparison.OrdinalIgnoreCase)) {
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
      return;
    }

    // ...and only the hosted modes need the hosted function, since it is what they emit from
    if (function.HostedFunction is null) {
      var shape = string.Join(", ", function.Function.AllInstructions
        .Select((instruction, index) => $"#{index}:{instruction.Opcode}({string.Join("|", instruction.Operands.Select(operand => operand.GetType().Name))})")
        .Distinct()
        .Take(64));
      throw new NotSupportedException($"x86 machine function '{function.Source.Name}' contains an unlowered opcode or operand: " +
        $"{function.HostedLoweringError ?? shape}");
    }

    var mode = function.Target.Name.ToLowerInvariant() switch {
      "x86-32" => X86Mode.Bit32,
      "x86-64" => X86Mode.Bit64,
      _ => throw new NotSupportedException($"target '{function.Target.Name}' is not an x86 hosted target")
    };
    var target = new X86TargetMachineEmitter(new X86InstructionEncoder(mode));
    var hosted = function.HostedFunction
      ?? throw new BackendInvariantException(
        "X86ProductionEmitter.EmitFunction",
        function.HostedLoweringError ?? $"machine function '{function.Source.Name}' has no hosted x86 lowering");

    // The target-owned address lowering currently materializes frame slots relative to BP. Keep a
    // canonical frame until the target prologue itself owns frame-elision proofs; silently eliding it
    // here would turn valid stack-slot addresses into references to the caller's frame.
    var code = target.Emit(hosted, preserveFramePointer: true,
      emitReturn: emitEpilogue is null);
    assembler.AppendMachineCode(code, symbol =>
      calleeLabel?.Invoke(symbol) ?? dataCellOf?.Invoke(symbol)?.Label);
    emitEpilogue?.Invoke(assembler);
  }
}
