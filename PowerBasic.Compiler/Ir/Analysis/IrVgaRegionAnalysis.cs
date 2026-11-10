namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// Device-observable actions of native IR operations. These are not ordinary
/// alias-analysis facts: reading a VGA pixel may update its four latches, and
/// writing indexed ports may alter how an unchanged A000h address is decoded.
/// The only trusted device-marked IR loads/stores come from late runtime
/// lowering. Unmarked far pointers are NOT inferred to be VGA memory.
/// </summary>
[Flags]
public enum IrVgaActivity : byte {
  None = 0,
  ReadsPixels = 1 << 0,
  WritesPixels = 1 << 1,
  ChangesLatches = 1 << 2,
  ChangesRegisters = 1 << 3,
  ChangesModeOrPage = 1 << 4,
  Unknown = 1 << 5,
}

/// <summary>
/// Classifies observable video behavior without pretending that ordinary
/// memory mod/ref, a BIOS mode number, or an A000h pointer proves VGA state.
/// These flags are conservative legality constraints, NOT a license to merge
/// or widen MMIO accesses in a later pass.
/// </summary>
public static class IrVgaEffects {
  private const IrVgaActivity _unknown = IrVgaActivity.ReadsPixels
    | IrVgaActivity.WritesPixels | IrVgaActivity.ChangesLatches
    | IrVgaActivity.ChangesRegisters | IrVgaActivity.ChangesModeOrPage
    | IrVgaActivity.Unknown;

  /// <summary>Unrecognized instructions may invalidate all cached VGA facts.</summary>
  public static IrVgaActivity ForInstruction(IrInstruction instruction) {
    ArgumentNullException.ThrowIfNull(instruction);

    return instruction switch {
      IrLoad { IsDeviceRead: true }
        => IrVgaActivity.ReadsPixels | IrVgaActivity.ChangesLatches,
      IrStore { IsPackedMode13Write: true }
        => IrVgaActivity.WritesPixels | IrVgaActivity.ChangesLatches,

      // Only the compiler's own stack slots are known not to alias video
      // memory or its state. No global/segment-address heuristic is allowed.
      IrLoad load when IsPrivateLocalPointer(load.Pointer) => IrVgaActivity.None,
      IrStore store when IsPrivateLocalPointer(store.Pointer) => IrVgaActivity.None,
      IrLoad or IrStore => _unknown,

      IrCall { Callee: IrFunction { IsDeclaration: true } target } call
        => ForRuntimeCall(target.Name, call),
      IrInlineAsm asm => IsProvenRegisterOnlyAsm(asm) ? IrVgaActivity.None : _unknown,
      IrCall => _unknown,

      _ => IrEffects.ForInstruction(instruction).IsEffectFree
        ? IrVgaActivity.None
        : _unknown,
    };
  }

  /// <summary>
  /// Proof for a deliberately small subset of 8086 inline assembly: one
  /// register-only ALU/data instruction or NOP. These cannot access memory,
  /// program VGA ports, invoke BIOS, or change the screen mode. The test
  /// is an allowlist over complete operands, never a substring search:
  /// partial parses, symbols, segments, directives and multiple instructions
  /// remain opaque. Register/flag effects are still honored separately by
  /// the assembler and allocator; this proves *only* absence of VGA effects.
  /// </summary>
  public static bool IsProvenRegisterOnlyAsm(IrInlineAsm instruction) {
    ArgumentNullException.ThrowIfNull(instruction);
    if (instruction.Names.Count != 0)
      return false;
    var pieces = instruction.Text.Trim().Split([' ', '\t'], 2,
      StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (pieces.Length == 1)
      return pieces[0].Equals("NOP", StringComparison.OrdinalIgnoreCase);
    if (pieces.Length != 2)
      return false;
    var arguments = pieces[1].Split(',', StringSplitOptions.TrimEntries);
    if (!arguments.All(IsGeneralPurposeRegister))
      return false;
    var operation = pieces[0].ToUpperInvariant();
    return arguments.Length switch {
      1 => operation is "INC" or "DEC" or "NEG" or "NOT",
      2 => operation is "MOV" or "XCHG" or "XOR" or "AND" or "OR"
        or "ADD" or "SUB" or "CMP" or "TEST",
      _ => false,
    };
  }

  private static bool IsGeneralPurposeRegister(string name)
    => name.ToUpperInvariant() is
      "AX" or "BX" or "CX" or "DX" or "SI" or "DI"
      or "AL" or "AH" or "BL" or "BH" or "CL" or "CH" or "DL" or "DH";

  private static IrVgaActivity ForRuntimeCall(string name, IrCall call) => name switch {
    // Reinitializing the display resets VGA mode, sequencer, GC and latches.
    "rt_screen_mode" => IrVgaActivity.ChangesModeOrPage
      | IrVgaActivity.ChangesRegisters | IrVgaActivity.ChangesLatches
      | IrVgaActivity.WritesPixels,
    // BIOS pixel calls may save/restore registers, but their intermediate
    // port operations and latch effects remain opaque to the native IR.
    "rt_pset" => IrVgaActivity.WritesPixels
      | IrVgaActivity.ChangesRegisters | IrVgaActivity.ChangesLatches,
    "rt_point" => IrVgaActivity.ReadsPixels
      | IrVgaActivity.ChangesRegisters | IrVgaActivity.ChangesLatches,
    "rt_pset_mode13_offset" => IrVgaActivity.WritesPixels
      | IrVgaActivity.ChangesLatches,
    // Raw INP/OUT, PEEK/POKE, BIOS, device I/O, arbitrary calls and
    // incorrectly proven aliases must all fence a potential VGA region.
    _ => IrEffects.ForInstruction(call).IsEffectFree
      ? IrVgaActivity.None
      : _unknown,
  };

  private static bool IsPrivateLocalPointer(IrValue pointer)
    => pointer switch {
      IrAlloca => true,
      IrGep gep => IsPrivateLocalPointer(gep.BasePtr),
      _ => false,
    };
}

/// <summary>
/// A per-basic-block device ordering token. Configuration, latch and
/// observation epochs are independent: caching the Map Mask is different
/// from reusing a VGA latch, and neither implies it is legal to reorder
/// writes. No token crosses a CFG edge or an unknown device effect.
/// </summary>
public readonly record struct IrVgaRegionToken(
  IrBasicBlock Block, int InstructionOrdinal,
  int ConfigurationEpoch, int LatchEpoch, int ObservationEpoch);

/// <summary>
/// Native-only device-region candidate analysis. Tracking tokens does not
/// itself reorder instructions. A future backend can consider reusing a
/// controller setup only across a proven unchanged configuration, and may
/// never use that proof as permission to reorder observed pixel reads/writes.
/// </summary>
public sealed class IrVgaRegionAnalysis {
  private readonly Dictionary<IrInstruction, IrVgaRegionToken> _before = new(ReferenceEqualityComparer.Instance);

  public IrVgaRegionAnalysis(IrFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    foreach (var block in function.Blocks) {
      var configurationEpoch = 0;
      var latchEpoch = 0;
      var observationEpoch = 0;
      var ordinal = 0;
      foreach (var instruction in block.Instructions) {
        this._before[instruction] = new(block, ordinal++,
          configurationEpoch, latchEpoch, observationEpoch);
        var activity = IrVgaEffects.ForInstruction(instruction);
        if ((activity & (IrVgaActivity.ChangesRegisters | IrVgaActivity.ChangesModeOrPage
                         | IrVgaActivity.Unknown)) != 0)
          ++configurationEpoch;
        if ((activity & (IrVgaActivity.ChangesLatches | IrVgaActivity.Unknown)) != 0)
          ++latchEpoch;
        if (activity != IrVgaActivity.None)
          ++observationEpoch;
      }
    }
  }

  /// <summary>Device-state facts immediately before an instruction.</summary>
  public IrVgaRegionToken? Before(IrInstruction instruction) {
    ArgumentNullException.ThrowIfNull(instruction);
    return this._before.TryGetValue(instruction, out var token) ? token : null;
  }

  /// <summary>
  /// Checks configuration eligibility only; it deliberately does not assert
  /// latch reuse, commutativity, write merging, or a certain graphics mode.
  /// </summary>
  public bool HasUnchangedConfiguration(IrInstruction first, IrInstruction second)
    => this.Before(first) is { } a && this.Before(second) is { } b
       && ReferenceEquals(a.Block, b.Block)
       && a.InstructionOrdinal <= b.InstructionOrdinal
       && a.ConfigurationEpoch == b.ConfigurationEpoch;

  /// <summary>
  /// Latch reuse additionally requires unchanged controller configuration.
  /// A framebuffer read or partial write can update the VGA latches even
  /// when no sequencer or graphics-controller register is modified.
  /// This is only a negative safety gate, not a proof of valid initial latches.
  /// </summary>
  public bool HasUnchangedLatchState(IrInstruction first, IrInstruction second)
    => this.Before(first) is { } a && this.Before(second) is { } b
       && ReferenceEquals(a.Block, b.Block)
       && a.InstructionOrdinal <= b.InstructionOrdinal
       && a.ConfigurationEpoch == b.ConfigurationEpoch
       && a.LatchEpoch == b.LatchEpoch;

  /// <summary>
  /// A stricter token equality check for an as-yet unimplemented batcher.
  /// Both operations must be in one basic block, with no VGA observation,
  /// latch update, or controller mutation between them.
  /// </summary>
  public bool HasNoInterveningDeviceEffects(IrInstruction first, IrInstruction second)
    => this.Before(first) is { } a && this.Before(second) is { } b
       && ReferenceEquals(a.Block, b.Block)
       && a.InstructionOrdinal <= b.InstructionOrdinal
       && a.ConfigurationEpoch == b.ConfigurationEpoch
       && a.LatchEpoch == b.LatchEpoch
       && a.ObservationEpoch == b.ObservationEpoch;
}
