namespace PowerBasic.Compiler.Backend.Mos6502;

/// <summary>The documented NMOS 6502 mnemonics.</summary>
public enum M6502Op {
  Adc, And, Asl, Bcc, Bcs, Beq, Bit, Bmi, Bne, Bpl, Brk, Bvc, Bvs, Clc, Cld, Cli, Clv, Cmp, Cpx, Cpy,
  Dec, Dex, Dey, Eor, Inc, Inx, Iny, Jmp, Jsr, Lda, Ldx, Ldy, Lsr, Nop, Ora, Pha, Php, Pla, Plp, Rol,
  Ror, Rti, Rts, Sbc, Sec, Sed, Sei, Sta, Stx, Sty, Tax, Tay, Tsx, Txa, Txs, Tya,
}

/// <summary>The 6502's addressing modes.</summary>
public enum M6502Mode {
  Implied, Accumulator, Immediate, ZeroPage, ZeroPageX, ZeroPageY, Absolute, AbsoluteX, AbsoluteY,
  Indirect, IndexedIndirectX, IndirectIndexedY, Relative,
}

/// <summary>
/// The opcode matrix: which (mnemonic, mode) pairs exist and the byte each encodes to. A pair that
/// is not in the table does not exist on the chip, and asking for it is an error rather than a guess -
/// the classic trap is <c>STX abs,X</c>, which reads plausibly and is not an instruction.
/// </summary>
public static class M6502Isa {

  private static readonly Dictionary<(M6502Op, M6502Mode), byte> _opcodes = Build();

  /// <summary>The opcode byte of <paramref name="op"/> in <paramref name="mode"/>.</summary>
  public static byte Opcode(M6502Op op, M6502Mode mode)
    => _opcodes.TryGetValue((op, mode), out var opcode)
      ? opcode
      : throw new ArgumentException($"6502 has no {op.ToString().ToUpperInvariant()} in {mode} mode");

  /// <summary>Whether the pair is an instruction.</summary>
  public static bool Exists(M6502Op op, M6502Mode mode) => _opcodes.ContainsKey((op, mode));

  /// <summary>The operand bytes <paramref name="mode"/> carries after the opcode.</summary>
  public static int OperandBytes(M6502Mode mode) => mode switch {
    M6502Mode.Implied or M6502Mode.Accumulator => 0,
    M6502Mode.Immediate or M6502Mode.ZeroPage or M6502Mode.ZeroPageX or M6502Mode.ZeroPageY
      or M6502Mode.IndexedIndirectX or M6502Mode.IndirectIndexedY or M6502Mode.Relative => 1,
    M6502Mode.Absolute or M6502Mode.AbsoluteX or M6502Mode.AbsoluteY or M6502Mode.Indirect => 2,
    _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
  };

  /// <summary>The conditional branches, each with the one that takes the opposite path.</summary>
  public static M6502Op Inverse(M6502Op branch) => branch switch {
    M6502Op.Bcc => M6502Op.Bcs, M6502Op.Bcs => M6502Op.Bcc,
    M6502Op.Beq => M6502Op.Bne, M6502Op.Bne => M6502Op.Beq,
    M6502Op.Bmi => M6502Op.Bpl, M6502Op.Bpl => M6502Op.Bmi,
    M6502Op.Bvc => M6502Op.Bvs, M6502Op.Bvs => M6502Op.Bvc,
    _ => throw new ArgumentException($"{branch} is not a conditional branch", nameof(branch)),
  };

  public static bool IsBranch(M6502Op op) => op is M6502Op.Bcc or M6502Op.Bcs or M6502Op.Beq or M6502Op.Bne
    or M6502Op.Bmi or M6502Op.Bpl or M6502Op.Bvc or M6502Op.Bvs;

  private static Dictionary<(M6502Op, M6502Mode), byte> Build() {
    var table = new Dictionary<(M6502Op, M6502Mode), byte>();
    void Add(M6502Op op, params (M6502Mode Mode, byte Opcode)[] modes) {
      foreach (var (mode, opcode) in modes)
        table.Add((op, mode), opcode);
    }

    // the eight "group one" ALU instructions share a layout: aaa bbb 01
    (M6502Mode, byte)[] GroupOne(byte baseOpcode, bool store = false) {
      var modes = new List<(M6502Mode, byte)> {
        (M6502Mode.IndexedIndirectX, (byte)(baseOpcode + 0x01)), (M6502Mode.ZeroPage, (byte)(baseOpcode + 0x05)),
        (M6502Mode.Absolute, (byte)(baseOpcode + 0x0D)), (M6502Mode.IndirectIndexedY, (byte)(baseOpcode + 0x11)),
        (M6502Mode.ZeroPageX, (byte)(baseOpcode + 0x15)), (M6502Mode.AbsoluteY, (byte)(baseOpcode + 0x19)),
        (M6502Mode.AbsoluteX, (byte)(baseOpcode + 0x1D)),
      };
      if (!store)
        modes.Add((M6502Mode.Immediate, (byte)(baseOpcode + 0x09)));
      return [.. modes];
    }
    Add(M6502Op.Ora, GroupOne(0x00));
    Add(M6502Op.And, GroupOne(0x20));
    Add(M6502Op.Eor, GroupOne(0x40));
    Add(M6502Op.Adc, GroupOne(0x60));
    Add(M6502Op.Sta, GroupOne(0x80, store: true));
    Add(M6502Op.Lda, GroupOne(0xA0));
    Add(M6502Op.Cmp, GroupOne(0xC0));
    Add(M6502Op.Sbc, GroupOne(0xE0));

    // the shifts and rotates, and INC/DEC, which have no immediate and index by X only
    (M6502Mode, byte)[] ReadModifyWrite(byte baseOpcode, bool accumulator) {
      var modes = new List<(M6502Mode, byte)> {
        (M6502Mode.ZeroPage, (byte)(baseOpcode + 0x06)), (M6502Mode.Absolute, (byte)(baseOpcode + 0x0E)),
        (M6502Mode.ZeroPageX, (byte)(baseOpcode + 0x16)), (M6502Mode.AbsoluteX, (byte)(baseOpcode + 0x1E)),
      };
      if (accumulator)
        modes.Add((M6502Mode.Accumulator, (byte)(baseOpcode + 0x0A)));
      return [.. modes];
    }
    Add(M6502Op.Asl, ReadModifyWrite(0x00, accumulator: true));
    Add(M6502Op.Rol, ReadModifyWrite(0x20, accumulator: true));
    Add(M6502Op.Lsr, ReadModifyWrite(0x40, accumulator: true));
    Add(M6502Op.Ror, ReadModifyWrite(0x60, accumulator: true));
    Add(M6502Op.Dec, ReadModifyWrite(0xC0, accumulator: false));
    Add(M6502Op.Inc, ReadModifyWrite(0xE0, accumulator: false));

    Add(M6502Op.Ldx, (M6502Mode.Immediate, 0xA2), (M6502Mode.ZeroPage, 0xA6), (M6502Mode.Absolute, 0xAE),
      (M6502Mode.ZeroPageY, 0xB6), (M6502Mode.AbsoluteY, 0xBE));
    Add(M6502Op.Ldy, (M6502Mode.Immediate, 0xA0), (M6502Mode.ZeroPage, 0xA4), (M6502Mode.Absolute, 0xAC),
      (M6502Mode.ZeroPageX, 0xB4), (M6502Mode.AbsoluteX, 0xBC));
    Add(M6502Op.Stx, (M6502Mode.ZeroPage, 0x86), (M6502Mode.Absolute, 0x8E), (M6502Mode.ZeroPageY, 0x96));
    Add(M6502Op.Sty, (M6502Mode.ZeroPage, 0x84), (M6502Mode.Absolute, 0x8C), (M6502Mode.ZeroPageX, 0x94));
    Add(M6502Op.Cpx, (M6502Mode.Immediate, 0xE0), (M6502Mode.ZeroPage, 0xE4), (M6502Mode.Absolute, 0xEC));
    Add(M6502Op.Cpy, (M6502Mode.Immediate, 0xC0), (M6502Mode.ZeroPage, 0xC4), (M6502Mode.Absolute, 0xCC));
    Add(M6502Op.Bit, (M6502Mode.ZeroPage, 0x24), (M6502Mode.Absolute, 0x2C));
    Add(M6502Op.Jmp, (M6502Mode.Absolute, 0x4C), (M6502Mode.Indirect, 0x6C));
    Add(M6502Op.Jsr, (M6502Mode.Absolute, 0x20));

    Add(M6502Op.Bpl, (M6502Mode.Relative, 0x10));
    Add(M6502Op.Bmi, (M6502Mode.Relative, 0x30));
    Add(M6502Op.Bvc, (M6502Mode.Relative, 0x50));
    Add(M6502Op.Bvs, (M6502Mode.Relative, 0x70));
    Add(M6502Op.Bcc, (M6502Mode.Relative, 0x90));
    Add(M6502Op.Bcs, (M6502Mode.Relative, 0xB0));
    Add(M6502Op.Bne, (M6502Mode.Relative, 0xD0));
    Add(M6502Op.Beq, (M6502Mode.Relative, 0xF0));

    foreach (var (op, opcode) in (ReadOnlySpan<(M6502Op, byte)>)[
        (M6502Op.Brk, 0x00), (M6502Op.Php, 0x08), (M6502Op.Clc, 0x18), (M6502Op.Plp, 0x28), (M6502Op.Sec, 0x38),
        (M6502Op.Rti, 0x40), (M6502Op.Pha, 0x48), (M6502Op.Cli, 0x58), (M6502Op.Rts, 0x60), (M6502Op.Pla, 0x68),
        (M6502Op.Sei, 0x78), (M6502Op.Dey, 0x88), (M6502Op.Txa, 0x8A), (M6502Op.Tya, 0x98), (M6502Op.Txs, 0x9A),
        (M6502Op.Tay, 0xA8), (M6502Op.Tax, 0xAA), (M6502Op.Clv, 0xB8), (M6502Op.Tsx, 0xBA), (M6502Op.Iny, 0xC8),
        (M6502Op.Dex, 0xCA), (M6502Op.Cld, 0xD8), (M6502Op.Inx, 0xE8), (M6502Op.Nop, 0xEA), (M6502Op.Sed, 0xF8),
      ])
      table.Add((op, M6502Mode.Implied), opcode);
    return table;
  }
}
