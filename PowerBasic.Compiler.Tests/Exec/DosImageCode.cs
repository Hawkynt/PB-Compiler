namespace PowerBasic.Compiler.Tests.Exec;

/// <summary>
/// A compiled DOS image's code, indexed the way the listing (<c>CodeGenerator.DescribeImage</c>)
/// numbers it. An MZ EXE's code starts after its header at origin 0; a flat COM's file IS the code,
/// assembled at origin 0100h, so its listing offsets are 0100h ahead of the file. Every fixture that
/// slices a procedure out of an image by its listing offset goes through here, because an optimized
/// self-contained program is now written as a COM.
/// </summary>
public static class DosImageCode {

  /// <summary>The image's code with index <c>i</c> at listing offset <c>i</c>.</summary>
  public static byte[] ByListingOffset(byte[] image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image is [(byte)'M', (byte)'Z', ..])
      return image[(BitConverter.ToUInt16(image, 8) * 16)..];
    return [.. new byte[0x100], .. image];
  }
}
