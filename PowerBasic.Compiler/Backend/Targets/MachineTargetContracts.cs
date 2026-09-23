using PowerBasic.Compiler.Backend;

namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>Target-independent stages shared by every machine backend.</summary>
public interface IMachineTarget {
  MachineTargetDescription Description { get; }
  IMachineAbi Abi { get; }
  IMachineInstructionEncoder Encoder { get; }
  IMachineEmitter Emitter { get; }

  /// <summary>Creates the target-owned Low IR machine lowering for one compilation.</summary>
  IMachineFunctionLowerer CreateLowerer(SelectionTarget selectionTarget);
}

public interface IMachineAbi {
  int PointerBits { get; }
  int StackAlignment { get; }
  int ShadowSpaceBytes { get; }
  IReadOnlyList<MachineRegister> ArgumentRegisters { get; }
  MachineRegister ReturnRegister { get; }
  IReadOnlySet<MachineRegister> CalleeSavedRegisters { get; }
}

public interface IMachineInstructionEncoder {
  byte[] Ret();
  byte[] Push(MachineRegister register);
  byte[] Pop(MachineRegister register);
  byte[] MoveImmediate(MachineRegister register, ulong value);
  byte[] AdjustStack(int bytes, bool allocate);
}

public interface IMachineEmitter {
  MachineCode EmitFunction(ReadOnlySpan<byte> body, bool preserveFramePointer = true);
}

public readonly record struct MachineTargetDescription(string Name, int PointerBits, int RegisterBits);

public readonly record struct MachineRegister(string Name, int Encoding, int Bits, int AliasGroup = -1) {
  public override string ToString() => this.Name;
}

public readonly record struct MachineCode(
    byte[] Bytes,
    IReadOnlyList<MachineRelocation> Relocations,
    IReadOnlyDictionary<string, int>? Labels = null);

public readonly record struct MachineRelocation(
    int Offset,
    MachineRelocationKind Kind,
    string Symbol,
    int Addend = 0);

public enum MachineRelocationKind {
  Relative16,
  Absolute16,
  Relative32,
  Absolute32,
  Absolute64,
}
