namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>Encodes the independent hosted x86 machine representation and its relocations.</summary>
public sealed class X86TargetMachineEmitter(X86InstructionEncoder encoder) {
  public MachineCode Emit(X86TargetMachineFunction function, bool preserveFramePointer = true,
      bool emitReturn = true) {
    ArgumentNullException.ThrowIfNull(function);
    var bytes = new List<byte>();
    var relocations = new List<MachineRelocation>();
    var labels = new Dictionary<string, int>(StringComparer.Ordinal);
    var registers = new X86TargetRegisterFile(function.Mode);
    void AddAddressRelocation(X86TargetAddress address, int instructionOffset, int encodedLength) {
      if (address.Symbol is not { Length: > 0 } symbol || address.Base is not null || address.Index is not null)
        return;
      var width = function.Mode == X86Mode.Bit16 ? 2 : 4;
      var kind = function.Mode == X86Mode.Bit16
        ? MachineRelocationKind.Absolute16 : MachineRelocationKind.Absolute32;
      relocations.Add(new MachineRelocation(instructionOffset + encodedLength - width, kind, symbol,
        address.Displacement));
    }
    void AppendFunctionEpilogue() {
      if (function.Abi.ShadowSpaceBytes != 0)
        bytes.AddRange(encoder.AdjustStack(function.Abi.ShadowSpaceBytes, allocate: false));
      if (function.FrameSizeBytes != 0)
        bytes.AddRange(encoder.AdjustStack(function.FrameSizeBytes, allocate: false));
      if (preserveFramePointer)
        bytes.AddRange(encoder.Pop(registers.FramePointer));
    }
    if (preserveFramePointer) {
      bytes.AddRange(encoder.Push(registers.FramePointer));
      bytes.AddRange(function.Mode == X86Mode.Bit64 ? [0x48, 0x89, 0xE5] : [0x89, 0xE5]);
    }
    if (function.FrameSizeBytes != 0)
      bytes.AddRange(encoder.AdjustStack(function.FrameSizeBytes, allocate: true));
    if (function.Abi.ShadowSpaceBytes != 0)
      bytes.AddRange(encoder.AdjustStack(function.Abi.ShadowSpaceBytes, allocate: true));

    foreach (var (instruction, index) in function.Instructions.Select((item, index) => (item, index))) {
      foreach (var label in function.LabelInstructionIndices.Where(pair => pair.Value == index))
        labels[label.Key] = bytes.Count;
      var offset = bytes.Count;
      switch (instruction.Opcode) {
        case X86TargetOpcode.Mov when instruction.Address is { } loadAddress && instruction.Immediate == 0:
          var loadBytes = encoder.MoveMemory(instruction.Registers[0], loadAddress, load: true);
          bytes.AddRange(loadBytes);
          AddAddressRelocation(loadAddress, offset, loadBytes.Length);
          break;
        case X86TargetOpcode.Mov when instruction.Address is { } storeAddress && instruction.Immediate == 1:
          var storeBytes = encoder.MoveMemory(instruction.Registers[0], storeAddress, load: false);
          bytes.AddRange(storeBytes);
          AddAddressRelocation(storeAddress, offset, storeBytes.Length);
          break;
        case X86TargetOpcode.Mov when instruction.Registers.Count == 1 && instruction.Address is null:
          bytes.AddRange(encoder.MoveImmediate(instruction.Registers[0], unchecked((ulong)instruction.Immediate)));
          break;
        case X86TargetOpcode.Mov:
          bytes.AddRange(encoder.MoveRegister(instruction.Registers[0], instruction.Registers[1]));
          break;
        case X86TargetOpcode.Lea when instruction.Address is { } leaAddress:
          var leaBytes = encoder.LeaMemory(instruction.Registers[0], leaAddress);
          bytes.AddRange(leaBytes);
          AddAddressRelocation(leaAddress, offset, leaBytes.Length);
          break;
        case X86TargetOpcode.Xchg when instruction.Registers.Count >= 2:
          bytes.AddRange(encoder.XchgRegister(instruction.Registers[0], instruction.Registers[1]));
          break;
        case X86TargetOpcode.Add when instruction.Address is { } addAddress:
          var addBytes = encoder.AluMemory(instruction.Registers[0], addAddress, 0x01, load: false);
          bytes.AddRange(addBytes);
          AddAddressRelocation(addAddress, offset, addBytes.Length);
          break;
        case X86TargetOpcode.Sub or X86TargetOpcode.And or X86TargetOpcode.Or or X86TargetOpcode.Xor
            or X86TargetOpcode.Cmp when instruction.Address is { } memoryAluAddress:
          var memoryOpcode = instruction.Opcode switch {
            X86TargetOpcode.Sub => 0x29,
            X86TargetOpcode.And => 0x21,
            X86TargetOpcode.Or => 0x09,
            X86TargetOpcode.Xor => 0x31,
            _ => 0x39,
          };
          var memoryAluBytes = encoder.AluMemory(instruction.Registers[0], memoryAluAddress,
            memoryOpcode, load: false);
          bytes.AddRange(memoryAluBytes);
          AddAddressRelocation(memoryAluAddress, offset, memoryAluBytes.Length);
          break;
        case X86TargetOpcode.Add when instruction.Registers.Count == 1:
          bytes.AddRange(encoder.AddImmediate(instruction.Registers[0], checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.Sub when instruction.Registers.Count == 1:
          bytes.AddRange(encoder.SubImmediate(instruction.Registers[0], checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.And when instruction.Registers.Count == 1:
          bytes.AddRange(encoder.AndImmediate(instruction.Registers[0], checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.Or when instruction.Registers.Count == 1:
          bytes.AddRange(encoder.OrImmediate(instruction.Registers[0], checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.Xor when instruction.Registers.Count == 1:
          bytes.AddRange(encoder.XorImmediate(instruction.Registers[0], checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.Cmp when instruction.Registers.Count == 1:
          bytes.AddRange(encoder.CompareImmediate(instruction.Registers[0], checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.Add when instruction.Registers.Count >= 2:
          bytes.AddRange(encoder.AluRegister(instruction.Registers[0], instruction.Registers[1], 0x01));
          break;
        case X86TargetOpcode.Sub when instruction.Registers.Count >= 2:
          bytes.AddRange(encoder.AluRegister(instruction.Registers[0], instruction.Registers[1], 0x29));
          break;
        case X86TargetOpcode.And when instruction.Registers.Count >= 2:
          bytes.AddRange(encoder.AluRegister(instruction.Registers[0], instruction.Registers[1], 0x21));
          break;
        case X86TargetOpcode.Or when instruction.Registers.Count >= 2:
          bytes.AddRange(encoder.AluRegister(instruction.Registers[0], instruction.Registers[1], 0x09));
          break;
        case X86TargetOpcode.Xor when instruction.Registers.Count >= 2:
          bytes.AddRange(encoder.AluRegister(instruction.Registers[0], instruction.Registers[1], 0x31));
          break;
        case X86TargetOpcode.Cmp when instruction.Registers.Count >= 2:
          bytes.AddRange(encoder.AluRegister(instruction.Registers[0], instruction.Registers[1], 0x39));
          break;
        case X86TargetOpcode.Test when instruction.Registers.Count >= 2:
          bytes.AddRange(encoder.AluRegister(instruction.Registers[0], instruction.Registers[1], 0x85));
          break;
        case X86TargetOpcode.Adc when instruction.Registers.Count >= 2:
          bytes.AddRange(encoder.AluRegister(instruction.Registers[0], instruction.Registers[1], 0x11));
          break;
        case X86TargetOpcode.Sbb when instruction.Registers.Count >= 2:
          bytes.AddRange(encoder.AluRegister(instruction.Registers[0], instruction.Registers[1], 0x19));
          break;
        case X86TargetOpcode.Neg:
          bytes.AddRange(encoder.UnaryRegister(instruction.Registers[0], 3));
          break;
        case X86TargetOpcode.Not:
          bytes.AddRange(encoder.UnaryRegister(instruction.Registers[0], 2));
          break;
        case X86TargetOpcode.Inc:
          bytes.AddRange(encoder.UnaryRegister(instruction.Registers[0], 0));
          break;
        case X86TargetOpcode.Dec:
          bytes.AddRange(encoder.UnaryRegister(instruction.Registers[0], 1));
          break;
        case X86TargetOpcode.Mul:
          bytes.AddRange(encoder.UnaryMultiplyDivide(instruction.Registers[0], 4));
          break;
        case X86TargetOpcode.Div:
          bytes.AddRange(encoder.UnaryMultiplyDivide(instruction.Registers[0], 6));
          break;
        case X86TargetOpcode.Idiv:
          bytes.AddRange(encoder.UnaryMultiplyDivide(instruction.Registers[0], 7));
          break;
        case X86TargetOpcode.Imul when instruction.Registers.Count >= 2:
          bytes.AddRange(encoder.ImulRegister(instruction.Registers[0], instruction.Registers[1]));
          break;
        case X86TargetOpcode.Shl:
          bytes.AddRange(encoder.ShiftRegister(instruction.Registers[0], 4, checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.Shr:
          bytes.AddRange(encoder.ShiftRegister(instruction.Registers[0], 5, checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.Sar:
          bytes.AddRange(encoder.ShiftRegister(instruction.Registers[0], 7, checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.Rcl:
          bytes.AddRange(encoder.ShiftRegister(instruction.Registers[0], 2, checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.Rcr:
          bytes.AddRange(encoder.ShiftRegister(instruction.Registers[0], 3, checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.Shld:
          bytes.AddRange(encoder.DoubleShift(instruction.Registers[0], instruction.Registers[1], right: false,
            checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.Shrd:
          bytes.AddRange(encoder.DoubleShift(instruction.Registers[0], instruction.Registers[1], right: true,
            checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.Cwd:
          bytes.AddRange(encoder.Cwd());
          break;
        case X86TargetOpcode.Cbw:
          bytes.AddRange(encoder.Cbw());
          break;
        case X86TargetOpcode.Faddp: bytes.Add(0xDE); bytes.Add(0xC1); break;
        case X86TargetOpcode.Fsubp: bytes.Add(0xDE); bytes.Add(0xE9); break;
        case X86TargetOpcode.Fmulp: bytes.Add(0xDE); bytes.Add(0xC9); break;
        case X86TargetOpcode.Fdivp: bytes.Add(0xDE); bytes.Add(0xF9); break;
        case X86TargetOpcode.Fcompp: bytes.Add(0xDE); bytes.Add(0xD9); break;
        case X86TargetOpcode.FstswAx: bytes.Add(0xDF); bytes.Add(0xE0); break;
        case X86TargetOpcode.Sahf: bytes.Add(0x9E); break;
        case X86TargetOpcode.Fsqrt: bytes.Add(0xD9); bytes.Add(0xFA); break;
        case X86TargetOpcode.Fsin: bytes.Add(0xD9); bytes.Add(0xFE); break;
        case X86TargetOpcode.Fcos: bytes.Add(0xD9); bytes.Add(0xFF); break;
        case X86TargetOpcode.Fptan: bytes.Add(0xD9); bytes.Add(0xF2); break;
        case X86TargetOpcode.Fpatan: bytes.Add(0xDE); bytes.Add(0xF3); break;
        case X86TargetOpcode.Fyl2x: bytes.Add(0xDE); bytes.Add(0xF1); break;
        case X86TargetOpcode.Fxch: bytes.Add(0xD9); bytes.Add(0xC9); break;
        case X86TargetOpcode.FstpSt0: bytes.Add(0xDD); bytes.Add(0xD8); break;
        case X86TargetOpcode.Fld1: bytes.Add(0xD9); bytes.Add(0xE8); break;
        case X86TargetOpcode.Fldln2: bytes.Add(0xD9); bytes.Add(0xED); break;
        case X86TargetOpcode.Fldlg2: bytes.Add(0xD9); bytes.Add(0xEC); break;
        case X86TargetOpcode.Fldl2e: bytes.Add(0xD9); bytes.Add(0xEA); break;
        case X86TargetOpcode.Fldl2t: bytes.Add(0xD9); bytes.Add(0xE9); break;
        case X86TargetOpcode.Fld when instruction.Address is { } fldAddress:
          var fldBytes = encoder.X87Memory(fldAddress, fldAddress.WidthBits == 64 ? (byte)0xDD : (byte)0xD9, 0);
          bytes.AddRange(fldBytes);
          AddAddressRelocation(fldAddress, offset, fldBytes.Length);
          break;
        case X86TargetOpcode.Fstp when instruction.Address is { } fstpAddress:
          var fstpBytes = encoder.X87Memory(fstpAddress, fstpAddress.WidthBits == 64 ? (byte)0xDD : (byte)0xD9, 3);
          bytes.AddRange(fstpBytes);
          AddAddressRelocation(fstpAddress, offset, fstpBytes.Length);
          break;
        case X86TargetOpcode.Fild when instruction.Address is { } fildAddress:
          var fildBytes = encoder.X87Memory(fildAddress, fildAddress.WidthBits == 64 ? (byte)0xDF : (byte)0xDB,
            fildAddress.WidthBits == 64 ? 5 : 0);
          bytes.AddRange(fildBytes);
          AddAddressRelocation(fildAddress, offset, fildBytes.Length);
          break;
        case X86TargetOpcode.Fistp when instruction.Address is { } fistpAddress:
          var fistpBytes = encoder.X87Memory(fistpAddress, fistpAddress.WidthBits == 64 ? (byte)0xDF : (byte)0xDB,
            fistpAddress.WidthBits == 64 ? 7 : 3);
          bytes.AddRange(fistpBytes);
          AddAddressRelocation(fistpAddress, offset, fistpBytes.Length);
          break;
        case X86TargetOpcode.Fadd or X86TargetOpcode.Fsub or X86TargetOpcode.Fmul
            or X86TargetOpcode.Fdiv or X86TargetOpcode.Fcomp when instruction.Address is { } fpAddress:
          var fpBytes = encoder.X87Memory(fpAddress,
            fpAddress.WidthBits == 64 ? (byte)0xDC : (byte)0xD8,
            instruction.Opcode switch {
              X86TargetOpcode.Fadd => 0,
              X86TargetOpcode.Fmul => 1,
              X86TargetOpcode.Fcomp => 3,
              X86TargetOpcode.Fsub => 4,
              _ => 6,
            });
          bytes.AddRange(fpBytes);
          AddAddressRelocation(fpAddress, offset, fpBytes.Length);
          break;
        case X86TargetOpcode.Fiadd or X86TargetOpcode.Fisub or X86TargetOpcode.Fimul
            or X86TargetOpcode.Fidiv when instruction.Address is { } integerAddress:
          var integerBytes = encoder.X87Memory(integerAddress,
            integerAddress.WidthBits == 16 ? (byte)0xDE : (byte)0xDA,
            instruction.Opcode switch {
              X86TargetOpcode.Fiadd => 0,
              X86TargetOpcode.Fimul => 1,
              X86TargetOpcode.Fisub => 4,
              _ => 6,
            });
          bytes.AddRange(integerBytes);
          AddAddressRelocation(integerAddress, offset, integerBytes.Length);
          break;
        case X86TargetOpcode.Jmp:
        case X86TargetOpcode.Jcc:
          if (string.IsNullOrWhiteSpace(instruction.Symbol))
            throw new InvalidOperationException("branches require a symbol");
          var shortRelocation = function.Mode == X86Mode.Bit16;
          var branch = instruction.Opcode == X86TargetOpcode.Jmp
            ? shortRelocation
              ? new MachineCode([0xE9, 0, 0], [new MachineRelocation(1, MachineRelocationKind.Relative16, instruction.Symbol, -2)])
              : new MachineCode([0xE9, 0, 0, 0, 0], [new MachineRelocation(1, MachineRelocationKind.Relative32, instruction.Symbol, -4)])
            : shortRelocation
              ? new MachineCode([0x0F, (byte)(0x80 + instruction.Immediate), 0, 0], [new MachineRelocation(2, MachineRelocationKind.Relative16, instruction.Symbol, -2)])
              : new MachineCode([(byte)0x0F, (byte)(0x80 + instruction.Immediate), 0, 0, 0, 0], [new MachineRelocation(2, MachineRelocationKind.Relative32, instruction.Symbol, -4)]);
          relocations.AddRange(branch.Relocations.Select(r => r with { Offset = r.Offset + offset }));
          bytes.AddRange(branch.Bytes);
          break;
        case X86TargetOpcode.Push when instruction.Registers.Count == 1:
          bytes.AddRange(encoder.Push(instruction.Registers[0]));
          break;
        case X86TargetOpcode.Push when instruction.Address is { } pushAddress:
          var pushBytes = encoder.IndirectMemory(pushAddress, 6);
          bytes.AddRange(pushBytes);
          AddAddressRelocation(pushAddress, offset, pushBytes.Length);
          break;
        case X86TargetOpcode.Pop:
          if (instruction.Address is { } popAddress) {
            var popBytes = encoder.IndirectMemory(popAddress, 0, 0x8F);
            bytes.AddRange(popBytes);
            AddAddressRelocation(popAddress, offset, popBytes.Length);
            break;
          }
          bytes.AddRange(encoder.Pop(instruction.Registers[0]));
          break;
        case X86TargetOpcode.Push when instruction.Registers.Count == 0:
          bytes.AddRange(encoder.PushImmediate(checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.Call:
          if (string.IsNullOrWhiteSpace(instruction.Symbol))
            bytes.AddRange(encoder.IndirectRegister(instruction.Registers[0], 2));
          else {
            var call = X86RelocationEncoder.CallRelative(function.Mode, instruction.Symbol);
            relocations.AddRange(call.Relocations.Select(r => r with { Offset = r.Offset + offset }));
            bytes.AddRange(call.Bytes);
          }
          break;
        case X86TargetOpcode.JmpIndirect:
          if (instruction.Address is { } indirectAddress) {
            var indirectBytes = encoder.IndirectMemory(indirectAddress, 4);
            bytes.AddRange(indirectBytes);
            AddAddressRelocation(indirectAddress, offset, indirectBytes.Length);
          } else
            bytes.AddRange(encoder.IndirectRegister(instruction.Registers[0], 4));
          break;
        case X86TargetOpcode.CallFar when instruction.Address is { } farAddress:
          var farBytes = encoder.IndirectMemory(farAddress, 3);
          bytes.AddRange(farBytes);
          AddAddressRelocation(farAddress, offset, farBytes.Length);
          break;
        case X86TargetOpcode.Ret:
          AppendFunctionEpilogue();
          bytes.AddRange(encoder.Ret());
          break;
        default:
          throw new NotSupportedException($"x86 target opcode '{instruction.Opcode}' is not encodable");
      }
    }

    if (emitReturn) {
      AppendFunctionEpilogue();
      bytes.AddRange(encoder.Ret());
    }
    foreach (var label in function.LabelInstructionIndices.Where(pair => pair.Value == function.Instructions.Count))
      labels[label.Key] = bytes.Count;
    return new(bytes.ToArray(), relocations, labels);
  }
}
