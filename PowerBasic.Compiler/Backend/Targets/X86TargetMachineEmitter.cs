namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>Encodes the independent hosted x86 machine representation and its relocations.</summary>
public sealed class X86TargetMachineEmitter(X86InstructionEncoder encoder) {
  private readonly X86VectorInstructionEncoder _vectorEncoder = new();
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
      var width = function.Mode switch { X86Mode.Bit16 => 2, X86Mode.Bit64 => 8, _ => 4 };
      var kind = function.Mode switch {
        X86Mode.Bit16 => MachineRelocationKind.Absolute16,
        X86Mode.Bit64 => MachineRelocationKind.Absolute64,
        _ => MachineRelocationKind.Absolute32,
      };
      relocations.Add(new MachineRelocation(instructionOffset + encodedLength - width, kind, symbol,
        address.Displacement));
    }
    void AddMemoryAddressRelocation(X86TargetAddress address, int instructionOffset, int encodedLength,
        int immediateWidth) {
      if (address.Symbol is not { Length: > 0 } symbol || address.Base is not null || address.Index is not null)
        return;
      var width = function.Mode switch { X86Mode.Bit16 => 2, X86Mode.Bit64 => 8, _ => 4 };
      var kind = function.Mode switch {
        X86Mode.Bit16 => MachineRelocationKind.Absolute16,
        X86Mode.Bit64 => MachineRelocationKind.Absolute64,
        _ => MachineRelocationKind.Absolute32,
      };
      relocations.Add(new MachineRelocation(instructionOffset + encodedLength - immediateWidth - width,
        kind, symbol, address.Displacement));
    }
    void AppendFunctionEpilogue() {
      if (function.Abi.ShadowSpaceBytes != 0)
        bytes.AddRange(encoder.AdjustStack(function.Abi.ShadowSpaceBytes, allocate: false));
      if (function.FrameSizeBytes != 0)
        bytes.AddRange(encoder.AdjustStack(function.FrameSizeBytes, allocate: false));
      if (preserveFramePointer)
        bytes.AddRange(encoder.Pop(registers.FramePointer));
    }
    MachineRegister ScratchRegister(int widthBits) => widthBits switch {
      8 => registers.LowBytes[0],
      16 => function.Mode == X86Mode.Bit64 ? registers.Words[0] : registers.Registers[0],
      32 => registers.Dwords[0],
      _ => registers.Registers[0],
    };
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
        case X86TargetOpcode.Nop:
          bytes.AddRange(encoder.Nop());
          break;
        case X86TargetOpcode.Popcnt when instruction.Address is { } popcntAddress:
          var popcntBytes = encoder.Popcnt(instruction.Registers[0], popcntAddress);
          bytes.AddRange(popcntBytes); AddAddressRelocation(popcntAddress, offset, popcntBytes.Length);
          break;
        case X86TargetOpcode.Popcnt when instruction.Registers.Count >= 2:
          bytes.AddRange(encoder.Popcnt(instruction.Registers[0], instruction.Registers[1]));
          break;
        case X86TargetOpcode.Bsf or X86TargetOpcode.Bsr when instruction.Registers.Count >= 2:
          bytes.AddRange(encoder.BitScan(instruction.Registers[0], instruction.Registers[1],
            instruction.Opcode == X86TargetOpcode.Bsr));
          break;
        case X86TargetOpcode.Bsf or X86TargetOpcode.Bsr when instruction.Address is { } scanAddress:
          var scanBytes = encoder.BitScan(instruction.Registers[0], scanAddress,
            instruction.Opcode == X86TargetOpcode.Bsr);
          bytes.AddRange(scanBytes); AddAddressRelocation(scanAddress, offset, scanBytes.Length);
          break;
        case X86TargetOpcode.Bextr when instruction.Registers.Count >= 3:
          bytes.AddRange(encoder.BmiRegister(instruction.Registers[0], instruction.Registers[1], instruction.Registers[2], 0xF7));
          break;
        case X86TargetOpcode.Andn when instruction.Registers.Count >= 3:
          bytes.AddRange(encoder.BmiRegister(instruction.Registers[0], instruction.Registers[1], instruction.Registers[2], 0xF2));
          break;
        case X86TargetOpcode.Blsi when instruction.Registers.Count >= 2:
          bytes.AddRange(encoder.BmiRegister(instruction.Registers[0], instruction.Registers[1], instruction.Registers[1], 0xF3));
          break;
        case X86TargetOpcode.Blsr when instruction.Registers.Count >= 2:
          bytes.AddRange(encoder.BmiRegister(instruction.Registers[0], instruction.Registers[1], instruction.Registers[1], 0xF3));
          break;
        case X86TargetOpcode.Bzhi or X86TargetOpcode.Pext or X86TargetOpcode.Pdep or X86TargetOpcode.Mulx
            when instruction.Registers.Count >= 3:
          bytes.AddRange(encoder.BmiRegister(instruction.Registers[0], instruction.Registers[1], instruction.Registers[2],
            instruction.Opcode switch { X86TargetOpcode.Bzhi => (byte)0xF5, X86TargetOpcode.Pext => (byte)0xF5,
              X86TargetOpcode.Pdep => (byte)0xF5, _ => (byte)0xF6 }));
          break;
        case X86TargetOpcode.AesEnc or X86TargetOpcode.AesDec or X86TargetOpcode.AesImc
            when instruction.Registers.Count >= 3 && instruction.Registers[0].Bits >= 128:
          bytes.AddRange(X86CryptoInstructionEncoder.AesVector(instruction.Registers[0], instruction.Registers[1], instruction.Registers[2],
            instruction.Opcode == X86TargetOpcode.AesDec, instruction.Opcode == X86TargetOpcode.AesImc,
            instruction.Registers[0].Bits));
          break;
        case X86TargetOpcode.AesEnc or X86TargetOpcode.AesDec or X86TargetOpcode.AesImc
            when instruction.Registers.Count >= 2:
          bytes.AddRange(X86CryptoInstructionEncoder.Aes(instruction.Registers[0], instruction.Registers[1],
            instruction.Opcode == X86TargetOpcode.AesDec, instruction.Opcode == X86TargetOpcode.AesImc));
          break;
        case X86TargetOpcode.Pclmul when instruction.Registers.Count >= 3:
          bytes.AddRange(X86CryptoInstructionEncoder.PclmulVector(instruction.Registers[0], instruction.Registers[1], instruction.Registers[2],
            (byte)instruction.Immediate, instruction.Registers[0].Bits));
          break;
        case X86TargetOpcode.Pclmul when instruction.Registers.Count >= 2:
          bytes.AddRange(X86CryptoInstructionEncoder.Pclmul(instruction.Registers[0], instruction.Registers[1],
            (byte)instruction.Immediate));
          break;
        case X86TargetOpcode.VectorBinary when instruction.Registers.Count >= 3:
          {
            var destination = instruction.Registers[0];
            var bits = destination.Bits;
            var operation = instruction.VectorOperation ?? X86VectorOpcode.Move;
            var (map, opcode, pp) = operation switch {
              X86VectorOpcode.Move => ((byte)1, (byte)0x6F, (byte)1),
              X86VectorOpcode.Add => ((byte)1, (byte)0xD4, (byte)1), // VPADDQ
              X86VectorOpcode.AddW => ((byte)1, (byte)0xFD, (byte)1), // VPADDW
              X86VectorOpcode.Sub => ((byte)1, (byte)0xFB, (byte)1), // VPSUBQ
              X86VectorOpcode.SubW => ((byte)1, (byte)0xF9, (byte)1), // VPSUBW
              X86VectorOpcode.And => ((byte)1, (byte)0xDB, (byte)1), // VPAND
              X86VectorOpcode.Or => ((byte)1, (byte)0xEB, (byte)1), // VPOR
              X86VectorOpcode.Xor => ((byte)1, (byte)0xEF, (byte)1), // VPXOR
              X86VectorOpcode.Multiply => ((byte)2, (byte)0x40, (byte)1), // VPMULLQ (AVX-512)
              X86VectorOpcode.MinUnsignedDword => ((byte)2, (byte)0x3B, (byte)1), // VPMINUD
              X86VectorOpcode.MaxUnsignedDword => ((byte)2, (byte)0x3D, (byte)1), // VPMAXUD
              X86VectorOpcode.BlendWord => ((byte)3, (byte)0x0E, (byte)1), // VPBLENDW
              X86VectorOpcode.AlignRight => ((byte)3, (byte)0x0F, (byte)3), // VPALIGNR
              _ => throw new BackendInvariantException("X86TargetMachineEmitter.VectorBinary",
                $"operation {operation} is not a generic vector operation"),
            };
            var vectorBytes = instruction.VectorEncoding == X86VectorEncoding.Legacy && bits == 128
              ? _vectorEncoder.LegacyXmm(destination, instruction.Registers[2], map, opcode, pp,
                instruction.Immediate == 0 ? null : (byte?)instruction.Immediate)
              : bits == 64
              ? _vectorEncoder.LegacyMmxImmediate(destination, instruction.Registers[2], opcode,
                instruction.Immediate == 0 ? null : (byte?)instruction.Immediate)
              : bits == 512
              ? _vectorEncoder.Evex(destination, instruction.Registers[1], instruction.Registers[2], map, opcode, pp,
                wide: operation == X86VectorOpcode.Multiply, vectorBits: bits)
              : _vectorEncoder.Vex3(destination, instruction.Registers[1], instruction.Registers[2], map, opcode, pp,
                wide: operation == X86VectorOpcode.Multiply, vectorBits: bits);
            bytes.AddRange(vectorBytes);
          }
          break;
        case X86TargetOpcode.Mov when instruction.Address is { } loadAddress && instruction.Immediate == 0:
          var loadBytes = encoder.MoveMemory(instruction.Registers[0], loadAddress, load: true);
          bytes.AddRange(loadBytes);
          AddAddressRelocation(loadAddress, offset, loadBytes.Length);
          break;
        case X86TargetOpcode.MoveMemoryImmediate when instruction.Address is { } immediateAddress:
          var immediateBytes = encoder.MoveMemoryImmediate(immediateAddress, instruction.Immediate);
          bytes.AddRange(immediateBytes);
          AddMemoryAddressRelocation(immediateAddress, offset, immediateBytes.Length,
            immediateAddress.WidthBits == 8 ? 1 : immediateAddress.WidthBits == 16 ? 2 : 4);
          break;
        case X86TargetOpcode.MoveMemorySymbol when instruction.Address is { } symbolAddress
            && instruction.Symbol is { Length: > 0 } sourceSymbol:
          var symbolBytes = encoder.MoveMemoryImmediate(symbolAddress, 0);
          bytes.AddRange(symbolBytes);
          var sourceWidth = symbolAddress.WidthBits == 8 ? 1 : symbolAddress.WidthBits == 16 ? 2 : 4;
          AddMemoryAddressRelocation(symbolAddress, offset, symbolBytes.Length, sourceWidth);
          relocations.Add(new MachineRelocation(offset + symbolBytes.Length - sourceWidth,
            sourceWidth == 2 ? MachineRelocationKind.Absolute16 : MachineRelocationKind.Absolute32,
            sourceSymbol));
          break;
        case X86TargetOpcode.MoveMemoryToMemory when instruction.Address is { } destinationAddress
            && instruction.SourceAddress is { } sourceAddress:
          var scratch = ScratchRegister(sourceAddress.WidthBits);
          var preserved = registers.Registers[0];
          bytes.AddRange(encoder.Push(preserved));
          var loadMemoryBytes = encoder.MoveMemory(scratch, sourceAddress, load: true);
          bytes.AddRange(loadMemoryBytes);
          var storeMemoryBytes = encoder.MoveMemory(scratch, destinationAddress, load: false);
          bytes.AddRange(storeMemoryBytes);
          bytes.AddRange(encoder.Pop(preserved));
          AddAddressRelocation(sourceAddress, offset + 1, loadMemoryBytes.Length);
          AddAddressRelocation(destinationAddress, offset + 1 + loadMemoryBytes.Length,
            storeMemoryBytes.Length);
          break;
        case X86TargetOpcode.AluMemoryImmediate when instruction.Address is { } aluAddress:
          var extension = instruction.Operands?.OfType<X86TargetOperand.Immediate>().FirstOrDefault()?.Value ?? 0;
          var aluImmediateBytes = encoder.AluMemoryImmediate(aluAddress, checked((int)extension), instruction.Immediate);
          bytes.AddRange(aluImmediateBytes);
          AddAddressRelocation(aluAddress, offset, aluImmediateBytes.Length);
          break;
        case X86TargetOpcode.CompareRegisterMemory when instruction.Address is { } compareAddress:
          var compareMemoryBytes = encoder.AluMemory(instruction.Registers[0], compareAddress, 0x3B, load: true);
          bytes.AddRange(compareMemoryBytes);
          AddAddressRelocation(compareAddress, offset, compareMemoryBytes.Length);
          break;
        case X86TargetOpcode.RegisterMemoryAlu when instruction.Address is { } registerMemoryAddress:
          var registerMemoryBytes = encoder.AluMemory(instruction.Registers[0], registerMemoryAddress,
            checked((int)instruction.Immediate), load: true);
          bytes.AddRange(registerMemoryBytes);
          AddAddressRelocation(registerMemoryAddress, offset, registerMemoryBytes.Length);
          break;
        case X86TargetOpcode.Adc or X86TargetOpcode.Sbb when instruction.Address is { } carryMemoryAddress:
          var carryOpcode = instruction.Opcode == X86TargetOpcode.Adc ? 0x11 : 0x19;
          var carryBytes = encoder.AluMemory(instruction.Registers[0], carryMemoryAddress, carryOpcode, load: false);
          bytes.AddRange(carryBytes); AddAddressRelocation(carryMemoryAddress, offset, carryBytes.Length);
          break;
        case X86TargetOpcode.MoveSymbolAddress when instruction.Symbol is { Length: > 0 } addressSymbol:
          var addressBytes = encoder.MoveImmediate(instruction.Registers[0], 0);
          bytes.AddRange(addressBytes);
          var addressWidth = function.Mode switch { X86Mode.Bit16 => 2, X86Mode.Bit64 => 8, _ => 4 };
          relocations.Add(new MachineRelocation(offset + addressBytes.Length - addressWidth,
            function.Mode switch {
              X86Mode.Bit16 => MachineRelocationKind.Absolute16,
              X86Mode.Bit64 => MachineRelocationKind.Absolute64,
              _ => MachineRelocationKind.Absolute32,
            }, addressSymbol));
          break;
        case X86TargetOpcode.MemoryShiftCount when instruction.Address is { } memoryShiftAddress:
          var shiftMemoryBytes = encoder.ShiftMemoryCount(memoryShiftAddress, checked((int)instruction.Immediate));
          bytes.AddRange(shiftMemoryBytes);
          AddAddressRelocation(memoryShiftAddress, offset, shiftMemoryBytes.Length);
          break;
        case X86TargetOpcode.MemoryShiftImmediate when instruction.Address is { } immediateShiftAddress:
          var immediateShiftExtension = checked((int)(instruction.Operands?.OfType<X86TargetOperand.Immediate>().FirstOrDefault()?.Value ?? 4));
          var immediateShiftBytes = encoder.ShiftMemory(immediateShiftAddress, immediateShiftExtension,
            checked((int)instruction.Immediate));
          bytes.AddRange(immediateShiftBytes);
          AddAddressRelocation(immediateShiftAddress, offset, immediateShiftBytes.Length);
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
        case X86TargetOpcode.Test when instruction.Registers.Count == 1:
          bytes.AddRange(encoder.TestImmediate(instruction.Registers[0], checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.Adc when instruction.Registers.Count >= 2:
          bytes.AddRange(encoder.AluRegister(instruction.Registers[0], instruction.Registers[1], 0x11));
          break;
        case X86TargetOpcode.Sbb when instruction.Registers.Count >= 2:
          bytes.AddRange(encoder.AluRegister(instruction.Registers[0], instruction.Registers[1], 0x19));
          break;
        case X86TargetOpcode.Sbb when instruction.Registers.Count == 1:
          bytes.AddRange(encoder.SbbImmediate(instruction.Registers[0], checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.Adc when instruction.Registers.Count == 1:
          bytes.AddRange(encoder.AdcImmediate(instruction.Registers[0], checked((int)instruction.Immediate)));
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
        case X86TargetOpcode.Mul when instruction.Address is null:
          bytes.AddRange(encoder.UnaryMultiplyDivide(instruction.Registers[0], 4));
          break;
        case X86TargetOpcode.Div when instruction.Address is null:
          bytes.AddRange(encoder.UnaryMultiplyDivide(instruction.Registers[0], 6));
          break;
        case X86TargetOpcode.Idiv:
          if (instruction.Address is { } idivAddress) {
            var idivBytes = encoder.IndirectMemory(idivAddress, 7, 0xF7);
            bytes.AddRange(idivBytes); AddAddressRelocation(idivAddress, offset, idivBytes.Length);
          } else
            bytes.AddRange(encoder.UnaryMultiplyDivide(instruction.Registers[0], 7));
          break;
        case X86TargetOpcode.Mul when instruction.Address is { } mulAddress:
          var mulBytes = encoder.IndirectMemory(mulAddress, 4, 0xF7);
          bytes.AddRange(mulBytes); AddAddressRelocation(mulAddress, offset, mulBytes.Length);
          break;
        case X86TargetOpcode.Div when instruction.Address is { } divAddress:
          var divBytes = encoder.IndirectMemory(divAddress, 6, 0xF7);
          bytes.AddRange(divBytes); AddAddressRelocation(divAddress, offset, divBytes.Length);
          break;
        case X86TargetOpcode.Imul when instruction.Registers.Count >= 2:
          bytes.AddRange(encoder.ImulRegister(instruction.Registers[0], instruction.Registers[1]));
          break;
        case X86TargetOpcode.Imul when instruction.Registers.Count == 1:
          bytes.AddRange(encoder.UnaryMultiplyDivide(instruction.Registers[0], 5));
          break;
        case X86TargetOpcode.Shl:
          bytes.AddRange(instruction.Registers.Count >= 2 ? encoder.ShiftRegisterCount(instruction.Registers[0], 4) : encoder.ShiftRegister(instruction.Registers[0], 4, checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.Shr:
          bytes.AddRange(instruction.Registers.Count >= 2 ? encoder.ShiftRegisterCount(instruction.Registers[0], 5) : encoder.ShiftRegister(instruction.Registers[0], 5, checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.Sar:
          bytes.AddRange(instruction.Registers.Count >= 2 ? encoder.ShiftRegisterCount(instruction.Registers[0], 7) : encoder.ShiftRegister(instruction.Registers[0], 7, checked((int)instruction.Immediate)));
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
        case X86TargetOpcode.JmpIndexed when instruction.Registers.Count == 1
            && instruction.Operands?.FirstOrDefault() is X86TargetOperand.Table table:
          var tableName = "__jt" + offset;
          var tableAddress = new X86TargetAddress(instruction.Registers[0], null, 1, 0,
            function.Mode == X86Mode.Bit16 ? 16 : 32, tableName);
          var indexedBytes = encoder.IndirectMemory(tableAddress, 4);
          bytes.AddRange(indexedBytes);
          AddAddressRelocation(tableAddress, offset, indexedBytes.Length);
          foreach (var label in table.Labels) {
            var tableOffset = bytes.Count;
            var width = function.Mode == X86Mode.Bit16 ? 2 : 4;
            bytes.AddRange(new byte[width]);
            relocations.Add(new MachineRelocation(tableOffset,
              function.Mode == X86Mode.Bit16 ? MachineRelocationKind.Absolute16 : MachineRelocationKind.Absolute32,
              label));
          }
          labels[tableName] = offset + indexedBytes.Length;
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
