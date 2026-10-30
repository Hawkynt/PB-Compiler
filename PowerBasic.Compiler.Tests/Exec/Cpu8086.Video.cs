namespace PowerBasic.Compiler.Tests.Exec;

public sealed partial class Cpu8086 {
  private Dictionary<(ushort X, ushort Y), byte>? _biosPixels;

  private void BiosPixel() {
    var x = this._r[_CX];
    var y = this._r[_DX];
    var write = this.Reg8(4) == 0x0C;
    if (this._videoMode is not (0x04 or 0x12 or 0x13))
      throw new Cpu8086Exception($"BIOS pixel service in unmodelled mode {this._videoMode:X2}h");
    var packed = this._videoMode == 0x13;
    var isCga = this._videoMode == 0x04;
    if (x >= (packed || isCga ? 320 : 640) || y >= (packed || isCga ? 200 : 480))
      throw new Cpu8086Exception("out-of-range BIOS pixel access is not modelled");
    if (packed) {
      var address = 0xA0000 + y * 320 + x;
      if (write)
        this._memory[address] = this.Reg8(_AX);
      else
        this.SetReg8(_AX, this._memory[address]);
      return;
    }
    this._biosPixels ??= [];
    var previous = this._biosPixels.GetValueOrDefault((x, y));
    if (!write) {
      this.SetReg8(_AX, previous);
      return;
    }
    var color = this.Reg8(_AX);
    var mask = isCga ? 3 : 15;
    this._biosPixels[(x, y)] = (byte)((color & 0x80) != 0 ? previous ^ (color & mask) : color & mask);
  }
}
