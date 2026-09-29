using PowerBasic.Compiler.Backend.Mos6502;

namespace PowerBasic.Compiler.Emit.Commodore;

/// <summary>
/// A Commodore 64 <c>.PRG</c>: a two-byte load address, then the bytes that load there. The program
/// loads at the start of BASIC memory behind a one-line BASIC program, <c>10 SYS 2061</c>, so
/// <c>LOAD "PROG",8</c> and <c>RUN</c> start it the way every machine-language program on the
/// machine starts, and its final <c>RTS</c> returns to <c>READY.</c>.
/// </summary>
public static class C64Prg {

  /// <summary>Where BASIC programs load.</summary>
  public const int LoadAddress = 0x0801;

  /// <summary>The first byte after the BASIC line: where the machine code starts, and what SYS names.</summary>
  public const int CodeOrigin = 0x080D;

  /// <summary>The first address the program cannot use: the BASIC ROM is mapped in from here.</summary>
  public const int MemoryTop = 0xA000;

  /// <summary>
  /// <c>10 SYS 2061</c>: the link to the next line, the line number, the SYS token, the address in
  /// PETSCII digits, the line's terminator, and a null link that ends the program.
  /// </summary>
  private static ReadOnlySpan<byte> BasicStub => [
    0x0B, 0x08, 0x0A, 0x00, 0x9E, (byte)'2', (byte)'0', (byte)'6', (byte)'1', 0x00, 0x00, 0x00,
  ];

  /// <summary>The file for <paramref name="image"/>, which must have been assembled at <see cref="CodeOrigin"/>.</summary>
  public static byte[] Write(Mos6502Assembler.Image image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.Origin != CodeOrigin)
      throw new ArgumentException($"a C64 program starts at ${CodeOrigin:X4}, not ${image.Origin:X4}", nameof(image));
    var file = new byte[2 + BasicStub.Length + image.Bytes.Length];
    file[0] = LoadAddress & 0xFF;
    file[1] = LoadAddress >> 8;
    BasicStub.CopyTo(file.AsSpan(2));
    image.Bytes.CopyTo(file, 2 + BasicStub.Length);
    return file;
  }
}
