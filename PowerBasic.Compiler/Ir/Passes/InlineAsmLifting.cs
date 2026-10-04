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
/// <c>XMMn</c> is the low sixteen of them, <c>YMMn</c> the low thirty-two, <c>ZMMn</c> all of them. A static that one procedure
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
/// <c>PUSH</c> and <c>POP</c> work on a stack of the assembly's own (<c>asm.stack</c>, 256 bytes that
/// wrap, with <c>asm.sp</c>), never the compiled code's, so a block may push and pop as it likes and
/// a value pushed in one block may be popped in the next; <c>PUSHF</c>/<c>POPF</c> carry the four
/// modelled flags. Addressing through registers is not modelled: an instruction that needs it is
/// declined by name, never compiled into something else. The set lifted so far: <c>MOV</c>,
/// <c>ADD</c>, <c>ADC</c>, <c>SUB</c>, <c>SBB</c>, <c>CMP</c>, <c>AND</c>, <c>OR</c>, <c>XOR</c>,
/// <c>TEST</c>, <c>NOT</c>, <c>NEG</c>, <c>INC</c>, <c>DEC</c>, <c>SHL</c>/<c>SAL</c>, <c>SHR</c>,
/// <c>SAR</c>, <c>MUL</c>, <c>IMUL</c> (one, two and three operands), <c>MOVZX</c>, <c>MOVSX</c>,
/// <c>XCHG</c>, <c>BSWAP</c>, <c>BSF</c>, <c>BSR</c>, <c>CBW</c>, <c>CWDE</c>, <c>CWD</c>, <c>CDQ</c>,
/// <c>DIV</c> and <c>IDIV</c> (a divide error is BASIC's error 11), <c>POPCNT</c>, <c>LZCNT</c>,
/// <c>TZCNT</c>, BMI1's <c>ANDN</c>, <c>BLSI</c>, <c>BLSR</c>, <c>BLSMSK</c> and BMI2's <c>BZHI</c>,
/// <c>PDEP</c>, <c>PEXT</c>, <c>SHLX</c>, <c>SHRX</c>, <c>SARX</c>, <c>RORX</c>, <c>MULX</c>, <c>CLC</c>, <c>STC</c>, <c>CMC</c>, <c>JMP</c>,
/// <c>Jcc</c>, <c>SETcc</c>, <c>CMOVcc</c>, <c>PUSH</c>, <c>POP</c>, <c>PUSHF(D)</c>, <c>POPF(D)</c>,
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
/// <c>VPERMQ</c>, <c>VPERM2I128</c>, <c>VZEROUPPER</c> and <c>VZEROALL</c> complete the set. AVX-512's
/// <c>ZMM</c> registers take the same packed operations at 512 bits through the same path, with
/// <c>VEXTRACTI32X4</c>/<c>I64X2</c>/<c>I64X4</c>/<c>I32X8</c>, the matching inserts and the
/// <c>VMOVDQA32</c>/<c>VMOVDQU8</c>-style moves; the mask registers <c>k0</c>-<c>k7</c> are not lifted.
/// </para>
/// <para>
/// Floating point: SSE and SSE2 <c>ADD</c>/<c>SUB</c>/<c>MUL</c>/<c>DIV</c>/<c>MIN</c>/<c>MAX</c>/<c>SQRT</c>
/// in their <c>PS</c>, <c>SS</c>, <c>PD</c> and <c>SD</c> forms, <c>AND</c>/<c>ANDN</c>/<c>OR</c>/<c>XOR</c>
/// <c>PS</c>/<c>PD</c>, <c>MOVAPS</c>/<c>MOVUPS</c>/<c>MOVAPD</c>/<c>MOVUPD</c>, <c>MOVSS</c>, <c>MOVSD</c>,
/// <c>CVTSI2SS</c>/<c>SD</c>, <c>CVT(T)SS2SI</c>/<c>SD2SI</c>, <c>CVTSS2SD</c>, <c>CVTSD2SS</c>,
/// <c>CVTDQ2PS</c>, <c>CVT(T)PS2DQ</c>, <c>(U)COMISS</c>/<c>SD</c>, and the VEX form of each. Exceptions
/// are masked, as the hardware's default leaves them: a zero divisor answers a signed infinity or the
/// default NaN and a negative root the default NaN, computed as bits so that a machine whose floats
/// are BASIC's never divides by zero. That machine is the 6502, and what it still does differently is
/// stated rather than hidden: its floats have no infinity, NaN or denormal to take IN, an overflow is
/// error 6, and a result too small for the format is zero.
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

  /// <summary>The bytes of the stack PUSH and POP work on - the assembly's own, apart from the compiled code's, wrapping at its end.</summary>
  private const int StackBytes = 256;

  /// <summary>The register statics, made on first use.</summary>
  private sealed class Registers(IrModule module) {
    private readonly Dictionary<string, IrGlobalVariable> _cells = [];

    public IrModule Module => module;

    public IrGlobalVariable General(Reg register) => this.Cell($"asm.r{register.WordSlot() & 7}", IrType.I32, 1);
    public IrGlobalVariable Mmx(Reg register) => this.Cell($"asm.mm{register.Index() & 7}", IrType.I8, 8);
    public IrGlobalVariable Vector(Reg register) => this.Cell($"asm.v{register.Index() & 7}", IrType.I8, 64);
    public IrGlobalVariable Flag(char flag) => this.Cell($"asm.{char.ToLowerInvariant(flag)}f", IrType.I1, 1);
    public IrGlobalVariable Stack() => this.Cell("asm.stack", IrType.I8, StackBytes);
    public IrGlobalVariable StackPointer() => this.Cell("asm.sp", IrType.I32, 1);

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
        case "MOVZX": return this.Extend(operands, IrCastOp.ZExt);
        case "MOVSX": return this.Extend(operands, IrCastOp.SExt);
        case "XCHG": return this.Exchange(operands);
        case "BSWAP": return this.ByteSwap(operands);
        case "MUL": return this.WideMultiply(operands, IrCastOp.ZExt);
        case "IMUL" when operands.Count == 1: return this.WideMultiply(operands, IrCastOp.SExt);
        case "IMUL": return this.TruncatingMultiply(operands);
        case "CBW": return this.SignExtendAccumulator(1, into: null);
        case "CWDE": return this.SignExtendAccumulator(2, into: null);
        case "CWD": return this.SignExtendAccumulator(2, into: Reg.EDX);
        case "CDQ": return this.SignExtendAccumulator(4, into: Reg.EDX);
        case "DIV": return this.Divide(operands, signed: false);
        case "IDIV": return this.Divide(operands, signed: true);
        case "POPCNT": return this.Count(operands, Counting.Population);
        case "LZCNT": return this.Count(operands, Counting.LeadingZeros);
        case "TZCNT": return this.Count(operands, Counting.TrailingZeros);
        case "ANDN": return this.ThreeOperand(operands, (a, b, t) => this.Add(new IrBinary(IrBinaryOp.And, this.Add(new IrBinary(IrBinaryOp.Xor, a, new IrConstantInt(t, -1))), b)), FlagRule.Logic);
        case "BLSI": return this.LowestBit(operands, (v, t) => this.Add(new IrBinary(IrBinaryOp.And, v, this.Add(new IrBinary(IrBinaryOp.Sub, new IrConstantInt(t, 0), v)))), carryWhenZero: false);
        case "BLSR": return this.LowestBit(operands, (v, t) => this.Add(new IrBinary(IrBinaryOp.And, v, this.Add(new IrBinary(IrBinaryOp.Sub, v, new IrConstantInt(t, 1))))), carryWhenZero: true);
        case "BLSMSK": return this.LowestBit(operands, (v, t) => this.Add(new IrBinary(IrBinaryOp.Xor, v, this.Add(new IrBinary(IrBinaryOp.Sub, v, new IrConstantInt(t, 1))))), carryWhenZero: true);
        case "BZHI": return this.ZeroHighBits(operands);
        case "PDEP": return this.ThreeOperand(operands, (a, b, t) => this.Deposit(a, b), null);
        case "PEXT": return this.ThreeOperand(operands, (a, b, t) => this.Extract(a, b), null);
        case "SHLX": return this.ThreeOperand(operands, (a, b, t) => this.Add(new IrBinary(IrBinaryOp.Shl, a, this.ShiftCount(b))), null);
        case "SHRX": return this.ThreeOperand(operands, (a, b, t) => this.Add(new IrBinary(IrBinaryOp.LShr, a, this.ShiftCount(b))), null);
        case "SARX": return this.ThreeOperand(operands, (a, b, t) => this.Add(new IrBinary(IrBinaryOp.AShr, a, this.ShiftCount(b))), null);
        case "RORX": return this.RotateRight(operands);
        case "MULX": return this.MultiplyNoFlags(operands);
        case "BSF": return this.BitScan(operands, forward: true);
        case "BSR": return this.BitScan(operands, forward: false);
        case "INT" when operands is [{ } interruptNumber]: return this.Interrupt(interruptNumber);
        case "PUSH": return this.Push(operands);
        case "POP": return this.Pop(operands);
        case "PUSHF": this.PushValue(this.PackFlags(IrType.I16)); return true;
        case "PUSHFD": this.PushValue(this.PackFlags(IrType.I32)); return true;
        case "POPF": this.UnpackFlags(this.PopValue(IrType.I16)); return true;
        case "POPFD": this.UnpackFlags(this.PopValue(IrType.I32)); return true;
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
        case "ADDPS": return this.FloatLanes(operands, IrType.F32, scalar: false, (a, b) => this.Add(new IrBinary(IrBinaryOp.FAdd, a, b)));
        case "SUBPS": return this.FloatLanes(operands, IrType.F32, scalar: false, (a, b) => this.Add(new IrBinary(IrBinaryOp.FSub, a, b)));
        case "MULPS": return this.FloatLanes(operands, IrType.F32, scalar: false, (a, b) => this.Add(new IrBinary(IrBinaryOp.FMul, a, b)));
        case "DIVPS": return this.FloatLanes(operands, IrType.F32, scalar: false, this.Divide);
        case "MINPS": return this.FloatLanes(operands, IrType.F32, scalar: false, (a, b) => this.Pick(IrCmpPred.Folt, a, b));
        case "MAXPS": return this.FloatLanes(operands, IrType.F32, scalar: false, (a, b) => this.Pick(IrCmpPred.Fogt, a, b));
        case "ADDSS": return this.FloatLanes(operands, IrType.F32, scalar: true, (a, b) => this.Add(new IrBinary(IrBinaryOp.FAdd, a, b)));
        case "SUBSS": return this.FloatLanes(operands, IrType.F32, scalar: true, (a, b) => this.Add(new IrBinary(IrBinaryOp.FSub, a, b)));
        case "MULSS": return this.FloatLanes(operands, IrType.F32, scalar: true, (a, b) => this.Add(new IrBinary(IrBinaryOp.FMul, a, b)));
        case "DIVSS": return this.FloatLanes(operands, IrType.F32, scalar: true, this.Divide);
        case "MINSS": return this.FloatLanes(operands, IrType.F32, scalar: true, (a, b) => this.Pick(IrCmpPred.Folt, a, b));
        case "MAXSS": return this.FloatLanes(operands, IrType.F32, scalar: true, (a, b) => this.Pick(IrCmpPred.Fogt, a, b));
        case "ADDPD": return this.FloatLanes(operands, IrType.F64, scalar: false, (a, b) => this.Add(new IrBinary(IrBinaryOp.FAdd, a, b)));
        case "SUBPD": return this.FloatLanes(operands, IrType.F64, scalar: false, (a, b) => this.Add(new IrBinary(IrBinaryOp.FSub, a, b)));
        case "MULPD": return this.FloatLanes(operands, IrType.F64, scalar: false, (a, b) => this.Add(new IrBinary(IrBinaryOp.FMul, a, b)));
        case "DIVPD": return this.FloatLanes(operands, IrType.F64, scalar: false, this.Divide);
        case "MINPD": return this.FloatLanes(operands, IrType.F64, scalar: false, (a, b) => this.Pick(IrCmpPred.Folt, a, b));
        case "MAXPD": return this.FloatLanes(operands, IrType.F64, scalar: false, (a, b) => this.Pick(IrCmpPred.Fogt, a, b));
        case "ADDSD": return this.FloatLanes(operands, IrType.F64, scalar: true, (a, b) => this.Add(new IrBinary(IrBinaryOp.FAdd, a, b)));
        case "SUBSD": return this.FloatLanes(operands, IrType.F64, scalar: true, (a, b) => this.Add(new IrBinary(IrBinaryOp.FSub, a, b)));
        case "MULSD": return this.FloatLanes(operands, IrType.F64, scalar: true, (a, b) => this.Add(new IrBinary(IrBinaryOp.FMul, a, b)));
        case "DIVSD": return this.FloatLanes(operands, IrType.F64, scalar: true, this.Divide);
        case "MINSD": return this.FloatLanes(operands, IrType.F64, scalar: true, (a, b) => this.Pick(IrCmpPred.Folt, a, b));
        case "MAXSD": return this.FloatLanes(operands, IrType.F64, scalar: true, (a, b) => this.Pick(IrCmpPred.Fogt, a, b));
        case "SQRTPS": return this.SquareRoot(operands, IrType.F32, scalar: false);
        case "SQRTSS": return this.SquareRoot(operands, IrType.F32, scalar: true);
        case "SQRTPD": return this.SquareRoot(operands, IrType.F64, scalar: false);
        case "SQRTSD": return this.SquareRoot(operands, IrType.F64, scalar: true);
        case "ANDPS" or "ANDPD": return this.Lanes(operands, 8, IrBinaryOp.And);
        case "ORPS" or "ORPD": return this.Lanes(operands, 8, IrBinaryOp.Or);
        case "XORPS" or "XORPD": return this.Lanes(operands, 8, IrBinaryOp.Xor);
        case "ANDNPS" or "ANDNPD": return this.AndNot(operands);
        case "MOVAPS" or "MOVUPS" or "MOVAPD" or "MOVUPD": return this.MoveVector(operands, 0);
        case "MOVSS": return this.MoveScalar(operands, 4);
        case "MOVSD" when operands.Count > 0: return this.MoveScalar(operands, 8);
        case "CVTSI2SS": return this.FromInteger(operands, IrType.F32);
        case "CVTSI2SD": return this.FromInteger(operands, IrType.F64);
        case "CVTTSS2SI": return this.ToInteger(operands, IrType.F32, IrCastOp.FPToSI);
        case "CVTTSD2SI": return this.ToInteger(operands, IrType.F64, IrCastOp.FPToSI);
        case "CVTSS2SI": return this.ToInteger(operands, IrType.F32, IrCastOp.FPToSIRound);
        case "CVTSD2SI": return this.ToInteger(operands, IrType.F64, IrCastOp.FPToSIRound);
        case "CVTSS2SD": return this.Reformat(operands, IrType.F32, IrType.F64);
        case "CVTSD2SS": return this.Reformat(operands, IrType.F64, IrType.F32);
        case "CVTDQ2PS": return this.ConvertLanes(operands, IrType.I32, IrType.F32, IrCastOp.SIToFP);
        case "CVTTPS2DQ": return this.ConvertLanes(operands, IrType.F32, IrType.I32, IrCastOp.FPToSI);
        case "CVTPS2DQ": return this.ConvertLanes(operands, IrType.F32, IrType.I32, IrCastOp.FPToSIRound);
        case "COMISS" or "UCOMISS": return this.CompareScalar(operands, IrType.F32);
        case "COMISD" or "UCOMISD": return this.CompareScalar(operands, IrType.F64);
        // AVX2's own, reached only through the V prefix below
        case "EXTRACTI128" when this._vex: return this.ExtractPart(operands, 16, 32);
        case "INSERTI128" when this._vex: return this.InsertPart(operands, 16, 32);
        case "EXTRACTI32X4" or "EXTRACTI64X2" when this._vex: return this.ExtractPart(operands, 16, 64);
        case "EXTRACTI64X4" or "EXTRACTI32X8" when this._vex: return this.ExtractPart(operands, 32, 64);
        case "INSERTI32X4" or "INSERTI64X2" when this._vex: return this.InsertPart(operands, 16, 64);
        case "INSERTI64X4" or "INSERTI32X8" when this._vex: return this.InsertPart(operands, 32, 64);
        case "MOVDQA32" or "MOVDQA64" or "MOVDQU8" or "MOVDQU16" or "MOVDQU32" or "MOVDQU64" when this._vex: return this.MoveVector(operands, 0);
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
            or "EXTRACTI128" or "INSERTI128" or "ZEROUPPER" or "ZEROALL"
            || legacy.StartsWith("EXTRACTI", StringComparison.Ordinal) || legacy.StartsWith("INSERTI", StringComparison.Ordinal)
            || legacy.StartsWith("MOVDQ", StringComparison.Ordinal)
            || legacy.EndsWith("PS", StringComparison.Ordinal) || legacy.EndsWith("PD", StringComparison.Ordinal)
            || legacy.EndsWith("SS", StringComparison.Ordinal) || legacy.EndsWith("SD", StringComparison.Ordinal)
            || legacy.StartsWith("CVT", StringComparison.Ordinal) || legacy.Contains("COMIS", StringComparison.Ordinal)):
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
      TextAssembler.ParsedAsmRegister { Register: var r } when r.IsZmm() => new VectorPlace(registers.Vector(r), 64),
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

    /// <summary>The general register or memory operand of a one-operand instruction, with its stated width.</summary>
    private Place SizedOperand(TextAssembler.ParsedAsmOperand operand) {
      var place = this.PlaceOf(operand, 0);
      if (place is VectorPlace or ImmediatePlace || place.Bytes is not (1 or 2 or 4))
        throw new NotLiftableException("takes a general register or a sized variable");
      return place;
    }

    /// <summary>MOVZX/MOVSX: a byte or word widened into a 16- or 32-bit register.</summary>
    private bool Extend(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, IrCastOp extend) {
      if (operands.Count != 2 || this.PlaceOf(operands[0], 0) is not GeneralPlace { Bytes: 2 or 4 } destination)
        throw new NotLiftableException("widens into a 16- or 32-bit register");
      var source = this.SizedOperand(operands[1]);
      if (source.Bytes >= destination.Bytes)
        throw new NotLiftableException("widens a narrower operand");
      this.Write(destination, this.Widen(this.Read(source, source.Bytes), extend, Integer(destination.Bytes)));
      return true;
    }

    private bool Exchange(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands) {
      if (operands.Count != 2)
        throw new NotLiftableException("expects two operands");
      var first = this.PlaceOf(operands[0], 0);
      var second = this.PlaceOf(operands[1], first.Bytes);
      var bytes = first.Bytes != 0 ? first.Bytes : second.Bytes;
      if (first is VectorPlace or ImmediatePlace || second is VectorPlace or ImmediatePlace || bytes is not (1 or 2 or 4))
        throw new NotLiftableException("exchanges general registers or a register and a variable");
      if (first is MemoryPlace m1) first = m1 with { Bytes = bytes };
      if (second is MemoryPlace m2) second = m2 with { Bytes = bytes };
      var (a, b) = (this.Read(first, bytes), this.Read(second, bytes));
      this.Write(first, b);
      this.Write(second, a);
      return true;
    }

    private bool ByteSwap(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands) {
      if (operands.Count != 1 || this.PlaceOf(operands[0], 0) is not GeneralPlace { Bytes: 4 } place)
        throw new NotLiftableException("swaps a 32-bit register");
      var value = this.Read(place, 4);
      IrValue result = new IrConstantInt(IrType.I32, 0);
      for (var i = 0; i < 4; ++i) {
        var octet = this.Add(new IrBinary(IrBinaryOp.And, this.Add(new IrBinary(IrBinaryOp.LShr, value, new IrConstantInt(IrType.I32, 8 * i))),
          new IrConstantInt(IrType.I32, 0xFF)));
        result = this.Add(new IrBinary(IrBinaryOp.Or, result, this.Add(new IrBinary(IrBinaryOp.Shl, octet, new IrConstantInt(IrType.I32, 8 * (3 - i))))));
      }
      this.Write(place, result);
      return true;
    }

    /// <summary>
    /// MUL and one-operand IMUL: AL, AX or EAX times the operand, the double-width product in AX, DX:AX
    /// or EDX:EAX; CF and OF say whether the upper half is more than the lower half's extension.
    /// </summary>
    private bool WideMultiply(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, IrCastOp extend) {
      if (operands.Count != 1)
        throw new NotLiftableException("expects one operand");
      var source = this.SizedOperand(operands[0]);
      var bytes = source.Bytes;
      var accumulator = new GeneralPlace(registers.General(Reg.EAX), bytes, 0);
      var wide = Integer(bytes * 2);
      var product = this.Add(new IrBinary(IrBinaryOp.Mul,
        this.Widen(this.Read(accumulator, bytes), extend, wide), this.Widen(this.Read(source, bytes), extend, wide)));
      var low = this.Narrow(product, Integer(bytes));
      var high = this.Narrow(this.Add(new IrBinary(IrBinaryOp.LShr, product, new IrConstantInt(wide, bytes * 8))), Integer(bytes));
      if (bytes == 1)
        this.Write(new GeneralPlace(registers.General(Reg.EAX), 2, 0), product);
      else {
        this.Write(accumulator, low);
        this.Write(new GeneralPlace(registers.General(Reg.EDX), bytes, 0), high);
      }
      var spill = this.Compare(IrCmpPred.Ne, product, this.Widen(low, extend, wide));
      this.SetFlag('C', spill);
      this.SetFlag('O', spill);
      return true;
    }

    /// <summary>Two- and three-operand IMUL: the product truncated to the destination; CF and OF say whether it fitted.</summary>
    private bool TruncatingMultiply(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands) {
      if (operands.Count is not (2 or 3) || this.PlaceOf(operands[0], 0) is not GeneralPlace { Bytes: 2 or 4 } destination)
        throw new NotLiftableException("multiplies into a 16- or 32-bit register");
      var bytes = destination.Bytes;
      var (left, right) = operands.Count == 2
        ? (this.Read(destination, bytes), this.Read(this.PlaceOf(operands[1], bytes), bytes))
        : (this.Read(this.PlaceOf(operands[1], bytes), bytes), this.Read(this.PlaceOf(operands[2], bytes), bytes));
      var wide = Integer(bytes * 2);
      var product = this.Add(new IrBinary(IrBinaryOp.Mul, this.Widen(left, IrCastOp.SExt, wide), this.Widen(right, IrCastOp.SExt, wide)));
      var low = this.Narrow(product, Integer(bytes));
      var spill = this.Compare(IrCmpPred.Ne, product, this.Widen(low, IrCastOp.SExt, wide));
      this.Write(destination, low);
      this.SetFlag('C', spill);
      this.SetFlag('O', spill);
      return true;
    }

    /// <summary>
    /// BSF/BSR: the index of the lowest or highest set bit, and ZF clear; a zero source sets ZF and
    /// leaves the destination as it was, which is what the processors do whatever the manual reserves.
    /// </summary>
    private bool BitScan(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, bool forward) {
      if (operands.Count != 2 || this.PlaceOf(operands[0], 0) is not GeneralPlace { Bytes: 2 or 4 } destination)
        throw new NotLiftableException("scans into a 16- or 32-bit register");
      var bytes = destination.Bytes;
      var type = Integer(bytes);
      var source = this.Read(this.PlaceOf(operands[1], bytes), bytes);
      var zero = this.Compare(IrCmpPred.Eq, source, new IrConstantInt(type, 0));
      // the scan as a chain of selects, checked from the far end so the nearest set bit wins
      IrValue index = this.Read(destination, bytes);
      var bits = bytes * 8;
      for (var step = 0; step < bits; ++step) {
        var bit = forward ? bits - 1 - step : step;
        var mask = new IrConstantInt(type, bit == 63 ? long.MinValue : 1L << bit);
        var set = this.Compare(IrCmpPred.Ne, this.Add(new IrBinary(IrBinaryOp.And, source, mask)), new IrConstantInt(type, 0));
        index = this.Add(new IrSelect(set, new IrConstantInt(type, bit), index));
      }
      this.Write(destination, index);
      this.SetFlag('Z', zero);
      return true;
    }

    /// <summary>
    /// DIV/IDIV: AX, DX:AX or EDX:EAX divided by the operand, quotient and remainder in AL/AH, AX/DX or
    /// EAX/EDX. A zero divisor or a quotient too wide for its register is the processor's divide error,
    /// which a PowerBASIC program reports as error 11.
    /// </summary>
    private bool Divide(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, bool signed) {
      if (operands.Count != 1)
        throw new NotLiftableException("expects one operand");
      var source = this.SizedOperand(operands[0]);
      var bytes = source.Bytes;
      var narrow = Integer(bytes);
      var wide = Integer(bytes * 2);
      var extend = signed ? IrCastOp.SExt : IrCastOp.ZExt;
      var divisor = this.Read(source, bytes);
      IrValue dividend;
      if (bytes == 1)
        dividend = this.Read(new GeneralPlace(registers.General(Reg.EAX), 2, 0), 2);
      else {
        var high = this.Widen(this.Read(new GeneralPlace(registers.General(Reg.EDX), bytes, 0), bytes), IrCastOp.ZExt, wide);
        var low = this.Widen(this.Read(new GeneralPlace(registers.General(Reg.EAX), bytes, 0), bytes), IrCastOp.ZExt, wide);
        dividend = this.Add(new IrBinary(IrBinaryOp.Or, this.Add(new IrBinary(IrBinaryOp.Shl, high, new IrConstantInt(wide, bytes * 8))), low));
      }
      var wideDivisor = this.Widen(divisor, extend, wide);
      var byZero = this.Compare(IrCmpPred.Eq, divisor, new IrConstantInt(narrow, 0));
      // the one signed overflow a division can make in its own width - the most negative dividend over
      // -1 - is out of every narrow register's range as well, so testing the wide quotient covers it
      var minimum = new IrConstantInt(wide, bytes * 16 >= 64 ? long.MinValue : -(1L << (bytes * 16 - 1)));
      IrValue wraps = signed
        ? this.Add(new IrBinary(IrBinaryOp.And, this.Compare(IrCmpPred.Eq, dividend, minimum),
            this.Compare(IrCmpPred.Eq, wideDivisor, new IrConstantInt(wide, -1))))
        : IrBuilder.ConstBool(false);
      var safeDivisor = this.Add(new IrSelect(this.Add(new IrBinary(IrBinaryOp.Or, byZero, wraps)), new IrConstantInt(wide, 1), wideDivisor));
      var quotient = this.Add(new IrBinary(signed ? IrBinaryOp.SDiv : IrBinaryOp.UDiv, dividend, safeDivisor));
      var remainder = this.Add(new IrBinary(signed ? IrBinaryOp.SRem : IrBinaryOp.URem, dividend, safeDivisor));
      var narrowQuotient = this.Narrow(quotient, narrow);
      var fits = this.Compare(IrCmpPred.Eq, quotient, this.Widen(narrowQuotient, extend, wide));
      var fault = this.Add(new IrBinary(IrBinaryOp.Or, this.Add(new IrBinary(IrBinaryOp.Or, byZero, wraps)),
        this.Add(new IrBinary(IrBinaryOp.Xor, fits, IrBuilder.ConstBool(true)))));
      this.RaiseWhen(fault, 11);
      if (bytes == 1) {
        var pair = this.Add(new IrBinary(IrBinaryOp.Or,
          this.Add(new IrBinary(IrBinaryOp.Shl, this.Widen(this.Narrow(remainder, narrow), IrCastOp.ZExt, wide), new IrConstantInt(wide, 8))),
          this.Widen(narrowQuotient, IrCastOp.ZExt, wide)));
        this.Write(new GeneralPlace(registers.General(Reg.EAX), 2, 0), pair);
      } else {
        this.Write(new GeneralPlace(registers.General(Reg.EAX), bytes, 0), narrowQuotient);
        this.Write(new GeneralPlace(registers.General(Reg.EDX), bytes, 0), this.Narrow(remainder, narrow));
      }
      return true;
    }

    /// <summary>
    /// CBW/CWDE extend AL or AX across AX or EAX; CWD/CDQ fill DX or EDX with the sign of AX or EAX.
    /// </summary>
    private bool SignExtendAccumulator(int bytes, Reg? into) {
      var value = this.Read(new GeneralPlace(registers.General(Reg.EAX), bytes, 0), bytes);
      if (into is { } register) {
        var sign = this.Add(new IrBinary(IrBinaryOp.AShr, value, new IrConstantInt(value.Type, bytes * 8 - 1)));
        this.Write(new GeneralPlace(registers.General(register), bytes, 0), sign);
      } else
        this.Write(new GeneralPlace(registers.General(Reg.EAX), bytes * 2, 0), this.Widen(value, IrCastOp.SExt, Integer(bytes * 2)));
      return true;
    }

    private enum Counting { Population, LeadingZeros, TrailingZeros }

    /// <summary>The number of set bits, by the halving sums of a SWAR count.</summary>
    private IrValue Population(IrValue value) {
      var type = value.Type;
      long Repeat(long pattern) => type.Bits == 16 ? pattern & 0xFFFF : pattern & 0xFFFFFFFF;
      IrValue Mask(IrValue v, long m) => this.Add(new IrBinary(IrBinaryOp.And, v, new IrConstantInt(type, m)));
      IrValue Down(IrValue v, int n) => this.Add(new IrBinary(IrBinaryOp.LShr, v, new IrConstantInt(type, n)));
      IrValue Sum(IrValue a, IrValue b) => this.Add(new IrBinary(IrBinaryOp.Add, a, b));
      IrValue v = this.Add(new IrBinary(IrBinaryOp.Sub, value, Mask(Down(value, 1), Repeat(0x55555555))));
      v = Sum(Mask(v, Repeat(0x33333333)), Mask(Down(v, 2), Repeat(0x33333333)));
      v = Mask(Sum(v, Down(v, 4)), Repeat(0x0F0F0F0F));
      for (var shift = 8; shift < type.Bits; shift *= 2)
        v = Sum(v, Down(v, shift));
      return Mask(v, type.Bits == 16 ? 0x1F : 0x3F);
    }

    /// <summary>POPCNT, LZCNT and TZCNT, with the flags each leaves: ZF of the source for POPCNT, CF of the source and ZF of the count for the other two.</summary>
    private bool Count(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, Counting counting) {
      if (operands.Count != 2 || this.PlaceOf(operands[0], 0) is not GeneralPlace { Bytes: 2 or 4 } destination)
        throw new NotLiftableException("counts into a 16- or 32-bit register");
      var bytes = destination.Bytes;
      var type = Integer(bytes);
      var source = this.Read(this.PlaceOf(operands[1], bytes), bytes);
      var zero = new IrConstantInt(type, 0);
      IrValue result;
      switch (counting) {
        case Counting.Population:
          result = this.Population(source);
          break;
        case Counting.LeadingZeros: {
          // smear the highest set bit downwards; what is left clear is the leading zeros
          var smeared = source;
          for (var shift = 1; shift < type.Bits; shift *= 2)
            smeared = this.Add(new IrBinary(IrBinaryOp.Or, smeared, this.Add(new IrBinary(IrBinaryOp.LShr, smeared, new IrConstantInt(type, shift)))));
          result = this.Add(new IrBinary(IrBinaryOp.Sub, new IrConstantInt(type, type.Bits), this.Population(smeared)));
          break;
        }
        default: {
          // the bits below the lowest set one, counted; a zero source counts every bit
          var below = this.Add(new IrBinary(IrBinaryOp.And, this.Add(new IrBinary(IrBinaryOp.Xor, source, new IrConstantInt(type, -1))),
            this.Add(new IrBinary(IrBinaryOp.Sub, source, new IrConstantInt(type, 1)))));
          result = this.Population(below);
          break;
        }
      }
      this.Write(destination, result);
      var sourceZero = this.Compare(IrCmpPred.Eq, source, zero);
      if (counting == Counting.Population) {
        this.SetFlag('Z', sourceZero);
        this.SetFlag('C', IrBuilder.ConstBool(false));
      } else {
        this.SetFlag('C', sourceZero);
        this.SetFlag('Z', this.Compare(IrCmpPred.Eq, result, zero));
      }
      this.SetFlag('O', IrBuilder.ConstBool(false));
      this.SetFlag('S', IrBuilder.ConstBool(false));
      return true;
    }

    /// <summary>The BMI three-operand shape: a 32-bit register written from a register and a register or memory operand.</summary>
    private bool ThreeOperand(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, Func<IrValue, IrValue, IrType, IrValue> apply, FlagRule? flags) {
      if (operands.Count != 3 || this.PlaceOf(operands[0], 0) is not GeneralPlace { Bytes: 4 } destination)
        throw new NotLiftableException("writes a 32-bit register from two sources");
      var a = this.Read(this.PlaceOf(operands[1], 4), 4);
      var b = this.Read(this.PlaceOf(operands[2], 4), 4);
      var result = apply(a, b, IrType.I32);
      if (flags is { } rule)
        this.SetFlags(rule, a, b, result);
      this.Write(destination, result);
      return true;
    }

    /// <summary>BLSI/BLSR/BLSMSK: a function of the lowest set bit; CF says whether the source was zero (or, for BLSI, was not).</summary>
    private bool LowestBit(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, Func<IrValue, IrType, IrValue> apply, bool carryWhenZero) {
      if (operands.Count != 2 || this.PlaceOf(operands[0], 0) is not GeneralPlace { Bytes: 4 } destination)
        throw new NotLiftableException("writes a 32-bit register");
      var source = this.Read(this.PlaceOf(operands[1], 4), 4);
      var result = apply(source, IrType.I32);
      this.SetFlags(FlagRule.Logic, source, source, result);
      this.SetFlag('C', this.Compare(carryWhenZero ? IrCmpPred.Eq : IrCmpPred.Ne, source, new IrConstantInt(IrType.I32, 0)));
      this.Write(destination, result);
      return true;
    }

    /// <summary>BZHI: the source with the bits from the index (the low byte of the third operand) upwards cleared; CF when the index is past the width.</summary>
    private bool ZeroHighBits(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands) {
      if (operands.Count != 3 || this.PlaceOf(operands[0], 0) is not GeneralPlace { Bytes: 4 } destination)
        throw new NotLiftableException("writes a 32-bit register from two sources");
      var source = this.Read(this.PlaceOf(operands[1], 4), 4);
      var index = this.Add(new IrBinary(IrBinaryOp.And, this.Read(this.PlaceOf(operands[2], 4), 4), new IrConstantInt(IrType.I32, 0xFF)));
      var past = this.Compare(IrCmpPred.Ugt, index, new IrConstantInt(IrType.I32, 31));
      var mask = this.Add(new IrBinary(IrBinaryOp.Sub,
        this.Add(new IrBinary(IrBinaryOp.Shl, new IrConstantInt(IrType.I32, 1), this.Add(new IrSelect(past, new IrConstantInt(IrType.I32, 0), index)))),
        new IrConstantInt(IrType.I32, 1)));
      var result = this.Add(new IrSelect(past, source, this.Add(new IrBinary(IrBinaryOp.And, source, mask))));
      this.SetFlags(FlagRule.Logic, source, source, result);
      this.SetFlag('C', past);
      this.Write(destination, result);
      return true;
    }

    private IrValue ShiftCount(IrValue count) => this.Add(new IrBinary(IrBinaryOp.And, count, new IrConstantInt(count.Type, 31)));

    /// <summary>PDEP: the source's low bits placed, in order, at the positions the mask sets.</summary>
    private IrValue Deposit(IrValue source, IrValue mask) {
      var type = source.Type;
      IrValue result = new IrConstantInt(type, 0);
      IrValue taken = new IrConstantInt(type, 0);
      for (var bit = 0; bit < 32; ++bit) {
        var selected = this.Add(new IrBinary(IrBinaryOp.And, this.Add(new IrBinary(IrBinaryOp.LShr, mask, new IrConstantInt(type, bit))), new IrConstantInt(type, 1)));
        var next = this.Add(new IrBinary(IrBinaryOp.And, this.Add(new IrBinary(IrBinaryOp.LShr, source, taken)), selected));
        result = this.Add(new IrBinary(IrBinaryOp.Or, result, this.Add(new IrBinary(IrBinaryOp.Shl, next, new IrConstantInt(type, bit)))));
        taken = this.Add(new IrBinary(IrBinaryOp.Add, taken, selected));
      }
      return result;
    }

    /// <summary>PEXT: the source's bits at the positions the mask sets, gathered in order into the low bits.</summary>
    private IrValue Extract(IrValue source, IrValue mask) {
      var type = source.Type;
      IrValue result = new IrConstantInt(type, 0);
      IrValue placed = new IrConstantInt(type, 0);
      for (var bit = 0; bit < 32; ++bit) {
        var selected = this.Add(new IrBinary(IrBinaryOp.And, this.Add(new IrBinary(IrBinaryOp.LShr, mask, new IrConstantInt(type, bit))), new IrConstantInt(type, 1)));
        var value = this.Add(new IrBinary(IrBinaryOp.And, this.Add(new IrBinary(IrBinaryOp.LShr, source, new IrConstantInt(type, bit))), selected));
        // placed stays below 32: it counts the mask bits seen so far
        result = this.Add(new IrBinary(IrBinaryOp.Or, result, this.Add(new IrBinary(IrBinaryOp.Shl, value, placed))));
        placed = this.Add(new IrBinary(IrBinaryOp.Add, placed, selected));
      }
      return result;
    }

    private bool RotateRight(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands) {
      if (operands.Count != 3 || this.PlaceOf(operands[0], 0) is not GeneralPlace { Bytes: 4 } destination
          || this.PlaceOf(operands[2], 1) is not ImmediatePlace { Value: var amount })
        throw new NotLiftableException("rotates into a 32-bit register by an immediate");
      var source = this.Read(this.PlaceOf(operands[1], 4), 4);
      var n = (int)(amount & 31);
      IrValue result = n == 0 ? source : this.Add(new IrBinary(IrBinaryOp.Or,
        this.Add(new IrBinary(IrBinaryOp.LShr, source, new IrConstantInt(IrType.I32, n))),
        this.Add(new IrBinary(IrBinaryOp.Shl, source, new IrConstantInt(IrType.I32, 32 - n)))));
      this.Write(destination, result);
      return true;
    }

    /// <summary>MULX: EDX times the source, unsigned, the high half into the first operand and the low into the second; no flag changes.</summary>
    private bool MultiplyNoFlags(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands) {
      if (operands.Count != 3 || this.PlaceOf(operands[0], 0) is not GeneralPlace { Bytes: 4 } high
          || this.PlaceOf(operands[1], 0) is not GeneralPlace { Bytes: 4 } low)
        throw new NotLiftableException("multiplies into two 32-bit registers");
      var source = this.Read(this.PlaceOf(operands[2], 4), 4);
      var edx = this.Read(new GeneralPlace(registers.General(Reg.EDX), 4, 0), 4);
      var product = this.Add(new IrBinary(IrBinaryOp.Mul, this.Widen(edx, IrCastOp.ZExt, IrType.I64), this.Widen(source, IrCastOp.ZExt, IrType.I64)));
      // the low half first, so that one register named twice ends up holding the high half
      this.Write(low, this.Narrow(product, IrType.I32));
      this.Write(high, this.Narrow(this.Add(new IrBinary(IrBinaryOp.LShr, product, new IrConstantInt(IrType.I64, 32))), IrType.I32));
      return true;
    }

    // --- the stack ------------------------------------------------------------------------------

    /// <summary>The stack slot <paramref name="pointer"/> addresses.</summary>
    private IrValue StackSlot(IrValue pointer) => this.Add(new IrGep(registers.Stack(), pointer));

    private void PushValue(IrValue value) {
      var old = this.Add(new IrLoad(IrType.I32, registers.StackPointer()));
      var moved = this.Add(new IrBinary(IrBinaryOp.And,
        this.Add(new IrBinary(IrBinaryOp.Sub, old, new IrConstantInt(IrType.I32, value.Type.Bits / 8))),
        new IrConstantInt(IrType.I32, StackBytes - 1)));
      this.Add(new IrStore(moved, registers.StackPointer()));
      // a value straddling the wrap is stored byte by byte, so the stack never writes past its end
      for (var i = 0; i < value.Type.Bits / 8; ++i) {
        var octet = this.Narrow(i == 0 ? value : this.Add(new IrBinary(IrBinaryOp.LShr, value, new IrConstantInt(value.Type, 8 * i))), IrType.I8);
        var at = this.Add(new IrBinary(IrBinaryOp.And, this.Add(new IrBinary(IrBinaryOp.Add, moved, new IrConstantInt(IrType.I32, i))),
          new IrConstantInt(IrType.I32, StackBytes - 1)));
        this.Add(new IrStore(octet, this.StackSlot(at)));
      }
    }

    private IrValue PopValue(IrType type) {
      var pointer = this.Add(new IrLoad(IrType.I32, registers.StackPointer()));
      IrValue value = new IrConstantInt(type, 0);
      for (var i = 0; i < type.Bits / 8; ++i) {
        var at = this.Add(new IrBinary(IrBinaryOp.And, this.Add(new IrBinary(IrBinaryOp.Add, pointer, new IrConstantInt(IrType.I32, i))),
          new IrConstantInt(IrType.I32, StackBytes - 1)));
        var octet = this.Widen(this.Add(new IrLoad(IrType.I8, this.StackSlot(at))), IrCastOp.ZExt, type);
        value = this.Add(new IrBinary(IrBinaryOp.Or, value, i == 0 ? octet : this.Add(new IrBinary(IrBinaryOp.Shl, octet, new IrConstantInt(type, 8 * i)))));
      }
      this.Add(new IrStore(this.Add(new IrBinary(IrBinaryOp.And,
        this.Add(new IrBinary(IrBinaryOp.Add, pointer, new IrConstantInt(IrType.I32, type.Bits / 8))),
        new IrConstantInt(IrType.I32, StackBytes - 1))), registers.StackPointer()));
      return value;
    }

    private bool Push(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands) {
      if (operands.Count != 1)
        throw new NotLiftableException("expects one operand");
      var place = this.PlaceOf(operands[0], 0);
      // an immediate is pushed as a doubleword, as a 32-bit assembler encodes it
      var bytes = place switch { ImmediatePlace => 4, { Bytes: 2 or 4 } => place.Bytes, _ => 0 };
      if (bytes == 0 || place is VectorPlace)
        throw new NotLiftableException("pushes a 16- or 32-bit register, variable or immediate");
      this.PushValue(this.Read(place, bytes));
      return true;
    }

    private bool Pop(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands) {
      if (operands.Count != 1)
        throw new NotLiftableException("expects one operand");
      var place = this.PlaceOf(operands[0], 0);
      if (place is VectorPlace or ImmediatePlace || place.Bytes is not (2 or 4))
        throw new NotLiftableException("pops into a 16- or 32-bit register or variable");
      this.Write(place, this.PopValue(Integer(place.Bytes)));
      return true;
    }

    /// <summary>
    /// The FLAGS image PUSHF writes: CF, ZF, SF and OF from their statics, bit 1 set as it always is and
    /// IF set as it is in a running program; PF and AF, which are not modelled, read as clear.
    /// </summary>
    private IrValue PackFlags(IrType type) {
      IrValue image = new IrConstantInt(type, 0x0202);
      foreach (var (flag, bit) in new[] { ('C', 0), ('Z', 6), ('S', 7), ('O', 11) }) {
        var set = this.Widen(this.GetFlag(flag), IrCastOp.ZExt, type);
        image = this.Add(new IrBinary(IrBinaryOp.Or, image, this.Add(new IrBinary(IrBinaryOp.Shl, set, new IrConstantInt(type, bit)))));
      }
      return image;
    }

    private void UnpackFlags(IrValue image) {
      foreach (var (flag, bit) in new[] { ('C', 0), ('Z', 6), ('S', 7), ('O', 11) })
        this.SetFlag(flag, this.Compare(IrCmpPred.Ne,
          this.Add(new IrBinary(IrBinaryOp.And, image, new IrConstantInt(image.Type, 1L << bit))), new IrConstantInt(image.Type, 0)));
    }

    // --- flags ----------------------------------------------------------------------------------

    private enum FlagRule { Add, Subtract, Logic, Increment, Decrement, Negate }

    /// <summary>
    /// <c>INT n</c>: the registers into the REG buffer, the runtime's <c>rt_interrupt</c> - which answers
    /// the DOS and BIOS services a program asks for most (<c>PortableRuntime.Interrupts</c>) - and the
    /// buffer back, its carry and zero bits as the flags. The same buffer is REG's, so an
    /// <c>INT 21h</c> written as assembly and a <c>CALL INTERRUPT &amp;H21</c> do one thing.
    /// </summary>
    private bool Interrupt(TextAssembler.ParsedAsmOperand vector) {
      if (this.PlaceOf(vector, 1) is not ImmediatePlace { Value: var number })
        throw new NotLiftableException("an interrupt number is a constant");
      var module = registers.Module;
      var buffer = module.FindGlobal("rt.regs") ?? module.AddGlobal(new IrGlobalVariable("rt.regs", IrType.I16) { Count = 10, IsZeroInitialized = true });
      var handler = module.FindFunction("rt_interrupt")
        ?? module.AddFunction(new IrFunction("rt_interrupt", IrType.Void, [new IrArgument(IrType.I16, 0)]));
      // REG's numbering: 1 AX, 2 BX, 3 CX, 4 DX, 5 SI, 6 DI, 7 BP; 0 the flags
      (int Slot, Reg Register)[] map = [(1, Reg.AX), (2, Reg.BX), (3, Reg.CX), (4, Reg.DX), (5, Reg.SI), (6, Reg.DI), (7, Reg.BP)];
      IrValue Cell(int slot) => this.Add(new IrGep(buffer, new IrConstantInt(IrType.I32, slot), IrType.I16));
      foreach (var (slot, register) in map)
        this.Add(new IrStore(this.Read(new GeneralPlace(registers.General(register), 2, 0), 2), Cell(slot)));
      this.Add(new IrStore(this.PackFlags(IrType.I16), Cell(0)));
      this.Add(new IrCall(IrType.Void, handler, [new IrConstantInt(IrType.I16, number & 0xFF)]));
      foreach (var (slot, register) in map)
        this.Write(new GeneralPlace(registers.General(register), 2, 0), this.Add(new IrLoad(IrType.I16, Cell(slot))));
      this.UnpackFlags(this.Add(new IrLoad(IrType.I16, Cell(0))));
      return true;
    }

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
      var (block, rest) = this.SplitHere();
      if (taken is null)
        block.Append(new IrBr(target));
      else
        block.Append(new IrCondBr(taken, target, rest));
      return true;
    }

    /// <summary>
    /// Ends the current block before the assembly: the assembly and everything after it move to a new
    /// block, which the caller enters from the old one with a terminator of its own.
    /// </summary>
    private (IrBasicBlock Block, IrBasicBlock Continuation) SplitHere() {
      var block = node.Parent!;
      var rest = block.Parent!.CreateBlock(block.Label + ".asm");
      var moving = block.Instructions.SkipWhile(instruction => !ReferenceEquals(instruction, node)).ToList();
      foreach (var instruction in moving) {
        block.Remove(instruction);
        rest.Append(instruction);
      }
      // the successors' phis now see the moved terminator's new block
      foreach (var successor in rest.Successors.Distinct())
        foreach (var phi in successor.Phis)
          phi.RenameIncomingBlock(block, rest);
      return (block, rest);
    }

    /// <summary>Raises BASIC error <paramref name="code"/> where <paramref name="condition"/> holds, in the shape <see cref="IrRaise"/> knows.</summary>
    private void RaiseWhen(IrValue condition, int code) {
      var (block, rest) = this.SplitHere();
      var trap = block.Parent!.CreateBlock(block.Label + ".asm.trap");
      var error = registers.Module.FindFunction("rt_error")
        ?? registers.Module.AddFunction(new IrFunction("rt_error", IrType.Void, [new IrArgument(IrType.I32, 0)]));
      trap.Append(new IrCall(IrType.Void, error, [new IrConstantInt(IrType.I32, code)]));
      trap.Append(new IrUnreachable());
      block.Append(new IrCondBr(condition, trap, rest));
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
    private IrValue Narrow(IrValue value, IrType type) => value.Type.Bits == type.Bits ? value : this.Add(new IrCast(IrCastOp.Trunc, value, type));

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

    // --- floating point ----------------------------------------------------------------------------

    /// <summary>
    /// A floating-point operation on every lane, or under <paramref name="scalar"/> on the lowest only,
    /// the others then taken from the first source (the destination itself without VEX).
    /// </summary>
    private bool FloatLanes(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, IrType type, bool scalar, Func<IrValue, IrValue, IrValue> apply) {
      var (destination, leftPlace, rightPlace, width) = this.Packed(operands);
      var lane = type.Bits / 8;
      var span = scalar ? lane : width;
      var left = this.LoadLanes(leftPlace, type, lane, span);
      var right = this.LoadLanes(rightPlace, type, lane, span);
      var upper = scalar && !ReferenceEquals(leftPlace, destination) ? this.LoadLanes(leftPlace, IrType.I32, 4, width - lane, lane) : null;
      this.StoreLanes(destination, left.Select((a, i) => apply(a, right[i])).ToList(), lane);
      if (upper is not null)
        for (var i = 0; i < upper.Count; ++i)
          this.Add(new IrStore(upper[i], this.LaneAddress(destination, lane + i * 4)));
      return true;
    }

    /// <summary>The square root of every lane of the source, or of the lowest under <paramref name="scalar"/>.</summary>
    private bool SquareRoot(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, IrType type, bool scalar) {
      var sqrt = this.Intrinsic($"llvm.sqrt.f{type.Bits}", type, type);
      if (scalar)
        return this.FloatLanes(operands, type, scalar: true, (_, b) => this.Root(sqrt, b));
      // the packed form has one source, under VEX as well
      if (operands.Count != 2 || this.PlaceOf(operands[0], 0) is not VectorPlace { Bytes: var width } destination)
        throw new NotLiftableException("expects a vector destination and a source");
      var source = this.PlaceOf(operands[1], width);
      if (source is not (VectorPlace or MemoryPlace))
        throw new NotLiftableException("reads a register or memory");
      var lane = type.Bits / 8;
      var roots = this.LoadLanes(source, type, lane, width).Select(value => this.Root(sqrt, value)).ToList();
      this.StoreLanes(destination, roots, lane);
      return true;
    }

    /// <summary>
    /// A quotient as the hardware gives it with its exceptions masked: a zero divisor answers infinity
    /// signed as the operands are, or the default NaN for 0/0, instead of dividing - a machine whose
    /// floats have no infinity (the 6502's are BASIC's, where a zero divisor is error 11) never sees
    /// the division. The answer is the lane's bits.
    /// </summary>
    private IrValue Divide(IrValue a, IrValue b) {
      var type = a.Type;
      var bits = Integer(type.Bits / 8);
      var zero = new IrConstantFloat(type, 0);
      var byZero = this.Compare(IrCmpPred.Foeq, b, zero);
      var quotient = this.Add(new IrBinary(IrBinaryOp.FDiv, a, this.Add(new IrSelect(byZero, new IrConstantFloat(type, 1), b))));
      var sign = this.Add(new IrBinary(IrBinaryOp.And,
        this.Add(new IrBinary(IrBinaryOp.Xor, this.Add(new IrCast(IrCastOp.BitCast, a, bits)), this.Add(new IrCast(IrCastOp.BitCast, b, bits)))),
        new IrConstantInt(bits, type.Bits == 32 ? unchecked((int)0x80000000) : long.MinValue)));
      var infinity = this.Add(new IrBinary(IrBinaryOp.Or, sign, new IrConstantInt(bits, type.Bits == 32 ? 0x7F800000 : 0x7FF0000000000000)));
      var special = this.Add(new IrSelect(this.Compare(IrCmpPred.Foeq, a, zero), DefaultNan(bits), infinity));
      return this.Add(new IrSelect(byZero, special, this.Add(new IrCast(IrCastOp.BitCast, quotient, bits))));
    }

    /// <summary>A square root as the hardware gives it: the default NaN for a negative operand, which is never passed on.</summary>
    private IrValue Root(IrFunction sqrt, IrValue value) {
      var type = value.Type;
      var bits = Integer(type.Bits / 8);
      var negative = this.Compare(IrCmpPred.Folt, value, new IrConstantFloat(type, 0));
      var root = this.Add(new IrCall(type, sqrt, [this.Add(new IrSelect(negative, new IrConstantFloat(type, 0), value))]));
      return this.Add(new IrSelect(negative, DefaultNan(bits), this.Add(new IrCast(IrCastOp.BitCast, root, bits))));
    }

    /// <summary>x86's default NaN, the "real indefinite": sign set, quiet, no payload.</summary>
    private static IrConstantInt DefaultNan(IrType bits)
      => new(bits, bits.Bits == 32 ? unchecked((int)0xFFC00000) : unchecked((long)0xFFF8000000000000));

    private IrFunction Intrinsic(string name, IrType result, params IrType[] parameters) {
      return registers.Module.FindFunction(name)
        ?? registers.Module.AddFunction(new IrFunction(name, result, parameters.Select((type, i) => new IrArgument(type, i)).ToList()));
    }

    /// <summary>
    /// MOVSS/MOVSD: between registers the lowest lane alone moves (under VEX the rest comes from the
    /// second operand); from memory the register is zeroed above it; to memory only the lane is stored.
    /// </summary>
    private bool MoveScalar(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, int lane) {
      if (this._vex && operands.Count == 3)
        return this.FloatLanes(operands, lane == 4 ? IrType.F32 : IrType.F64, scalar: true, (_, b) => b);
      if (operands.Count != 2)
        throw new NotLiftableException("expects two operands");
      var destination = this.PlaceOf(operands[0], lane);
      var source = this.PlaceOf(operands[1], lane);
      if (destination is not (VectorPlace or MemoryPlace) || source is not (VectorPlace or MemoryPlace)
          || (destination is MemoryPlace && source is MemoryPlace))
        throw new NotLiftableException("moves between an XMM register and an XMM register or memory");
      var value = this.Add(new IrLoad(Integer(lane), this.LaneAddress(source, 0)));
      this.Add(new IrStore(value, this.LaneAddress(destination, 0)));
      if (destination is VectorPlace vector && source is MemoryPlace)
        this.ZeroBytes(vector, lane, vector.Bytes);
      return true;
    }

    /// <summary>The XMM operands of a scalar conversion: destination, the source of the upper lanes, and the converted operand.</summary>
    private (VectorPlace Destination, Place Upper, Place Source) ScalarConversion(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, int sourceBytes) {
      var count = this._vex ? 3 : 2;
      if (operands.Count != count || this.PlaceOf(operands[0], 0) is not VectorPlace destination)
        throw new NotLiftableException($"expects an XMM destination and {count - 1} sources");
      var upper = this._vex ? this.PlaceOf(operands[1], 16) : destination;
      return (destination, upper, this.PlaceOf(operands[count - 1], sourceBytes));
    }

    /// <summary>Writes <paramref name="value"/> to the lowest lane and the rest of the XMM register from <paramref name="upper"/>.</summary>
    private void StoreScalar(VectorPlace destination, Place upper, IrValue value) {
      var lane = value.Type.Bits / 8;
      var rest = ReferenceEquals(upper, destination) ? null : this.LoadLanes(upper, IrType.I32, 4, 16 - lane, lane);
      this.Add(new IrStore(value, this.LaneAddress(destination, 0)));
      if (rest is not null)
        for (var i = 0; i < rest.Count; ++i)
          this.Add(new IrStore(rest[i], this.LaneAddress(destination, lane + i * 4)));
    }

    /// <summary>CVTSI2SS/CVTSI2SD: a 32-bit integer from a register or memory into the lowest lane.</summary>
    private bool FromInteger(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, IrType type) {
      var (destination, upper, source) = this.ScalarConversion(operands, 4);
      if (source is not (GeneralPlace { Bytes: 4 } or MemoryPlace))
        throw new NotLiftableException("converts a 32-bit register or memory");
      var integer = source is MemoryPlace memory ? this.Add(new IrLoad(IrType.I32, memory.Address)) : this.Read(source, 4);
      this.StoreScalar(destination, upper, this.Add(new IrCast(IrCastOp.SIToFP, integer, type)));
      return true;
    }

    /// <summary>
    /// CVT(T)SS2SI/CVT(T)SD2SI: the lowest lane into a 32-bit register, truncated or rounded to
    /// nearest-even; a value outside the 32-bit range, or a NaN, gives the hardware's 80000000h.
    /// </summary>
    private bool ToInteger(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, IrType type, IrCastOp conversion) {
      if (operands.Count != 2 || this.PlaceOf(operands[0], 4) is not GeneralPlace { Bytes: 4 } destination)
        throw new NotLiftableException("converts into a 32-bit register");
      var source = this.PlaceOf(operands[1], type.Bits / 8);
      if (source is not (VectorPlace or MemoryPlace))
        throw new NotLiftableException("converts an XMM register or memory");
      var value = this.Add(new IrLoad(type, this.LaneAddress(source, 0)));
      this.Write(destination, this.ToInt32(value, conversion));
      return true;
    }

    private IrValue ToInt32(IrValue value, IrCastOp conversion) {
      var type = value.Type;
      // in range when -2^31 - 1 < x < 2^31 for a truncation, -2^31 - 0.5 <= x < 2^31 - 0.5 for a rounding; a NaN is never in range
      var (low, high) = conversion == IrCastOp.FPToSI ? (-2147483649.0, 2147483648.0) : (-2147483648.5, 2147483647.5);
      var fits = this.Add(new IrBinary(IrBinaryOp.And,
        this.Compare(conversion == IrCastOp.FPToSI ? IrCmpPred.Fogt : IrCmpPred.Foge, value, new IrConstantFloat(type, low)),
        this.Compare(IrCmpPred.Folt, value, new IrConstantFloat(type, high))));
      var safe = this.Add(new IrSelect(fits, value, new IrConstantFloat(type, 0)));
      var converted = this.Add(new IrCast(conversion, safe, IrType.I32));
      return this.Add(new IrSelect(fits, converted, new IrConstantInt(IrType.I32, int.MinValue)));
    }

    /// <summary>CVTSS2SD/CVTSD2SS: the lowest lane widened or rounded to the other precision.</summary>
    private bool Reformat(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, IrType from, IrType to) {
      var (destination, upper, source) = this.ScalarConversion(operands, from.Bits / 8);
      if (source is not (VectorPlace or MemoryPlace))
        throw new NotLiftableException("converts an XMM register or memory");
      var value = this.Add(new IrLoad(from, this.LaneAddress(source, 0)));
      this.StoreScalar(destination, upper, this.Add(new IrCast(to.Bits > from.Bits ? IrCastOp.FPExt : IrCastOp.FPTrunc, value, to)));
      return true;
    }

    /// <summary>CVTDQ2PS/CVTTPS2DQ/CVTPS2DQ: every 32-bit lane converted.</summary>
    private bool ConvertLanes(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, IrType from, IrType to, IrCastOp conversion) {
      if (operands.Count != 2 || this.PlaceOf(operands[0], 0) is not VectorPlace { Bytes: var width } destination)
        throw new NotLiftableException("expects a vector destination and a source");
      var source = this.PlaceOf(operands[1], width);
      if (source is not (VectorPlace or MemoryPlace))
        throw new NotLiftableException("reads a register or memory");
      var converted = this.LoadLanes(source, from, 4, width)
        .Select(value => to.IsFloat ? this.Add(new IrCast(conversion, value, to)) : this.ToInt32(value, conversion)).ToList();
      this.StoreLanes(destination, converted, 4);
      return true;
    }

    /// <summary>COMISS/UCOMISS/COMISD/UCOMISD: ZF, CF (and PF, not modelled) from an ordered compare; unordered sets both; OF and SF clear.</summary>
    private bool CompareScalar(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, IrType type) {
      if (operands.Count != 2)
        throw new NotLiftableException("expects two operands");
      var left = this.PlaceOf(operands[0], 0);
      var right = this.PlaceOf(operands[1], type.Bits / 8);
      if (left is not VectorPlace || right is not (VectorPlace or MemoryPlace))
        throw new NotLiftableException("compares an XMM register with an XMM register or memory");
      var a = this.Add(new IrLoad(type, this.LaneAddress(left, 0)));
      var b = this.Add(new IrLoad(type, this.LaneAddress(right, 0)));
      var ordered = this.Add(new IrBinary(IrBinaryOp.Or, this.Compare(IrCmpPred.Foge, a, b), this.Compare(IrCmpPred.Folt, a, b)));
      var unordered = this.Add(new IrBinary(IrBinaryOp.Xor, ordered, IrBuilder.ConstBool(true)));
      this.SetFlag('Z', this.Add(new IrBinary(IrBinaryOp.Or, unordered, this.Compare(IrCmpPred.Foeq, a, b))));
      this.SetFlag('C', this.Add(new IrBinary(IrBinaryOp.Or, unordered, this.Compare(IrCmpPred.Folt, a, b))));
      this.SetFlag('O', IrBuilder.ConstBool(false));
      this.SetFlag('S', IrBuilder.ConstBool(false));
      return true;
    }

    // --- AVX2's own instructions, each reached through its V prefix ---------------------------------

    /// <summary>
    /// VEXTRACTI128 and AVX-512's VEXTRACTI32X4/VEXTRACTI64X4: the <paramref name="chunk"/>-byte part of a
    /// <paramref name="from"/>-byte register the immediate names, into a register or memory.
    /// </summary>
    private bool ExtractPart(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, int chunk, int from) {
      if (operands.Count != 3 || this.PlaceOf(operands[2], 1) is not ImmediatePlace { Value: var part })
        throw new NotLiftableException("expects a destination, a vector register and an immediate");
      if (this.PlaceOf(operands[1], from) is not VectorPlace { Bytes: var width } source || width != from)
        throw new NotLiftableException($"extracts from a {from * 8}-bit register");
      var destination = this.PlaceOf(operands[0], chunk);
      if (destination is not (VectorPlace or MemoryPlace))
        throw new NotLiftableException("writes a vector register or memory");
      var lanes = this.LoadLanes(source, IrType.I64, 8, chunk, (int)(part % (from / chunk)) * chunk);
      this.StoreLanes(destination, lanes, 8);
      return true;
    }

    /// <summary>VINSERTI128 and AVX-512's VINSERTI32X4/VINSERTI64X4: the second operand with the part the immediate names replaced by the third.</summary>
    private bool InsertPart(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, int chunk, int into) {
      if (operands.Count != 4 || this.PlaceOf(operands[3], 1) is not ImmediatePlace { Value: var part })
        throw new NotLiftableException("expects a destination, a source, the part to insert and an immediate");
      if (this.PlaceOf(operands[0], into) is not VectorPlace { Bytes: var width } destination || width != into
          || this.PlaceOf(operands[1], into) is not VectorPlace source || source.Bytes != into)
        throw new NotLiftableException($"inserts into {into * 8}-bit registers");
      var insert = this.PlaceOf(operands[2], chunk);
      if (insert is not (VectorPlace or MemoryPlace))
        throw new NotLiftableException("inserts a register or memory");
      var lanes = this.LoadLanes(source, IrType.I64, 8, into);
      var replacement = this.LoadLanes(insert, IrType.I64, 8, chunk);
      var at = (int)(part % (into / chunk)) * chunk / 8;
      for (var i = 0; i < replacement.Count; ++i)
        lanes[at + i] = replacement[i];
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
