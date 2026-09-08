namespace PowerBasic.Compiler.Asm;

/// <summary>How a recorded relocation site has to be treated by a linker.</summary>
public enum AsmRelocationKind {
  /// <summary>Site holds a resolved 16-bit offset within this image; rebase when the image moves.</summary>
  Absolute,
  /// <summary>Site holds a segment paragraph word patched by the DOS loader (MZ relocation).</summary>
  Segment,
  /// <summary>Site is the 16-bit displacement of a near CALL/JMP/Jcc to an external symbol.</summary>
  ExternalRelative,
  /// <summary>Site holds an addend; the external symbol's final 16-bit offset is added at link time.</summary>
  ExternalAbsolute,
}

/// <summary>One linker-visible site inside a relocatable image; <paramref name="Symbol"/> is set for external kinds.</summary>
public readonly record struct AsmRelocation(int Site, AsmRelocationKind Kind, string? Symbol);

/// <summary>The semantic kind of an already-resolved internal PC-relative control transfer.</summary>
public enum AsmRelativeFixupKind : byte {
  Call,
  Jump,
  Conditional,
}

/// <summary>
/// One internal PC-relative instruction retained for post-link rewriting. <paramref name="Condition"/>
/// is the x86 condition-code nibble for <see cref="AsmRelativeFixupKind.Conditional"/> and zero for
/// the other kinds; <paramref name="TargetOffset"/> already includes the assembler fixup's addend.
/// </summary>
public readonly record struct AsmRelativeFixup(
  int InstructionOffset,
  int EncodedLength,
  AsmRelativeFixupKind Kind,
  byte Condition,
  int TargetOffset);

/// <summary>A bound assembler label, including anonymous block labels that are absent from the symbol table.</summary>
public readonly record struct AsmBoundLabel(string? Name, int Offset);

/// <summary>
/// Result of <see cref="Assembler.ToRelocatable"/>: the image with all internal
/// fixups resolved (identical to <see cref="Assembler.ToArray"/> when nothing
/// is external), every site a linker must touch, and the positions of all
/// bound named labels (the image's symbol table).
/// </summary>
public sealed record RelocatableImage(
  byte[] Image,
  IReadOnlyList<AsmRelocation> Relocations,
  IReadOnlyDictionary<string, int> BoundLabels) {

  /// <summary>
  /// Internal CALL/JMP/Jcc records retained by <see cref="Assembler.ToPostLinkRelocatable"/>. Ordinary
  /// <see cref="Assembler.ToRelocatable"/> callers receive an empty list and keep the historical API.
  /// </summary>
  public IReadOnlyList<AsmRelativeFixup> RelativeFixups { get; init; } = [];

  /// <summary>
  /// Every non-constant bound label, including anonymous labels. O0360 uses this to retain block
  /// boundaries without requiring those internal labels to become exported linker symbols.
  /// </summary>
  public IReadOnlyList<AsmBoundLabel> AllBoundLabels { get; init; } = [];
}
