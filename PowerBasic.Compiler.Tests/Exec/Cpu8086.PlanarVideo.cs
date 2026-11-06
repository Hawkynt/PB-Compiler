namespace PowerBasic.Compiler.Tests.Exec;

/// <summary>
/// VGA 640x480x16 planar aperture. Models the documented sequencer Map Mask,
/// graphics-controller write modes, bit mask and read latches, rather than
/// treating A0000 as a single byte-per-pixel array.
/// </summary>
public sealed partial class Cpu8086 {
  private readonly byte[][] _vgaPlanes = Enumerable.Range(0, 4)
    .Select(_ => new byte[0x10000]).ToArray();
  private readonly byte[] _vgaLatches = new byte[4];
  private readonly byte[] _vgaGc = new byte[9];
  private readonly byte[] _vgaSeq = new byte[5];
  private byte _vgaGcIndex;
  private byte _vgaSeqIndex;

  /// <summary>Host CPU byte reads entering the planar VGA aperture in the current video mode.</summary>
  public long PlanarHostReads { get; private set; }

  /// <summary>Host CPU byte writes entering the planar VGA aperture in the current video mode.</summary>
  public long PlanarHostWrites { get; private set; }

  private void ResetPlanarVideo() {
    foreach (var plane in this._vgaPlanes)
      Array.Clear(plane);
    Array.Clear(this._vgaLatches);
    Array.Clear(this._vgaGc);
    Array.Clear(this._vgaSeq);
    this._vgaGc[6] = 0x05; // Graphics, A0000–AFFFF 64K aperture, no odd/even.
    this._vgaGc[8] = 0xFF;
    this._vgaSeq[2] = 0x0F;
    this._vgaSeq[4] = 0x06; // Extended memory, odd/even disabled, chain four off.
    this._vgaGcIndex = 0;
    this._vgaSeqIndex = 0;
    this.PlanarHostReads = 0;
    this.PlanarHostWrites = 0;
  }

  private bool IsPlanarAddress(int address)
    => this._videoMode == 0x12
       && (address & (_MEMORY - 1)) is >= 0xA0000 and < 0xB0000;

  private byte PlanarRead(int address) {
    ++this.PlanarHostReads;
    var offset = (address & (_MEMORY - 1)) - 0xA0000;
    for (var plane = 0; plane < 4; ++plane)
      this._vgaLatches[plane] = this._vgaPlanes[plane][offset];
    if ((this._vgaGc[5] & 8) == 0)
      return this._vgaLatches[this._vgaGc[4] & 3];
    byte result = 0;
    for (var bit = 0; bit < 8; ++bit) {
      var matches = true;
      for (var plane = 0; plane < 4; ++plane)
        if (((this._vgaGc[7] >> plane) & 1) != 0
            && ((this._vgaLatches[plane] >> bit) & 1) != ((this._vgaGc[2] >> plane) & 1))
          matches = false;
      if (matches)
        result |= (byte)(1 << bit);
    }
    return result;
  }

  private void PlanarWrite(int address, byte value) {
    ++this.PlanarHostWrites;
    var offset = (address & (_MEMORY - 1)) - 0xA0000;
    var mode = this._vgaGc[5] & 3;
    var rotate = this._vgaGc[3] & 7;
    var rotated = (byte)((value >> rotate) | (value << ((8 - rotate) & 7)));
    var mask = this._vgaGc[8];
    if (mode == 3)
      mask &= rotated;
    for (var plane = 0; plane < 4; ++plane) {
      if ((this._vgaSeq[2] & (1 << plane)) == 0)
        continue;
      byte source = mode switch {
        0 => ((this._vgaGc[1] & (1 << plane)) != 0)
          ? (byte)(((this._vgaGc[0] & (1 << plane)) != 0) ? 255 : 0)
          : rotated,
        1 => this._vgaLatches[plane],
        2 => (byte)(((value & (1 << plane)) != 0) ? 255 : 0),
        3 => (byte)(((this._vgaGc[0] & (1 << plane)) != 0) ? 255 : 0),
        _ => throw new InvalidOperationException(),
      };
      if (mode != 1) {
        source = ((this._vgaGc[3] >> 3) & 3) switch {
          1 => (byte)(source & this._vgaLatches[plane]),
          2 => (byte)(source | this._vgaLatches[plane]),
          3 => (byte)(source ^ this._vgaLatches[plane]),
          _ => source,
        };
      }
      this._vgaPlanes[plane][offset] = mode == 1
        ? source
        : (byte)((source & mask) | (this._vgaLatches[plane] & ~mask));
    }
  }

  private byte PlanarPixel(ushort x, ushort y) {
    var offset = y * 80 + (x >> 3);
    var bit = 0x80 >> (x & 7);
    var color = 0;
    for (var plane = 0; plane < 4; ++plane)
      if ((this._vgaPlanes[plane][offset] & bit) != 0)
        color |= 1 << plane;
    return (byte)color;
  }

  private void SetPlanarPixel(ushort x, ushort y, byte color) {
    var offset = y * 80 + (x >> 3);
    var bit = 0x80 >> (x & 7);
    var inverted = (byte)~bit;
    for (var plane = 0; plane < 4; ++plane)
      this._vgaPlanes[plane][offset] = (byte)((this._vgaPlanes[plane][offset] & inverted)
        | (((color >> plane) & 1) != 0 ? bit : 0));
  }

  private bool HandleVgaPortWrite(ushort port, byte value) {
    switch (port) {
      case 0x3CE: this._vgaGcIndex = value; return true;
      case 0x3CF:
        if (this._vgaGcIndex < this._vgaGc.Length)
          this._vgaGc[this._vgaGcIndex] = value;
        return true;
      case 0x3C4: this._vgaSeqIndex = value; return true;
      case 0x3C5:
        if (this._vgaSeqIndex < this._vgaSeq.Length)
          this._vgaSeq[this._vgaSeqIndex] = value;
        return true;
      default: return false;
    }
  }

  private byte? ReadVgaPort(ushort port) => port switch {
    0x3CE => this._vgaGcIndex,
    0x3CF => this._vgaGcIndex < this._vgaGc.Length ? this._vgaGc[this._vgaGcIndex] : (byte)0,
    0x3C4 => this._vgaSeqIndex,
    0x3C5 => this._vgaSeqIndex < this._vgaSeq.Length ? this._vgaSeq[this._vgaSeqIndex] : (byte)0,
    _ => null,
  };
}
