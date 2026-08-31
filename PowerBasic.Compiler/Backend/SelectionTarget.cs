namespace PowerBasic.Compiler.Backend;

/// <summary>
/// What the instruction selector is compiling for: the CPU generation it may assume and the
/// optimization objective it is trading against. Both are properties of the whole compilation rather
/// than of an individual function; the IR remains target-independent.
///
/// <para>
/// <see cref="Cpu386"/> is retained as a compatibility input for existing selector tests and callers.
/// Whole-program code generation also supplies <see cref="CpuLevel"/>, so 80186/80286 and the
/// 80486/80586/80686 tiers are no longer collapsed into the old pre-386/386-or-later split.
/// </para>
/// </summary>
/// <param name="Cpu386">compatibility flag meaning the declared target is at least an 80386</param>
/// <param name="Optimize">whether optimization is enabled</param>
/// <param name="OptimizeSpeed">whether speed is preferred</param>
/// <param name="OptimizeSize">whether size is preferred</param>
/// <param name="Cost">optional target instruction cost model</param>
/// <param name="CpuLevel">normalized x86 generation: 86, 186, 286, 386, 486, 586 or 686</param>
public readonly record struct SelectionTarget(
  bool Cpu386 = false,
  bool Optimize = false,
  bool OptimizeSpeed = false,
  bool OptimizeSize = false,
  CodeGen.TargetCost? Cost = null,
  int CpuLevel = 86) {

  /// <summary>The effective generation after honoring the historical <see cref="Cpu386"/> flag.</summary>
  public int EffectiveCpuLevel => Math.Max(this.CpuLevel, this.Cpu386 ? 386 : 86);

  /// <summary>Whether 80186 instructions may be selected.</summary>
  public bool Cpu186OrLater => this.EffectiveCpuLevel >= 186;

  /// <summary>Whether 80286 instructions and semantics may be selected.</summary>
  public bool Cpu286OrLater => this.EffectiveCpuLevel >= 286;

  /// <summary>Whether 80386 instructions and 32-bit general-purpose registers may be selected.</summary>
  public bool Cpu386OrLater => this.EffectiveCpuLevel >= 386;

  /// <summary>Whether 80486 instructions may be selected.</summary>
  public bool Cpu486OrLater => this.EffectiveCpuLevel >= 486;

  /// <summary>Whether Pentium/80586 instructions may be selected.</summary>
  public bool Cpu586OrLater => this.EffectiveCpuLevel >= 586;

  /// <summary>Whether P6/80686 instructions may be selected.</summary>
  public bool Cpu686OrLater => this.EffectiveCpuLevel >= 686;

  /// <summary>An 8086 with no optimization - what a hand-built test function is selected for.</summary>
  public static SelectionTarget Baseline { get; } = new();
}
