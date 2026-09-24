using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Emit;

/// <summary>
/// Writes a DOS .COM image. DOS loads the file at PSP:0100h and provides no relocation table, so
/// internal absolute offsets are biased by 0100h here while relative control flow stays unchanged.
/// Segment and unresolved external relocations are not representable in COM and are rejected.
/// </summary>
public static class ComWriter {
  public const int LoadOffset = 0x100;
  public const int MaximumImageBytes = 0x10000 - LoadOffset;

  public static byte[] Write(RelocatableImage image, int virtualEnd = -1) {
    ArgumentNullException.ThrowIfNull(image);
    virtualEnd = virtualEnd < 0 ? image.Image.Length : Math.Max(virtualEnd, image.Image.Length);

    if (image.Image.Length > MaximumImageBytes)
      throw new InvalidDataException(
        $"COM image is {image.Image.Length} bytes; at most {MaximumImageBytes} bytes fit after PSP:0100h");
    if (virtualEnd > MaximumImageBytes)
      throw new InvalidDataException(
        $"COM image plus virtual BSS reaches {virtualEnd:X}h; it must fit below offset FFFFh after the 0100h PSP bias");

    var result = (byte[])image.Image.Clone();
    var rebased = new HashSet<int>();
    foreach (var relocation in image.Relocations) {
      switch (relocation.Kind) {
        case AsmRelocationKind.Absolute:
          if (!rebased.Add(relocation.Site))
            continue;
          if (relocation.Site < 0 || relocation.Site + 1 >= result.Length)
            throw new InvalidDataException($"COM absolute relocation site {relocation.Site:X}h is outside the image");
          var value = result[relocation.Site] | result[relocation.Site + 1] << 8;
          var biased = value + LoadOffset;
          if (biased > 0xFFFF)
            throw new InvalidDataException(
              $"COM absolute relocation at {relocation.Site:X}h overflows after +0100h (value {value:X4}h)");
          result[relocation.Site] = (byte)biased;
          result[relocation.Site + 1] = (byte)(biased >> 8);
          break;

        case AsmRelocationKind.Segment:
          throw new InvalidDataException(
            $"COM cannot encode load-time segment relocation at {relocation.Site:X}h; use EXE for far/segment-relocated code");

        case AsmRelocationKind.ExternalRelative:
        case AsmRelocationKind.ExternalAbsolute:
          throw new InvalidDataException(
            $"COM cannot contain unresolved external symbol '{relocation.Symbol ?? "?"}' at {relocation.Site:X}h");

        default:
          throw new ArgumentOutOfRangeException(nameof(relocation.Kind), relocation.Kind, null);
      }
    }

    return result;
  }
}
