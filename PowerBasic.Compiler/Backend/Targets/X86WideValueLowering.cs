namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>Physical decomposition of integer values wider than the scalar target register width.</summary>
public sealed record X86WideValue(int WidthBits, IReadOnlyList<MachineRegister> Parts) {
  public int PartBits => Parts.Count == 0 ? 0 : Parts[0].Bits;
  public static X86WideValue Split(int widthBits, X86TargetRegisterFile scalar, int first = 0) {
    if (widthBits is not (128 or 256 or 512))
      throw new ArgumentOutOfRangeException(nameof(widthBits));
    var count = (widthBits + scalar.Registers[0].Bits - 1) / scalar.Registers[0].Bits;
    if (first + count > scalar.Registers.Count)
      throw new InvalidOperationException("not enough scalar registers for wide value");
    return new(widthBits, scalar.Registers.Skip(first).Take(count).ToArray());
  }
}

public static class X86WideValueLowering {
  public static IReadOnlyList<MachineRegister> PlaceValue(int widthBits, X86Abi abi, int registerStart = 0) {
    var parts = Math.Max(1, (widthBits + abi.PointerBits - 1) / abi.PointerBits);
    if (registerStart + parts > abi.ArgumentRegisters.Count)
      return [];
    return abi.ArgumentRegisters.Skip(registerStart).Take(parts).ToArray();
  }

  public static IReadOnlyList<MachineRegister> PlaceReturn(int widthBits, X86Abi abi) =>
    PlaceValue(widthBits, abi, 0);

  /// <summary>Builds the carry-chain operation used when no SIMD class is available.</summary>
  public static IEnumerable<X86TargetInstruction> Add(X86WideValue left, X86WideValue right) {
    if (left.WidthBits != right.WidthBits || left.Parts.Count != right.Parts.Count)
      throw new ArgumentException("wide values must have matching shapes");
    for (var i = 0; i < left.Parts.Count; ++i)
      yield return new(i == 0 ? X86TargetOpcode.Add : X86TargetOpcode.Adc,
        [left.Parts[i], right.Parts[i]]);
  }
}
