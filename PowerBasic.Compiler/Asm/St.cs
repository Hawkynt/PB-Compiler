namespace PowerBasic.Compiler.Asm;

/// <summary>An x87 FPU stack register ST(0)..ST(7).</summary>
public readonly record struct St {

  public int Index { get; }

  public St(int index) {
    if (index is < 0 or > 7)
      throw new ArgumentOutOfRangeException(nameof(index), index, "FPU stack registers are ST(0)..ST(7).");

    this.Index = index;
  }

  public static St St0 => new(0);
  public static St St1 => new(1);
  public static St St2 => new(2);
  public static St St3 => new(3);
  public static St St4 => new(4);
  public static St St5 => new(5);
  public static St St6 => new(6);
  public static St St7 => new(7);

  public override string ToString() => $"ST({this.Index})";
}
