using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>Bridges the allocated x86 machine product into the hosted target-owned representation.</summary>
public static class X86HostedMachineBuilder {
  public static bool TryBuild(IrMachineFunction machine, out X86TargetMachineFunction? hosted) {
    return TryBuild(machine, out hosted, out _);
  }

  public static bool TryBuild(IrMachineFunction machine, out X86TargetMachineFunction? hosted,
      out string? error) {
    ArgumentNullException.ThrowIfNull(machine);
    hosted = null;
    error = null;
    var mode = machine.Target.Name switch {
      "x86-64" => X86Mode.Bit64,
      "x86-32" => X86Mode.Bit32,
      "x86-16" => X86Mode.Bit16,
      _ => (X86Mode?)null,
    };
    if (mode is not { } selectedMode)
      return true;

    var registers = new X86TargetRegisterFile(selectedMode);
    var abi = X86Abi.For(machine.Source.Convention, selectedMode);
    var instructions = new List<X86TargetInstruction>();
    var labels = new Dictionary<string, int>(StringComparer.Ordinal);
    var blockLabels = machine.Function.Blocks.ToDictionary(
      block => block.Label, block => machine.Function.Name + "$" + block.Label,
      StringComparer.Ordinal);
    foreach (var block in machine.Function.Blocks) {
      labels[blockLabels[block.Label]] = instructions.Count;
      foreach (var (instruction, index) in block.Instructions.Select((item, index) => (item, index))) {
        if (!TryBuildInstruction(instruction, selectedMode, registers, machine.Function, machine.Allocation, blockLabels,
              out var targetInstruction, out var instructionError)) {
          error = $"block '{block.Label}' instruction #{index}: {instruction.Opcode} " +
            $"({string.Join("|", instruction.Operands.Select(operand => operand.GetType().Name))})" +
            (instructionError is null ? "" : $" - {instructionError}");
          return false;
        }
        if (targetInstruction is not null)
          instructions.Add(targetInstruction);
      }
    }

    hosted = new X86TargetMachineFunction(selectedMode,
      new X86TargetAbi(selectedMode, abi.Name, abi.StackAlignment, abi.ShadowSpaceBytes,
        abi.ArgumentRegisters, abi.ReturnRegister, abi.CalleeSavedRegisters), instructions, labels,
      machine.Function.StackSlots.Sum(size => (size + 1) & ~1),
      abi.PlaceArguments(machine.Source.Parameters.Select(parameter => parameter.Type).ToArray()));
    return true;
  }

  private static bool TryBuildInstruction(
      MInstr instruction,
      X86Mode mode,
      X86TargetRegisterFile registers,
      X86MachineFunction function,
      IReadOnlyDictionary<int, Reg> allocation,
      IReadOnlyDictionary<string, string> blockLabels,
      out X86TargetInstruction? target,
      out string? error) {
    target = null;
    error = null;
    MachineRegister Register(MOperand operand) {
      if (operand is not MOperand.Register { Reg: var register })
        throw new InvalidOperationException("hosted x86 lowering requires allocated physical registers");
      if (register.IsVirtual) {
        if (!allocation.TryGetValue(register.VirtualId, out var physical))
          throw new InvalidOperationException("hosted x86 lowering requires allocated physical registers");
        register = MReg.Physical_(physical, register.Size);
      }
      return registers.RegisterFor(register);
    }
    MachineRegister RegisterValue(MReg register) => Register(new MOperand.Register(register));

    try {
      switch (instruction.Opcode) {
        case MOpcode.InlineAsm when instruction.Operands.FirstOrDefault() is MOperand.InlineAsmText asm:
          return TryExpandInlineAsm(instruction, asm, mode, registers, function, allocation, out target);
        case MOpcode.Mov when instruction.Operands.Count == 2
            && TryAddress(instruction.Operands[0], function, registers, allocation, out var immediateAddress)
            && instruction.Operands[1] is MOperand.Immediate immediateMemory:
          target = new(X86TargetOpcode.MoveMemoryImmediate, [], immediateMemory.Value, immediateAddress);
          return true;
        case MOpcode.Mov when instruction.Operands.Count == 2
            && TryAddress(instruction.Operands[0], function, registers, allocation, out var symbolAddress)
            && instruction.Operands[1] is MOperand.DataOffset dataOffset:
          target = new(X86TargetOpcode.MoveMemorySymbol, [], Address: symbolAddress, Symbol: dataOffset.Name);
          return true;
        case MOpcode.Mov when instruction.Operands.Count == 2
            && TryAddress(instruction.Operands[0], function, registers, allocation, out var blockAddressTarget)
            && instruction.Operands[1] is MOperand.BlockOffset blockOffset:
          target = new(X86TargetOpcode.MoveMemorySymbol, [], Address: blockAddressTarget,
            Symbol: blockLabels.GetValueOrDefault(blockOffset.Block, blockOffset.Block));
          return true;
        case MOpcode.Mov when instruction.Operands.Count == 2
            && instruction.Operands[0] is MOperand.Register symbolRegister
            && instruction.Operands[1] is MOperand.LabelRef symbolLabel:
          target = new(X86TargetOpcode.MoveSymbolAddress, [Register(symbolRegister)], Symbol: symbolLabel.Name);
          return true;
        case MOpcode.Mov when instruction.Operands.Count == 2
            && TryAddress(instruction.Operands[0], function, registers, allocation, out var destinationAddress)
            && TryAddress(instruction.Operands[1], function, registers, allocation, out var sourceAddress):
          target = new(X86TargetOpcode.MoveMemoryToMemory, [], Address: destinationAddress,
            SourceAddress: sourceAddress);
          return true;
        case MOpcode.Mov when instruction.Operands.Count == 2:
          if (instruction.Operands[1] is MOperand.Immediate immediate)
            target = new(X86TargetOpcode.MoveImmediate, [Register(instruction.Operands[0])], immediate.Value);
          else if (TryAddress(instruction.Operands[1], function, registers, allocation, out var loadAddress))
            target = new(X86TargetOpcode.Mov, [Register(instruction.Operands[0])], Address: loadAddress);
          else if (instruction.Operands[0] is MOperand.Memory or MOperand.StackSlot or MOperand.DataCell or MOperand.ParamCell
                   && instruction.Operands[1] is MOperand.Register storeRegister
                   && TryAddress(instruction.Operands[0], function, registers, allocation, out var storeAddress))
            target = new(X86TargetOpcode.Mov, [RegisterValue(storeRegister.Reg)], Address: storeAddress,
              Immediate: 1);
          else
            target = new(X86TargetOpcode.MoveRegister,
              [Register(instruction.Operands[0]), Register(instruction.Operands[1])]);
          return true;
        case MOpcode.Lea when instruction.Operands.Count == 2
            && TryAddress(instruction.Operands[1], function, registers, allocation, out var leaAddress):
          target = new(X86TargetOpcode.Lea, [Register(instruction.Operands[0])], Address: leaAddress);
          return true;
        case MOpcode.Xchg when instruction.Operands.Count == 2
            && instruction.Operands[0] is MOperand.Register && instruction.Operands[1] is MOperand.Register:
          target = new(X86TargetOpcode.Xchg, [Register(instruction.Operands[0]), Register(instruction.Operands[1])]);
          return true;
        case MOpcode.Add when instruction.Operands[0] is MOperand.Register && instruction.Operands[1] is MOperand.Immediate add:
          target = new(X86TargetOpcode.AddImmediate, [Register(instruction.Operands[0])], add.Value);
          return true;
        case MOpcode.Add when instruction.Operands.Count == 2
            && (instruction.Operands[0] is MOperand.Memory or MOperand.StackSlot or MOperand.DataCell or MOperand.ParamCell)
            && instruction.Operands[1] is MOperand.Register addRegister
            && TryAddress(instruction.Operands[0], function, registers, allocation, out var addAddress):
          target = new(X86TargetOpcode.Add, [RegisterValue(addRegister.Reg)], Address: addAddress);
          return true;
        case MOpcode.Add or MOpcode.Sub or MOpcode.And or MOpcode.Or or MOpcode.Xor or MOpcode.Adc or MOpcode.Cmp
            when instruction.Operands.Count == 2
            && TryAddress(instruction.Operands[0], function, registers, allocation, out var immediateAluAddress)
            && instruction.Operands[1] is MOperand.Immediate memoryImmediate:
          target = new(X86TargetOpcode.AluMemoryImmediate, [], memoryImmediate.Value, immediateAluAddress,
            Operands: [new X86TargetOperand.Immediate((long)(instruction.Opcode switch {
              MOpcode.Add => 0, MOpcode.Or => 1, MOpcode.And => 4, MOpcode.Sub => 5, MOpcode.Xor => 6,
              MOpcode.Adc => 2, _ => 7 }))]);
          return true;
        case MOpcode.Cmp when instruction.Operands.Count == 2
            && instruction.Operands[0] is MOperand.Register
            && TryAddress(instruction.Operands[1], function, registers, allocation, out var compareMemoryAddress):
          target = new(X86TargetOpcode.CompareRegisterMemory, [Register(instruction.Operands[0])], Address: compareMemoryAddress);
          return true;
        case MOpcode.Add or MOpcode.Sub or MOpcode.And or MOpcode.Or or MOpcode.Xor or MOpcode.Adc or MOpcode.Sbb
            when instruction.Operands.Count == 2 && instruction.Operands[0] is MOperand.Register
            && TryAddress(instruction.Operands[1], function, registers, allocation, out var registerMemoryAddress):
          target = new(X86TargetOpcode.RegisterMemoryAlu, [Register(instruction.Operands[0])], Address: registerMemoryAddress,
            Immediate: instruction.Opcode switch {
              MOpcode.Add => 0x03, MOpcode.Or => 0x0B, MOpcode.And => 0x23, MOpcode.Xor => 0x33,
              MOpcode.Adc => 0x13, MOpcode.Sbb => 0x1B, _ => 0x2B });
          return true;
        case MOpcode.Sub or MOpcode.And or MOpcode.Or or MOpcode.Xor or MOpcode.Cmp
            when instruction.Operands.Count == 2
            && (instruction.Operands[0] is MOperand.Memory or MOperand.StackSlot or MOperand.DataCell or MOperand.ParamCell)
            && instruction.Operands[1] is MOperand.Register memoryRegister
            && TryAddress(instruction.Operands[0], function, registers, allocation, out var memoryAluAddress):
          target = new(instruction.Opcode switch {
            MOpcode.Sub => X86TargetOpcode.Sub,
            MOpcode.And => X86TargetOpcode.And,
            MOpcode.Or => X86TargetOpcode.Or,
            MOpcode.Xor => X86TargetOpcode.Xor,
            _ => X86TargetOpcode.Cmp,
          }, [RegisterValue(memoryRegister.Reg)], Address: memoryAluAddress);
          return true;
        case MOpcode.Sub when instruction.Operands[0] is MOperand.Register && instruction.Operands[1] is MOperand.Immediate sub:
          target = new(X86TargetOpcode.SubImmediate, [Register(instruction.Operands[0])], sub.Value);
          return true;
        case MOpcode.And when instruction.Operands[0] is MOperand.Register && instruction.Operands[1] is MOperand.Immediate andImmediate:
          target = new(X86TargetOpcode.AndImmediate, [Register(instruction.Operands[0])], andImmediate.Value);
          return true;
        case MOpcode.Or when instruction.Operands[0] is MOperand.Register && instruction.Operands[1] is MOperand.Immediate orImmediate:
          target = new(X86TargetOpcode.OrImmediate, [Register(instruction.Operands[0])], orImmediate.Value);
          return true;
        case MOpcode.Xor when instruction.Operands[0] is MOperand.Register && instruction.Operands[1] is MOperand.Immediate xorImmediate:
          target = new(X86TargetOpcode.XorImmediate, [Register(instruction.Operands[0])], xorImmediate.Value);
          return true;
        case MOpcode.Cmp when instruction.Operands[0] is MOperand.Register && instruction.Operands[1] is MOperand.Immediate compare:
          target = new(X86TargetOpcode.CompareImmediate, [Register(instruction.Operands[0])], compare.Value);
          return true;
        case MOpcode.Sbb when instruction.Operands.Count == 2 && instruction.Operands[0] is MOperand.Register
            && instruction.Operands[1] is MOperand.Immediate sbbImmediate:
          target = new(X86TargetOpcode.Sbb, [Register(instruction.Operands[0])], sbbImmediate.Value);
          return true;
        case MOpcode.Adc when instruction.Operands.Count == 2 && instruction.Operands[0] is MOperand.Register
            && instruction.Operands[1] is MOperand.Immediate adcImmediate:
          target = new(X86TargetOpcode.Adc, [Register(instruction.Operands[0])], adcImmediate.Value);
          return true;
        case MOpcode.Shl or MOpcode.Shr or MOpcode.Sar
            when instruction.Operands.Count == 2
            && instruction.Operands[0] is MOperand.Register
            && instruction.Operands[1] is MOperand.Register:
          target = new(instruction.Opcode switch {
            MOpcode.Shl => X86TargetOpcode.Shl,
            MOpcode.Shr => X86TargetOpcode.Shr,
            _ => X86TargetOpcode.Sar,
          }, [Register(instruction.Operands[0]), Register(instruction.Operands[1])]);
          return true;
        case MOpcode.Shl or MOpcode.Shr or MOpcode.Sar
            when instruction.Operands.Count == 2
            && TryAddress(instruction.Operands[0], function, registers, allocation, out var memoryShiftAddress)
            && instruction.Operands[1] is MOperand.Register:
          target = new(X86TargetOpcode.MemoryShiftCount, [], instruction.Opcode switch {
            MOpcode.Shl => 4, MOpcode.Shr => 5, _ => 7 }, memoryShiftAddress);
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
        case MOpcode.Imul when instruction.Operands.Count == 2
            && instruction.Operands[0] is MOperand.Register
            && instruction.Operands[1] is MOperand.Register:
          target = new(X86TargetOpcode.Imul,
            [Register(instruction.Operands[0]), Register(instruction.Operands[1])]);
          return true;
        case MOpcode.Imul when instruction.Operands.Count == 1
            && instruction.Operands[0] is MOperand.Register:
          target = new(X86TargetOpcode.Imul, [Register(instruction.Operands[0])]);
          return true;
        case MOpcode.Cwd:
          target = new(X86TargetOpcode.Cwd, []);
          return true;
        case MOpcode.Cbw:
          target = new(X86TargetOpcode.Cbw, []);
          return true;
        case MOpcode.Shl or MOpcode.Shr or MOpcode.Sar or MOpcode.Rcl or MOpcode.Rcr
            when instruction.Operands.Count == 2
            && instruction.Operands[0] is MOperand.Register
            && instruction.Operands[1] is MOperand.Immediate shift:
          target = new(instruction.Opcode switch {
            MOpcode.Shl => X86TargetOpcode.Shl,
            MOpcode.Shr => X86TargetOpcode.Shr,
            MOpcode.Sar => X86TargetOpcode.Sar,
            MOpcode.Rcl => X86TargetOpcode.Rcl,
            _ => X86TargetOpcode.Rcr,
          }, [Register(instruction.Operands[0])], shift.Value);
          return true;
        case MOpcode.Shld or MOpcode.Shrd
            when instruction.Operands.Count == 3
            && instruction.Operands[0] is MOperand.Register
            && instruction.Operands[1] is MOperand.Register
            && instruction.Operands[2] is MOperand.Immediate doubleShift:
          target = new(instruction.Opcode == MOpcode.Shld ? X86TargetOpcode.Shld : X86TargetOpcode.Shrd,
            [Register(instruction.Operands[0]), Register(instruction.Operands[1])], doubleShift.Value);
          return true;
        case MOpcode.Jmp when instruction.Operands[0] is MOperand.LabelRef label:
          target = new(X86TargetOpcode.Jmp, [], Symbol: blockLabels.GetValueOrDefault(label.Name, label.Name));
          return true;
        case MOpcode.Jcc when instruction.Operands[0] is MOperand.LabelRef label:
          target = new(X86TargetOpcode.Jcc, [], (long)(instruction.Condition ?? Condition.Equal),
            Symbol: blockLabels.GetValueOrDefault(label.Name, label.Name));
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
        case MOpcode.JmpIndirect when instruction.Operands.Count == 1
            && TryAddress(instruction.Operands[0], function, registers, allocation, out var indirectAddress):
          target = new(X86TargetOpcode.JmpIndirect, [], Address: indirectAddress);
          return true;
        case MOpcode.CallFar when instruction.Operands.Count == 1
            && TryAddress(instruction.Operands[0], function, registers, allocation, out var farAddress):
          target = new(X86TargetOpcode.CallFar, [], Address: farAddress);
          return true;
        case MOpcode.JmpIndexed when instruction.Operands.Count >= 2
            && instruction.Operands[0] is MOperand.Register indexRegister
            && instruction.Operands[1] is MOperand.BlockAddressTable table:
          var tableLabels = table.Blocks.Select(label => blockLabels.GetValueOrDefault(label, label)).ToArray();
          target = new(X86TargetOpcode.JmpIndexed, [Register(indexRegister)],
            Symbol: instruction.Operands.OfType<MOperand.LabelRef>().FirstOrDefault()?.Name,
            Operands: [new X86TargetOperand.Table(tableLabels, table.Keys)]);
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
            && TryAddress(instruction.Operands[0], function, registers, allocation, out var memoryAddress):
          target = new(instruction.Opcode switch {
            MOpcode.Fld => X86TargetOpcode.Fld,
            MOpcode.Fstp => X86TargetOpcode.Fstp,
            MOpcode.Fild => X86TargetOpcode.Fild,
            _ => X86TargetOpcode.Fistp,
          }, [], Address: memoryAddress);
          return true;
        case MOpcode.Fadd or MOpcode.Fsub or MOpcode.Fmul or MOpcode.Fdiv or MOpcode.Fcomp
            or MOpcode.Fiadd or MOpcode.Fisub or MOpcode.Fimul or MOpcode.Fidiv
            when instruction.Operands.Count == 1
            && TryAddress(instruction.Operands[0], function, registers, allocation, out var arithmeticAddress):
          target = new(instruction.Opcode switch {
            MOpcode.Fadd => X86TargetOpcode.Fadd,
            MOpcode.Fsub => X86TargetOpcode.Fsub,
            MOpcode.Fmul => X86TargetOpcode.Fmul,
            MOpcode.Fdiv => X86TargetOpcode.Fdiv,
            MOpcode.Fcomp => X86TargetOpcode.Fcomp,
            MOpcode.Fiadd => X86TargetOpcode.Fiadd,
            MOpcode.Fisub => X86TargetOpcode.Fisub,
            MOpcode.Fimul => X86TargetOpcode.Fimul,
            _ => X86TargetOpcode.Fidiv,
          }, [], Address: arithmeticAddress);
          return true;
        case MOpcode.Push when instruction.Operands.Count == 1 && instruction.Operands[0] is MOperand.Immediate immediatePush:
          target = new(X86TargetOpcode.Push, [], immediatePush.Value);
          return true;
        case MOpcode.Push when instruction.Operands.Count == 1
            && TryAddress(instruction.Operands[0], function, registers, allocation, out var pushAddress):
          target = new(X86TargetOpcode.Push, [], Address: pushAddress);
          return true;
        case MOpcode.Pop when instruction.Operands.Count == 1
            && TryAddress(instruction.Operands[0], function, registers, allocation, out var popAddress):
          target = new(X86TargetOpcode.Pop, [], Address: popAddress);
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
    } catch (ArgumentException ex) {
      error = ex.Message;
      return false;
    } catch (InvalidOperationException ex) {
      error = ex.Message;
      return false;
    }
  }

  private static bool TryAddress(MOperand.Memory memory, X86TargetRegisterFile registers,
      IReadOnlyDictionary<int, Reg> allocation,
      out X86TargetAddress address) {
    static MachineRegister? ConvertAllocated(MReg? register, X86TargetRegisterFile file,
        IReadOnlyDictionary<int, Reg> map) {
      if (register is not { } value) return null;
      if (value.IsVirtual) {
        if (!map.TryGetValue(value.VirtualId, out var physical)) return null;
        value = MReg.Physical_(physical, value.Size);
      }
      return file.RegisterFor(value);
    }
    address = new(ConvertAllocated(memory.Base, registers, allocation), ConvertAllocated(memory.Index, registers, allocation), (byte)memory.Scale,
      memory.Disp, memory.Size switch {
        MRegSize.Byte => 8,
        MRegSize.Word => 16,
        MRegSize.Dword => 32,
        MRegSize.Qword => 64,
        _ => 80,
      });
    // Absolute displacement-only memory operands are valid in all hosted modes.
    return address.Base is not null || address.Index is not null || memory.Disp != 0;
  }

  private static bool TryAddress(MOperand operand, X86MachineFunction function,
      X86TargetRegisterFile registers, IReadOnlyDictionary<int, Reg> allocation,
      out X86TargetAddress address) {
    if (operand is MOperand.Memory memory)
      return TryAddress(memory, registers, allocation, out address);
    if (operand is MOperand.StackSlot slot) {
      var slotOffset = 0;
      for (var index = 0; index <= slot.Index && index < function.StackSlots.Count; ++index)
        slotOffset += (function.StackSlots[index] + 1) & ~1;
      address = new(registers.FramePointer, null, 1, -slotOffset + slot.Disp, slot.Size switch {
        MRegSize.Byte => 8,
        MRegSize.Word => 16,
        MRegSize.Dword => 32,
        MRegSize.Qword => 64,
        _ => 80,
      });
      return true;
    }
    if (operand is MOperand.DataCell cell) {
      address = new(null, null, 1, cell.Disp, cell.Size switch {
        MRegSize.Byte => 8,
        MRegSize.Word => 16,
        MRegSize.Dword => 32,
        MRegSize.Qword => 64,
        _ => 80,
      }, cell.Name);
      return true;
    }
    if (operand is MOperand.DataOffset offset) {
      address = new(null, null, 1, offset.Disp, 0, offset.Name);
      return true;
    }
    if (operand is MOperand.ParamCell parameter) {
      // Stack-only routed ABIs place the first incoming word at BP+4.  Wide parameters carry
      // their own byte delta, so this remains correct for the split LONG/DOUBLE forms emitted by
      // the selector without reintroducing a source-procedure frame dependency.
      address = new(registers.FramePointer, null, 1, 4 + parameter.ArgumentIndex * 2 + parameter.ByteDelta,
        parameter.Size switch {
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

  private static bool TryExpandInlineAsm(MInstr instruction, MOperand.InlineAsmText asm, X86Mode mode,
      X86TargetRegisterFile registers, X86MachineFunction function,
      IReadOnlyDictionary<int, Reg> allocation, out X86TargetInstruction? target) {
    target = null;
    var text = asm.Text.Trim();
    if (text.Length == 0)
      return true;
    if (text.Contains('\n') || text.Contains('\r'))
      return false;
    var mnemonic = text.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries)[0]
      .ToUpperInvariant();
    if (mnemonic is "AESENC" or "AESDEC" or "AESIMC" or "PCLMULQDQ") {
      var vectorOperands = instruction.Operands.Skip(1).OfType<MOperand.Register>()
        .Select(operand => TryMachineRegister(operand.Reg, registers, allocation)).ToArray();
      if (vectorOperands.Any(register => register is null) || vectorOperands.Length < 2)
        return false;
      var xmm0 = vectorOperands[0]!.Value;
      var xmm1 = vectorOperands[1]!.Value;
      target = new(mnemonic switch {
        "AESENC" => X86TargetOpcode.AesEnc,
        "AESDEC" => X86TargetOpcode.AesDec,
        "AESIMC" => X86TargetOpcode.AesImc,
        _ => X86TargetOpcode.Pclmul,
      }, [xmm0, xmm1], Immediate: 0);
      return true;
    }
    if (TryExpandVectorAsm(mnemonic, instruction, registers, allocation, out target))
      return true;
    if (mnemonic is "POPCNT" or "BSF" or "BSR" or "BEXTR" or "ANDN" or "BLSI" or "BLSR" or "BZHI" or "PEXT" or "PDEP" or "MULX") {
      var registerOperands = instruction.Operands.Skip(1).OfType<MOperand.Register>()
        .Select(operand => TryMachineRegister(operand.Reg, registers, allocation)).ToArray();
      var requiredRegisters = mnemonic is "BLSI" or "BLSR" or "POPCNT" or "BSF" or "BSR" ? 2 : 3;
      if (registerOperands.Length >= requiredRegisters && registerOperands.All(register => register is not null)) {
        target = new(mnemonic switch {
          "POPCNT" => X86TargetOpcode.Popcnt,
          "BSF" => X86TargetOpcode.Bsf,
          "BSR" => X86TargetOpcode.Bsr,
          "BEXTR" => X86TargetOpcode.Bextr,
          "ANDN" => X86TargetOpcode.Andn,
          "BLSI" => X86TargetOpcode.Blsi,
          "BLSR" => X86TargetOpcode.Blsr,
          "BZHI" => X86TargetOpcode.Bzhi,
          "PEXT" => X86TargetOpcode.Pext,
          "PDEP" => X86TargetOpcode.Pdep,
          _ => X86TargetOpcode.Mulx,
        }, registerOperands.Take(requiredRegisters).Select(register => register!.Value).ToArray());
        return true;
      }
      var source = instruction.Operands.Skip(1).FirstOrDefault();
      if (source is null || !TryAddress(source, function, registers, allocation, out var address))
        return false;
      var destination = registers.Registers[0] with { Bits = mode == X86Mode.Bit64 ? 64 : mode == X86Mode.Bit32 ? 32 : 16 };
      target = new(mnemonic switch {
        "POPCNT" => X86TargetOpcode.Popcnt,
        "BSF" => X86TargetOpcode.Bsf,
        "BSR" => X86TargetOpcode.Bsr,
        "BEXTR" => X86TargetOpcode.Bextr,
        "ANDN" => X86TargetOpcode.Andn,
        "BLSI" => X86TargetOpcode.Blsi,
        "BLSR" => X86TargetOpcode.Blsr,
        "BZHI" => X86TargetOpcode.Bzhi,
        "PEXT" => X86TargetOpcode.Pext,
        "PDEP" => X86TargetOpcode.Pdep,
        _ => X86TargetOpcode.Mulx,
      }, [destination], Address: address);
      return true;
    }
    target = mnemonic switch {
      "NOP" => new X86TargetInstruction(X86TargetOpcode.Nop, []),
      "CBW" => new X86TargetInstruction(X86TargetOpcode.Cbw, []),
      "CWD" or "CDQ" or "CQO" => new X86TargetInstruction(X86TargetOpcode.Cwd, []),
      "SAHF" => new X86TargetInstruction(X86TargetOpcode.Sahf, []),
      "FSQRT" => new X86TargetInstruction(X86TargetOpcode.Fsqrt, []),
      "FSIN" => new X86TargetInstruction(X86TargetOpcode.Fsin, []),
      "FCOS" => new X86TargetInstruction(X86TargetOpcode.Fcos, []),
      "FPTAN" => new X86TargetInstruction(X86TargetOpcode.Fptan, []),
      "FPATAN" => new X86TargetInstruction(X86TargetOpcode.Fpatan, []),
      "FYL2X" => new X86TargetInstruction(X86TargetOpcode.Fyl2x, []),
      "FLD1" => new X86TargetInstruction(X86TargetOpcode.Fld1, []),
      "FLDLN2" => new X86TargetInstruction(X86TargetOpcode.Fldln2, []),
      "FLDLG2" => new X86TargetInstruction(X86TargetOpcode.Fldlg2, []),
      "FLDL2E" => new X86TargetInstruction(X86TargetOpcode.Fldl2e, []),
      "FLDL2T" => new X86TargetInstruction(X86TargetOpcode.Fldl2t, []),
      _ => null,
    };
    if (target is not null)
      return true;
    var runtimeRegisters = instruction.Operands.Skip(1).OfType<MOperand.Register>()
      .Select(operand => TryMachineRegister(operand.Reg, registers))
      .Where(register => register is not null).Select(register => register!.Value).ToArray();
    target = new X86TargetInstruction(X86TargetOpcode.Call, runtimeRegisters,
      Symbol: PowerBasic.Compiler.Runtime.InlineAsmExports.EmulationRoutine(mnemonic));
    return true;
  }

  private static bool TryExpandVectorAsm(string mnemonic, MInstr instruction,
      X86TargetRegisterFile registers, IReadOnlyDictionary<int, Reg> allocation,
      out X86TargetInstruction? target) {
    target = null;
    var operation = mnemonic switch {
      "MOVDQA" or "MOVDQU" or "MOVQ" => X86VectorOpcode.Move,
      "PADDW" or "VPADDW" => X86VectorOpcode.AddW,
      "PADDQ" or "VPADDQ" => X86VectorOpcode.Add,
      "PSUBW" or "VPSUBW" => X86VectorOpcode.SubW,
      "PSUBQ" or "VPSUBQ" => X86VectorOpcode.Sub,
      "PAND" or "VPAND" => X86VectorOpcode.And,
      "POR" or "VPOR" => X86VectorOpcode.Or,
      "PXOR" or "VPXOR" => X86VectorOpcode.Xor,
      "PMINUD" => X86VectorOpcode.MinUnsignedDword,
      "PMAXUD" => X86VectorOpcode.MaxUnsignedDword,
      "PBLENDW" => X86VectorOpcode.BlendWord,
      "PALIGNR" => X86VectorOpcode.AlignRight,
      _ => (X86VectorOpcode?)null,
    };
    if (operation is not { } selected)
      return false;
    var operands = instruction.Operands.Skip(1).OfType<MOperand.Register>()
      .Select(operand => TryMachineRegister(operand.Reg, registers, allocation)).ToArray();
    if (operands.Any(register => register is null) || operands.Length < 2)
      return false;
    var vectorOperands = operands.Select(register => register!.Value).ToArray();
    var legacy = !mnemonic.StartsWith("V", StringComparison.Ordinal);
    var selectedRegisters = vectorOperands.Length >= 3
      ? vectorOperands.Take(3).ToArray()
      : [vectorOperands[0], vectorOperands[0], vectorOperands[1]];
    var immediate = instruction.Operands.Skip(1).OfType<MOperand.Immediate>()
      .Select(value => (byte)value.Value).FirstOrDefault();
    target = new(X86TargetOpcode.VectorBinary, selectedRegisters, immediate,
      VectorOperation: selected,
      VectorEncoding: legacy ? X86VectorEncoding.Legacy : null);
    return true;
  }

  private static MachineRegister? TryMachineRegister(MReg register, X86TargetRegisterFile registers,
      IReadOnlyDictionary<int, Reg>? allocation = null) {
    if (register.IsVirtual) {
      if (allocation is null)
        return null;
      if (!allocation.TryGetValue(register.VirtualId, out var physical))
        return null;
      register = MReg.Physical_(physical, register.Size);
    }
    if (register.Physical.IsMmx() || register.Physical.IsXmm() || register.Physical.IsYmm() || register.Physical.IsZmm()) {
      var index = register.Physical.Index();
      var (prefix, bits) = register.Physical.IsMmx() ? ("mm", 64)
        : register.Physical.IsXmm() ? ("xmm", 128)
        : register.Physical.IsYmm() ? ("ymm", 256) : ("zmm", 512);
      return new MachineRegister(prefix + index, index, bits);
    }
    return registers.RegisterFor(register);
  }
}
