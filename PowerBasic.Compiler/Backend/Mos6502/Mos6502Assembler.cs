namespace PowerBasic.Compiler.Backend.Mos6502;

/// <summary>A position in the program, bound once with <see cref="Mos6502Assembler.Bind"/>.</summary>
public readonly record struct M6502Label(int Id);

/// <summary>An address: a label plus a displacement, or a plain number when <see cref="Label"/> is null.</summary>
public readonly record struct M6502Address(M6502Label? Label, int Offset) {
  public static implicit operator M6502Address(M6502Label label) => new(label, 0);
  public static M6502Address Absolute(int address) => new(null, address);
  public M6502Address Plus(int bytes) => this with { Offset = this.Offset + bytes };

  /// <summary>True for a known address in page zero, which the short addressing modes can reach.</summary>
  public bool IsZeroPage => this.Label is null && this.Offset is >= 0 and <= 0xFF;
}

/// <summary>Which index register a memory operand adds, if any.</summary>
public enum M6502Index { None, X, Y }

/// <summary>
/// A 6502 assembler over typed operands: every instruction is an <see cref="M6502Op"/> in an
/// <see cref="M6502Mode"/> the chip has, so an operand the 6502 cannot encode fails where it is
/// written. Labels resolve at <see cref="Assemble"/>, which also relaxes conditional branches: one
/// whose target is out of the 8-bit reach becomes the inverse branch over a <c>JMP</c>, and the
/// layout is repeated until nothing moves.
///
/// <para>
/// A program is code and initialised data, then an optional uninitialised tail opened by
/// <see cref="BeginUninitialized"/>: its labels get addresses, but its bytes are not part of the
/// image - the program clears them at start-up, so a large array costs nothing to load.
/// </para>
/// </summary>
public sealed class Mos6502Assembler {

  private enum ItemKind { Instruction, Branch, Label, Bytes, Word, Reserve }

  private enum ByteSelect { Whole, Low, High }

  private sealed record Item(ItemKind Kind) {
    public M6502Op Op { get; init; }
    public M6502Mode Mode { get; init; }
    public M6502Address Operand { get; init; }
    public ByteSelect Select { get; init; }
    public byte[]? Data { get; init; }
    public int Count { get; init; }
    public M6502Label Label { get; init; }
    public bool Long { get; set; }
  }

  private readonly List<Item> _items = [];
  private readonly List<string> _labelNames = [];
  private readonly HashSet<M6502Label> _bound = [];
  private int _uninitializedStart = -1;

  /// <summary>A fresh label; <paramref name="name"/> only labels listings and error messages.</summary>
  public M6502Label NewLabel(string name) {
    this._labelNames.Add(name);
    return new(this._labelNames.Count - 1);
  }

  public string NameOf(M6502Label label) => this._labelNames[label.Id];

  /// <summary>Places <paramref name="label"/> at the current position.</summary>
  public void Bind(M6502Label label) {
    if (!this._bound.Add(label))
      throw new InvalidOperationException($"label '{this.NameOf(label)}' is bound twice");
    this._items.Add(new(ItemKind.Label) { Label = label });
  }

  /// <summary>An instruction without an operand.</summary>
  public void Emit(M6502Op op) => this.Add(op, M6502Mode.Implied, default);

  /// <summary>A shift or rotate of the accumulator.</summary>
  public void EmitAccumulator(M6502Op op) => this.Add(op, M6502Mode.Accumulator, default);

  /// <summary><c>op #value</c>.</summary>
  public void Immediate(M6502Op op, int value) {
    if (value is < -128 or > 255)
      throw new ArgumentOutOfRangeException(nameof(value), value, "an immediate is one byte");
    this.Add(op, M6502Mode.Immediate, M6502Address.Absolute(value & 0xFF));
  }

  /// <summary><c>op #&lt;address</c>: the low byte of an address.</summary>
  public void ImmediateLow(M6502Op op, M6502Address address) => this.Add(op, M6502Mode.Immediate, address, ByteSelect.Low);

  /// <summary><c>op #&gt;address</c>: the high byte of an address.</summary>
  public void ImmediateHigh(M6502Op op, M6502Address address) => this.Add(op, M6502Mode.Immediate, address, ByteSelect.High);

  /// <summary>
  /// <c>op address[,index]</c>, in the zero-page form when the address is a page-zero number and the
  /// instruction has one, else absolute.
  /// </summary>
  public void Memory(M6502Op op, M6502Address address, M6502Index index = M6502Index.None) {
    var (zeroPage, absolute) = index switch {
      M6502Index.X => (M6502Mode.ZeroPageX, M6502Mode.AbsoluteX),
      M6502Index.Y => (M6502Mode.ZeroPageY, M6502Mode.AbsoluteY),
      _ => (M6502Mode.ZeroPage, M6502Mode.Absolute),
    };
    this.Add(op, address.IsZeroPage && M6502Isa.Exists(op, zeroPage) ? zeroPage : absolute, address);
  }

  /// <summary><c>op (zp),Y</c>.</summary>
  public void IndirectY(M6502Op op, byte zeroPage) => this.Add(op, M6502Mode.IndirectIndexedY, M6502Address.Absolute(zeroPage));

  /// <summary><c>JMP (address)</c>.</summary>
  public void JumpIndirect(M6502Address address) => this.Add(M6502Op.Jmp, M6502Mode.Indirect, address);

  public void Jump(M6502Address target) => this.Add(M6502Op.Jmp, M6502Mode.Absolute, target);

  public void Call(M6502Address target) => this.Add(M6502Op.Jsr, M6502Mode.Absolute, target);

  /// <summary>A conditional branch to <paramref name="target"/>, however far away it turns out to be.</summary>
  public void Branch(M6502Op condition, M6502Label target) {
    if (!M6502Isa.IsBranch(condition))
      throw new ArgumentException($"{condition} is not a conditional branch", nameof(condition));
    this._items.Add(new(ItemKind.Branch) { Op = condition, Operand = target });
  }

  /// <summary>Initialised data.</summary>
  public void Bytes(ReadOnlySpan<byte> bytes) {
    this.RequireInitialized();
    this._items.Add(new(ItemKind.Bytes) { Data = bytes.ToArray() });
  }

  /// <summary>A little-endian address word.</summary>
  public void Word(M6502Address address) {
    this.RequireInitialized();
    this._items.Add(new(ItemKind.Word) { Operand = address });
  }

  /// <summary>Starts the uninitialised tail: from here on only labels and <see cref="Reserve"/>.</summary>
  public void BeginUninitialized() {
    if (this._uninitializedStart < 0)
      this._uninitializedStart = this._items.Count;
  }

  /// <summary><paramref name="bytes"/> of uninitialised storage.</summary>
  public void Reserve(int bytes) {
    ArgumentOutOfRangeException.ThrowIfNegative(bytes);
    if (this._uninitializedStart < 0)
      throw new InvalidOperationException("Reserve belongs in the uninitialised tail; call BeginUninitialized first");
    this._items.Add(new(ItemKind.Reserve) { Count = bytes });
  }

  private void RequireInitialized() {
    if (this._uninitializedStart >= 0)
      throw new InvalidOperationException("initialised bytes cannot follow the uninitialised tail");
  }

  private void Add(M6502Op op, M6502Mode mode, M6502Address operand, ByteSelect select = ByteSelect.Whole) {
    _ = M6502Isa.Opcode(op, mode);
    if (this._uninitializedStart >= 0)
      throw new InvalidOperationException("instructions cannot follow the uninitialised tail");
    this._items.Add(new(ItemKind.Instruction) { Op = op, Mode = mode, Operand = operand, Select = select });
  }

  /// <summary>The result of <see cref="Assemble"/>: the stored bytes and where everything landed.</summary>
  public sealed record Image(int Origin, byte[] Bytes, int End, IReadOnlyDictionary<M6502Label, int> Labels) {
    /// <summary>The first address after the stored bytes, where the uninitialised tail starts.</summary>
    public int UninitializedStart => this.Origin + this.Bytes.Length;
  }

  /// <summary>Lays the program out at <paramref name="origin"/> and encodes it.</summary>
  public Image Assemble(int origin) {
    var addresses = new Dictionary<M6502Label, int>();
    int[] offsets;
    bool relaxed;
    do {
      offsets = this.Layout(origin, addresses);
      relaxed = false;
      for (var i = 0; i < this._items.Count; ++i) {
        var item = this._items[i];
        if (item.Kind != ItemKind.Branch || item.Long)
          continue;
        var displacement = this.Resolve(item.Operand, addresses) - (offsets[i] + 2);
        if (displacement is < -128 or > 127) {
          item.Long = true;
          relaxed = true;
        }
      }
    } while (relaxed);

    var end = this._uninitializedStart < 0 ? this._items.Count : this._uninitializedStart;
    var bytes = new List<byte>();
    for (var i = 0; i < end; ++i) {
      var item = this._items[i];
      switch (item.Kind) {
        case ItemKind.Instruction: {
          bytes.Add(M6502Isa.Opcode(item.Op, item.Mode));
          var value = this.Resolve(item.Operand, addresses);
          switch (M6502Isa.OperandBytes(item.Mode)) {
            case 1:
              var one = item.Select switch {
                ByteSelect.Low => value & 0xFF,
                ByteSelect.High => (value >> 8) & 0xFF,
                _ => value,
              };
              if (one is < 0 or > 0xFF)
                throw new InvalidOperationException($"{item.Op} operand {value:X} does not fit a byte");
              bytes.Add((byte)one);
              break;
            case 2:
              AddWord(bytes, value);
              break;
          }
          break;
        }
        case ItemKind.Branch: {
          var target = this.Resolve(item.Operand, addresses);
          if (item.Long) {
            bytes.Add(M6502Isa.Opcode(M6502Isa.Inverse(item.Op), M6502Mode.Relative));
            bytes.Add(3);
            bytes.Add(M6502Isa.Opcode(M6502Op.Jmp, M6502Mode.Absolute));
            AddWord(bytes, target);
          } else {
            bytes.Add(M6502Isa.Opcode(item.Op, M6502Mode.Relative));
            bytes.Add(unchecked((byte)(sbyte)(target - (offsets[i] + 2))));
          }
          break;
        }
        case ItemKind.Bytes:
          bytes.AddRange(item.Data!);
          break;
        case ItemKind.Word:
          AddWord(bytes, this.Resolve(item.Operand, addresses));
          break;
      }
    }
    var last = this._items.Count == 0 ? origin : offsets[^1] + Size(this._items[^1]);
    if (last > 0x10000)
      throw new InvalidOperationException($"the program needs {last - origin} bytes and runs past the top of memory");
    return new(origin, [.. bytes], last, addresses);
  }

  private int[] Layout(int origin, Dictionary<M6502Label, int> addresses) {
    var offsets = new int[this._items.Count];
    var at = origin;
    for (var i = 0; i < this._items.Count; ++i) {
      offsets[i] = at;
      var item = this._items[i];
      if (item.Kind == ItemKind.Label)
        addresses[item.Label] = at;
      at += Size(item);
    }
    return offsets;
  }

  private static int Size(Item item) => item.Kind switch {
    ItemKind.Instruction => 1 + M6502Isa.OperandBytes(item.Mode),
    ItemKind.Branch => item.Long ? 5 : 2,
    ItemKind.Bytes => item.Data!.Length,
    ItemKind.Word => 2,
    ItemKind.Reserve => item.Count,
    _ => 0,
  };

  private int Resolve(M6502Address address, IReadOnlyDictionary<M6502Label, int> addresses) {
    if (address.Label is not { } label)
      return address.Offset;
    return addresses.TryGetValue(label, out var at) ? at + address.Offset
      : throw new InvalidOperationException($"label '{this.NameOf(label)}' is used but never bound");
  }

  private static void AddWord(List<byte> bytes, int value) {
    bytes.Add((byte)(value & 0xFF));
    bytes.Add((byte)((value >> 8) & 0xFF));
  }
}
