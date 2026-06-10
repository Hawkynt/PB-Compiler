namespace PowerBasic.Compiler.Asm;

/// <summary>
/// A 16-bit real-mode memory operand: any legal combination of base
/// (BX/BP), index (SI/DI), displacement and/or label, an optional segment
/// override and an optional explicit operand size.
/// </summary>
public readonly struct Mem {

  public Reg? Base { get; private init; }
  public Reg? Index { get; private init; }
  public int Displacement { get; private init; }

  /// <summary>When set, the bound label's image offset is added to <see cref="Displacement"/>.</summary>
  public Label? Label { get; private init; }

  public OperandSize Size { get; private init; }
  public Reg? Segment { get; private init; }

  #region sizeless factories

  public static Mem At(int displacement) => new() { Displacement = displacement };

  public static Mem At(Reg @base, int displacement = 0) {
    ValidateRegister(@base, null);
    return new() { Base = @base, Displacement = displacement };
  }

  public static Mem At(Reg @base, Reg index, int displacement = 0) {
    ValidateRegister(@base, index);
    return new() { Base = @base, Index = index, Displacement = displacement };
  }

  public static Mem At(Label label, int displacement = 0) => new() { Label = label ?? throw new ArgumentNullException(nameof(label)), Displacement = displacement };

  public static Mem At(Reg @base, Label label, int displacement = 0) {
    ValidateRegister(@base, null);
    return new() { Base = @base, Label = label ?? throw new ArgumentNullException(nameof(label)), Displacement = displacement };
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

  private static void ValidateRegister(Reg @base, Reg? index) {
    if (@base is not (Reg.BX or Reg.BP or Reg.SI or Reg.DI))
      throw new ArgumentException($"{@base} cannot address memory in 16-bit mode (use BX, BP, SI or DI).", nameof(@base));

    if (index is not { } i)
      return;

    if (i is not (Reg.SI or Reg.DI))
      throw new ArgumentException($"{i} is not a valid index register (use SI or DI).", nameof(index));
    if (@base is not (Reg.BX or Reg.BP))
      throw new ArgumentException($"{@base}+{i} is not a valid base/index combination.", nameof(@base));
  }

  public override string ToString() {
    var parts = new List<string>();
    if (this.Base is { } b)
      parts.Add(b.ToString());
    if (this.Index is { } i)
      parts.Add(i.ToString());
    if (this.Label is { } l)
      parts.Add(l.ToString());
    if (this.Displacement != 0 || parts.Count == 0)
      parts.Add(this.Displacement.ToString());

    var prefix = this.Segment is { } s ? $"{s}:" : "";
    return $"{prefix}[{string.Join("+", parts)}]";
  }
}
