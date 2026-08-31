namespace PowerBasic.Compiler.Asm;

/// <summary>POPCNT encoder used by target-aware inline assembly.</summary>
public sealed partial class Assembler {
  /// <summary>POPCNT r16/r32, r16/r32 (F3 0F B8 /r; operand size selects 16 or 32 bits).</summary>
  public void Popcnt(Reg destination, Reg source) {
    RequireWordOrDword(destination, nameof(destination));
    RequireWordOrDword(source, nameof(source));
    RequireSameSize(destination, source);

    this.EmitOperandSizePrefixIf(destination.IsDword());
    this.EmitByte(0xF3);
    this.EmitByte(0x0F);
    this.EmitByte(0xB8);
    this.EmitModRmRegister(destination.Index(), source);
  }

  /// <summary>POPCNT r16/r32, m16/m32. An unsized memory operand inherits the destination width.</summary>
  public void Popcnt(Reg destination, Mem source) {
    RequireWordOrDword(destination, nameof(destination));
    var requiredSize = destination.Size();
    if (source.Size != OperandSize.None && source.Size != requiredSize)
      throw new ArgumentException($"POPCNT {destination}, {source}: memory width must be {requiredSize}.", nameof(source));

    this.EmitSegmentPrefix(source);
    this.EmitOperandSizePrefixIf(destination.IsDword());
    this.EmitByte(0xF3);
    this.EmitByte(0x0F);
    this.EmitByte(0xB8);
    this.EmitModRmMemory(destination.Index(), source);
  }
}
