using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Emit;

/// <summary>
/// Writes a DOS .COM file from an image assembled at the real PSP:0100h origin.
/// The first 0100h bytes are a synthetic origin prefix and are removed from the file. Because every
/// label was assigned its final DOS offset before fixup resolution, absolute addresses, jump tables
/// and virtual BSS references need no post-hoc rebasing. COM has no relocation table, so segment and
/// unresolved external relocations remain unrepresentable and are rejected.
/// </summary>
public static class ComWriter {
  public const int LoadOffset = 0x100;
  public const int MaximumFileBytes = 0x10000 - LoadOffset;

  public static byte[] Write(RelocatableImage originatedImage, int virtualEnd = -1) {
    ArgumentNullException.ThrowIfNull(originatedImage);
    if (originatedImage.Image.Length < LoadOffset)
      throw new InvalidDataException("COM image was not assembled at PSP:0100h");

    virtualEnd = virtualEnd < 0 ? originatedImage.Image.Length
      : Math.Max(virtualEnd, originatedImage.Image.Length);
    if (originatedImage.Image.Length > 0x10000)
      throw new InvalidDataException(
        $"COM image is {originatedImage.Image.Length - LoadOffset} bytes; at most {MaximumFileBytes} bytes fit after PSP:0100h");
    if (virtualEnd > 0x10000)
      throw new InvalidDataException(
        $"COM image plus virtual BSS reaches {virtualEnd:X}h; it must fit at or below offset FFFFh");

    foreach (var relocation in originatedImage.Relocations)
      switch (relocation.Kind) {
        case AsmRelocationKind.Absolute:
          // Already resolved against labels whose positions include the 0100h origin.
          break;
        case AsmRelocationKind.Segment:
          throw new InvalidDataException(
            $"COM cannot encode load-time segment relocation at {relocation.Site - LoadOffset:X}h; use EXE for far/segment-relocated code");
        case AsmRelocationKind.ExternalRelative:
        case AsmRelocationKind.ExternalAbsolute:
          throw new InvalidDataException(
            $"COM cannot contain unresolved external symbol '{relocation.Symbol ?? "?"}' at {relocation.Site - LoadOffset:X}h");
        default:
          throw new ArgumentOutOfRangeException(nameof(relocation.Kind), relocation.Kind, null);
      }

    return originatedImage.Image[LoadOffset..];
  }
}
