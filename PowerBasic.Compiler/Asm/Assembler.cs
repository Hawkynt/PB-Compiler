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

  /// <summary>
  /// <c>Rel16Pair</c> is the rel16 of the near JMP inside the 8086 spelling of a long conditional
  /// jump (<c>J!cc over; JMP target; over:</c>). It resolves exactly as <see cref="Rel16"/>; the
  /// distinct kind exists so the relaxation can recognise the pair by RECORD rather than by matching
  /// bytes backwards, which is not safe - `add ax,0370h` is 05 70 03 and `mov bx,0371h` is BB 71 03,
  /// so either one before a near JMP would look exactly like the pair's first two bytes.
  /// </summary>
  private enum FixupKind { Rel8, Rel16, Abs16, Rel16Pair }

  private readonly record struct Fixup(int Position, FixupKind Kind, Label Target, int Addend);

  private readonly List<byte> _buffer = [];
  private readonly List<Fixup> _fixups = [];
  private readonly List<int> _segmentRelocations = [];
  private readonly Dictionary<string, Label> _namedLabels = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// Every label bound into the image, named or not. A shrinking pass has to slide all of them,
  /// and the named/referenced ones are not all of them: <see cref="DefineLabel"/> hands out
  /// anonymous labels that nothing registers, and tail-merge's fold regions are delimited by
  /// exactly those - a stale boundary there makes it compare the wrong bytes.
  /// </summary>
  private readonly List<Label> _boundLabels = [];

  /// <summary>Current emit offset within the image.</summary>
  public int Position => this._buffer.Count;

  /// <summary>Image offsets of words holding segment values the DOS loader must patch.</summary>
  public IReadOnlyList<int> SegmentRelocations => this._segmentRelocations;

  /// <summary>All label references recorded so far (fixup position and target); trimming/optimizer support.</summary>
  public IEnumerable<(int Position, Label Target)> LabelReferences() => this._fixups.Select(f => (f.Position, f.Target));

  /// <summary>The named labels created so far (case-insensitive registry); trimming/optimizer support.</summary>
  public IReadOnlyCollection<Label> KnownNamedLabels => this._namedLabels.Values;

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
    this._boundLabels.Add(label);
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
    this.TrimPeep(position);
    this.TrimSched(position);
  }

  #region image generation

  /// <summary>Resolves all fixups and returns the assembled image.</summary>
  public byte[] ToArray() {
    this.RunLoadForwarding();
    this.RunSchedule();
    this.RunPeephole();
    this.RunJumpThreading();
    this.RunTailMerge();
    this.RunJumpRelaxation();
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
    this.RunLoadForwarding();
    this.RunSchedule();
    this.RunPeephole();
    this.RunJumpThreading();
    this.RunTailMerge();
    this.RunJumpRelaxation();
    var result = this._buffer.ToArray();
    var relocations = new List<AsmRelocation>();
    var registered = new HashSet<Label>(this._namedLabels.Values, ReferenceEqualityComparer.Instance);
    foreach (var fixup in this._fixups) {
      if (fixup.Target.IsExternal || (!fixup.Target.IsBound && registered.Contains(fixup.Target))) {
        switch (fixup.Kind) {
          case FixupKind.Rel16:
          case FixupKind.Rel16Pair:
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

  /// <summary>
  /// When set, jump fixups whose bound target lands on an unconditional JMP are retargeted to
  /// that jump's final destination before resolution (the cascade an ITERATE creates - jump to
  /// the loop end, which jumps back to the loop head - collapses to one hop; GOTO -> GOTO chains
  /// likewise). Off by default; the optimizer turns it on for the program image.
  /// </summary>
  public bool EnableJumpThreading { get; set; }
  private bool _jumpThreadingRan;

  /// <summary>
  /// Jump threading over the finished stream: a pure fixup rewrite (byte-length-preserving, no
  /// instruction moves), run after the scheduler/peephole so every position is final. Only real
  /// jump instructions thread (short/near JMP and Jcc - identified by the opcode byte in front of
  /// their displacement fixup); CALL keeps its target. Chains are followed with a hop budget so a
  /// jump cycle (an intentional endless loop) terminates; a short jump only retargets while the
  /// new displacement still fits in a byte.
  /// </summary>
  public void RunJumpThreading() {
    if (!this.EnableJumpThreading || this._jumpThreadingRan)
      return;
    this._jumpThreadingRan = true;

    // unconditional JMPs by instruction start: E9 rel16 / EB rel8, displacement fixup right after
    var jmpAt = new Dictionary<int, int>();
    for (var i = 0; i < this._fixups.Count; ++i) {
      var f = this._fixups[i];
      if (f.Position < 1)
        continue;
      var op = this._buffer[f.Position - 1];
      if ((f.Kind == FixupKind.Rel16 && op == 0xE9) || (f.Kind == FixupKind.Rel8 && op == 0xEB))
        jmpAt[f.Position - 1] = i;
    }
    if (jmpAt.Count == 0)
      return;

    for (var i = 0; i < this._fixups.Count; ++i) {
      var f = this._fixups[i];
      if (f.Position < 1 || !f.Target.IsBound)
        continue;
      var op = this._buffer[f.Position - 1];
      var isJump = f.Kind switch {
        FixupKind.Rel8 => op is 0xEB or (>= 0x70 and <= 0x7F),
        FixupKind.Rel16 => op == 0xE9 || (f.Position >= 2 && this._buffer[f.Position - 2] == 0x0F && op is >= 0x80 and <= 0x8F),
        // the 8086 long-conditional pair jumps through its own near JMP, so it threads like one -
        // without this a conditional jump silently stops following JMP chains on an 8086 target
        FixupKind.Rel16Pair => op == 0xE9,
        _ => false,
      };
      if (!isJump)
        continue;

      var (target, addend) = (f.Target, f.Addend);
      for (var hops = 0; hops < 8 && target.IsBound; ++hops) {
        if (!jmpAt.TryGetValue(target.Position + addend, out var j) || j == i)
          break;
        var next = this._fixups[j];
        if (!next.Target.IsBound || (ReferenceEquals(next.Target, target) && next.Addend == addend))
          break;
        (target, addend) = (next.Target, next.Addend);
      }
      if (ReferenceEquals(target, f.Target) && addend == f.Addend)
        continue;
      if (f.Kind == FixupKind.Rel8 && target.Position + addend - (f.Position + 1) is < sbyte.MinValue or > sbyte.MaxValue)
        continue;   // the short encoding cannot reach the final destination - keep the hop
      this._fixups[i] = f with { Target = target, Addend = addend };
    }

    this.RemoveOrphanedJumpHops();
  }

  /// <summary>
  /// O0093, second half: the <c>A: JMP B</c> hop threading has just bypassed is dead code once
  /// nothing reaches it, and deleting it is the size saving the taken-jump saving left behind.
  ///
  /// Two conditions, both deliberately conservative, because a wrong deletion here is a miscompile
  /// anywhere:
  ///
  /// NOTHING MAY TARGET IT. Every fixup's resolved destination is collected as
  /// <c>Position + Addend</c>, not just <c>Position</c>, so a label reached through an addend still
  /// counts as a reference. A named label bound on the hop keeps it too - another module may
  /// reference it by name, and this assembler cannot see that.
  ///
  /// CONTROL MAY NOT FALL INTO IT. The only instruction proven not to fall through is another
  /// unconditional JMP, so a hop qualifies only when one ends exactly at its first byte. A RET
  /// would qualify as well but is not attempted: <c>C3</c> cannot be told from a displacement byte
  /// by looking at it, and guessing wrong deletes reachable code.
  ///
  /// Deleting can only SHRINK the distance a jump spans, so no short displacement can be pushed out
  /// of range by this - the cut either sits between a jump and its target, bringing them closer, or
  /// outside it, changing nothing. Cuts run from the end backwards so earlier offsets stay valid,
  /// and the whole thing iterates a few times because removing one hop can orphan the next.
  /// </summary>
  private void RemoveOrphanedJumpHops() {
    for (var pass = 0; pass < 4; ++pass) {
      var jmps = new Dictionary<int, int>();               // instruction start -> encoded length
      foreach (var f in this._fixups) {
        if (f.Position < 1)
          continue;
        var op = this._buffer[f.Position - 1];
        if (f.Kind == FixupKind.Rel16 && op == 0xE9)
          jmps[f.Position - 1] = 3;
        else if (f.Kind == FixupKind.Rel8 && op == 0xEB)
          jmps[f.Position - 1] = 2;
      }
      if (jmps.Count == 0)
        return;

      var targeted = new HashSet<int>();
      foreach (var f in this._fixups)
        if (f.Target.IsBound && !f.Target.IsConstant)
          targeted.Add(f.Target.Position + f.Addend);
      foreach (var label in this._namedLabels.Values)
        if (label.IsBound && !label.IsConstant)
          targeted.Add(label.Position);

      var afterAJump = new HashSet<int>();
      foreach (var (start, length) in jmps)
        afterAJump.Add(start + length);

      var victims = new List<(int Start, int Length)>();
      foreach (var (start, length) in jmps)
        if (!targeted.Contains(start) && afterAJump.Contains(start))
          victims.Add((start, length));
      if (victims.Count == 0)
        return;

      victims.Sort((a, b) => b.Start.CompareTo(a.Start));
      foreach (var (start, length) in victims)
        this.RemoveBytes(start, length);
    }
  }

  /// <summary>
  /// When set, near jumps whose displacement fits a signed byte are rewritten to the short
  /// form before fixup resolution (S1 $OPTIMIZE SIZE): E9 rel16 (3 bytes) becomes EB rel8
  /// (2), 0F 8x rel16 (4) becomes 7x rel8 (2). Off by default.
  /// </summary>
  public bool EnableJumpRelaxation { get; set; }
  private bool _jumpRelaxationRan;

  /// <summary>
  /// Whether the target may execute an 80386 near conditional jump (<c>0F 8x</c>). Off by default:
  /// the default target is an 8086, and without this the assembler has no idea what it is building
  /// for - it emitted the 386 encoding for every jump it could not reach in a byte and relied on
  /// relaxation to shrink it back, which cannot happen past 127 bytes.
  /// </summary>
  public bool Allow386Jcc { get; set; }

  /// <summary>
  /// Whether the target may execute an 80186 immediate-count shift or rotate (<c>C0</c>/<c>C1 /n ib</c>).
  /// Off by default, for the reason <see cref="Allow386Jcc"/> is: the default target is an 8086, whose
  /// group-2 forms are only the count-one <c>D0</c>/<c>D1</c> and the CL-count <c>D2</c>/<c>D3</c>.
  /// With this clear a multi-bit immediate count is emitted as that many count-one instructions.
  ///
  /// <para>
  /// The expansion is exact rather than approximate. Each of the eight operations is defined as its
  /// own single-bit step applied n times, so the result, CF and the carry chain <c>RCL</c>/<c>RCR</c>
  /// ride on are identical; OF is the only difference and the immediate form leaves it undefined for
  /// every count above one, so nothing conforming can observe it. The CL form is deliberately NOT
  /// used as a shortcut for a large count: it would put a CX clobber on an instruction whose caller
  /// asked for none. That trade needs register liveness and is therefore made a level up, in
  /// <c>InstructionSelector.SelectConstantShift</c>, which stages CL past four steps.
  /// </para>
  /// </summary>
  public bool Allow186ImmediateShifts { get; set; }

  /// <summary>
  /// Short-jump relaxation over the finished stream, iterated to fixpoint (each shrink can
  /// bring further jumps into short range). Every cut goes through the peephole's
  /// RemoveBytes, which slides all labels/fixups/relocations past it, so downstream offsets
  /// stay consistent. Only bound, internal targets relax; externals keep the near form.
  /// </summary>
  public void RunJumpRelaxation() {
    if (!this.EnableJumpRelaxation || this._jumpRelaxationRan)
      return;
    this._jumpRelaxationRan = true;

    bool changed;
    do {
      changed = false;
      for (var i = 0; i < this._fixups.Count; ++i) {
        var f = this._fixups[i];
        // a JMP to the very next instruction is a no-op: the arm-closing jump of an IF with no
        // ELSE, an ITERATE at the loop's last statement. Removing it leaves any label bound on
        // the jump sitting on its own destination, so nothing that branched here changes.
        if (f.Kind == FixupKind.Rel8 && f.Target.IsBound && !f.Target.IsExternal && f.Position >= 1
            && this._buffer[f.Position - 1] == 0xEB && f.Target.Position + f.Addend == f.Position + 1) {
          this.RemoveBytes(f.Position - 1, 2);
          changed = true;
          continue;
        }
        // the 8086 long-conditional pair folding back into the one short jump it stands in for
        if (f.Kind == FixupKind.Rel16Pair && f.Target.IsBound && !f.Target.IsExternal && f.Position >= 3) {
          var pairStart = f.Position - 3;
          var pairTarget = f.Target.Position + f.Addend;
          var pairEffective = pairTarget > pairStart + 2 ? pairTarget - 3 : pairTarget;
          var pairRel = pairEffective - (pairStart + 2);
          if (pairRel is >= sbyte.MinValue and <= sbyte.MaxValue) {
            this._buffer[pairStart] = (byte)(0x70 | ((this._buffer[pairStart] & 0x0F) ^ 1));   // un-invert
            this._fixups[i] = new(pairStart + 1, FixupKind.Rel8, f.Target, f.Addend);
            this.RemoveBytes(pairStart + 2, 3);
            changed = true;
          }
          continue;
        }
        if (f.Kind != FixupKind.Rel16 || !f.Target.IsBound || f.Target.IsExternal || f.Position < 1)
          continue;
        var op = this._buffer[f.Position - 1];
        var isJmp = op == 0xE9;
        var isJcc = !isJmp && f.Position >= 2 && this._buffer[f.Position - 2] == 0x0F && op is >= 0x80 and <= 0x8F;
        if (!isJmp && !isJcc)
          continue;
        var start = isJmp ? f.Position - 1 : f.Position - 2;   // instruction start
        var surplus = isJmp ? 1 : 2;                           // bytes the short form saves
        var target = f.Target.Position + f.Addend;
        var effective = target > start + 2 ? target - surplus : target;   // a forward target slides with the cut
        var rel = effective - (start + 2);                     // displacement from the SHORT encoding's end
        if (rel is < sbyte.MinValue or > sbyte.MaxValue)
          continue;
        this._buffer[start] = isJmp ? (byte)0xEB : (byte)(0x70 | (op & 0x0F));
        this._fixups[i] = new(start + 1, FixupKind.Rel8, f.Target, f.Addend);
        this.RemoveBytes(start + 2, surplus);                  // cut the tail; our new fixup sits before it
        changed = true;
      }
    } while (changed);
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
      case FixupKind.Rel16:
      case FixupKind.Rel16Pair: {
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

  /// <summary>The rel16 of the JMP inside an 8086 long-conditional pair; see <see cref="FixupKind"/>.</summary>
  private void EmitRel16Pair(Label target) {
    this._fixups.Add(new(this.Position, FixupKind.Rel16Pair, target, 0));
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
