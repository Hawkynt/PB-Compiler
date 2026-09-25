using System.Text;

namespace PowerBasic.Compiler.Tests.Exec;

/// <summary>
/// An NMOS 6502 interpreter for running the 6502 back end's programs in tests, with just enough of
/// a Commodore 64 around it: 64 KB of RAM, the KERNAL's <c>CHROUT</c> at <c>$FFD2</c> answered by
/// capturing the character, and a program that ends by returning from the <c>SYS</c> that started it.
///
/// <para>
/// Its decoder is written out by hand from the data sheet rather than derived from the compiler's
/// opcode table, so an encoding mistake in the compiler shows up as a disagreement instead of being
/// reproduced on both sides. Every documented instruction is implemented, decimal mode included;
/// an undocumented opcode stops the run, because the compiler never emits one.
/// </para>
/// </summary>
public sealed class Cpu6502 {

  private const ushort Chrout = 0xFFD2;
  private const ushort Sentinel = 0xFFF0;

  private readonly byte[] _memory = new byte[0x10000];
  private readonly StringBuilder _output = new();
  private byte _a, _x, _y, _s = 0xFF;
  private ushort _pc;
  private bool _carry, _zero, _interruptDisable, _decimal, _overflow, _negative;

  /// <summary>
  /// What <see cref="RunC64Program"/> saw. <see cref="StackPointer"/> is <c>$FF</c> after a clean
  /// return: the program left the hardware stack as it found it.
  /// </summary>
  public sealed record Result(string Output, bool Returned, long Steps, byte StackPointer, byte[] Memory);

  /// <summary>
  /// Loads a <c>.PRG</c> and calls its machine code at <paramref name="start"/> as <c>SYS</c> would,
  /// until it returns or <paramref name="maxSteps"/> instructions have run.
  /// </summary>
  public static Result RunC64Program(byte[] prg, int start = 0x080D, long maxSteps = 50_000_000) {
    var cpu = new Cpu6502();
    var load = prg[0] | (prg[1] << 8);
    prg.AsSpan(2).CopyTo(cpu._memory.AsSpan(load));
    cpu.Push((byte)((Sentinel - 1) >> 8));
    cpu.Push((byte)((Sentinel - 1) & 0xFF));
    cpu._pc = (ushort)start;
    long steps = 0;
    while (cpu._pc != Sentinel && steps < maxSteps) {
      if (cpu._pc == Chrout) {
        cpu.Capture(cpu._a);
        cpu.Return();
      } else {
        cpu.Step();
      }
      ++steps;
    }
    return new(cpu._output.ToString(), cpu._pc == Sentinel, steps, cpu._s, cpu._memory);
  }

  /// <summary>PETSCII, in the upper- and lower-case set the programs select, back to ASCII.</summary>
  private void Capture(byte character) {
    switch (character) {
      case 0x0D: this._output.Append('\n'); break;
      case 0x0E: break;
      case >= 0x41 and <= 0x5A: this._output.Append((char)(character + 0x20)); break;
      case >= 0xC1 and <= 0xDA: this._output.Append((char)(character - 0x80)); break;
      default: this._output.Append((char)character); break;
    }
  }

  private byte Read(int address) => this._memory[address & 0xFFFF];
  private void Write(int address, byte value) => this._memory[address & 0xFFFF] = value;
  private ushort Word(int address) => (ushort)(this.Read(address) | (this.Read(address + 1) << 8));

  private byte Fetch() => this.Read(this._pc++);
  private ushort FetchWord() {
    var value = this.Word(this._pc);
    this._pc += 2;
    return value;
  }

  private void Push(byte value) => this.Write(0x100 | this._s--, value);
  private byte Pull() => this.Read(0x100 | ++this._s);

  private void Return() {
    var low = this.Pull();
    var high = this.Pull();
    this._pc = (ushort)(((high << 8) | low) + 1);
  }

  private byte Flags(bool brk) => (byte)((this._carry ? 0x01 : 0) | (this._zero ? 0x02 : 0)
    | (this._interruptDisable ? 0x04 : 0) | (this._decimal ? 0x08 : 0) | (brk ? 0x10 : 0) | 0x20
    | (this._overflow ? 0x40 : 0) | (this._negative ? 0x80 : 0));

  private void SetFlags(byte p) {
    this._carry = (p & 0x01) != 0;
    this._zero = (p & 0x02) != 0;
    this._interruptDisable = (p & 0x04) != 0;
    this._decimal = (p & 0x08) != 0;
    this._overflow = (p & 0x40) != 0;
    this._negative = (p & 0x80) != 0;
  }

  private byte Nz(byte value) {
    this._zero = value == 0;
    this._negative = (value & 0x80) != 0;
    return value;
  }

  // --- addressing: each answers the effective address ---------------------------------------

  private int ZeroPage() => this.Fetch();
  private int ZeroPageX() => (this.Fetch() + this._x) & 0xFF;
  private int ZeroPageY() => (this.Fetch() + this._y) & 0xFF;
  private int Absolute() => this.FetchWord();
  private int AbsoluteX() => (this.FetchWord() + this._x) & 0xFFFF;
  private int AbsoluteY() => (this.FetchWord() + this._y) & 0xFFFF;
  private int IndexedIndirect() {
    var pointer = (this.Fetch() + this._x) & 0xFF;
    return this.Read(pointer) | (this.Read((pointer + 1) & 0xFF) << 8);
  }
  private int IndirectIndexed() {
    var pointer = this.Fetch();
    var address = this.Read(pointer) | (this.Read((pointer + 1) & 0xFF) << 8);
    return (address + this._y) & 0xFFFF;
  }

  // --- operations ---------------------------------------------------------------------------

  private void Adc(byte value) {
    if (this._decimal) {
      var low = (this._a & 0x0F) + (value & 0x0F) + (this._carry ? 1 : 0);
      if (low > 9) low += 6;
      var high = (this._a >> 4) + (value >> 4) + (low > 0x0F ? 1 : 0);
      var binary = this._a + value + (this._carry ? 1 : 0);
      this._zero = (binary & 0xFF) == 0;
      this._negative = (high & 0x08) != 0;
      this._overflow = ((~(this._a ^ value) & (this._a ^ (high << 4))) & 0x80) != 0;
      if (high > 9) high += 6;
      this._carry = high > 0x0F;
      this._a = (byte)(((high << 4) | (low & 0x0F)) & 0xFF);
      return;
    }
    var sum = this._a + value + (this._carry ? 1 : 0);
    this._overflow = ((~(this._a ^ value) & (this._a ^ sum)) & 0x80) != 0;
    this._carry = sum > 0xFF;
    this._a = this.Nz((byte)sum);
  }

  private void Sbc(byte value) {
    var borrow = this._carry ? 0 : 1;
    var difference = this._a - value - borrow;
    this._overflow = (((this._a ^ value) & (this._a ^ difference)) & 0x80) != 0;
    if (this._decimal) {
      var low = (this._a & 0x0F) - (value & 0x0F) - borrow;
      var high = (this._a >> 4) - (value >> 4) - (low < 0 ? 1 : 0);
      if (low < 0) low -= 6;
      if (high < 0) high -= 6;
      this._carry = difference >= 0;
      this.Nz((byte)difference);
      this._a = (byte)(((high << 4) | (low & 0x0F)) & 0xFF);
      return;
    }
    this._carry = difference >= 0;
    this._a = this.Nz((byte)difference);
  }

  private void Compare(byte register, byte value) {
    var difference = register - value;
    this._carry = difference >= 0;
    this.Nz((byte)difference);
  }

  private void Bit(byte value) {
    this._zero = (this._a & value) == 0;
    this._overflow = (value & 0x40) != 0;
    this._negative = (value & 0x80) != 0;
  }

  private byte Asl(byte value) {
    this._carry = (value & 0x80) != 0;
    return this.Nz((byte)(value << 1));
  }

  private byte Lsr(byte value) {
    this._carry = (value & 0x01) != 0;
    return this.Nz((byte)(value >> 1));
  }

  private byte Rol(byte value) {
    var carryIn = this._carry ? 1 : 0;
    this._carry = (value & 0x80) != 0;
    return this.Nz((byte)((value << 1) | carryIn));
  }

  private byte Ror(byte value) {
    var carryIn = this._carry ? 0x80 : 0;
    this._carry = (value & 0x01) != 0;
    return this.Nz((byte)((value >> 1) | carryIn));
  }

  private void Modify(int address, Func<byte, byte> operation) => this.Write(address, operation(this.Read(address)));

  private void Branch(bool taken) {
    var displacement = (sbyte)this.Fetch();
    if (taken)
      this._pc = (ushort)(this._pc + displacement);
  }

  private void Step() {
    var at = this._pc;
    var opcode = this.Fetch();
    switch (opcode) {
      // loads and stores
      case 0xA9: this._a = this.Nz(this.Fetch()); break;
      case 0xA5: this._a = this.Nz(this.Read(this.ZeroPage())); break;
      case 0xB5: this._a = this.Nz(this.Read(this.ZeroPageX())); break;
      case 0xAD: this._a = this.Nz(this.Read(this.Absolute())); break;
      case 0xBD: this._a = this.Nz(this.Read(this.AbsoluteX())); break;
      case 0xB9: this._a = this.Nz(this.Read(this.AbsoluteY())); break;
      case 0xA1: this._a = this.Nz(this.Read(this.IndexedIndirect())); break;
      case 0xB1: this._a = this.Nz(this.Read(this.IndirectIndexed())); break;
      case 0xA2: this._x = this.Nz(this.Fetch()); break;
      case 0xA6: this._x = this.Nz(this.Read(this.ZeroPage())); break;
      case 0xB6: this._x = this.Nz(this.Read(this.ZeroPageY())); break;
      case 0xAE: this._x = this.Nz(this.Read(this.Absolute())); break;
      case 0xBE: this._x = this.Nz(this.Read(this.AbsoluteY())); break;
      case 0xA0: this._y = this.Nz(this.Fetch()); break;
      case 0xA4: this._y = this.Nz(this.Read(this.ZeroPage())); break;
      case 0xB4: this._y = this.Nz(this.Read(this.ZeroPageX())); break;
      case 0xAC: this._y = this.Nz(this.Read(this.Absolute())); break;
      case 0xBC: this._y = this.Nz(this.Read(this.AbsoluteX())); break;
      case 0x85: this.Write(this.ZeroPage(), this._a); break;
      case 0x95: this.Write(this.ZeroPageX(), this._a); break;
      case 0x8D: this.Write(this.Absolute(), this._a); break;
      case 0x9D: this.Write(this.AbsoluteX(), this._a); break;
      case 0x99: this.Write(this.AbsoluteY(), this._a); break;
      case 0x81: this.Write(this.IndexedIndirect(), this._a); break;
      case 0x91: this.Write(this.IndirectIndexed(), this._a); break;
      case 0x86: this.Write(this.ZeroPage(), this._x); break;
      case 0x96: this.Write(this.ZeroPageY(), this._x); break;
      case 0x8E: this.Write(this.Absolute(), this._x); break;
      case 0x84: this.Write(this.ZeroPage(), this._y); break;
      case 0x94: this.Write(this.ZeroPageX(), this._y); break;
      case 0x8C: this.Write(this.Absolute(), this._y); break;

      // transfers and the stack
      case 0xAA: this._x = this.Nz(this._a); break;
      case 0xA8: this._y = this.Nz(this._a); break;
      case 0x8A: this._a = this.Nz(this._x); break;
      case 0x98: this._a = this.Nz(this._y); break;
      case 0xBA: this._x = this.Nz(this._s); break;
      case 0x9A: this._s = this._x; break;
      case 0x48: this.Push(this._a); break;
      case 0x68: this._a = this.Nz(this.Pull()); break;
      case 0x08: this.Push(this.Flags(brk: true)); break;
      case 0x28: this.SetFlags(this.Pull()); break;

      // arithmetic and logic
      case 0x69: this.Adc(this.Fetch()); break;
      case 0x65: this.Adc(this.Read(this.ZeroPage())); break;
      case 0x75: this.Adc(this.Read(this.ZeroPageX())); break;
      case 0x6D: this.Adc(this.Read(this.Absolute())); break;
      case 0x7D: this.Adc(this.Read(this.AbsoluteX())); break;
      case 0x79: this.Adc(this.Read(this.AbsoluteY())); break;
      case 0x61: this.Adc(this.Read(this.IndexedIndirect())); break;
      case 0x71: this.Adc(this.Read(this.IndirectIndexed())); break;
      case 0xE9: this.Sbc(this.Fetch()); break;
      case 0xE5: this.Sbc(this.Read(this.ZeroPage())); break;
      case 0xF5: this.Sbc(this.Read(this.ZeroPageX())); break;
      case 0xED: this.Sbc(this.Read(this.Absolute())); break;
      case 0xFD: this.Sbc(this.Read(this.AbsoluteX())); break;
      case 0xF9: this.Sbc(this.Read(this.AbsoluteY())); break;
      case 0xE1: this.Sbc(this.Read(this.IndexedIndirect())); break;
      case 0xF1: this.Sbc(this.Read(this.IndirectIndexed())); break;
      case 0x29: this._a = this.Nz((byte)(this._a & this.Fetch())); break;
      case 0x25: this._a = this.Nz((byte)(this._a & this.Read(this.ZeroPage()))); break;
      case 0x35: this._a = this.Nz((byte)(this._a & this.Read(this.ZeroPageX()))); break;
      case 0x2D: this._a = this.Nz((byte)(this._a & this.Read(this.Absolute()))); break;
      case 0x3D: this._a = this.Nz((byte)(this._a & this.Read(this.AbsoluteX()))); break;
      case 0x39: this._a = this.Nz((byte)(this._a & this.Read(this.AbsoluteY()))); break;
      case 0x21: this._a = this.Nz((byte)(this._a & this.Read(this.IndexedIndirect()))); break;
      case 0x31: this._a = this.Nz((byte)(this._a & this.Read(this.IndirectIndexed()))); break;
      case 0x09: this._a = this.Nz((byte)(this._a | this.Fetch())); break;
      case 0x05: this._a = this.Nz((byte)(this._a | this.Read(this.ZeroPage()))); break;
      case 0x15: this._a = this.Nz((byte)(this._a | this.Read(this.ZeroPageX()))); break;
      case 0x0D: this._a = this.Nz((byte)(this._a | this.Read(this.Absolute()))); break;
      case 0x1D: this._a = this.Nz((byte)(this._a | this.Read(this.AbsoluteX()))); break;
      case 0x19: this._a = this.Nz((byte)(this._a | this.Read(this.AbsoluteY()))); break;
      case 0x01: this._a = this.Nz((byte)(this._a | this.Read(this.IndexedIndirect()))); break;
      case 0x11: this._a = this.Nz((byte)(this._a | this.Read(this.IndirectIndexed()))); break;
      case 0x49: this._a = this.Nz((byte)(this._a ^ this.Fetch())); break;
      case 0x45: this._a = this.Nz((byte)(this._a ^ this.Read(this.ZeroPage()))); break;
      case 0x55: this._a = this.Nz((byte)(this._a ^ this.Read(this.ZeroPageX()))); break;
      case 0x4D: this._a = this.Nz((byte)(this._a ^ this.Read(this.Absolute()))); break;
      case 0x5D: this._a = this.Nz((byte)(this._a ^ this.Read(this.AbsoluteX()))); break;
      case 0x59: this._a = this.Nz((byte)(this._a ^ this.Read(this.AbsoluteY()))); break;
      case 0x41: this._a = this.Nz((byte)(this._a ^ this.Read(this.IndexedIndirect()))); break;
      case 0x51: this._a = this.Nz((byte)(this._a ^ this.Read(this.IndirectIndexed()))); break;
      case 0xC9: this.Compare(this._a, this.Fetch()); break;
      case 0xC5: this.Compare(this._a, this.Read(this.ZeroPage())); break;
      case 0xD5: this.Compare(this._a, this.Read(this.ZeroPageX())); break;
      case 0xCD: this.Compare(this._a, this.Read(this.Absolute())); break;
      case 0xDD: this.Compare(this._a, this.Read(this.AbsoluteX())); break;
      case 0xD9: this.Compare(this._a, this.Read(this.AbsoluteY())); break;
      case 0xC1: this.Compare(this._a, this.Read(this.IndexedIndirect())); break;
      case 0xD1: this.Compare(this._a, this.Read(this.IndirectIndexed())); break;
      case 0xE0: this.Compare(this._x, this.Fetch()); break;
      case 0xE4: this.Compare(this._x, this.Read(this.ZeroPage())); break;
      case 0xEC: this.Compare(this._x, this.Read(this.Absolute())); break;
      case 0xC0: this.Compare(this._y, this.Fetch()); break;
      case 0xC4: this.Compare(this._y, this.Read(this.ZeroPage())); break;
      case 0xCC: this.Compare(this._y, this.Read(this.Absolute())); break;
      case 0x24: this.Bit(this.Read(this.ZeroPage())); break;
      case 0x2C: this.Bit(this.Read(this.Absolute())); break;

      // increments, decrements, shifts and rotates
      case 0xE6: this.Modify(this.ZeroPage(), value => this.Nz((byte)(value + 1))); break;
      case 0xF6: this.Modify(this.ZeroPageX(), value => this.Nz((byte)(value + 1))); break;
      case 0xEE: this.Modify(this.Absolute(), value => this.Nz((byte)(value + 1))); break;
      case 0xFE: this.Modify(this.AbsoluteX(), value => this.Nz((byte)(value + 1))); break;
      case 0xC6: this.Modify(this.ZeroPage(), value => this.Nz((byte)(value - 1))); break;
      case 0xD6: this.Modify(this.ZeroPageX(), value => this.Nz((byte)(value - 1))); break;
      case 0xCE: this.Modify(this.Absolute(), value => this.Nz((byte)(value - 1))); break;
      case 0xDE: this.Modify(this.AbsoluteX(), value => this.Nz((byte)(value - 1))); break;
      case 0xE8: this._x = this.Nz((byte)(this._x + 1)); break;
      case 0xC8: this._y = this.Nz((byte)(this._y + 1)); break;
      case 0xCA: this._x = this.Nz((byte)(this._x - 1)); break;
      case 0x88: this._y = this.Nz((byte)(this._y - 1)); break;
      case 0x0A: this._a = this.Asl(this._a); break;
      case 0x06: this.Modify(this.ZeroPage(), this.Asl); break;
      case 0x16: this.Modify(this.ZeroPageX(), this.Asl); break;
      case 0x0E: this.Modify(this.Absolute(), this.Asl); break;
      case 0x1E: this.Modify(this.AbsoluteX(), this.Asl); break;
      case 0x4A: this._a = this.Lsr(this._a); break;
      case 0x46: this.Modify(this.ZeroPage(), this.Lsr); break;
      case 0x56: this.Modify(this.ZeroPageX(), this.Lsr); break;
      case 0x4E: this.Modify(this.Absolute(), this.Lsr); break;
      case 0x5E: this.Modify(this.AbsoluteX(), this.Lsr); break;
      case 0x2A: this._a = this.Rol(this._a); break;
      case 0x26: this.Modify(this.ZeroPage(), this.Rol); break;
      case 0x36: this.Modify(this.ZeroPageX(), this.Rol); break;
      case 0x2E: this.Modify(this.Absolute(), this.Rol); break;
      case 0x3E: this.Modify(this.AbsoluteX(), this.Rol); break;
      case 0x6A: this._a = this.Ror(this._a); break;
      case 0x66: this.Modify(this.ZeroPage(), this.Ror); break;
      case 0x76: this.Modify(this.ZeroPageX(), this.Ror); break;
      case 0x6E: this.Modify(this.Absolute(), this.Ror); break;
      case 0x7E: this.Modify(this.AbsoluteX(), this.Ror); break;

      // control flow
      case 0x4C: this._pc = this.FetchWord(); break;
      case 0x6C: {
        // the NMOS indirect JMP does not carry into the pointer's high byte
        var pointer = this.FetchWord();
        this._pc = (ushort)(this.Read(pointer) | (this.Read((pointer & 0xFF00) | ((pointer + 1) & 0xFF)) << 8));
        break;
      }
      case 0x20: {
        var target = this.FetchWord();
        var back = (ushort)(this._pc - 1);
        this.Push((byte)(back >> 8));
        this.Push((byte)(back & 0xFF));
        this._pc = target;
        break;
      }
      case 0x60: this.Return(); break;
      case 0x40:
        this.SetFlags(this.Pull());
        this._pc = (ushort)(this.Pull() | (this.Pull() << 8));
        break;
      case 0x10: this.Branch(!this._negative); break;
      case 0x30: this.Branch(this._negative); break;
      case 0x50: this.Branch(!this._overflow); break;
      case 0x70: this.Branch(this._overflow); break;
      case 0x90: this.Branch(!this._carry); break;
      case 0xB0: this.Branch(this._carry); break;
      case 0xD0: this.Branch(!this._zero); break;
      case 0xF0: this.Branch(this._zero); break;

      // flags
      case 0x18: this._carry = false; break;
      case 0x38: this._carry = true; break;
      case 0x58: this._interruptDisable = false; break;
      case 0x78: this._interruptDisable = true; break;
      case 0xB8: this._overflow = false; break;
      case 0xD8: this._decimal = false; break;
      case 0xF8: this._decimal = true; break;
      case 0xEA: break;
      case 0x00:
        throw new InvalidOperationException($"BRK at ${at:X4}");
      default:
        throw new InvalidOperationException($"undocumented opcode ${opcode:X2} at ${at:X4}");
    }
  }
}
