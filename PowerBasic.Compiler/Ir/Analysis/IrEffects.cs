namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>Observable or legality-relevant effects an IR operation or call may have.</summary>
[Flags]
public enum IrEffectKind : ushort {
  None = 0,
  ReadsMemory = 1 << 0,
  WritesMemory = 1 << 1,
  MayAllocate = 1 << 2,
  MayRelease = 1 << 3,
  MayTrap = 1 << 4,
  MaySynchronize = 1 << 5,
  PerformsIo = 1 << 6,
  Volatile = 1 << 7,
  MayThrow = 1 << 8,
  MayBlock = 1 << 9,
  Atomic = 1 << 10,
}

/// <summary>
/// Conservative semantic description of one operation. Effects describe what may be observable; determinism states
/// whether equal inputs are guaranteed to produce the same result. A transform may only use a property when it is
/// positively established here.
/// </summary>
public readonly record struct IrEffectSummary(IrEffectKind Effects, bool Deterministic) {

  /// <summary>True when the operation has no modeled observable effects.</summary>
  public bool IsEffectFree => this.Effects == IrEffectKind.None;

  /// <summary>True when repeated equal calls may be value-numbered together.</summary>
  public bool CanCse => this.Deterministic && this.IsEffectFree;

  /// <summary>True when the operation may be executed on a path where it did not originally execute.</summary>
  public bool CanSpeculate => this.CanCse;

  /// <summary>Conservative summary for an external operation with no stronger contract.</summary>
  public static IrEffectSummary UnknownExternal { get; } = new(
    IrEffectKind.ReadsMemory
    | IrEffectKind.WritesMemory
    | IrEffectKind.MayAllocate
    | IrEffectKind.MayRelease
    | IrEffectKind.MayTrap
    | IrEffectKind.MaySynchronize
    | IrEffectKind.PerformsIo
    | IrEffectKind.Volatile
    | IrEffectKind.MayThrow
    | IrEffectKind.MayBlock
    | IrEffectKind.Atomic,
    Deterministic: false);
}

/// <summary>Central target-independent effect contracts shared by analyses and transforms.</summary>
public static class IrEffects {

  private static readonly HashSet<string> _pureMathIntrinsics = new(StringComparer.Ordinal) {
    "sqrt", "sin", "cos", "tan", "atan", "log", "exp", "pow",
  };

  /// <summary>
  /// Returns the checked contract for an external declaration. Unknown runtime/library calls remain maximally
  /// conservative; the only effect-free externals currently admitted are the floating math intrinsics already
  /// proven safe by the existing optimizer contract.
  /// </summary>
  public static IrEffectSummary ForExternalCall(string name) {
    ArgumentNullException.ThrowIfNull(name);
    if (!name.StartsWith("llvm.", StringComparison.Ordinal))
      return IrEffectSummary.UnknownExternal;

    var bare = name[5..];
    var width = bare.IndexOf(".f", StringComparison.Ordinal);
    return _pureMathIntrinsics.Contains(width > 0 ? bare[..width] : bare)
      ? new(IrEffectKind.None, Deterministic: true)
      : IrEffectSummary.UnknownExternal;
  }

  /// <summary>
  /// Returns the checked contract for a direct call. Name-based runtime contracts apply only to
  /// declarations: a definition named like an LLVM intrinsic is still an ordinary function body.
  /// Until its body summary is available, treat it conservatively.
  /// </summary>
  public static IrEffectSummary ForCall(IrFunction callee) {
    ArgumentNullException.ThrowIfNull(callee);
    return callee.IsDeclaration ? ForExternalCall(callee.Name) : IrEffectSummary.UnknownExternal;
  }
}
