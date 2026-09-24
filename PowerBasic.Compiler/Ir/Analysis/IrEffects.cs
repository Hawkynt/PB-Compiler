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

  private const IrEffectKind _MEMORY_READ_EFFECTS =
    IrEffectKind.ReadsMemory | IrEffectKind.MaySynchronize | IrEffectKind.Volatile | IrEffectKind.Atomic;

  private const IrEffectKind _MEMORY_DEFINITION_EFFECTS =
    IrEffectKind.WritesMemory | IrEffectKind.MayAllocate | IrEffectKind.MayRelease
    | IrEffectKind.MaySynchronize | IrEffectKind.Volatile | IrEffectKind.Atomic;

  private const IrEffectKind _NON_DISCARDABLE_EFFECTS =
    _MEMORY_DEFINITION_EFFECTS | IrEffectKind.MayTrap | IrEffectKind.PerformsIo
    | IrEffectKind.MayThrow | IrEffectKind.MayBlock;

  /// <summary>True when the operation has no modeled observable effects.</summary>
  public bool IsEffectFree => this.Effects == IrEffectKind.None;

  /// <summary>True when the operation may observe the modeled memory state.</summary>
  public bool MayReadMemory => (this.Effects & _MEMORY_READ_EFFECTS) != 0;

  /// <summary>
  /// True when the operation creates a new MemorySSA state or acts as an ordering barrier.
  /// Allocation/release, synchronization, volatile and atomic operations are definitions even when a
  /// narrower contract does not also spell them as ordinary writes.
  /// </summary>
  public bool DefinesMemory => (this.Effects & _MEMORY_DEFINITION_EFFECTS) != 0;

  /// <summary>True when the operation participates in the modeled memory state at all.</summary>
  public bool MayAccessMemory => this.MayReadMemory || this.DefinesMemory;

  /// <summary>
  /// True when an unused result may be discarded. Ordinary non-volatile reads are deliberately
  /// discardable; writes, lifetime changes, traps, synchronization, IO, exceptions and blocking are not.
  /// </summary>
  public bool CanDiscard => (this.Effects & _NON_DISCARDABLE_EFFECTS) == 0;

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

  private static readonly IrEffectSummary _pureDeterministic = new(IrEffectKind.None, Deterministic: true);
  private static readonly IrEffectSummary _pureUnique = new(IrEffectKind.None, Deterministic: false);
  private static readonly IrEffectSummary _read = new(IrEffectKind.ReadsMemory, Deterministic: false);
  private static readonly IrEffectSummary _deterministicRead = new(IrEffectKind.ReadsMemory, Deterministic: true);
  private static readonly IrEffectSummary _write = new(IrEffectKind.WritesMemory, Deterministic: false);
  private static readonly IrEffectSummary _readWrite = new(
    IrEffectKind.ReadsMemory | IrEffectKind.WritesMemory, Deterministic: false);
  private static readonly IrEffectSummary _mayTrap = new(IrEffectKind.MayTrap, Deterministic: true);
  private static readonly IrEffectSummary _consumeString = new(
    IrEffectKind.ReadsMemory | IrEffectKind.MayRelease, Deterministic: true);
  private static readonly IrEffectSummary _duplicateString = new(
    IrEffectKind.ReadsMemory | IrEffectKind.MayAllocate | IrEffectKind.MayTrap, Deterministic: false);
  private static readonly IrEffectSummary _release = new(IrEffectKind.MayRelease, Deterministic: false);
  private static readonly IrEffectSummary _allocateZeroed = new(
    IrEffectKind.WritesMemory | IrEffectKind.MayAllocate | IrEffectKind.MayTrap, Deterministic: false);
  private static readonly IrEffectSummary _allocate = new(
    IrEffectKind.MayAllocate | IrEffectKind.MayTrap, Deterministic: false);
  private static readonly IrEffectSummary _reallocate = new(
    IrEffectKind.ReadsMemory | IrEffectKind.WritesMemory | IrEffectKind.MayAllocate
    | IrEffectKind.MayRelease | IrEffectKind.MayTrap,
    Deterministic: false);
  private static readonly IrEffectSummary _runtimeError = new(
    IrEffectKind.WritesMemory | IrEffectKind.MayTrap | IrEffectKind.MayThrow,
    Deterministic: false);

  /// <summary>
  /// Returns the checked contract for an external declaration. This table is deliberately semantic,
  /// not a naming heuristic: every runtime row below has an ownership/mod-ref contract pinned by the
  /// lowering and runtime ABI. Anything not listed remains maximally conservative.
  /// </summary>
  public static IrEffectSummary ForExternalCall(string name) {
    ArgumentNullException.ThrowIfNull(name);

    var modeled = name switch {
      // Stable-handle query introduced by O0297: reads the descriptor without consuming it.
      "rt_str_len_borrow" => _deterministicRead,

      // DOS LEN consumes its owned handle; DUP creates the owned copy lowering feeds it.
      "rt_str_len" => _consumeString,
      "rt_str_dup" => _duplicateString,
      "rt_str_free" => _release,

      // Raw memory helpers. memcpy/memset have a volatile operand at the call site; the declaration-level
      // fact is conservatively volatile because this overload cannot inspect that operand.
      "rt_mem_compare" => _deterministicRead,
      "rt_mem_copy" => _readWrite,
      "llvm.memcpy.p0.p0.i32" => new(
        IrEffectKind.ReadsMemory | IrEffectKind.WritesMemory | IrEffectKind.Volatile,
        Deterministic: false),
      "llvm.memset.p0.i32" => new(
        IrEffectKind.WritesMemory | IrEffectKind.Volatile,
        Deterministic: false),

      // Dynamic-array lifetime. Allocation can raise PB Error 7; PRESERVE copies the old bytes before release.
      "rt_arr_alloc" or "rt_arr_alloc_ptr" => _allocateZeroed,
      "rt_arr_alloc_nz" => _allocate,
      "rt_arr_realloc" or "rt_arr_realloc_ptr" => _reallocate,
      "rt_arr_free" or "rt_arr_free_ptr" => _release,

      // Raises through ON ERROR on DOS, and terminates on hosted targets. ERR/ERL are observable state.
      "rt_error" => _runtimeError,
      _ => default,
    };
    if (modeled != default)
      return modeled;

    if (!name.StartsWith("llvm.", StringComparison.Ordinal))
      return IrEffectSummary.UnknownExternal;

    var bare = name[5..];
    var width = bare.IndexOf(".f", StringComparison.Ordinal);
    return _pureMathIntrinsics.Contains(width > 0 ? bare[..width] : bare)
      ? _pureDeterministic
      : IrEffectSummary.UnknownExternal;
  }

  /// <summary>
  /// Returns the call-site contract when operands refine a declaration-level effect. The memory intrinsics'
  /// final i1 controls volatility; a literal false therefore keeps the ordinary mod/ref effect without
  /// manufacturing a volatile barrier. Unknown/non-literal flags remain conservative.
  /// </summary>
  public static IrEffectSummary ForCall(IrCall call) {
    ArgumentNullException.ThrowIfNull(call);
    if (call.Callee is not IrFunction callee)
      return IrEffectSummary.UnknownExternal;

    var effects = ForCall(callee);
    if (!callee.IsDeclaration || call.ArgCount < 4
        || callee.Name is not ("llvm.memcpy.p0.p0.i32" or "llvm.memset.p0.i32"))
      return effects;

    return call.GetOperand(4) is IrConstantInt { Value: 0 }
      ? effects with { Effects = effects.Effects & ~IrEffectKind.Volatile }
      : effects;
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

  /// <summary>
  /// Returns the target-independent effect contract for one IR instruction.
  ///
  /// <para>
  /// This switch is intentionally exhaustive rather than ending in a conservative fallback. Adding a new
  /// instruction without deciding its semantics must fail immediately in effect-aware code; silently calling
  /// it pure or opaque would merely choose a different class of miscompile.
  /// </para>
  /// </summary>
  public static IrEffectSummary ForInstruction(IrInstruction instruction) {
    ArgumentNullException.ThrowIfNull(instruction);
    return instruction switch {
      IrBinary { Op: IrBinaryOp.SDiv or IrBinaryOp.UDiv or IrBinaryOp.SRem or IrBinaryOp.URem or IrBinaryOp.FDiv }
        => _mayTrap,
      IrBinary or IrCmp or IrCast or IrGep or IrFarPtr or IrPhi or IrSelect
        => _pureDeterministic,
      IrAlloca => _pureUnique,
      IrLoad => _read,
      IrStore => _write,
      IrInlineAsm => IrEffectSummary.UnknownExternal,
      IrCall call => ForCall(call),
      IrRet or IrBr or IrCondBr or IrSwitch or IrIndirectBr or IrUnreachable => _pureDeterministic,
      _ => throw new NotSupportedException(
        $"No effect contract is defined for IR instruction '{instruction.GetType().Name}'."),
    };
  }
}
