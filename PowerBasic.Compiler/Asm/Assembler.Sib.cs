namespace PowerBasic.Compiler.Asm;

public sealed partial class Assembler {

  /// <summary>
  /// Emits the 80386 address-size form of LEA. In 16-bit real mode a 32-bit effective address needs
  /// the 67h address-size override; a 32-bit destination additionally needs the ordinary 66h operand
  /// override. The ModRM/SIB byte layout follows the 80386 architecture definition directly.
  /// </summary>
  public void Lea386(Reg destination, Mem source) {
    var start = this.Position;
    RequireWordOrDword(destination, nameof(destination));
    if (!source.Uses32BitAddressing)
      throw new ArgumentException("LEA386 requires at least one 32-bit address register.", nameof(source));
    if (source.Label is not null)
      throw new ArgumentException("32-bit effective-address labels are not supported by the 16-bit linker.", nameof(source));

    this.EmitSegmentPrefix(source);
    this.EmitByte(0x67);
    this.EmitOperandSizePrefixIf(destination.IsDword());
    this.EmitByte(0x8D);
    this.EmitModRmMemory32(destination.Index(), source);
    // LEA reads its address registers and writes only the destination; it never touches memory or flags.
    this.RecordSchedMem(start, 0, RegBit(destination), false, false, false, false, source);
  }

  private void EmitModRmMemory32(int regField, Mem memory) {
    var @base = memory.Base;
    var index = memory.Index;
    if (@base is { } baseRegister && !baseRegister.IsDword())
      throw new ArgumentException("32-bit addressing requires a 32-bit base register.", nameof(memory));
    if (index is { } indexRegister && !indexRegister.IsDword())
      throw new ArgumentException("32-bit addressing requires a 32-bit index register.", nameof(memory));
    if (index == Reg.ESP)
      throw new ArgumentException("ESP cannot be encoded as a SIB index register.", nameof(memory));

    var scale = memory.Scale switch {
      1 => 0,
      2 => 1,
      4 => 2,
      8 => 3,
      _ => throw new ArgumentException($"Invalid SIB scale {memory.Scale}.", nameof(memory)),
    };

    // An index without a base uses SIB base=101 with mod=00 and therefore always carries disp32.
    if (@base is null) {
      if (index is null)
        throw new ArgumentException("A 32-bit effective address needs a base or index register.", nameof(memory));
      this.EmitByte((byte)(regField << 3 | 4));
      this.EmitByte((byte)(scale << 6 | index.Value.Index() << 3 | 5));
      this.EmitDword((uint)memory.Displacement);
      return;
    }

    var baseIndex = @base.Value.Index();
    var displacement = memory.Displacement;
    var mod = displacement switch {
      0 when @base != Reg.EBP => 0,
      >= sbyte.MinValue and <= sbyte.MaxValue => 1,
      _ => 2,
    };
    var needsSib = index is not null || @base == Reg.ESP;
    this.EmitByte((byte)(mod << 6 | regField << 3 | (needsSib ? 4 : baseIndex)));

    if (needsSib) {
      // SIB index=100 means "no index"; that is how an ESP-only base is encoded.
      var indexBits = index?.Index() ?? 4;
      this.EmitByte((byte)(scale << 6 | indexBits << 3 | baseIndex));
    }

    switch (mod) {
      case 1:
        this.EmitByte((byte)(sbyte)displacement);
        break;
      case 2:
        this.EmitDword((uint)displacement);
        break;
    }
  }
}
