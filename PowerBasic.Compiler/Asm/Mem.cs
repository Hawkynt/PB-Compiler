namespace PowerBasic.Compiler.Asm;

/// <summary>
/// A real-mode memory/effective-address operand: 8086 16-bit base/index forms or, when a 386
/// register is named, a 32-bit ModRM/SIB form with scale 1/2/4/8. The operand may additionally carry
/// a displacement or label, segment override and explicit data width.
/// </summary>
public readonly struct Mem {

  private int _scale { get; init; }

  public Reg? Base { get; private init; }
  public Reg? Index { get; private init; }
  public int Scale => this._scale == 0 ? 1 : this._scale;
  public int Displacement { get; private init; }

  /// <summary>Whether this operand needs the 386 address-size override in 16-bit real mode.</summary>
  public bool Uses32BitAddressing => this.Base?.IsDword() == true || this.Index?.IsDword() == true;

  /// <summary>When set, the bound label's image offset is added to <see cref="Displacement"/>.</summary>
  public Label? Label { get; private init; }

  public OperandSize Size { get; private init; }
  public Reg? Segment { get; private init; }

  #region sizeless factories

  public static Mem At(int displacement) => new() { Displacement = displacement };

  public static Mem At(Reg @base, int displacement = 0) {
    ValidateRegisters(@base, null, 1);
    return new() { Base = @base, _scale = 1, Displacement = displacement };
  }

  public static Mem At(Reg @base, Reg index, int displacement = 0) {
    ValidateRegisters(@base, index, 1);
    return new() { Base = @base, Index = index, _scale = 1, Displacement = displacement };
  }

  /// <summary>Creates a 386 SIB address <c>[base + index*scale + displacement]</c>.</summary>
  public static Mem AtScaled(Reg? @base, Reg index, int scale, int displacement = 0) {
    ValidateRegisters(@base, index, scale);
    if (!index.IsDword())
      throw new ArgumentException("Scaled addressing requires 32-bit address registers.", nameof(index));
    return new() { Base = @base, Index = index, _scale = scale, Displacement = displacement };
  }

  public static Mem At(Label label, int displacement = 0) => new() { Label = label ?? throw new ArgumentNullException(nameof(label)), Displacement = displacement };

  public static Mem At(Reg @base, Label label, int displacement = 0) {
    ValidateRegisters(@base, null, 1);
    return new() { Base = @base, _scale = 1, Label = label ?? throw new ArgumentNullException(nameof(label)), Displacement = displacement };
  }

  #endregion

  #region sized factories

  public static Mem Byte(int displacement) => At(displacement).WithSize(OperandSize.Byte);
  public static Mem Byte(Reg @base, int displacement = 0) => At(@base, displacement).WithSize(OperandSize.Byte);
  public static Mem Byte(Reg @base, Reg index, int displacement = 0) => At(@base, index, displacement).WithSize(OperandSize.Byte);
  public static Mem Byte(Label label, int displacement = 0) => At(label, displacement).WithSize(OperandSize.Byte);
  public static Mem Byte(Reg @base, Label label, int displacement = 0) => At(@base, label, displacement).WithSize(OperandSize.Byte);

  public static Mem Word(int displacement) => At(displacement).WithSize(OperandSize.Word);
  public static Mem Word(Reg @base, int displacement = 0) => At(@base, displacement).WithSize(OperandSize.Word);
  public static Mem Word(Reg @base, Reg index, int displacement = 0) => At(@base, index, displacement).WithSize(OperandSize.Word);
  public static Mem Word(Label label, int displacement = 0) => At(label, displacement).WithSize(OperandSize.Word);
  public static Mem Word(Reg @base, Label label, int displacement = 0) => At(@base, label, displacement).WithSize(OperandSize.Word);

  public static Mem Dword(int displacement) => At(displacement).WithSize(OperandSize.Dword);
  public static Mem Dword(Reg @base, int displacement = 0) => At(@base, displacement).WithSize(OperandSize.Dword);
  public static Mem Dword(Reg @base, Reg index, int displacement = 0) => At(@base, index, displacement).WithSize(OperandSize.Dword);
  public static Mem Dword(Label label, int displacement = 0) => At(label, displacement).WithSize(OperandSize.Dword);
  public static Mem Dword(Reg @base, Label label, int displacement = 0) => At(@base, label, displacement).WithSize(OperandSize.Dword);

  public static Mem Qword(int displacement) => At(displacement).WithSize(OperandSize.Qword);
  public static Mem Qword(Reg @base, int displacement = 0) => At(@base, displacement).WithSize(OperandSize.Qword);
  public static Mem Qword(Reg @base, Reg index, int displacement = 0) => At(@base, index, displacement).WithSize(OperandSize.Qword);
  public static Mem Qword(Label label, int displacement = 0) => At(label, displacement).WithSize(OperandSize.Qword);
  public static Mem Qword(Reg @base, Label label, int displacement = 0) => At(@base, label, displacement).WithSize(OperandSize.Qword);

  public static Mem Tbyte(int displacement) => At(displacement).WithSize(OperandSize.Tbyte);
  public static Mem Tbyte(Reg @base, int displacement = 0) => At(@base, displacement).WithSize(OperandSize.Tbyte);
  public static Mem Tbyte(Reg @base, Reg index, int displacement = 0) => At(@base, index, displacement).WithSize(OperandSize.Tbyte);
  public static Mem Tbyte(Label label, int displacement = 0) => At(label, displacement).WithSize(OperandSize.Tbyte);
  public static Mem Tbyte(Reg @base, Label label, int displacement = 0) => At(@base, label, displacement).WithSize(OperandSize.Tbyte);

  #endregion

  public Mem WithSize(OperandSize size) => this with { Size = size };

  /// <summary>Returns the same addressing expression displaced by <paramref name="delta"/> bytes.</summary>
  public Mem Offset(int delta) => this with { Displacement = checked(this.Displacement + delta) };

  /// <summary>Applies a segment override prefix to this operand.</summary>
  public Mem Seg(Reg segment) => segment.IsSegment()
    ? this with { Segment = segment }
    : throw new ArgumentException($"{segment} is not a segment register.", nameof(segment));

  public Mem Es() => this.Seg(Reg.ES);
  public Mem Cs() => this.Seg(Reg.CS);
  public Mem Ss() => this.Seg(Reg.SS);
  public Mem Ds() => this.Seg(Reg.DS);
  public Mem Fs() => this.Seg(Reg.FS);
  public Mem Gs() => this.Seg(Reg.GS);

  private static void ValidateRegisters(Reg? @base, Reg? index, int scale) {
    if (scale is not (1 or 2 or 4 or 8))
      throw new ArgumentOutOfRangeException(nameof(scale), scale, "x86 scale must be 1, 2, 4 or 8.");

    var usesDword = @base?.IsDword() == true || index?.IsDword() == true;
    if (usesDword) {
      if (@base is { } b && !b.IsDword())
        throw new ArgumentException("16- and 32-bit address registers cannot be mixed.", nameof(@base));
      if (index is { } i && !i.IsDword())
        throw new ArgumentException("16- and 32-bit address registers cannot be mixed.", nameof(index));
      if (index == Reg.ESP)
        throw new ArgumentException("ESP cannot be a SIB index register.", nameof(index));
      return;
    }

    if (@base is not (Reg.BX or Reg.BP or Reg.SI or Reg.DI))
      throw new ArgumentException($"{@base} cannot address memory in 16-bit mode (use BX, BP, SI or DI).", nameof(@base));
    if (scale != 1)
      throw new ArgumentException("Scaled addressing requires 32-bit address registers.", nameof(scale));
    if (index is not { } wordIndex)
      return;
    if (wordIndex is not (Reg.SI or Reg.DI))
      throw new ArgumentException($"{wordIndex} is not a valid index register (use SI or DI).", nameof(index));
    if (@base is not (Reg.BX or Reg.BP))
      throw new ArgumentException($"{@base}+{wordIndex} is not a valid base/index combination.", nameof(@base));
  }

  public override string ToString() {
    var parts = new List<string>();
    if (this.Base is { } b)
      parts.Add(b.ToString());
    if (this.Index is { } i)
      parts.Add(this.Scale == 1 ? i.ToString() : $"{i}*{this.Scale}");
    if (this.Label is { } l)
      parts.Add(l.ToString());
    if (this.Displacement != 0 || parts.Count == 0)
      parts.Add(this.Displacement.ToString());

    var prefix = this.Segment is { } s ? $"{s}:" : "";
    return $"{prefix}[{string.Join("+", parts)}]";
  }
}
