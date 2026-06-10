using System.Numerics;

namespace PowerBasic.Compiler.Asm;

/// <summary>
/// In-memory assembler for 16-bit real-mode x86 (8086..80386 subset, x87).
/// Instructions are appended through the fluent instruction methods (see the
/// partials); <see cref="ToArray"/> resolves all label fixups and returns the
/// flat image. Positions of words that hold segment values (to be patched by
/// the DOS loader) are reported via <see cref="SegmentRelocations"/>.
/// </summary>
public sealed partial class Assembler {

  private enum FixupKind { Rel8, Rel16, Abs16 }

  private readonly record struct Fixup(int Position, FixupKind Kind, Label Target, int Addend);

  private readonly List<byte> _buffer = [];
  private readonly List<Fixup> _fixups = [];
  private readonly List<int> _segmentRelocations = [];
  private readonly Dictionary<string, Label> _namedLabels = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Current emit offset within the image.</summary>
  public int Position => this._buffer.Count;

  /// <summary>Image offsets of words holding segment values the DOS loader must patch.</summary>
  public IReadOnlyList<int> SegmentRelocations => this._segmentRelocations;

  #region labels

  /// <summary>Creates a fresh, unbound label.</summary>
  public Label DefineLabel(string? name = null) => new(name);

  /// <summary>Gets or creates the named label (case-insensitive).</summary>
  public Label Lbl(string name) {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    if (!this._namedLabels.TryGetValue(name, out var label))
      this._namedLabels.Add(name, label = new(name));

    return label;
  }

  /// <summary>Binds <paramref name="label"/> to the current position.</summary>
  public void MarkLabel(Label label) {
    ArgumentNullException.ThrowIfNull(label);
    if (label.IsBound)
      throw new InvalidOperationException($"Label {label} is already bound to offset {label.Position}.");
    if (label.IsExternal)
      throw new InvalidOperationException($"Label {label} is external and cannot be bound.");

    label.Position = this.Position;
  }

  /// <summary>Gets or creates the named label and binds it to the current position.</summary>
  public Label MarkLabel(string name) {
    var label = this.Lbl(name);
    this.MarkLabel(label);
    return label;
  }

  /// <summary>Gets or creates the named label and flags it external (resolved at link time, never bindable).</summary>
  public Label External(string name) {
    var label = this.Lbl(name);
    if (label.IsBound)
      throw new InvalidOperationException($"Label {label} is already bound and cannot become external.");

    label.IsExternal = true;
    return label;
  }

  #endregion

  /// <summary>Rolls the buffer back to <paramref name="position"/>, dropping fixups and relocations behind it.</summary>
  internal void Truncate(int position) {
    if (position < 0 || position > this._buffer.Count)
      throw new ArgumentOutOfRangeException(nameof(position));

    this._buffer.RemoveRange(position, this._buffer.Count - position);
    this._fixups.RemoveAll(fixup => fixup.Position >= position);
    this._segmentRelocations.RemoveAll(offset => offset >= position);
  }

  #region image generation

  /// <summary>Resolves all fixups and returns the assembled image.</summary>
  public byte[] ToArray() {
    var result = this._buffer.ToArray();
    foreach (var fixup in this._fixups) {
      if (!fixup.Target.IsBound)
        throw new InvalidOperationException($"Label {fixup.Target} was referenced but never bound.");

      ApplyFixup(result, fixup);
    }

    return result;
  }

  /// <summary>
  /// Resolves all internal fixups and returns the image together with the
  /// sites a linker must patch: absolute 16-bit offsets (rebase when the image
  /// moves), segment words, and references to external labels (unbound named
  /// labels count as external too - units import runtime symbols by name).
  /// </summary>
  public RelocatableImage ToRelocatable() {
    var result = this._buffer.ToArray();
    var relocations = new List<AsmRelocation>();
    var registered = new HashSet<Label>(this._namedLabels.Values, ReferenceEqualityComparer.Instance);
    foreach (var fixup in this._fixups) {
      if (fixup.Target.IsExternal || (!fixup.Target.IsBound && registered.Contains(fixup.Target))) {
        switch (fixup.Kind) {
          case FixupKind.Rel16:
            relocations.Add(new(fixup.Position, AsmRelocationKind.ExternalRelative, fixup.Target.Name));
            break;
          case FixupKind.Abs16:
            // the addend stays in the site; the linker adds the symbol's final offset
            result[fixup.Position] = (byte)fixup.Addend;
            result[fixup.Position + 1] = (byte)(fixup.Addend >> 8);
            relocations.Add(new(fixup.Position, AsmRelocationKind.ExternalAbsolute, fixup.Target.Name));
            break;
          default:
            throw new InvalidOperationException($"Short jump to external label {fixup.Target} is not linkable.");
        }
        continue;
      }

      if (!fixup.Target.IsBound)
        throw new InvalidOperationException($"Label {fixup.Target} was referenced but never bound.");

      ApplyFixup(result, fixup);
      if (fixup.Kind == FixupKind.Abs16 && !fixup.Target.IsConstant)
        relocations.Add(new(fixup.Position, AsmRelocationKind.Absolute, null));
    }

    foreach (var site in this._segmentRelocations)
      relocations.Add(new(site, AsmRelocationKind.Segment, null));

    var bound = this._namedLabels
      .Where(pair => pair.Value.IsBound)
      .ToDictionary(pair => pair.Key, pair => pair.Value.Position, StringComparer.OrdinalIgnoreCase);
    return new(result, relocations, bound);
  }

  private static void ApplyFixup(byte[] result, Fixup fixup) {
    var target = fixup.Target.Position + fixup.Addend;
    switch (fixup.Kind) {
      case FixupKind.Rel8: {
        var rel = target - (fixup.Position + 1);
        if (rel is < sbyte.MinValue or > sbyte.MaxValue)
          throw new InvalidOperationException($"Short jump to {fixup.Target} is out of range ({rel}).");

        result[fixup.Position] = (byte)(sbyte)rel;
        break;
      }
      case FixupKind.Rel16: {
        var rel = target - (fixup.Position + 2);
        result[fixup.Position] = (byte)rel;
        result[fixup.Position + 1] = (byte)(rel >> 8);
        break;
      }
      case FixupKind.Abs16: {
        result[fixup.Position] = (byte)target;
        result[fixup.Position + 1] = (byte)(target >> 8);
        break;
      }
      default:
        throw new InvalidOperationException($"Unknown fixup kind {fixup.Kind}.");
    }
  }

  #endregion

  #region data emission

  public void Db(params byte[] bytes) {
    ArgumentNullException.ThrowIfNull(bytes);
    this._buffer.AddRange(bytes);
  }

  /// <summary>Emits the ASCII bytes of <paramref name="text"/>.</summary>
  public void Db(string text) {
    ArgumentNullException.ThrowIfNull(text);
    foreach (var c in text) {
      if (c > '\xFF')
        throw new ArgumentException($"Character '{c}' is not representable as a single byte.", nameof(text));

      this.EmitByte((byte)c);
    }
  }

  public void Dw(params ushort[] words) {
    ArgumentNullException.ThrowIfNull(words);
    foreach (var word in words)
      this.EmitWord(word);
  }

  /// <summary>Emits the 16-bit offset of <paramref name="label"/> (patched when bound).</summary>
  public void Dw(Label label, int addend = 0) {
    ArgumentNullException.ThrowIfNull(label);
    this._fixups.Add(new(this.Position, FixupKind.Abs16, label, addend));
    this.EmitWord(0);
  }

  /// <summary>Emits a segment word patched by the DOS loader; its position is recorded as a relocation.</summary>
  public void DwSegment(ushort paragraph = 0) {
    this._segmentRelocations.Add(this.Position);
    this.EmitWord(paragraph);
  }

  public void Dd(params uint[] dwords) {
    ArgumentNullException.ThrowIfNull(dwords);
    foreach (var dword in dwords)
      this.EmitDword(dword);
  }

  /// <summary>Emits an IEEE 754 single-precision value.</summary>
  public void Dd(float value) => this.EmitDword(BitConverter.SingleToUInt32Bits(value));

  public void Dq(params ulong[] qwords) {
    ArgumentNullException.ThrowIfNull(qwords);
    foreach (var qword in qwords) {
      this.EmitDword((uint)qword);
      this.EmitDword((uint)(qword >> 32));
    }
  }

  /// <summary>Emits an IEEE 754 double-precision value.</summary>
  public void Dq(double value) => this.Dq(BitConverter.DoubleToUInt64Bits(value));

  /// <summary>Emits an 80-bit x87 extended-real converted exactly from <paramref name="value"/>.</summary>
  public void Dt(double value) {
    var bits = BitConverter.DoubleToUInt64Bits(value);
    var sign = (ushort)(bits >> 63 << 15);
    var biasedExponent = (int)(bits >> 52) & 0x7FF;
    var fraction = bits & 0xF_FFFF_FFFF_FFFF;

    ulong mantissa;
    ushort exponent;
    if (biasedExponent == 0x7FF) {
      // infinity / NaN: integer bit set, payload shifted into the explicit mantissa
      exponent = (ushort)(sign | 0x7FFF);
      mantissa = 0x8000_0000_0000_0000 | fraction << 11;
    } else if (biasedExponent == 0) {
      if (fraction == 0) {
        this.EmitExtended(sign, 0);
        return;
      }

      // subnormal double: normalize into the explicit integer bit
      var shift = BitOperations.LeadingZeroCount(fraction);
      mantissa = fraction << shift;
      exponent = (ushort)(sign | (16383 - 1022 - (shift - 11)));
    } else {
      mantissa = 0x8000_0000_0000_0000 | fraction << 11;
      exponent = (ushort)(sign | (biasedExponent - 1023 + 16383));
    }

    this.EmitExtended(exponent, mantissa);
  }

  /// <summary>Emits an 80-bit x87 extended-real of the decimal value, rounded to nearest-even.</summary>
  public void Dt(decimal value) {
    if (value == 0m) {
      this.EmitExtended(0, 0);
      return;
    }

    var parts = decimal.GetBits(value);
    var magnitude = new BigInteger((uint)parts[2]) << 64 | new BigInteger((uint)parts[1]) << 32 | new BigInteger((uint)parts[0]);
    var scale = parts[3] >> 16 & 0xFF;
    var isNegative = parts[3] < 0;
    var divisor = BigInteger.Pow(10, scale);

    // find shift so that (magnitude << shift) / divisor has 65 significant bits,
    // then round the extra bit half-to-even
    var shift = 65 + (int)divisor.GetBitLength() - (int)magnitude.GetBitLength();
    BigInteger quotient, remainder;
    for (;;) {
      var numerator = shift >= 0 ? magnitude << shift : magnitude >> -shift;
      quotient = BigInteger.DivRem(numerator, divisor, out remainder);
      var bitLength = (int)quotient.GetBitLength();
      if (bitLength == 65)
        break;

      shift += 65 - bitLength;
    }

    var roundBit = !quotient.IsEven;
    quotient >>= 1;
    if (roundBit && (!remainder.IsZero || !quotient.IsEven))
      ++quotient;

    var exponent = 16383 + 63 - (shift - 1);
    if (quotient.GetBitLength() > 64) {
      quotient >>= 1;
      ++exponent;
    }

    var sign = (ushort)(isNegative ? 0x8000 : 0);
    this.EmitExtended((ushort)(sign | exponent), (ulong)quotient);
  }

  private void EmitExtended(ushort signAndExponent, ulong mantissa) {
    this.EmitDword((uint)mantissa);
    this.EmitDword((uint)(mantissa >> 32));
    this.EmitWord(signAndExponent);
  }

  /// <summary>Pads with <paramref name="fill"/> bytes until the position is a multiple of <paramref name="alignment"/>.</summary>
  public void Align(int alignment, byte fill = 0) {
    if (alignment < 1 || (alignment & (alignment - 1)) != 0)
      throw new ArgumentOutOfRangeException(nameof(alignment), alignment, "Alignment must be a positive power of two.");

    while (this.Position % alignment != 0)
      this.EmitByte(fill);
  }

  #endregion

  #region low-level emit helpers

  private void EmitByte(byte value) => this._buffer.Add(value);

  private void EmitWord(ushort value) {
    this._buffer.Add((byte)value);
    this._buffer.Add((byte)(value >> 8));
  }

  private void EmitDword(uint value) {
    this.EmitWord((ushort)value);
    this.EmitWord((ushort)(value >> 16));
  }

  private void EmitRel8(Label target) {
    this._fixups.Add(new(this.Position, FixupKind.Rel8, target, 0));
    this.EmitByte(0);
  }

  private void EmitRel16(Label target) {
    this._fixups.Add(new(this.Position, FixupKind.Rel16, target, 0));
    this.EmitWord(0);
  }

  private static byte SegmentPrefix(Reg segment) => segment switch {
    Reg.ES => 0x26,
    Reg.CS => 0x2E,
    Reg.SS => 0x36,
    Reg.DS => 0x3E,
    Reg.FS => 0x64,
    Reg.GS => 0x65,
    _ => throw new ArgumentException($"{segment} is not a segment register.", nameof(segment)),
  };

  /// <summary>Emits a bare segment-override prefix byte (e.g. in front of a string instruction).</summary>
  public void Seg(Reg segment) => this.EmitByte(SegmentPrefix(segment));

  private void EmitSegmentPrefix(Mem memory) {
    if (memory.Segment is { } segment)
      this.EmitByte(SegmentPrefix(segment));
  }

  private void EmitOperandSizePrefixIf(bool isDword) {
    if (isDword)
      this.EmitByte(0x66);
  }

  private void EmitModRmRegister(int regField, Reg rm) => this.EmitByte((byte)(0xC0 | regField << 3 | rm.Index()));

  /// <summary>Emits ModRM + displacement for a memory operand (16-bit addressing forms).</summary>
  private void EmitModRmMemory(int regField, Mem memory) {
    var rm = (memory.Base, memory.Index) switch {
      (Reg.BX, Reg.SI) => 0,
      (Reg.BX, Reg.DI) => 1,
      (Reg.BP, Reg.SI) => 2,
      (Reg.BP, Reg.DI) => 3,
      (Reg.SI, null) => 4,
      (Reg.DI, null) => 5,
      (Reg.BP, null) => 6,
      (Reg.BX, null) => 7,
      (null, null) => -1,
      _ => throw new ArgumentException($"Invalid addressing combination {memory}.", nameof(memory)),
    };

    if (rm < 0) {
      // direct address: mod=00 rm=110 disp16
      this.EmitByte((byte)(regField << 3 | 6));
      this.EmitDisp16(memory);
      return;
    }

    if (memory.Label is not null) {
      this.EmitByte((byte)(0x80 | regField << 3 | rm));
      this.EmitDisp16(memory);
      return;
    }

    var displacement = memory.Displacement;
    if (displacement == 0 && rm != 6) {
      this.EmitByte((byte)(regField << 3 | rm));
      return;
    }

    if (displacement is >= sbyte.MinValue and <= sbyte.MaxValue) {
      this.EmitByte((byte)(0x40 | regField << 3 | rm));
      this.EmitByte((byte)(sbyte)displacement);
      return;
    }

    this.EmitByte((byte)(0x80 | regField << 3 | rm));
    this.EmitDisp16(memory);
  }

  private void EmitDisp16(Mem memory) {
    if (memory.Label is { } label) {
      this._fixups.Add(new(this.Position, FixupKind.Abs16, label, memory.Displacement));
      this.EmitWord(0);
    } else
      this.EmitWord((ushort)memory.Displacement);
  }

  /// <summary>Emits an immediate of the given size, registering label/segment fixups as needed.</summary>
  private void EmitImmediate(OperandSize size, Imm immediate) {
    if (immediate.Label is { } label) {
      if (size != OperandSize.Word)
        throw new ArgumentException("Label offsets are 16-bit immediates.", nameof(immediate));

      this._fixups.Add(new(this.Position, FixupKind.Abs16, label, immediate.Value));
      this.EmitWord(0);
      return;
    }

    if (immediate.IsSegmentReference) {
      if (size != OperandSize.Word)
        throw new ArgumentException("Segment references are 16-bit immediates.", nameof(immediate));

      this.DwSegment((ushort)immediate.Value);
      return;
    }

    switch (size) {
      case OperandSize.Byte:
        this.EmitByte((byte)immediate.Value);
        break;
      case OperandSize.Word:
        this.EmitWord((ushort)immediate.Value);
        break;
      case OperandSize.Dword:
        this.EmitDword((uint)immediate.Value);
        break;
      default:
        throw new ArgumentException($"Unsupported immediate size {size}.", nameof(size));
    }
  }

  #endregion

  #region operand validation helpers

  private static void RequireGeneralPurpose(Reg register, string parameterName) {
    if (!register.IsGeneralPurpose())
      throw new ArgumentException($"{register} is not a general-purpose register.", parameterName);
  }

  private static void RequireWordOrDword(Reg register, string parameterName) {
    if (!register.IsWord() && !register.IsDword())
      throw new ArgumentException($"{register} must be a 16- or 32-bit register.", parameterName);
  }

  private static void RequireSameSize(Reg first, Reg second) {
    if (first.Size() != second.Size())
      throw new ArgumentException($"Operand size mismatch: {first} vs {second}.");
  }

  private static OperandSize RequireSized(Mem memory) => memory.Size != OperandSize.None
    ? memory.Size
    : throw new ArgumentException($"Memory operand {memory} needs an explicit size.", nameof(memory));

  private static void RequireMatchingSize(Reg register, Mem memory) {
    if (memory.Size != OperandSize.None && memory.Size != register.Size())
      throw new ArgumentException($"Operand size mismatch: {register} vs {memory}.");
  }

  private static bool FitsSByte(int value) => value is >= sbyte.MinValue and <= sbyte.MaxValue;

  #endregion
}
