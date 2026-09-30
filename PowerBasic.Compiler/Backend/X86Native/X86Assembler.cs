namespace PowerBasic.Compiler.Backend.X86Native;

/// <summary>A position in a section, bound once.</summary>
public readonly record struct X86Label(int Id);

/// <summary>
/// A memory operand: a register plus a displacement, or a label plus an addend. A label is reached
/// RIP-relative on x64 and by its absolute address on i386.
/// </summary>
public readonly record struct X86Mem(X86Reg? Base, int Displacement, X86Label? Label) {
  public static X86Mem At(X86Reg @base, int displacement = 0) => new(@base, displacement, null);
  public static X86Mem At(X86Label label, int addend = 0) => new(null, addend, label);
  public X86Mem Plus(int bytes) => this with { Displacement = this.Displacement + bytes };
}

/// <summary>How a fixup's four or eight bytes are computed from its target.</summary>
public enum X86FixupKind {
  /// <summary><c>target - (site + 4)</c>: a branch or call displacement.</summary>
  Relative32,
  /// <summary><c>target - end of instruction</c>: x64's RIP-relative data reference.</summary>
  RipRelative32,
  /// <summary>The target's absolute address in four bytes: i386's data references.</summary>
  Absolute32,
  /// <summary>The target's absolute address in eight bytes.</summary>
  Absolute64,
}

/// <summary>A reference from <see cref="Section"/> at <see cref="Offset"/> to a label, resolved at layout.</summary>
public sealed record X86Fixup(X86Section Section, int Offset, X86FixupKind Kind, X86Label Target, int Addend) {
  /// <summary>For <see cref="X86FixupKind.RipRelative32"/>: how far past the field the instruction ends.</summary>
  public int TrailingBytes { get; set; }
}

/// <summary>The x87 memory formats.</summary>
public enum X87Format { Single = 4, Double = 8, Extended = 10 }

/// <summary>
/// An x86 assembler over typed operands for i386 and x64 code: every instruction is a method whose
/// parameters are the registers, widths and memory operands the chip accepts, so an operand the
/// chosen mode cannot encode - a 64-bit width on i386 - fails where it is written.
///
/// <para>
/// It keeps three sections, as an object file does: code, initialised data and uninitialised data.
/// A reference to a label is a <see cref="X86Fixup"/>; <see cref="Link"/> resolves them all against
/// section addresses for an executable, and an object writer turns the ones that cross sections into
/// relocations instead.
/// </para>
/// </summary>
public sealed class X86Assembler(X86Machine machine) {

  private readonly List<byte> _text = [];
  private readonly List<byte> _data = [];
  private int _bssSize;
  private readonly List<string> _names = [];
  private readonly Dictionary<X86Label, (X86Section Section, int Offset)> _bound = [];
  private readonly List<X86Fixup> _fixups = [];
  private readonly List<X86Fixup> _pending = [];

  public X86Machine Machine { get; } = machine;
  public IReadOnlyList<byte> Text => this._text;
  public IReadOnlyList<byte> Data => this._data;
  public int BssSize => this._bssSize;
  public IReadOnlyList<X86Fixup> Fixups => this._fixups;
  public IReadOnlyDictionary<X86Label, (X86Section Section, int Offset)> Labels => this._bound;
  public string NameOf(X86Label label) => this._names[label.Id];

  public X86Label NewLabel(string name) {
    this._names.Add(name);
    return new(this._names.Count - 1);
  }

  /// <summary>Places <paramref name="label"/> at the current end of the code.</summary>
  public void Bind(X86Label label) => this.BindIn(label, X86Section.Text, this._text.Count);

  private void BindIn(X86Label label, X86Section section, int offset) {
    if (!this._bound.TryAdd(label, (section, offset)))
      throw new InvalidOperationException($"label '{this.NameOf(label)}' is bound twice");
  }

  // --- data ------------------------------------------------------------------------------------

  /// <summary>Initialised data, aligned to <paramref name="alignment"/>, labelled.</summary>
  public void DataBytes(X86Label label, ReadOnlySpan<byte> bytes, int alignment = 1) {
    while (this._data.Count % alignment != 0)
      this._data.Add(0);
    this.BindIn(label, X86Section.Data, this._data.Count);
    this._data.AddRange(bytes.ToArray());
  }

  /// <summary>An address-sized word of data holding <paramref name="target"/>'s address.</summary>
  public void DataAddress(X86Label label, X86Label target) {
    var bytes = this.Machine.Word().Bytes();
    while (this._data.Count % bytes != 0)
      this._data.Add(0);
    this.BindIn(label, X86Section.Data, this._data.Count);
    this._fixups.Add(new(X86Section.Data, this._data.Count,
      bytes == 8 ? X86FixupKind.Absolute64 : X86FixupKind.Absolute32, target, 0));
    this._data.AddRange(new byte[bytes]);
  }

  /// <summary>Uninitialised storage: zero when the program starts.</summary>
  public void Reserve(X86Label label, int bytes, int alignment = 1) {
    this._bssSize = (this._bssSize + alignment - 1) / alignment * alignment;
    this.BindIn(label, X86Section.Bss, this._bssSize);
    this._bssSize += bytes;
  }

  // --- encoding helpers ------------------------------------------------------------------------

  private void Byte(int value) => this._text.Add((byte)value);

  private void Int32(int value) {
    for (var i = 0; i < 4; ++i)
      this._text.Add((byte)(value >> (8 * i)));
  }

  /// <summary>The operand-size prefixes: <c>66</c> for a word, <c>REX.W</c> for a quadword.</summary>
  private void Prefix(X86Width width) {
    if (width == X86Width.Word)
      this.Byte(0x66);
    else if (width == X86Width.Qword) {
      if (this.Machine != X86Machine.Amd64)
        throw new ArgumentException("i386 has no 64-bit operands", nameof(width));
      this.Byte(0x48);
    }
  }

  private void ModRmRegister(int field, X86Reg rm) => this.Byte(0xC0 | (field << 3) | (int)rm);

  private void ModRmMemory(int field, X86Mem memory) {
    if (memory.Label is { } label) {
      // mod 00, r/m 101: disp32 - RIP-relative on x64, absolute on i386
      this.Byte((field << 3) | 0x05);
      var fixup = new X86Fixup(X86Section.Text, this._text.Count,
        this.Machine == X86Machine.Amd64 ? X86FixupKind.RipRelative32 : X86FixupKind.Absolute32, label, memory.Displacement);
      this._fixups.Add(fixup);
      this._pending.Add(fixup);
      this.Int32(0);
      return;
    }
    var @base = memory.Base ?? throw new ArgumentException("a memory operand needs a base or a label", nameof(memory));
    var displacement = memory.Displacement;
    var mod = displacement == 0 && @base != X86Reg.Bp ? 0 : displacement is >= -128 and <= 127 ? 1 : 2;
    this.Byte((mod << 6) | (field << 3) | (int)@base);
    if (@base == X86Reg.Sp)
      this.Byte(0x24);
    if (mod == 1)
      this.Byte(displacement);
    else if (mod == 2)
      this.Int32(displacement);
  }

  /// <summary>Ends an instruction: a RIP-relative field learns how many bytes followed it.</summary>
  private void End() {
    foreach (var fixup in this._pending)
      fixup.TrailingBytes = this._text.Count - (fixup.Offset + 4);
    this._pending.Clear();
  }

  private void Immediate(X86Width width, long value) {
    switch (width) {
      case X86Width.Byte: this.Byte((int)value); break;
      case X86Width.Word: this.Byte((int)value); this.Byte((int)(value >> 8)); break;
      default: this.Int32(checked((int)value)); break;
    }
  }

  private static int Wide(X86Width width, int byteOpcode) => width == X86Width.Byte ? byteOpcode : byteOpcode + 1;

  // --- integer instructions ----------------------------------------------------------------------

  /// <summary><c>op dst, src</c> between registers.</summary>
  public void Alu(X86Alu op, X86Width width, X86Reg destination, X86Reg source) {
    this.Prefix(width);
    this.Byte(Wide(width, (int)op << 3));
    this.ModRmRegister((int)source, destination);
    this.End();
  }

  /// <summary><c>op reg, [mem]</c>.</summary>
  public void Alu(X86Alu op, X86Width width, X86Reg destination, X86Mem source) {
    this.Prefix(width);
    this.Byte(Wide(width, ((int)op << 3) + 2));
    this.ModRmMemory((int)destination, source);
    this.End();
  }

  /// <summary><c>op [mem], reg</c>.</summary>
  public void Alu(X86Alu op, X86Width width, X86Mem destination, X86Reg source) {
    this.Prefix(width);
    this.Byte(Wide(width, (int)op << 3));
    this.ModRmMemory((int)source, destination);
    this.End();
  }

  /// <summary><c>op reg, imm</c>.</summary>
  public void AluImmediate(X86Alu op, X86Width width, X86Reg destination, long immediate) {
    this.Prefix(width);
    if (width != X86Width.Byte && immediate is >= -128 and <= 127) {
      this.Byte(0x83);
      this.ModRmRegister((int)op, destination);
      this.Byte((int)immediate);
    } else {
      this.Byte(width == X86Width.Byte ? 0x80 : 0x81);
      this.ModRmRegister((int)op, destination);
      this.Immediate(width, immediate);
    }
    this.End();
  }

  /// <summary><c>op [mem], imm</c>.</summary>
  public void AluImmediate(X86Alu op, X86Width width, X86Mem destination, long immediate) {
    this.Prefix(width);
    var small = width != X86Width.Byte && immediate is >= -128 and <= 127;
    this.Byte(width == X86Width.Byte ? 0x80 : small ? 0x83 : 0x81);
    this.ModRmMemory((int)op, destination);
    if (small)
      this.Byte((int)immediate);
    else
      this.Immediate(width, immediate);
    this.End();
  }

  public void Mov(X86Width width, X86Reg destination, X86Reg source) {
    this.Prefix(width);
    this.Byte(Wide(width, 0x88));
    this.ModRmRegister((int)source, destination);
    this.End();
  }

  /// <summary>A load: <c>mov reg, [mem]</c>.</summary>
  public void Mov(X86Width width, X86Reg destination, X86Mem source) {
    this.Prefix(width);
    this.Byte(Wide(width, 0x8A));
    this.ModRmMemory((int)destination, source);
    this.End();
  }

  /// <summary>A store: <c>mov [mem], reg</c>.</summary>
  public void Mov(X86Width width, X86Mem destination, X86Reg source) {
    this.Prefix(width);
    this.Byte(Wide(width, 0x88));
    this.ModRmMemory((int)source, destination);
    this.End();
  }

  /// <summary><c>mov reg, imm</c>: a full 64-bit immediate when a quadword needs one.</summary>
  public void MovImmediate(X86Width width, X86Reg destination, long immediate) {
    if (width == X86Width.Qword && immediate is < int.MinValue or > int.MaxValue) {
      this.Prefix(width);
      this.Byte(0xB8 + (int)destination);
      for (var i = 0; i < 8; ++i)
        this.Byte((int)(immediate >> (8 * i)));
    } else if (width == X86Width.Qword) {
      // C7 /0 sign-extends its 32 bits
      this.Prefix(width);
      this.Byte(0xC7);
      this.ModRmRegister(0, destination);
      this.Int32((int)immediate);
    } else {
      this.Prefix(width);
      this.Byte((width == X86Width.Byte ? 0xB0 : 0xB8) + (int)destination);
      this.Immediate(width, immediate);
    }
    this.End();
  }

  public void MovImmediate(X86Width width, X86Mem destination, long immediate) {
    this.Prefix(width);
    this.Byte(Wide(width, 0xC6));
    this.ModRmMemory(0, destination);
    this.Immediate(width, immediate);
    this.End();
  }

  /// <summary><c>movzx</c>/<c>movsx</c> of a byte or word in memory into a register of <paramref name="width"/>.</summary>
  public void Extend(bool signed, X86Width width, X86Reg destination, X86Width from, X86Mem source) {
    if (from == X86Width.Dword) {
      if (!signed)
        throw new ArgumentException("a 32-bit load already zero-extends; use Mov", nameof(signed));
      this.Prefix(X86Width.Qword);
      this.Byte(0x63);
    } else {
      this.Prefix(width);
      this.Byte(0x0F);
      this.Byte((signed ? 0xBE : 0xB6) + (from == X86Width.Word ? 1 : 0));
    }
    this.ModRmMemory((int)destination, source);
    this.End();
  }

  /// <summary><c>movzx</c>/<c>movsx</c> between registers.</summary>
  public void Extend(bool signed, X86Width width, X86Reg destination, X86Width from, X86Reg source) {
    if (from == X86Width.Dword) {
      if (!signed) {
        this.Mov(X86Width.Dword, destination, source);
        return;
      }
      this.Prefix(X86Width.Qword);
      this.Byte(0x63);
    } else {
      this.Prefix(width);
      this.Byte(0x0F);
      this.Byte((signed ? 0xBE : 0xB6) + (from == X86Width.Word ? 1 : 0));
    }
    this.ModRmRegister((int)destination, source);
    this.End();
  }

  public void Lea(X86Width width, X86Reg destination, X86Mem source) {
    this.Prefix(width);
    this.Byte(0x8D);
    this.ModRmMemory((int)destination, source);
    this.End();
  }

  /// <summary><c>imul reg, reg</c>: the low half of the product, which is the same signed or not.</summary>
  public void Imul(X86Width width, X86Reg destination, X86Reg source) {
    this.Prefix(width);
    this.Byte(0x0F);
    this.Byte(0xAF);
    this.ModRmRegister((int)destination, source);
    this.End();
  }

  public void Unary(X86Unary op, X86Width width, X86Reg operand) {
    this.Prefix(width);
    this.Byte(Wide(width, 0xF6));
    this.ModRmRegister((int)op, operand);
    this.End();
  }

  /// <summary>A shift by CL.</summary>
  public void Shift(X86Shift op, X86Width width, X86Reg operand) {
    this.Prefix(width);
    this.Byte(Wide(width, 0xD2));
    this.ModRmRegister((int)op, operand);
    this.End();
  }

  /// <summary>A shift by a constant.</summary>
  public void Shift(X86Shift op, X86Width width, X86Reg operand, int count) {
    this.Prefix(width);
    this.Byte(Wide(width, 0xC0));
    this.ModRmRegister((int)op, operand);
    this.Byte(count);
    this.End();
  }

  /// <summary>Sign-extends the accumulator into the data register: <c>cwd</c>, <c>cdq</c> or <c>cqo</c>.</summary>
  public void SignExtendIntoDx(X86Width width) {
    this.Prefix(width);
    this.Byte(0x99);
    this.End();
  }

  public void Test(X86Width width, X86Reg a, X86Reg b) {
    this.Prefix(width);
    this.Byte(Wide(width, 0x84));
    this.ModRmRegister((int)b, a);
    this.End();
  }

  public void Set(X86Cond condition, X86Reg byteRegister) {
    this.Byte(0x0F);
    this.Byte(0x90 + (int)condition);
    this.ModRmRegister(0, byteRegister);
    this.End();
  }

  public void Push(X86Reg register) {
    this.Byte(0x50 + (int)register);
    this.End();
  }

  public void Pop(X86Reg register) {
    this.Byte(0x58 + (int)register);
    this.End();
  }

  public void Ret() {
    this.Byte(0xC3);
    this.End();
  }

  /// <summary>The system call: <c>syscall</c> on x64, <c>int 0x80</c> on i386.</summary>
  public void SystemCall() {
    if (this.Machine == X86Machine.Amd64) {
      this.Byte(0x0F);
      this.Byte(0x05);
    } else {
      this.Byte(0xCD);
      this.Byte(0x80);
    }
    this.End();
  }

  public void Jump(X86Label target) => this.Branch(null, target);

  /// <summary><c>jmp [mem]</c>: through an address held in memory.</summary>
  public void JumpIndirect(X86Mem target) {
    this.Byte(0xFF);
    this.ModRmMemory(4, target);
    this.End();
  }

  public void Jump(X86Cond condition, X86Label target) => this.Branch(condition, target);

  public void Call(X86Label target) {
    this.Byte(0xE8);
    this.Relative(target);
    this.End();
  }

  private void Branch(X86Cond? condition, X86Label target) {
    if (condition is { } code) {
      this.Byte(0x0F);
      this.Byte(0x80 + (int)code);
    } else {
      this.Byte(0xE9);
    }
    this.Relative(target);
    this.End();
  }

  private void Relative(X86Label target) {
    this._fixups.Add(new(X86Section.Text, this._text.Count, X86FixupKind.Relative32, target, 0));
    this.Int32(0);
  }

  // --- x87 -------------------------------------------------------------------------------------

  public void Fld(X87Format format, X86Mem source) {
    switch (format) {
      case X87Format.Single: this.Byte(0xD9); this.ModRmMemory(0, source); break;
      case X87Format.Double: this.Byte(0xDD); this.ModRmMemory(0, source); break;
      default: this.Byte(0xDB); this.ModRmMemory(5, source); break;
    }
    this.End();
  }

  public void Fstp(X87Format format, X86Mem destination) {
    switch (format) {
      case X87Format.Single: this.Byte(0xD9); this.ModRmMemory(3, destination); break;
      case X87Format.Double: this.Byte(0xDD); this.ModRmMemory(3, destination); break;
      default: this.Byte(0xDB); this.ModRmMemory(7, destination); break;
    }
    this.End();
  }

  /// <summary>Loads a signed integer of <paramref name="width"/> (word, dword or qword).</summary>
  public void Fild(X86Width width, X86Mem source) {
    switch (width) {
      case X86Width.Word: this.Byte(0xDF); this.ModRmMemory(0, source); break;
      case X86Width.Dword: this.Byte(0xDB); this.ModRmMemory(0, source); break;
      case X86Width.Qword: this.Byte(0xDF); this.ModRmMemory(5, source); break;
      default: throw new ArgumentException("x87 loads word, dword or qword integers", nameof(width));
    }
    this.End();
  }

  /// <summary>Stores and pops a signed integer, rounded by the control word's mode.</summary>
  public void Fistp(X86Width width, X86Mem destination) {
    switch (width) {
      case X86Width.Word: this.Byte(0xDF); this.ModRmMemory(3, destination); break;
      case X86Width.Dword: this.Byte(0xDB); this.ModRmMemory(3, destination); break;
      case X86Width.Qword: this.Byte(0xDF); this.ModRmMemory(7, destination); break;
      default: throw new ArgumentException("x87 stores word, dword or qword integers", nameof(width));
    }
    this.End();
  }

  /// <summary><c>ST(1) = ST(1) op ST(0)</c>, then pop.</summary>
  public void FArithmetic(X87Op op) {
    this.Byte(0xDE);
    this.Byte((int)op);
    this.End();
  }

  public void X87(X87Code code) {
    this.Byte((int)code >> 8);
    this.Byte((int)code & 0xFF);
    this.End();
  }

  /// <summary><c>fadd qword [mem]</c>: adds a double from memory to ST(0).</summary>
  public void FaddDouble(X86Mem source) {
    this.Byte(0xDC);
    this.ModRmMemory(0, source);
    this.End();
  }

  /// <summary><c>fucomip st(1)</c>: compares ST(0) with ST(1) into ZF/PF/CF and pops once.</summary>
  public void FucomipSt1() {
    this.Byte(0xDF);
    this.Byte(0xE9);
    this.End();
  }

  /// <summary><c>fstp st(0)</c>: drops the top of the stack.</summary>
  public void FPop() {
    this.Byte(0xDD);
    this.Byte(0xD8);
    this.End();
  }

  public void Fnstcw(X86Mem destination) {
    this.Byte(0xD9);
    this.ModRmMemory(7, destination);
    this.End();
  }

  public void Fldcw(X86Mem source) {
    this.Byte(0xD9);
    this.ModRmMemory(5, source);
    this.End();
  }

  // --- layout ----------------------------------------------------------------------------------

  /// <summary>The address of <paramref name="label"/> given where each section starts.</summary>
  public long AddressOf(X86Label label, long textBase, long dataBase, long bssBase) {
    if (!this._bound.TryGetValue(label, out var place))
      throw new InvalidOperationException($"label '{this.NameOf(label)}' is used but never bound");
    return place.Section switch {
      X86Section.Text => textBase,
      X86Section.Data => dataBase,
      _ => bssBase,
    } + place.Offset;
  }

  /// <summary>Resolves every fixup for sections at the given addresses: the bytes of an executable.</summary>
  public (byte[] Text, byte[] Data) Link(long textBase, long dataBase, long bssBase) {
    var text = this._text.ToArray();
    var data = this._data.ToArray();
    foreach (var fixup in this._fixups) {
      var bytes = fixup.Section == X86Section.Text ? text : data;
      var site = (fixup.Section == X86Section.Text ? textBase : dataBase) + fixup.Offset;
      var target = this.AddressOf(fixup.Target, textBase, dataBase, bssBase) + fixup.Addend;
      long value = fixup.Kind switch {
        X86FixupKind.Relative32 => target - (site + 4),
        X86FixupKind.RipRelative32 => target - (site + 4 + fixup.TrailingBytes),
        _ => target,
      };
      var width = fixup.Kind == X86FixupKind.Absolute64 ? 8 : 4;
      if (width == 4 && value is < int.MinValue or > uint.MaxValue)
        throw new InvalidOperationException($"a reference to '{this.NameOf(fixup.Target)}' does not fit 32 bits");
      for (var i = 0; i < width; ++i)
        bytes[fixup.Offset + i] = (byte)(value >> (8 * i));
    }
    return (text, data);
  }
}
