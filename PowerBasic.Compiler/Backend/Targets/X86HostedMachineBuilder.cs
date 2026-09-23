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
    var labels = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var block in machine.Function.Blocks) {
      labels[block.Label] = instructions.Count;
      foreach (var instruction in block.Instructions) {
        if (!TryBuildInstruction(instruction, selectedMode, registers, machine.Function, out var targetInstruction))
          return false;
        if (targetInstruction is not null)
          instructions.Add(targetInstruction);
      }
    }

    hosted = new X86TargetMachineFunction(selectedMode,
      new X86TargetAbi(selectedMode, abi.Name, abi.StackAlignment, abi.ShadowSpaceBytes,
        abi.ArgumentRegisters, abi.ReturnRegister, abi.CalleeSavedRegisters), instructions, labels);
    return true;
  }

  private static bool TryBuildInstruction(
      MInstr instruction,
      X86Mode mode,
      X86TargetRegisterFile registers,
      X86MachineFunction function,
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
          else if (TryAddress(instruction.Operands[1], function, registers, out var loadAddress))
            target = new(X86TargetOpcode.Mov, [Register(instruction.Operands[0])], Address: loadAddress);
          else if (instruction.Operands[0] is MOperand.Memory or MOperand.StackSlot
                   && instruction.Operands[1] is MOperand.Register storeRegister
                   && TryAddress(instruction.Operands[0], function, registers, out var storeAddress))
            target = new(X86TargetOpcode.Mov, [Register(storeRegister.Reg)], Address: storeAddress,
              Immediate: 1);
          else
            target = new(X86TargetOpcode.MoveRegister,
              [Register(instruction.Operands[0]), Register(instruction.Operands[1])]);
          return true;
        case MOpcode.Lea when instruction.Operands.Count == 2
            && TryAddress(instruction.Operands[1], function, registers, out var leaAddress):
          target = new(X86TargetOpcode.Lea, [Register(instruction.Operands[0])], Address: leaAddress);
          return true;
        case MOpcode.Xchg when instruction.Operands.Count == 2
            && instruction.Operands[0] is MOperand.Register && instruction.Operands[1] is MOperand.Register:
          target = new(X86TargetOpcode.Xchg, [Register(instruction.Operands[0]), Register(instruction.Operands[1])]);
          return true;
        case MOpcode.Add when instruction.Operands[1] is MOperand.Immediate add:
          target = new(X86TargetOpcode.AddImmediate, [Register(instruction.Operands[0])], add.Value);
          return true;
        case MOpcode.Add when instruction.Operands.Count == 2
            && (instruction.Operands[0] is MOperand.Memory or MOperand.StackSlot)
            && instruction.Operands[1] is MOperand.Register addRegister
            && TryAddress(instruction.Operands[0], function, registers, out var addAddress):
          target = new(X86TargetOpcode.Add, [Register(addRegister.Reg)], Address: addAddress);
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
        case MOpcode.Add when instruction.Operands.Count == 2 && instruction.Operands[1] is MOperand.Register:
          target = new(X86TargetOpcode.Add, [Register(instruction.Operands[0]), Register(instruction.Operands[1])]);
          return true;
        case MOpcode.Sub when instruction.Operands.Count == 2 && instruction.Operands[1] is MOperand.Register:
          target = new(X86TargetOpcode.Sub, [Register(instruction.Operands[0]), Register(instruction.Operands[1])]);
          return true;
        case MOpcode.And when instruction.Operands.Count == 2 && instruction.Operands[1] is MOperand.Register:
          target = new(X86TargetOpcode.And, [Register(instruction.Operands[0]), Register(instruction.Operands[1])]);
          return true;
        case MOpcode.Or when instruction.Operands.Count == 2 && instruction.Operands[1] is MOperand.Register:
          target = new(X86TargetOpcode.Or, [Register(instruction.Operands[0]), Register(instruction.Operands[1])]);
          return true;
        case MOpcode.Xor when instruction.Operands.Count == 2 && instruction.Operands[1] is MOperand.Register:
          target = new(X86TargetOpcode.Xor, [Register(instruction.Operands[0]), Register(instruction.Operands[1])]);
          return true;
        case MOpcode.Cmp when instruction.Operands.Count == 2 && instruction.Operands[1] is MOperand.Register:
          target = new(X86TargetOpcode.Cmp, [Register(instruction.Operands[0]), Register(instruction.Operands[1])]);
          return true;
        case MOpcode.Adc when instruction.Operands.Count == 2 && instruction.Operands[1] is MOperand.Register:
          target = new(X86TargetOpcode.Adc, [Register(instruction.Operands[0]), Register(instruction.Operands[1])]);
          return true;
        case MOpcode.Sbb when instruction.Operands.Count == 2 && instruction.Operands[1] is MOperand.Register:
          target = new(X86TargetOpcode.Sbb, [Register(instruction.Operands[0]), Register(instruction.Operands[1])]);
          return true;
        case MOpcode.Test when instruction.Operands.Count == 2 && instruction.Operands[1] is MOperand.Register:
          target = new(X86TargetOpcode.Test, [Register(instruction.Operands[0]), Register(instruction.Operands[1])]);
          return true;
        case MOpcode.Neg or MOpcode.Not or MOpcode.Inc or MOpcode.Dec
            when instruction.Operands.Count == 1 && instruction.Operands[0] is MOperand.Register:
          target = new(instruction.Opcode switch {
            MOpcode.Neg => X86TargetOpcode.Neg,
            MOpcode.Not => X86TargetOpcode.Not,
            MOpcode.Inc => X86TargetOpcode.Inc,
            _ => X86TargetOpcode.Dec,
          }, [Register(instruction.Operands[0])]);
          return true;
        case MOpcode.Mul or MOpcode.Div or MOpcode.Idiv
            when instruction.Operands.Count == 1 && instruction.Operands[0] is MOperand.Register:
          target = new(instruction.Opcode switch {
            MOpcode.Mul => X86TargetOpcode.Mul,
            MOpcode.Div => X86TargetOpcode.Div,
            _ => X86TargetOpcode.Idiv,
          }, [Register(instruction.Operands[0])]);
          return true;
        case MOpcode.Cwd:
          target = new(X86TargetOpcode.Cwd, []);
          return true;
        case MOpcode.Cbw:
          target = new(X86TargetOpcode.Cbw, []);
          return true;
        case MOpcode.Jmp when instruction.Operands[0] is MOperand.LabelRef label:
          target = new(X86TargetOpcode.Jmp, [], Symbol: label.Name);
          return true;
        case MOpcode.Jcc when instruction.Operands[0] is MOperand.LabelRef label:
          target = new(X86TargetOpcode.Jcc, [], (long)(instruction.Condition ?? Condition.Equal), Symbol: label.Name);
          return true;
        case MOpcode.Call when instruction.Operands[0] is MOperand.LabelRef label:
          target = new(X86TargetOpcode.Call, [], Symbol: label.Name);
          return true;
        case MOpcode.Call when instruction.Operands.Count == 1 && instruction.Operands[0] is MOperand.Register:
          target = new(X86TargetOpcode.Call, [Register(instruction.Operands[0])]);
          return true;
        case MOpcode.JmpIndirect when instruction.Operands.Count == 1 && instruction.Operands[0] is MOperand.Register:
          target = new(X86TargetOpcode.JmpIndirect, [Register(instruction.Operands[0])]);
          return true;
        case MOpcode.Ret:
          target = new(X86TargetOpcode.Ret, []);
          return true;
        case MOpcode.Faddp or MOpcode.Fsubp or MOpcode.Fmulp or MOpcode.Fdivp
            or MOpcode.Fcompp or MOpcode.FstswAx or MOpcode.Sahf or MOpcode.Fsqrt
            or MOpcode.Fsin or MOpcode.Fcos or MOpcode.Fptan or MOpcode.Fpatan
            or MOpcode.Fyl2x or MOpcode.Fxch or MOpcode.FstpSt0
            or MOpcode.Fld1 or MOpcode.Fldln2 or MOpcode.Fldlg2 or MOpcode.Fldl2e or MOpcode.Fldl2t:
          target = new(instruction.Opcode switch {
            MOpcode.Faddp => X86TargetOpcode.Faddp,
            MOpcode.Fsubp => X86TargetOpcode.Fsubp,
            MOpcode.Fmulp => X86TargetOpcode.Fmulp,
            MOpcode.Fdivp => X86TargetOpcode.Fdivp,
            MOpcode.Fcompp => X86TargetOpcode.Fcompp,
            MOpcode.FstswAx => X86TargetOpcode.FstswAx,
            MOpcode.Sahf => X86TargetOpcode.Sahf,
            MOpcode.Fsqrt => X86TargetOpcode.Fsqrt,
            MOpcode.Fsin => X86TargetOpcode.Fsin,
            MOpcode.Fcos => X86TargetOpcode.Fcos,
            MOpcode.Fptan => X86TargetOpcode.Fptan,
            MOpcode.Fpatan => X86TargetOpcode.Fpatan,
            MOpcode.Fyl2x => X86TargetOpcode.Fyl2x,
            MOpcode.Fxch => X86TargetOpcode.Fxch,
            MOpcode.FstpSt0 => X86TargetOpcode.FstpSt0,
            MOpcode.Fld1 => X86TargetOpcode.Fld1,
            MOpcode.Fldln2 => X86TargetOpcode.Fldln2,
            MOpcode.Fldlg2 => X86TargetOpcode.Fldlg2,
            MOpcode.Fldl2e => X86TargetOpcode.Fldl2e,
            _ => X86TargetOpcode.Fldl2t,
          }, []);
          return true;
        case MOpcode.Fld or MOpcode.Fstp or MOpcode.Fild or MOpcode.Fistp
            when instruction.Operands.Count == 1
            && TryAddress(instruction.Operands[0], function, registers, out var memoryAddress):
          target = new(instruction.Opcode switch {
            MOpcode.Fld => X86TargetOpcode.Fld,
            MOpcode.Fstp => X86TargetOpcode.Fstp,
            MOpcode.Fild => X86TargetOpcode.Fild,
            _ => X86TargetOpcode.Fistp,
          }, [], Address: memoryAddress);
          return true;
        case MOpcode.Push when instruction.Operands.Count == 1 && instruction.Operands[0] is MOperand.Immediate immediatePush:
          target = new(X86TargetOpcode.Push, [], immediatePush.Value);
          return true;
        case MOpcode.Push when instruction.Operands.Count == 1:
          target = new(X86TargetOpcode.PushRegister, [Register(instruction.Operands[0])]);
          return true;
        case MOpcode.Pop when instruction.Operands.Count == 1:
          target = new(X86TargetOpcode.PopRegister, [Register(instruction.Operands[0])]);
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

  private static bool TryAddress(MOperand.Memory memory, X86TargetRegisterFile registers,
      out X86TargetAddress address) {
    static MachineRegister? Convert(MReg? register, X86TargetRegisterFile file)
      => register is { IsVirtual: false } value && (uint)value.Physical.Index() < (uint)file.Registers.Count
        ? file.Registers[value.Physical.Index()]
        : null;
    address = new(Convert(memory.Base, registers), Convert(memory.Index, registers), (byte)memory.Scale,
      memory.Disp, memory.Size switch {
        MRegSize.Byte => 8,
        MRegSize.Word => 16,
        MRegSize.Dword => 32,
        MRegSize.Qword => 64,
        _ => 80,
      });
    return address.Base is not null || address.Index is not null;
  }

  private static bool TryAddress(MOperand operand, X86MachineFunction function,
      X86TargetRegisterFile registers, out X86TargetAddress address) {
    if (operand is MOperand.Memory memory)
      return TryAddress(memory, registers, out address);
    if (operand is MOperand.StackSlot slot) {
      var offset = 0;
      for (var index = 0; index <= slot.Index && index < function.StackSlots.Count; ++index)
        offset += (function.StackSlots[index] + 1) & ~1;
      address = new(registers.FramePointer, null, 1, -offset + slot.Disp, slot.Size switch {
        MRegSize.Byte => 8,
        MRegSize.Word => 16,
        MRegSize.Dword => 32,
        MRegSize.Qword => 64,
        _ => 80,
      });
      return true;
    }
    address = default;
    return false;
  }
}
