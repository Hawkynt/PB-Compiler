using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>Bridges the allocated x86 machine product into the hosted target-owned representation.</summary>
public static class X86HostedMachineBuilder {
  public static bool TryBuild(IrMachineFunction machine, out X86TargetMachineFunction? hosted) {
    ArgumentNullException.ThrowIfNull(machine);
    hosted = null;
    var mode = machine.Target.Name switch {
      "x86-64" => X86Mode.Bit64,
      "x86-32" => X86Mode.Bit32,
      "x86-16" => X86Mode.Bit16,
      _ => (X86Mode?)null,
    };
    if (mode is not { } selectedMode)
      return false;

    var registers = new X86TargetRegisterFile(selectedMode);
    var abi = selectedMode switch {
      X86Mode.Bit64 => X86Abi.SysV64,
      X86Mode.Bit32 => X86Abi.I386Cdecl,
      _ => X86Abi.I8086Cdecl,
    };
    var instructions = new List<X86TargetInstruction>();
    foreach (var block in machine.Function.Blocks)
      foreach (var instruction in block.Instructions) {
        if (!TryBuildInstruction(instruction, selectedMode, registers, out var targetInstruction))
          return false;
        if (targetInstruction is not null)
          instructions.Add(targetInstruction);
      }

    hosted = new X86TargetMachineFunction(selectedMode,
      new X86TargetAbi(selectedMode, abi.Name, abi.StackAlignment, abi.ShadowSpaceBytes,
        abi.ArgumentRegisters, abi.ReturnRegister, abi.CalleeSavedRegisters), instructions);
    return true;
  }

  private static bool TryBuildInstruction(
      MInstr instruction,
      X86Mode mode,
      X86TargetRegisterFile registers,
      out X86TargetInstruction? target) {
    target = null;
    MachineRegister Register(MOperand operand) {
      if (operand is not MOperand.Register { Reg: var register } || register.IsVirtual)
        throw new InvalidOperationException("hosted x86 lowering requires allocated physical registers");
      var index = register.Physical.Index();
      if ((uint)index >= (uint)registers.Registers.Count)
        throw new InvalidOperationException("allocated register is not part of the hosted x86 register file");
      return registers.Registers[index];
    }

    try {
      switch (instruction.Opcode) {
        case MOpcode.Mov when instruction.Operands.Count == 2:
          if (instruction.Operands[1] is MOperand.Immediate immediate)
            target = new(X86TargetOpcode.MoveImmediate, [Register(instruction.Operands[0])], immediate.Value);
          else
            target = new(X86TargetOpcode.MoveRegister,
              [Register(instruction.Operands[0]), Register(instruction.Operands[1])]);
          return true;
        case MOpcode.Add when instruction.Operands[1] is MOperand.Immediate add:
          target = new(X86TargetOpcode.AddImmediate, [Register(instruction.Operands[0])], add.Value);
          return true;
        case MOpcode.Sub when instruction.Operands[1] is MOperand.Immediate sub:
          target = new(X86TargetOpcode.SubImmediate, [Register(instruction.Operands[0])], sub.Value);
          return true;
        case MOpcode.And when instruction.Operands[1] is MOperand.Immediate andImmediate:
          target = new(X86TargetOpcode.AndImmediate, [Register(instruction.Operands[0])], andImmediate.Value);
          return true;
        case MOpcode.Or when instruction.Operands[1] is MOperand.Immediate orImmediate:
          target = new(X86TargetOpcode.OrImmediate, [Register(instruction.Operands[0])], orImmediate.Value);
          return true;
        case MOpcode.Xor when instruction.Operands[1] is MOperand.Immediate xorImmediate:
          target = new(X86TargetOpcode.XorImmediate, [Register(instruction.Operands[0])], xorImmediate.Value);
          return true;
        case MOpcode.Cmp when instruction.Operands[1] is MOperand.Immediate compare:
          target = new(X86TargetOpcode.CompareImmediate, [Register(instruction.Operands[0])], compare.Value);
          return true;
        case MOpcode.Push when instruction.Operands.Count == 1:
          target = new(X86TargetOpcode.PushRegister, [Register(instruction.Operands[0])]);
          return true;
        case MOpcode.Pop when instruction.Operands.Count == 1:
          target = new(X86TargetOpcode.PopRegister, [Register(instruction.Operands[0])]);
          return true;
        case MOpcode.Ret:
          return true;
        default:
          return false;
      }
    } catch (ArgumentException) {
      return false;
    } catch (InvalidOperationException) {
      return false;
    }
  }
}
