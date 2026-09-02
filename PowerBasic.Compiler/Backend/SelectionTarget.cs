namespace PowerBasic.Compiler.Backend;

/// <summary>
/// What the instruction selector is compiling for: the CPU generation it may assume and the
/// optimization objective it is trading against. Both are properties of the whole compilation rather
/// than of an individual function; the IR remains target-independent.
/// </summary>
/// <param name="Optimize">whether optimization is enabled</param>
/// <param name="OptimizeSpeed">whether speed is preferred</param>
/// <param name="OptimizeSize">whether size is preferred</param>
/// <param name="Cost">optional target instruction cost model</param>
/// <param name="CpuLevel">normalized x86 generation: 86, 186, 286, 386, 486, 586 or 686</param>
public readonly record struct SelectionTarget(
  bool Optimize = false,
  bool OptimizeSpeed = false,
  bool OptimizeSize = false,
  CodeGen.TargetCost? Cost = null,
  int CpuLevel = 86) {

  /// <summary>Whether 80186 instructions may be selected.</summary>
  public bool Cpu186OrLater => this.CpuLevel >= 186;

  /// <summary>Whether 80286 instructions and semantics may be selected.</summary>
  public bool Cpu286OrLater => this.CpuLevel >= 286;

  /// <summary>Whether 80386 instructions and 32-bit general-purpose registers may be selected.</summary>
  public bool Cpu386OrLater => this.CpuLevel >= 386;

  /// <summary>Whether 80486 instructions may be selected.</summary>
  public bool Cpu486OrLater => this.CpuLevel >= 486;

  /// <summary>Whether Pentium/80586 instructions may be selected.</summary>
  public bool Cpu586OrLater => this.CpuLevel >= 586;

  /// <summary>Whether P6/80686 instructions may be selected.</summary>
  public bool Cpu686OrLater => this.CpuLevel >= 686;

  /// <summary>An 8086 with no optimization - what a hand-built test function is selected for.</summary>
  public static SelectionTarget Baseline { get; } = new();
}
