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

/// <summary>
/// Result of <see cref="Assembler.ToRelocatable"/>: the image with all internal
/// fixups resolved (identical to <see cref="Assembler.ToArray"/> when nothing
/// is external), every site a linker must touch, and the positions of all
/// bound named labels (the image's symbol table).
/// </summary>
public sealed record RelocatableImage(
  byte[] Image,
  IReadOnlyList<AsmRelocation> Relocations,
  IReadOnlyDictionary<string, int> BoundLabels);
