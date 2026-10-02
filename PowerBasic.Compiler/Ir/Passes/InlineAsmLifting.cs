using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Turns x86 inline assembly into ordinary IR, so a program that uses it compiles on a machine that is
/// not an x86 - the 6502 - or on one whose back end does not take x86 text: x86-32, x64, C and LLVM.
/// x86-16 keeps its own path, which assembles the text and emulates what the CPU lacks.
///
/// <para>
/// Every x86 register is a module static: <c>asm.r0</c>..<c>asm.r7</c> hold <c>EAX</c>..<c>EDI</c>
/// as 32 bits (<c>AL</c>, <c>AH</c> and <c>AX</c> are views of them), <c>asm.mm0</c>..<c>asm.mm7</c> the
/// MMX registers as eight bytes, and <c>asm.v0</c>..<c>asm.v7</c> the vector registers as 64 bytes -
/// <c>XMMn</c> is the low sixteen of them, <c>YMMn</c> the low thirty-two. A static that one procedure
/// uses alone becomes a local (<see cref="LocalizeGlobals"/>) and then SSA (<see cref="Mem2Reg"/>), so
/// a back end with registers keeps it in one; at worst it stays in memory. A vector operation works
/// lane by lane on byte offsets into its register's static, every source lane read before any
/// destination lane is written, which is the snapshot semantics the hardware has.
/// </para>
/// <para>
/// The flags are statics too, one <c>i1</c> each: <c>asm.cf</c>, <c>asm.zf</c>, <c>asm.sf</c> and
/// <c>asm.of</c>, written by the arithmetic and logic instructions and read by <c>Jcc</c>,
/// <c>SETcc</c>, <c>CMOVcc</c>, <c>ADC</c> and <c>SBB</c>. A flag nothing reads is a dead store the
/// middle end removes, so modelling them costs nothing where they are not used. The parity and
/// auxiliary flags are not modelled, and a condition that reads parity is declined. A jump goes to a
/// BASIC label: the block is split at the assembly and ends in a branch to the label's block.
/// </para>
/// <para>
/// The stack and addressing through registers are not modelled: an instruction that needs either is
/// declined by name, never compiled into something else. The set lifted so far: <c>MOV</c>,
/// <c>ADD</c>, <c>ADC</c>, <c>SUB</c>, <c>SBB</c>, <c>CMP</c>, <c>AND</c>, <c>OR</c>, <c>XOR</c>,
/// <c>TEST</c>, <c>NOT</c>, <c>NEG</c>, <c>INC</c>, <c>DEC</c>, <c>SHL</c>/<c>SAL</c>, <c>SHR</c>,
/// <c>SAR</c>, <c>CLC</c>, <c>STC</c>, <c>CMC</c>, <c>JMP</c>, <c>Jcc</c>, <c>SETcc</c>, <c>CMOVcc</c>,
/// <c>NOP</c>; MMX
/// <c>MOVD</c>, <c>MOVQ</c>, <c>PADDB/W/D/Q</c>, <c>PSUBB/W/D/Q</c>, <c>PAND</c>, <c>PANDN</c>, <c>POR</c>,
/// <c>PXOR</c>, <c>PCMPEQB/W/D</c>, <c>PCMPGTB/W/D</c>, <c>PMULLW</c>, <c>PMULHW</c>, <c>PMADDWD</c>,
/// <c>PADDS</c>/<c>PSUBS</c>/<c>PADDUS</c>/<c>PSUBUS</c> on bytes and words, <c>PSLL</c>/<c>PSRL</c>
/// W/D/Q and <c>PSRAW</c>/<c>PSRAD</c> by an immediate or a register, <c>PUNPCKL</c>/<c>PUNPCKH</c>
/// BW/WD/DQ, <c>PACKSSWB</c>, <c>PACKSSDW</c>, <c>PACKUSWB</c>, <c>EMMS</c>; SSE's integer additions
/// <c>PMULHUW</c>, <c>PMINUB</c>, <c>PMAXUB</c>, <c>PMINSW</c>, <c>PMAXSW</c>, <c>PAVGB</c>,
/// <c>PAVGW</c>, <c>PSHUFW</c>; SSE2 <c>MOVDQA</c>/<c>MOVDQU</c>, the same packed operations on
/// <c>XMM</c>, <c>PUNPCKLQDQ</c>/<c>PUNPCKHQDQ</c>, <c>PSHUFD</c>, <c>PSHUFLW</c>, <c>PSHUFHW</c>;
/// SSSE3 <c>PSHUFB</c>. Under AVX and AVX2 every one of those has its VEX form - <c>VPADDB YMM0, YMM1,
/// YMM2</c> takes a separate first source, works on <c>XMM</c> or <c>YMM</c>, and zeroes the register
/// above what it writes - with the unpacks, packs and shuffles kept within each 128-bit half as the
/// hardware keeps them; AVX2's own <c>VEXTRACTI128</c>, <c>VINSERTI128</c>, <c>VPBROADCASTB/W/D/Q</c>,
/// <c>VPERMQ</c>, <c>VPERM2I128</c>, <c>VZEROUPPER</c> and <c>VZEROALL</c> complete the set.
/// </para>
/// </summary>
public static class InlineAsmLifting {

  /// <summary>
  /// Lifts every inline-assembly block in <paramref name="module"/>. False, with the instruction that
  /// stopped it, when a block holds something not lifted yet; the module is then left as it was for
  /// that function.
  /// </summary>
  public static bool TryRun(IrModule module, out string? declined) {
    ArgumentNullException.ThrowIfNull(module);
    declined = null;
    var registers = new Registers(module);
    foreach (var function in module.Functions.Where(function => function.HasInlineAsm && !function.IsDeclaration).ToList()) {
      foreach (var node in function.AllInstructions.OfType<IrInlineAsm>().ToList()) {
        var lifter = new BlockLifter(registers, node);
        foreach (var raw in node.Text.Split('\n')) {
          if (!lifter.TryLift(raw, out var why)) {
            declined = $"'{function.Name}': inline assembly {why}";
            return false;
          }
        }
        node.EraseFromParent();
      }
      function.HasInlineAsm = false;
    }
    return true;
  }

  /// <summary>The register statics, made on first use.</summary>
  private sealed class Registers(IrModule module) {
    private readonly Dictionary<string, IrGlobalVariable> _cells = [];

    public IrGlobalVariable General(Reg register) => this.Cell($"asm.r{register.WordSlot() & 7}", IrType.I32, 1);
    public IrGlobalVariable Mmx(Reg register) => this.Cell($"asm.mm{register.Index() & 7}", IrType.I8, 8);
    public IrGlobalVariable Vector(Reg register) => this.Cell($"asm.v{register.Index() & 7}", IrType.I8, 64);
    public IrGlobalVariable Flag(char flag) => this.Cell($"asm.{char.ToLowerInvariant(flag)}f", IrType.I1, 1);

    private IrGlobalVariable Cell(string name, IrType type, int count) {
      if (!this._cells.TryGetValue(name, out var cell))
        this._cells[name] = cell = module.FindGlobal(name) ?? module.AddGlobal(new IrGlobalVariable(name, type) { Count = count });
      return cell;
    }
  }

  /// <summary>Where an operand lives: a general register (with its width and byte position), a byte vector, or memory.</summary>
  private abstract record Place(int Bytes);
  private sealed record GeneralPlace(IrGlobalVariable Cell, int Bytes, int Shift) : Place(Bytes);
  private sealed record VectorPlace(IrValue Base, int Bytes) : Place(Bytes);
  private sealed record MemoryPlace(IrValue Address, int Bytes) : Place(Bytes);
  private sealed record ImmediatePlace(long Value) : Place(0);

  private sealed class BlockLifter(Registers registers, IrInlineAsm node) : IAsmSymbolResolver {

    private readonly Assembler _probe = new();
    private readonly Dictionary<Label, int> _labels = [];

    /// <summary>Set while a VEX-encoded instruction is lifted through its legacy name: three operands, and the register zeroed above what it writes.</summary>
    private bool _vex;

    /// <summary>A name the block binds is a memory operand at a label of its own, which maps back to the bound pointer.</summary>
    public bool TryResolve(string name, out AsmSymbol symbol) {
      var index = node.Names.FindIndex(bound => bound.Equals(name, StringComparison.OrdinalIgnoreCase));
      if (index < 0) {
        symbol = default;
        return false;
      }
      var label = this._labels.FirstOrDefault(pair => pair.Value == index).Key ?? this._probe.DefineLabel();
      this._labels[label] = index;
      // a BASIC label is a jump target, everything else a variable's storage
      symbol = node.GetOperand(index) is IrBlockAddress ? AsmSymbol.OfLabel(label) : AsmSymbol.OfMemory(Mem.At(label));
      return true;
    }

    private T Add<T>(T instruction) where T : IrInstruction => node.Parent!.InsertBefore(instruction, node);

    public bool TryLift(string raw, out string? why) {
      why = null;
      var line = raw;
      var comment = line.IndexOf(';');
      if (comment >= 0)
        line = line[..comment];
      line = line.Trim();
      if (line.Length == 0)
        return true;
      var space = line.IndexOfAny([' ', '\t']);
      var mnemonic = (space < 0 ? line : line[..space]).ToUpperInvariant();
      var operandText = space < 0 ? "" : line[(space + 1)..].Trim();
      IReadOnlyList<TextAssembler.ParsedAsmOperand> operands = [];
      if (operandText.Length > 0 && !new TextAssembler(this._probe).TryParseOperands(operandText, this, out operands, out var error)) {
        why = $"'{line}': {error}";
        return false;
      }
      try {
        if (this.Lift(mnemonic, operands))
          return true;
        why = $"'{mnemonic}' has no IR lifting yet";
        return false;
      } catch (NotLiftableException exception) {
        why = $"'{line}': {exception.Message}";
        return false;
      }
    }

    private sealed class NotLiftableException(string message) : Exception(message);

    private bool Lift(string mnemonic, IReadOnlyList<TextAssembler.ParsedAsmOperand> operands) {
      switch (mnemonic) {
        case "NOP" or "EMMS":
          return true;
        case "MOV": return this.Scalar(operands, null);
        case "ADD": return this.Scalar(operands, IrBinaryOp.Add);
        case "SUB": return this.Scalar(operands, IrBinaryOp.Sub);
        case "AND": return this.Scalar(operands, IrBinaryOp.And);
        case "OR": return this.Scalar(operands, IrBinaryOp.Or);
        case "XOR": return this.Scalar(operands, IrBinaryOp.Xor);
        case "CMP": return this.Scalar(operands, IrBinaryOp.Sub, write: false);
        case "TEST": return this.Scalar(operands, IrBinaryOp.And, write: false);
        case "ADC": return this.WithCarry(operands, IrBinaryOp.Add);
        case "SBB": return this.WithCarry(operands, IrBinaryOp.Sub);
        case "NOT": return this.Unary(operands, (v, type) => new IrBinary(IrBinaryOp.Xor, v, new IrConstantInt(type, -1)), null);
        case "NEG": return this.Unary(operands, (v, type) => new IrBinary(IrBinaryOp.Sub, new IrConstantInt(type, 0), v), FlagRule.Negate);
        case "INC": return this.Unary(operands, (v, type) => new IrBinary(IrBinaryOp.Add, v, new IrConstantInt(type, 1)), FlagRule.Increment);
        case "DEC": return this.Unary(operands, (v, type) => new IrBinary(IrBinaryOp.Sub, v, new IrConstantInt(type, 1)), FlagRule.Decrement);
        case "CLC": this.SetFlag('C', IrBuilder.ConstBool(false)); return true;
        case "STC": this.SetFlag('C', IrBuilder.ConstBool(true)); return true;
        case "CMC": this.SetFlag('C', this.Add(new IrBinary(IrBinaryOp.Xor, this.GetFlag('C'), IrBuilder.ConstBool(true)))); return true;
        case "JMP": return this.Jump(operands, null);
        case var jcc when jcc.StartsWith('J') && ConditionOf(jcc[1..]) is { } condition: return this.Jump(operands, condition);
        case var set when set.StartsWith("SET", StringComparison.Ordinal) && ConditionOf(set[3..]) is { } condition:
          return this.SetCondition(operands, condition);
        case var cmov when cmov.StartsWith("CMOV", StringComparison.Ordinal) && ConditionOf(cmov[4..]) is { } condition:
          return this.MoveIf(operands, condition);
        case "SHL" or "SAL": return this.Shift(operands, IrBinaryOp.Shl);
        case "SHR": return this.Shift(operands, IrBinaryOp.LShr);
        case "SAR": return this.Shift(operands, IrBinaryOp.AShr);
        case "MOVD": return this.MoveVector(operands, 4);
        case "MOVQ": return this.MoveVector(operands, 8);
        case "MOVDQA" or "MOVDQU": return this.MoveVector(operands, 0);
        case "PADDB": return this.Lanes(operands, 1, IrBinaryOp.Add);
        case "PADDW": return this.Lanes(operands, 2, IrBinaryOp.Add);
        case "PADDD": return this.Lanes(operands, 4, IrBinaryOp.Add);
        case "PADDQ": return this.Lanes(operands, 8, IrBinaryOp.Add);
        case "PSUBB": return this.Lanes(operands, 1, IrBinaryOp.Sub);
        case "PSUBW": return this.Lanes(operands, 2, IrBinaryOp.Sub);
        case "PSUBD": return this.Lanes(operands, 4, IrBinaryOp.Sub);
        case "PSUBQ": return this.Lanes(operands, 8, IrBinaryOp.Sub);
        case "PAND": return this.Lanes(operands, 8, IrBinaryOp.And);
        case "POR": return this.Lanes(operands, 8, IrBinaryOp.Or);
        case "PXOR": return this.Lanes(operands, 8, IrBinaryOp.Xor);
        case "PANDN": return this.AndNot(operands);
        case "PSHUFB": return this.Pshufb(operands);
        case "PCMPEQB": return this.LaneWise(operands, 1, (a, b, t) => this.Mask(IrCmpPred.Eq, a, b, t));
        case "PCMPEQW": return this.LaneWise(operands, 2, (a, b, t) => this.Mask(IrCmpPred.Eq, a, b, t));
        case "PCMPEQD": return this.LaneWise(operands, 4, (a, b, t) => this.Mask(IrCmpPred.Eq, a, b, t));
        case "PCMPGTB": return this.LaneWise(operands, 1, (a, b, t) => this.Mask(IrCmpPred.Sgt, a, b, t));
        case "PCMPGTW": return this.LaneWise(operands, 2, (a, b, t) => this.Mask(IrCmpPred.Sgt, a, b, t));
        case "PCMPGTD": return this.LaneWise(operands, 4, (a, b, t) => this.Mask(IrCmpPred.Sgt, a, b, t));
        case "PMULLW": return this.LaneWise(operands, 2, (a, b, t) => this.Add(new IrBinary(IrBinaryOp.Mul, a, b)));
        case "PMULHW": return this.LaneWise(operands, 2, (a, b, t) => this.HighProduct(a, b, IrCastOp.SExt));
        case "PMULHUW": return this.LaneWise(operands, 2, (a, b, t) => this.HighProduct(a, b, IrCastOp.ZExt));
        case "PADDSB": return this.LaneWise(operands, 1, (a, b, t) => this.Saturate(IrBinaryOp.Add, a, b, signed: true));
        case "PADDSW": return this.LaneWise(operands, 2, (a, b, t) => this.Saturate(IrBinaryOp.Add, a, b, signed: true));
        case "PSUBSB": return this.LaneWise(operands, 1, (a, b, t) => this.Saturate(IrBinaryOp.Sub, a, b, signed: true));
        case "PSUBSW": return this.LaneWise(operands, 2, (a, b, t) => this.Saturate(IrBinaryOp.Sub, a, b, signed: true));
        case "PADDUSB": return this.LaneWise(operands, 1, (a, b, t) => this.Saturate(IrBinaryOp.Add, a, b, signed: false));
        case "PADDUSW": return this.LaneWise(operands, 2, (a, b, t) => this.Saturate(IrBinaryOp.Add, a, b, signed: false));
        case "PSUBUSB": return this.LaneWise(operands, 1, (a, b, t) => this.Saturate(IrBinaryOp.Sub, a, b, signed: false));
        case "PSUBUSW": return this.LaneWise(operands, 2, (a, b, t) => this.Saturate(IrBinaryOp.Sub, a, b, signed: false));
        case "PMINUB": return this.LaneWise(operands, 1, (a, b, t) => this.Pick(IrCmpPred.Ult, a, b));
        case "PMAXUB": return this.LaneWise(operands, 1, (a, b, t) => this.Pick(IrCmpPred.Ugt, a, b));
        case "PMINSW": return this.LaneWise(operands, 2, (a, b, t) => this.Pick(IrCmpPred.Slt, a, b));
        case "PMAXSW": return this.LaneWise(operands, 2, (a, b, t) => this.Pick(IrCmpPred.Sgt, a, b));
        case "PAVGB": return this.LaneWise(operands, 1, (a, b, t) => this.Average(a, b));
        case "PAVGW": return this.LaneWise(operands, 2, (a, b, t) => this.Average(a, b));
        case "PSLLW": return this.PackedShift(operands, 2, IrBinaryOp.Shl);
        case "PSLLD": return this.PackedShift(operands, 4, IrBinaryOp.Shl);
        case "PSLLQ": return this.PackedShift(operands, 8, IrBinaryOp.Shl);
        case "PSRLW": return this.PackedShift(operands, 2, IrBinaryOp.LShr);
        case "PSRLD": return this.PackedShift(operands, 4, IrBinaryOp.LShr);
        case "PSRLQ": return this.PackedShift(operands, 8, IrBinaryOp.LShr);
        case "PSRAW": return this.PackedShift(operands, 2, IrBinaryOp.AShr);
        case "PSRAD": return this.PackedShift(operands, 4, IrBinaryOp.AShr);
        case "PUNPCKLBW": return this.Unpack(operands, 1, high: false);
        case "PUNPCKLWD": return this.Unpack(operands, 2, high: false);
        case "PUNPCKLDQ": return this.Unpack(operands, 4, high: false);
        case "PUNPCKLQDQ": return this.Unpack(operands, 8, high: false);
        case "PUNPCKHBW": return this.Unpack(operands, 1, high: true);
        case "PUNPCKHWD": return this.Unpack(operands, 2, high: true);
        case "PUNPCKHDQ": return this.Unpack(operands, 4, high: true);
        case "PUNPCKHQDQ": return this.Unpack(operands, 8, high: true);
        case "PACKSSWB": return this.Pack(operands, 2, signedResult: true);
        case "PACKSSDW": return this.Pack(operands, 4, signedResult: true);
        case "PACKUSWB": return this.Pack(operands, 2, signedResult: false);
        case "PMADDWD": return this.MultiplyAdd(operands);
        case "PSHUFD": return this.Shuffle(operands, 4, 0);
        case "PSHUFW": return this.Shuffle(operands, 2, 0);
        case "PSHUFLW": return this.Shuffle(operands, 2, 0);
        case "PSHUFHW": return this.Shuffle(operands, 2, 8);
        // AVX2's own, reached only through the V prefix below
        case "EXTRACTI128" when this._vex: return this.Extract128(operands);
        case "INSERTI128" when this._vex: return this.Insert128(operands);
        case "PBROADCASTB" when this._vex: return this.Broadcast(operands, 1);
        case "PBROADCASTW" when this._vex: return this.Broadcast(operands, 2);
        case "PBROADCASTD" when this._vex: return this.Broadcast(operands, 4);
        case "PBROADCASTQ" when this._vex: return this.Broadcast(operands, 8);
        case "PERMQ" when this._vex: return this.PermuteQuadwords(operands);
        case "PERM2I128" when this._vex: return this.PermuteHalves(operands);
        case "ZEROUPPER" when this._vex: return this.ZeroVectors(16);
        case "ZEROALL" when this._vex: return this.ZeroVectors(0);
        // a VEX form is its legacy instruction with a separate first source, and a vector register it
        // writes is zeroed above its own width, up to the widest register there is
        case ['V', .. var legacy] when !this._vex && (legacy.StartsWith('P') || legacy is "MOVD" or "MOVQ" or "MOVDQA" or "MOVDQU"
            or "EXTRACTI128" or "INSERTI128" or "ZEROUPPER" or "ZEROALL"):
          this._vex = true;
          try {
            if (!this.Lift(legacy, operands))
              return false;
          } finally {
            this._vex = false;
          }
          if (operands is [TextAssembler.ParsedAsmRegister first, ..] && this.PlaceOf(first, 0) is VectorPlace { Bytes: var written } vector)
            this.ZeroBytes(vector with { Bytes = 64 }, written, 64);
          return true;
        default:
          return false;
      }
    }

    // --- operands ---------------------------------------------------------------------------

    private static IrType Integer(int bytes) => IrType.Integer(bytes * 8);

    private Place PlaceOf(TextAssembler.ParsedAsmOperand operand, int defaultBytes) => operand switch {
      TextAssembler.ParsedAsmRegister { Register: var r } when r.IsByte()
        => new GeneralPlace(registers.General(r), 1, r.Index() >= 4 ? 8 : 0),
      TextAssembler.ParsedAsmRegister { Register: var r } when r.IsWord() && r.Index() is not (4 or 5)
        => new GeneralPlace(registers.General(r), 2, 0),
      TextAssembler.ParsedAsmRegister { Register: var r } when r.IsDword() && r.Index() is not (4 or 5)
        => new GeneralPlace(registers.General(r), 4, 0),
      TextAssembler.ParsedAsmRegister { Register: var r } when r.IsMmx() => new VectorPlace(registers.Mmx(r), 8),
      TextAssembler.ParsedAsmRegister { Register: var r } when r.IsXmm() => new VectorPlace(registers.Vector(r), 16),
      TextAssembler.ParsedAsmRegister { Register: var r } when r.IsYmm() => new VectorPlace(registers.Vector(r), 32),
      TextAssembler.ParsedAsmRegister { Register: var r } => throw new NotLiftableException($"register {r} is not lifted (the stack and frame pointers belong to the compiled code)"),
      TextAssembler.ParsedAsmImmediate { Value: var value } => new ImmediatePlace(value),
      TextAssembler.ParsedAsmMemory { Memory: var memory } => this.MemoryOf(memory, defaultBytes),
      _ => throw new NotLiftableException("this operand is not lifted"),
    };

    private MemoryPlace MemoryOf(Mem memory, int defaultBytes) {
      if (memory.Base is not null || memory.Index is not null || memory.Segment is not null || memory.Label is not Label label
          || !this._labels.TryGetValue(label, out var index))
        throw new NotLiftableException("only a BASIC variable, with a constant offset, is lifted as a memory operand");
      // a stated size, else the instruction's own (MOVQ reads eight bytes whatever the variable is),
      // else the variable's
      var bytes = memory.Size switch {
        OperandSize.Byte => 1, OperandSize.Word => 2, OperandSize.Dword => 4, OperandSize.Qword => 8,
        _ => defaultBytes != 0 ? defaultBytes : node.Sizes[index],
      };
      IrValue address = node.GetOperand(index);
      if (memory.Displacement != 0)
        address = this.Add(new IrGep(address, new IrConstantInt(IrType.I32, memory.Displacement)));
      return new MemoryPlace(address, bytes);
    }

    /// <summary>The value an operand holds, as an integer of <paramref name="bytes"/>.</summary>
    private IrValue Read(Place place, int bytes) {
      var type = Integer(bytes);
      switch (place) {
        case ImmediatePlace { Value: var value }:
          return new IrConstantInt(type, value);
        case GeneralPlace general: {
          IrValue whole = this.Add(new IrLoad(IrType.I32, general.Cell));
          if (general.Shift != 0)
            whole = this.Add(new IrBinary(IrBinaryOp.LShr, whole, new IrConstantInt(IrType.I32, general.Shift)));
          return bytes == 4 ? whole : this.Add(new IrCast(IrCastOp.Trunc, whole, type));
        }
        case VectorPlace vector:
          return this.Add(new IrLoad(type, vector.Base));
        case MemoryPlace memory:
          return this.Add(new IrLoad(type, memory.Address));
        default:
          throw new NotLiftableException("unreadable operand");
      }
    }

    /// <summary>Writes an integer of the place's width; a general register keeps its other bytes, as the hardware does for 8- and 16-bit writes.</summary>
    private void Write(Place place, IrValue value) {
      switch (place) {
        case GeneralPlace { Bytes: 4 } general:
          this.Add(new IrStore(value, general.Cell));
          return;
        case GeneralPlace general: {
          var keep = ~(((1L << (general.Bytes * 8)) - 1) << general.Shift) & 0xFFFFFFFFL;
          var old = this.Add(new IrLoad(IrType.I32, general.Cell));
          var kept = this.Add(new IrBinary(IrBinaryOp.And, old, new IrConstantInt(IrType.I32, keep)));
          IrValue part = this.Add(new IrCast(IrCastOp.ZExt, value, IrType.I32));
          if (general.Shift != 0)
            part = this.Add(new IrBinary(IrBinaryOp.Shl, part, new IrConstantInt(IrType.I32, general.Shift)));
          this.Add(new IrStore(this.Add(new IrBinary(IrBinaryOp.Or, kept, part)), general.Cell));
          return;
        }
        case VectorPlace vector:
          this.Add(new IrStore(value, vector.Base));
          return;
        case MemoryPlace memory:
          this.Add(new IrStore(value, memory.Address));
          return;
        default:
          throw new NotLiftableException("an immediate is not a destination");
      }
    }

    // --- general-purpose ----------------------------------------------------------------------

    /// <summary>
    /// A two-operand integer instruction: MOV when <paramref name="op"/> is null, else
    /// <c>destination = destination op source</c>. The width is the register's, or a variable's stated
    /// size, or - for an unsized variable - the other operand's.
    /// </summary>
    private bool Scalar(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, IrBinaryOp? op, bool write = true) {
      if (operands.Count != 2)
        throw new NotLiftableException("expects two operands");
      var destination = this.PlaceOf(operands[0], 0);
      if (destination is VectorPlace)
        return false;
      var source = this.PlaceOf(operands[1], destination.Bytes);
      if (source is VectorPlace)
        return false;
      var bytes = destination.Bytes != 0 ? destination.Bytes : source.Bytes;
      if (bytes == 0)
        throw new NotLiftableException("the operand size is not stated");
      if (destination is MemoryPlace memory)
        destination = memory with { Bytes = bytes };
      var value = this.Read(source, bytes);
      if (op is not { } binary) {
        this.Write(destination, value);
        return true;
      }
      var current = this.Read(destination, bytes);
      var result = this.Add(new IrBinary(binary, current, value));
      this.SetFlags(binary switch {
        IrBinaryOp.Add => FlagRule.Add,
        IrBinaryOp.Sub => FlagRule.Subtract,
        _ => FlagRule.Logic,
      }, current, value, result);
      if (write)
        this.Write(destination, result);
      return true;
    }

    // --- flags ----------------------------------------------------------------------------------

    private enum FlagRule { Add, Subtract, Logic, Increment, Decrement, Negate }

    private IrValue GetFlag(char flag) => this.Add(new IrLoad(IrType.I1, registers.Flag(flag)));
    private void SetFlag(char flag, IrValue value) => this.Add(new IrStore(value, registers.Flag(flag)));

    private IrValue Compare(IrCmpPred predicate, IrValue a, IrValue b) => this.Add(new IrCmp(predicate, a, b));

    /// <summary>ZF, SF, CF and OF of <c>a op b = result</c>, by x86's rules (PF and AF are not modelled).</summary>
    private void SetFlags(FlagRule rule, IrValue a, IrValue b, IrValue result) {
      var type = result.Type;
      var zero = new IrConstantInt(type, 0);
      this.SetFlag('Z', this.Compare(IrCmpPred.Eq, result, zero));
      this.SetFlag('S', this.Compare(IrCmpPred.Slt, result, zero));
      IrValue SignOf(IrValue v) => this.Compare(IrCmpPred.Slt, v, zero);
      IrValue Xor(IrValue x, IrValue y) => this.Add(new IrBinary(IrBinaryOp.Xor, x, y));
      IrValue And(IrValue x, IrValue y) => this.Add(new IrBinary(IrBinaryOp.And, x, y));
      switch (rule) {
        case FlagRule.Add:
          this.SetFlag('C', this.Compare(IrCmpPred.Ult, result, a));
          this.SetFlag('O', SignOf(And(Xor(a, result), Xor(b, result))));
          break;
        case FlagRule.Subtract:
          this.SetFlag('C', this.Compare(IrCmpPred.Ult, a, b));
          this.SetFlag('O', SignOf(And(Xor(a, b), Xor(a, result))));
          break;
        case FlagRule.Logic:
          this.SetFlag('C', IrBuilder.ConstBool(false));
          this.SetFlag('O', IrBuilder.ConstBool(false));
          break;
        case FlagRule.Increment:           // CF is left as it was
          this.SetFlag('O', this.Compare(IrCmpPred.Eq, result, new IrConstantInt(type, MinOf(type))));
          break;
        case FlagRule.Decrement:
          this.SetFlag('O', this.Compare(IrCmpPred.Eq, a, new IrConstantInt(type, MinOf(type))));
          break;
        case FlagRule.Negate:
          this.SetFlag('C', this.Compare(IrCmpPred.Ne, a, zero));
          this.SetFlag('O', this.Compare(IrCmpPred.Eq, a, new IrConstantInt(type, MinOf(type))));
          break;
      }
    }

    private static long MinOf(IrType type) => type.Bits >= 64 ? long.MinValue : -(1L << (type.Bits - 1));

    /// <summary>The x86 condition a <c>Jcc</c>/<c>SETcc</c>/<c>CMOVcc</c> suffix names, or null for an unknown one.</summary>
    private static Condition? ConditionOf(string suffix) => suffix switch {
      "O" => Condition.Overflow, "NO" => Condition.NotOverflow,
      "B" or "C" or "NAE" => Condition.Below, "AE" or "NB" or "NC" => Condition.AboveOrEqual,
      "E" or "Z" => Condition.Equal, "NE" or "NZ" => Condition.NotEqual,
      "BE" or "NA" => Condition.BelowOrEqual, "A" or "NBE" => Condition.Above,
      "S" => Condition.Sign, "NS" => Condition.NotSign,
      "P" or "PE" => Condition.Parity, "NP" or "PO" => Condition.NotParity,
      "L" or "NGE" => Condition.Less, "GE" or "NL" => Condition.GreaterOrEqual,
      "LE" or "NG" => Condition.LessOrEqual, "G" or "NLE" => Condition.Greater,
      _ => null,
    };

    /// <summary>The truth of <paramref name="condition"/> over the modelled flags.</summary>
    private IrValue Holds(Condition condition) {
      IrValue Not(IrValue v) => this.Add(new IrBinary(IrBinaryOp.Xor, v, IrBuilder.ConstBool(true)));
      IrValue Or(IrValue x, IrValue y) => this.Add(new IrBinary(IrBinaryOp.Or, x, y));
      IrValue SignNotOverflow() => this.Add(new IrBinary(IrBinaryOp.Xor, this.GetFlag('S'), this.GetFlag('O')));
      return condition switch {
        Condition.Overflow => this.GetFlag('O'),
        Condition.NotOverflow => Not(this.GetFlag('O')),
        Condition.Below => this.GetFlag('C'),
        Condition.AboveOrEqual => Not(this.GetFlag('C')),
        Condition.Equal => this.GetFlag('Z'),
        Condition.NotEqual => Not(this.GetFlag('Z')),
        Condition.BelowOrEqual => Or(this.GetFlag('C'), this.GetFlag('Z')),
        Condition.Above => Not(Or(this.GetFlag('C'), this.GetFlag('Z'))),
        Condition.Sign => this.GetFlag('S'),
        Condition.NotSign => Not(this.GetFlag('S')),
        Condition.Less => SignNotOverflow(),
        Condition.GreaterOrEqual => Not(SignNotOverflow()),
        Condition.LessOrEqual => Or(this.GetFlag('Z'), SignNotOverflow()),
        Condition.Greater => Not(Or(this.GetFlag('Z'), SignNotOverflow())),
        _ => throw new NotLiftableException("the parity flag is not modelled"),
      };
    }

    /// <summary>ADC/SBB: the operation with the carry flag folded in, its carry out computed one size wider.</summary>
    private bool WithCarry(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, IrBinaryOp op) {
      if (operands.Count != 2)
        throw new NotLiftableException("expects two operands");
      var destination = this.PlaceOf(operands[0], 0);
      var source = this.PlaceOf(operands[1], destination.Bytes);
      if (destination is VectorPlace || source is VectorPlace)
        return false;
      var bytes = destination.Bytes != 0 ? destination.Bytes : source.Bytes;
      if (bytes == 0 || bytes > 4)
        throw new NotLiftableException("the operand size is not stated");
      if (destination is MemoryPlace memory)
        destination = memory with { Bytes = bytes };
      var type = Integer(bytes);
      var wide = IrType.I64;
      var a = this.Read(destination, bytes);
      var b = this.Read(source, bytes);
      var carry = this.Add(new IrCast(IrCastOp.ZExt, this.GetFlag('C'), type));
      var result = this.Add(new IrBinary(op, this.Add(new IrBinary(op, a, b)), carry));
      // the carry out: the unsigned sum or difference, taken in 64 bits, leaves the operand's width
      var ua = this.Add(new IrCast(IrCastOp.ZExt, a, wide));
      var ub = this.Add(new IrCast(IrCastOp.ZExt, b, wide));
      var uc = this.Add(new IrCast(IrCastOp.ZExt, carry, wide));
      var exact = this.Add(new IrBinary(op, this.Add(new IrBinary(op, ua, ub)), uc));
      var limit = new IrConstantInt(wide, 1L << (bytes * 8));
      var carried = op == IrBinaryOp.Add
        ? this.Compare(IrCmpPred.Uge, exact, limit)
        : this.Compare(IrCmpPred.Slt, exact, new IrConstantInt(wide, 0));
      this.SetFlags(op == IrBinaryOp.Add ? FlagRule.Add : FlagRule.Subtract, a, b, result);
      this.SetFlag('C', carried);
      this.Write(destination, result);
      return true;
    }

    private bool SetCondition(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, Condition condition) {
      if (operands.Count != 1)
        throw new NotLiftableException("expects one operand");
      var place = this.PlaceOf(operands[0], 1);
      if (place is VectorPlace or ImmediatePlace)
        throw new NotLiftableException("SETcc writes a byte register or variable");
      this.Write(place is MemoryPlace memory ? memory with { Bytes = 1 } : place,
        this.Add(new IrCast(IrCastOp.ZExt, this.Holds(condition), IrType.I8)));
      return true;
    }

    private bool MoveIf(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, Condition condition) {
      if (operands.Count != 2)
        throw new NotLiftableException("expects two operands");
      var destination = this.PlaceOf(operands[0], 0);
      if (destination is not GeneralPlace { Bytes: 2 or 4 } general)
        throw new NotLiftableException("CMOVcc writes a 16- or 32-bit register");
      var source = this.PlaceOf(operands[1], general.Bytes);
      var chosen = this.Add(new IrSelect(this.Holds(condition), this.Read(source, general.Bytes), this.Read(destination, general.Bytes)));
      // a 32-bit CMOV writes its register whether or not it moves, as the hardware does
      this.Write(destination, chosen);
      return true;
    }

    /// <summary>
    /// JMP/Jcc to a BASIC label: the block ends here with a branch, and what followed the assembly
    /// continues in a block of its own, entered when a conditional jump is not taken.
    /// </summary>
    private bool Jump(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, Condition? condition) {
      if (operands is not [TextAssembler.ParsedAsmLabel { Label: var label }] || !this._labels.TryGetValue(label, out var index)
          || node.GetOperand(index) is not IrBlockAddress { Block: var target })
        throw new NotLiftableException("a jump is lifted only to a BASIC label");
      var taken = condition is { } c ? this.Holds(c) : null;
      var block = node.Parent!;
      var function = block.Parent!;
      var rest = function.CreateBlock(block.Label + ".asm");
      var moving = block.Instructions.SkipWhile(instruction => !ReferenceEquals(instruction, node)).ToList();
      foreach (var instruction in moving) {
        block.Remove(instruction);
        rest.Append(instruction);
      }
      // the successors' phis now see the moved terminator's new block
      foreach (var successor in rest.Successors.Distinct())
        foreach (var phi in successor.Phis)
          phi.RenameIncomingBlock(block, rest);
      if (taken is null)
        block.Append(new IrBr(target));
      else
        block.Append(new IrCondBr(taken, target, rest));
      return true;
    }

    private bool Unary(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, Func<IrValue, IrType, IrInstruction> apply, FlagRule? flags) {
      if (operands.Count != 1)
        throw new NotLiftableException("expects one operand");
      var place = this.PlaceOf(operands[0], 0);
      if (place.Bytes == 0 || place is VectorPlace)
        throw new NotLiftableException("the operand size is not stated");
      var value = this.Read(place, place.Bytes);
      var result = this.Add(apply(value, Integer(place.Bytes)));
      if (flags is { } rule)            // NOT changes no flag
        this.SetFlags(rule, value, value, result);
      this.Write(place, result);
      return true;
    }

    private bool Shift(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, IrBinaryOp op) {
      if (operands.Count != 2)
        throw new NotLiftableException("expects two operands");
      var place = this.PlaceOf(operands[0], 0);
      if (place.Bytes == 0 || place is VectorPlace)
        throw new NotLiftableException("the operand size is not stated");
      var type = Integer(place.Bytes);
      // the count is taken modulo 32, as every x86 since the 286 does
      IrValue count = operands[1] switch {
        TextAssembler.ParsedAsmImmediate { Value: var immediate } => new IrConstantInt(type, immediate & 31),
        TextAssembler.ParsedAsmRegister { Register: Reg.CL } => this.Add(new IrBinary(IrBinaryOp.And,
          this.Widen(this.Read(new GeneralPlace(registers.General(Reg.CL), 1, 0), 1), type), new IrConstantInt(type, 31))),
        _ => throw new NotLiftableException("a shift count is an immediate or CL"),
      };
      var value = this.Read(place, place.Bytes);
      // a count at or past the width leaves zeros (or the sign) - SHL/SHR/SAR of an 8- or 16-bit
      // operand by up to 31 - where the IR's shift would be undefined
      var bits = place.Bytes * 8;
      IrValue shifted = this.Add(new IrBinary(op, value, count));
      if (bits < 32) {
        var tooFar = this.Add(new IrCmp(IrCmpPred.Uge, count, new IrConstantInt(type, bits)));
        IrValue saturated = op == IrBinaryOp.AShr
          ? this.Add(new IrBinary(IrBinaryOp.AShr, value, new IrConstantInt(type, bits - 1)))
          : new IrConstantInt(type, 0);
        shifted = this.Add(new IrSelect(tooFar, saturated, shifted));
      }
      this.Write(place, shifted);
      return true;
    }

    private IrValue Widen(IrValue value, IrType type)
      => value.Type.Bits == type.Bits ? value : this.Add(new IrCast(IrCastOp.ZExt, value, type));

    // --- vectors --------------------------------------------------------------------------------

    /// <summary>A byte offset into a vector or memory operand.</summary>
    private IrValue LaneAddress(Place place, int offset) {
      var basePointer = place switch {
        VectorPlace vector => vector.Base,
        MemoryPlace memory => memory.Address,
        _ => throw new NotLiftableException("not a vector operand"),
      };
      return offset == 0 ? basePointer : this.Add(new IrGep(basePointer, new IrConstantInt(IrType.I32, offset)));
    }

    /// <summary>
    /// MOVD/MOVQ/MOVDQA/MOVDQU and their VEX forms: <paramref name="bytes"/> from source to destination,
    /// or the vector register's whole width when <paramref name="bytes"/> is 0. A vector register
    /// written with less than its width is zeroed above what was written, as the hardware zeroes it
    /// (a VEX form zeroes on up to the widest register, which <see cref="Lift"/> does after it).
    /// </summary>
    private bool MoveVector(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, int bytes) {
      if (operands.Count != 2)
        throw new NotLiftableException("expects two operands");
      var destination = this.PlaceOf(operands[0], bytes);
      var source = this.PlaceOf(operands[1], bytes);
      if (destination is not VectorPlace && source is not VectorPlace)
        return false;
      if (bytes == 0)
        bytes = destination is VectorPlace { Bytes: var to } ? to : ((VectorPlace)source).Bytes;
      var chunk = Math.Min(bytes, 8);
      var values = new List<IrValue>();
      for (var offset = 0; offset < bytes; offset += chunk)
        values.Add(source is GeneralPlace ? this.Read(source, chunk) : this.Add(new IrLoad(Integer(chunk), this.LaneAddress(source, offset))));
      for (var i = 0; i < values.Count; ++i) {
        if (destination is GeneralPlace)
          this.Write(destination, values[i]);
        else
          this.Add(new IrStore(values[i], this.LaneAddress(destination, i * chunk)));
      }
      if (destination is VectorPlace vector)
        this.ZeroBytes(vector, bytes, vector.Bytes);
      return true;
    }

    private void ZeroBytes(VectorPlace vector, int from, int to) {
      for (var offset = from; offset < to; offset += 4)
        this.Add(new IrStore(new IrConstantInt(IrType.I32, 0), this.LaneAddress(vector, offset)));
    }

    /// <summary>
    /// The operands of a packed operation: the vector destination and the two it computes from - the
    /// destination itself and the source, or under a VEX encoding the second and third operands.
    /// </summary>
    private (Place Destination, Place Left, Place Right, int Width) Packed(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands) {
      var count = this._vex ? 3 : 2;
      if (operands.Count != count)
        throw new NotLiftableException($"expects {count} operands");
      var destination = this.PlaceOf(operands[0], 0);
      if (destination is not VectorPlace { Bytes: var width })
        throw new NotLiftableException("a packed operation writes a vector register");
      var left = this._vex ? this.PlaceOf(operands[1], width) : destination;
      var right = this.PlaceOf(operands[count - 1], width);
      if (left is not (VectorPlace or MemoryPlace) || right is not (VectorPlace or MemoryPlace))
        throw new NotLiftableException("a packed operation reads registers or memory");
      return (destination, left, right, width);
    }

    /// <summary>The 128-bit blocks a vector operation keeps apart (an MMX register is one block of eight bytes).</summary>
    private static int BlockOf(int width) => Math.Min(width, 16);

    /// <summary>Reads every lane of both operands, then writes every result: the hardware's snapshot semantics.</summary>
    private bool Lanes(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, int lane, IrBinaryOp op, bool invertDestination = false)
      => this.LaneWise(operands, lane, (a, b, type) => this.Add(new IrBinary(op,
        invertDestination ? this.Add(new IrBinary(IrBinaryOp.Xor, a, new IrConstantInt(type, -1))) : a, b)));

    /// <summary>A lane-by-lane operation over the snapshot of both operands.</summary>
    private bool LaneWise(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, int lane, Func<IrValue, IrValue, IrType, IrValue> apply) {
      var (destination, leftPlace, rightPlace, width) = this.Packed(operands);
      var type = Integer(lane);
      var (left, right) = (this.LoadLanes(leftPlace, type, lane, width), this.LoadLanes(rightPlace, type, lane, width));
      var results = left.Select((a, i) => apply(a, right[i], type)).ToList();
      this.StoreLanes(destination, results, lane);
      return true;
    }

    private List<IrValue> LoadLanes(Place place, IrType type, int lane, int width, int start = 0) {
      var lanes = new List<IrValue>();
      for (var offset = start; offset < start + width; offset += lane)
        lanes.Add(this.Add(new IrLoad(type, this.LaneAddress(place, offset))));
      return lanes;
    }

    private void StoreLanes(Place place, IReadOnlyList<IrValue> lanes, int lane) {
      for (var i = 0; i < lanes.Count; ++i)
        this.Add(new IrStore(lanes[i], this.LaneAddress(place, i * lane)));
    }

    private IrValue Widen(IrValue value, IrCastOp op, IrType type) => this.Add(new IrCast(op, value, type));
    private IrValue Narrow(IrValue value, IrType type) => this.Add(new IrCast(IrCastOp.Trunc, value, type));

    /// <summary>All ones where the comparison holds, zero where it does not.</summary>
    private IrValue Mask(IrCmpPred predicate, IrValue a, IrValue b, IrType type)
      => this.Add(new IrSelect(this.Compare(predicate, a, b), new IrConstantInt(type, -1), new IrConstantInt(type, 0)));

    private IrValue Pick(IrCmpPred predicate, IrValue a, IrValue b) => this.Add(new IrSelect(this.Compare(predicate, a, b), a, b));

    /// <summary>The upper half of a word product, the words taken as signed or unsigned.</summary>
    private IrValue HighProduct(IrValue a, IrValue b, IrCastOp extend) {
      var product = this.Add(new IrBinary(IrBinaryOp.Mul, this.Widen(a, extend, IrType.I32), this.Widen(b, extend, IrType.I32)));
      return this.Narrow(this.Add(new IrBinary(IrBinaryOp.LShr, product, new IrConstantInt(IrType.I32, 16))), a.Type);
    }

    /// <summary>A sum or difference clamped to the lane's signed or unsigned range, computed in 32 bits.</summary>
    private IrValue Saturate(IrBinaryOp op, IrValue a, IrValue b, bool signed) {
      var extend = signed ? IrCastOp.SExt : IrCastOp.ZExt;
      var exact = this.Add(new IrBinary(op, this.Widen(a, extend, IrType.I32), this.Widen(b, extend, IrType.I32)));
      return this.Clamp(exact, a.Type, signed);
    }

    private IrValue Clamp(IrValue exact, IrType lane, bool signed) {
      var bits = lane.Bits;
      var (low, high) = signed ? (-(1L << (bits - 1)), (1L << (bits - 1)) - 1) : (0L, (1L << bits) - 1);
      var lowConstant = new IrConstantInt(exact.Type, low);
      var highConstant = new IrConstantInt(exact.Type, high);
      var floored = this.Add(new IrSelect(this.Compare(IrCmpPred.Slt, exact, lowConstant), lowConstant, exact));
      var clamped = this.Add(new IrSelect(this.Compare(IrCmpPred.Sgt, floored, highConstant), highConstant, floored));
      return this.Narrow(clamped, lane);
    }

    /// <summary>PAVGB/PAVGW: the unsigned mean, rounded up.</summary>
    private IrValue Average(IrValue a, IrValue b) {
      var sum = this.Add(new IrBinary(IrBinaryOp.Add,
        this.Add(new IrBinary(IrBinaryOp.Add, this.Widen(a, IrCastOp.ZExt, IrType.I32), this.Widen(b, IrCastOp.ZExt, IrType.I32))),
        new IrConstantInt(IrType.I32, 1)));
      return this.Narrow(this.Add(new IrBinary(IrBinaryOp.LShr, sum, new IrConstantInt(IrType.I32, 1))), a.Type);
    }

    /// <summary>
    /// PSLL/PSRL/PSRA by an immediate or by the low quadword of a register or memory operand. A count
    /// past the lane's width empties a logical shift and fills an arithmetic one with the sign.
    /// </summary>
    private bool PackedShift(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, int lane, IrBinaryOp op) {
      var count = this._vex ? 3 : 2;
      if (operands.Count != count)
        throw new NotLiftableException($"expects {count} operands");
      var destination = this.PlaceOf(operands[0], 0);
      if (destination is not VectorPlace { Bytes: var width })
        throw new NotLiftableException("a packed shift writes a vector register");
      var source = this._vex ? this.PlaceOf(operands[1], width) : destination;
      if (source is not (VectorPlace or MemoryPlace))
        throw new NotLiftableException("a packed shift reads a register or memory");
      var countPlace = this.PlaceOf(operands[count - 1], 16);
      var type = Integer(lane);
      var bits = lane * 8;
      var lanes = this.LoadLanes(source, type, lane, width);
      IrValue amount = countPlace switch {
        ImmediatePlace { Value: var value } => new IrConstantInt(IrType.I64, value & 0xFF),
        VectorPlace or MemoryPlace => this.Add(new IrLoad(IrType.I64, this.LaneAddress(countPlace, 0))),
        _ => throw new NotLiftableException("a packed shift counts by an immediate, a register or memory"),
      };
      // past the width the count saturates: bits - 1 for an arithmetic shift, an empty lane otherwise
      var outOfRange = this.Compare(IrCmpPred.Ugt, amount, new IrConstantInt(IrType.I64, bits - 1));
      var inRange = this.Add(new IrSelect(outOfRange, new IrConstantInt(IrType.I64, bits - 1), amount));
      var by = lane == 8 ? inRange : this.Narrow(inRange, type);
      var results = lanes.Select(IrValue (value) => {
        var shifted = this.Add(new IrBinary(op, value, by));
        return op == IrBinaryOp.AShr ? shifted : this.Add(new IrSelect(outOfRange, new IrConstantInt(type, 0), shifted));
      }).ToList();
      this.StoreLanes(destination, results, lane);
      return true;
    }

    /// <summary>PUNPCKL/PUNPCKH: within each 128-bit block, the lanes of one half of each operand, interleaved left first.</summary>
    private bool Unpack(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, int lane, bool high) {
      var (destination, leftPlace, rightPlace, width) = this.Packed(operands);
      if (lane == 8 && width < 16)
        throw new NotLiftableException("PUNPCKLQDQ/PUNPCKHQDQ take XMM or YMM registers");
      var type = Integer(lane);
      var block = BlockOf(width);
      var results = new List<IrValue>();
      for (var start = 0; start < width; start += block) {
        var half = start + (high ? block / 2 : 0);
        var left = this.LoadLanes(leftPlace, type, lane, block / 2, half);
        var right = this.LoadLanes(rightPlace, type, lane, block / 2, half);
        for (var i = 0; i < left.Count; ++i) {
          results.Add(left[i]);
          results.Add(right[i]);
        }
      }
      this.StoreLanes(destination, results, lane);
      return true;
    }

    /// <summary>PACKSSWB/PACKSSDW/PACKUSWB: within each 128-bit block, each operand's lanes narrowed to half their width with saturation, left first.</summary>
    private bool Pack(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, int lane, bool signedResult) {
      var (destination, leftPlace, rightPlace, width) = this.Packed(operands);
      var type = Integer(lane);
      var narrow = Integer(lane / 2);
      var block = BlockOf(width);
      var results = new List<IrValue>();
      for (var start = 0; start < width; start += block) {
        var wide = this.LoadLanes(leftPlace, type, lane, block, start).Concat(this.LoadLanes(rightPlace, type, lane, block, start));
        results.AddRange(wide.Select(value => this.Clamp(this.Widen(value, IrCastOp.SExt, IrType.I64), narrow, signedResult)));
      }
      this.StoreLanes(destination, results, lane / 2);
      return true;
    }

    /// <summary>PMADDWD: signed word products, adjacent pairs summed into doublewords.</summary>
    private bool MultiplyAdd(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands) {
      var (destination, leftPlace, rightPlace, width) = this.Packed(operands);
      var left = this.LoadLanes(leftPlace, IrType.I16, 2, width);
      var right = this.LoadLanes(rightPlace, IrType.I16, 2, width);
      IrValue Product(int i) => this.Add(new IrBinary(IrBinaryOp.Mul,
        this.Widen(left[i], IrCastOp.SExt, IrType.I32), this.Widen(right[i], IrCastOp.SExt, IrType.I32)));
      var results = new List<IrValue>();
      for (var i = 0; i < left.Count; i += 2)
        results.Add(this.Add(new IrBinary(IrBinaryOp.Add, Product(i), Product(i + 1))));
      this.StoreLanes(destination, results, 4);
      return true;
    }

    /// <summary>
    /// PSHUFD/PSHUFW/PSHUFLW/PSHUFHW: within each 128-bit block, four lanes of the source chosen by the
    /// immediate's bit pairs, written at <paramref name="start"/> bytes into the block; PSHUFLW and
    /// PSHUFHW copy the other half unchanged.
    /// </summary>
    private bool Shuffle(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, int lane, int start) {
      var (destination, source, order, width) = this.WithImmediate(operands);
      var type = Integer(lane);
      var all = this.LoadLanes(source, type, lane, width);
      var results = new List<IrValue>(all);
      var perBlock = BlockOf(width) / lane;
      for (var block = 0; block < all.Count; block += perBlock) {
        var first = block + start / lane;
        for (var i = 0; i < 4; ++i)
          results[first + i] = all[first + (int)((order >> (2 * i)) & 3)];
      }
      this.StoreLanes(destination, results, lane);
      return true;
    }

    /// <summary>A vector destination, a register or memory source and an immediate - the shape of the shuffles and permutes.</summary>
    private (VectorPlace Destination, Place Source, long Immediate, int Width) WithImmediate(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands) {
      if (operands.Count != 3 || this.PlaceOf(operands[2], 1) is not ImmediatePlace { Value: var immediate })
        throw new NotLiftableException("expects a register, a source and an immediate");
      if (this.PlaceOf(operands[0], 0) is not VectorPlace { Bytes: var width } destination)
        throw new NotLiftableException("writes a vector register");
      var source = this.PlaceOf(operands[1], width);
      if (source is not (VectorPlace or MemoryPlace))
        throw new NotLiftableException("reads a register or memory");
      return (destination, source, immediate, width);
    }

    private bool AndNot(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands)
      => this.Lanes(operands, 8, IrBinaryOp.And, invertDestination: true);

    /// <summary>
    /// PSHUFB: each destination byte takes the byte of its own 128-bit block that its mask byte selects
    /// (the low three or four bits), or zero when the mask byte's top bit is set.
    /// </summary>
    private bool Pshufb(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands) {
      var (destination, valuePlace, maskPlace, width) = this.Packed(operands);
      var values = this.LoadLanes(valuePlace, IrType.I8, 1, width);
      var masks = this.LoadLanes(maskPlace, IrType.I8, 1, width);
      var block = BlockOf(width);
      var results = new List<IrValue>();
      for (var i = 0; i < width; ++i) {
        // select the indexed byte with a chain of selects over the snapshot - no table in memory, so
        // it stays a pure function of the loaded lanes
        var first = i / block * block;
        var index = this.Add(new IrBinary(IrBinaryOp.And, masks[i], new IrConstantInt(IrType.I8, block - 1)));
        var picked = values[first];
        for (var k = 1; k < block; ++k) {
          var hit = this.Add(new IrCmp(IrCmpPred.Eq, index, new IrConstantInt(IrType.I8, k)));
          picked = this.Add(new IrSelect(hit, values[first + k], picked));
        }
        var zeroed = this.Add(new IrCmp(IrCmpPred.Slt, masks[i], new IrConstantInt(IrType.I8, 0)));
        results.Add(this.Add(new IrSelect(zeroed, new IrConstantInt(IrType.I8, 0), picked)));
      }
      this.StoreLanes(destination, results, 1);
      return true;
    }

    // --- AVX2's own instructions, each reached through its V prefix ---------------------------------

    /// <summary>VEXTRACTI128: the 128-bit half of a YMM register the immediate names.</summary>
    private bool Extract128(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands) {
      if (operands.Count != 3 || this.PlaceOf(operands[2], 1) is not ImmediatePlace { Value: var half })
        throw new NotLiftableException("expects a destination, a YMM register and an immediate");
      var source = this.PlaceOf(operands[1], 32);
      if (source is not VectorPlace { Bytes: 32 })
        throw new NotLiftableException("extracts from a YMM register");
      var destination = this.PlaceOf(operands[0], 16);
      if (destination is not (VectorPlace or MemoryPlace))
        throw new NotLiftableException("writes an XMM register or memory");
      var lanes = this.LoadLanes(source, IrType.I64, 8, 16, (int)(half & 1) * 16);
      this.StoreLanes(destination, lanes, 8);
      return true;
    }

    /// <summary>VINSERTI128: the second operand with the half the immediate names replaced by the third.</summary>
    private bool Insert128(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands) {
      if (operands.Count != 4 || this.PlaceOf(operands[3], 1) is not ImmediatePlace { Value: var half })
        throw new NotLiftableException("expects a YMM destination, a YMM source, a 128-bit source and an immediate");
      if (this.PlaceOf(operands[0], 32) is not VectorPlace { Bytes: 32 } destination
          || this.PlaceOf(operands[1], 32) is not VectorPlace { Bytes: 32 } source)
        throw new NotLiftableException("inserts into YMM registers");
      var insert = this.PlaceOf(operands[2], 16);
      if (insert is not (VectorPlace or MemoryPlace))
        throw new NotLiftableException("inserts a register or memory");
      var lanes = this.LoadLanes(source, IrType.I64, 8, 32);
      var replacement = this.LoadLanes(insert, IrType.I64, 8, 16);
      var at = (int)(half & 1) * 2;
      lanes[at] = replacement[0];
      lanes[at + 1] = replacement[1];
      this.StoreLanes(destination, lanes, 8);
      return true;
    }

    /// <summary>VPBROADCASTB/W/D/Q: the source's lowest lane in every lane of the destination.</summary>
    private bool Broadcast(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, int lane) {
      if (operands.Count != 2 || this.PlaceOf(operands[0], 0) is not VectorPlace { Bytes: var width } destination)
        throw new NotLiftableException("expects a vector destination and a source");
      var source = this.PlaceOf(operands[1], lane);
      if (source is not (VectorPlace or MemoryPlace))
        throw new NotLiftableException("broadcasts from an XMM register or memory");
      var value = this.Add(new IrLoad(Integer(lane), this.LaneAddress(source, 0)));
      this.StoreLanes(destination, Enumerable.Repeat(value, width / lane).ToList(), lane);
      return true;
    }

    /// <summary>VPERMQ: four quadwords of the source in the order the immediate's bit pairs give, across the whole register.</summary>
    private bool PermuteQuadwords(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands) {
      var (destination, source, order, width) = this.WithImmediate(operands);
      if (width != 32)
        throw new NotLiftableException("permutes a YMM register");
      var all = this.LoadLanes(source, IrType.I64, 8, 32);
      this.StoreLanes(destination, Enumerable.Range(0, 4).Select(i => all[(int)((order >> (2 * i)) & 3)]).ToList(), 8);
      return true;
    }

    /// <summary>VPERM2I128: each half of the destination one of the four halves of the two sources, or zero.</summary>
    private bool PermuteHalves(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands) {
      if (operands.Count != 4 || this.PlaceOf(operands[3], 1) is not ImmediatePlace { Value: var order })
        throw new NotLiftableException("expects a YMM destination, two sources and an immediate");
      if (this.PlaceOf(operands[0], 32) is not VectorPlace { Bytes: 32 } destination)
        throw new NotLiftableException("writes a YMM register");
      var halves = new List<IrValue>();
      foreach (var operand in new[] { operands[1], operands[2] }) {
        var place = this.PlaceOf(operand, 32);
        if (place is not (VectorPlace or MemoryPlace))
          throw new NotLiftableException("reads YMM registers or memory");
        halves.AddRange(this.LoadLanes(place, IrType.I64, 8, 32));
      }
      var results = new List<IrValue>();
      for (var half = 0; half < 2; ++half) {
        var control = (int)(order >> (4 * half)) & 0xF;
        for (var q = 0; q < 2; ++q)
          results.Add((control & 8) != 0 ? new IrConstantInt(IrType.I64, 0) : halves[(control & 3) * 2 + q]);
      }
      this.StoreLanes(destination, results, 8);
      return true;
    }

    /// <summary>VZEROUPPER/VZEROALL: every vector register cleared above its low 128 bits, or entirely.</summary>
    private bool ZeroVectors(int from) {
      for (var index = 0; index < 8; ++index)
        this.ZeroBytes(new VectorPlace(registers.Vector((Reg)((int)Reg.XMM0 + index)), 64), from, 64);
      return true;
    }
  }
}
