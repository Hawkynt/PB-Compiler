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
/// <c>PXOR</c>, <c>EMMS</c>; SSE2 <c>MOVDQA</c>/<c>MOVDQU</c> and the same packed operations on
/// <c>XMM</c>; SSSE3 <c>PSHUFB</c>.
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
        case "MOVDQA" or "MOVDQU": return this.MoveVector(operands, 16);
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
    /// MOVD/MOVQ/MOVDQA/MOVDQU: <paramref name="bytes"/> from source to destination. A vector
    /// destination written from a general register or memory is zeroed above what was written, as the
    /// hardware zeroes it.
    /// </summary>
    private bool MoveVector(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, int bytes) {
      if (operands.Count != 2)
        throw new NotLiftableException("expects two operands");
      var destination = this.PlaceOf(operands[0], bytes);
      var source = this.PlaceOf(operands[1], bytes);
      if (destination is not VectorPlace && source is not VectorPlace)
        return false;
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
      if (destination is VectorPlace vector && source is not VectorPlace)
        for (var offset = bytes; offset < vector.Bytes; offset += 4)
          this.Add(new IrStore(new IrConstantInt(IrType.I32, 0), this.LaneAddress(destination, offset)));
      return true;
    }

    /// <summary>The two operands of a packed operation: a vector destination and a vector or memory source of the same width.</summary>
    private (Place Destination, Place Source, int Width) Packed(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands) {
      if (operands.Count != 2)
        throw new NotLiftableException("expects two operands");
      var destination = this.PlaceOf(operands[0], 0);
      if (destination is not VectorPlace { Bytes: var width })
        throw new NotLiftableException("a packed operation writes an MMX or XMM register");
      var source = this.PlaceOf(operands[1], width);
      if (source is not (VectorPlace or MemoryPlace))
        throw new NotLiftableException("a packed operation reads a register or memory");
      return (destination, source, width);
    }

    /// <summary>Reads every lane of both operands, then writes every result: the hardware's snapshot semantics.</summary>
    private bool Lanes(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, int lane, IrBinaryOp op, bool invertDestination = false) {
      var (destination, source, width) = this.Packed(operands);
      var type = Integer(lane);
      var left = new List<IrValue>();
      var right = new List<IrValue>();
      for (var offset = 0; offset < width; offset += lane) {
        left.Add(this.Add(new IrLoad(type, this.LaneAddress(destination, offset))));
        right.Add(this.Add(new IrLoad(type, this.LaneAddress(source, offset))));
      }
      for (var i = 0; i < left.Count; ++i) {
        var a = invertDestination ? this.Add(new IrBinary(IrBinaryOp.Xor, left[i], new IrConstantInt(type, -1))) : left[i];
        var result = this.Add(new IrBinary(op, a, right[i]));
        this.Add(new IrStore(result, this.LaneAddress(destination, i * lane)));
      }
      return true;
    }

    private bool AndNot(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands)
      => this.Lanes(operands, 8, IrBinaryOp.And, invertDestination: true);

    /// <summary>
    /// PSHUFB: each destination byte takes the value byte its mask byte selects (the low three or four
    /// bits), or zero when the mask byte's top bit is set.
    /// </summary>
    private bool Pshufb(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands) {
      var (destination, source, width) = this.Packed(operands);
      var values = new List<IrValue>();
      var masks = new List<IrValue>();
      for (var i = 0; i < width; ++i) {
        values.Add(this.Add(new IrLoad(IrType.I8, this.LaneAddress(destination, i))));
        masks.Add(this.Add(new IrLoad(IrType.I8, this.LaneAddress(source, i))));
      }
      for (var i = 0; i < width; ++i) {
        // select the indexed byte with a chain of selects over the snapshot - no table in memory, so
        // it stays a pure function of the loaded lanes
        var index = this.Add(new IrBinary(IrBinaryOp.And, masks[i], new IrConstantInt(IrType.I8, width - 1)));
        IrValue picked = values[0];
        for (var k = 1; k < width; ++k) {
          var hit = this.Add(new IrCmp(IrCmpPred.Eq, index, new IrConstantInt(IrType.I8, k)));
          picked = this.Add(new IrSelect(hit, values[k], picked));
        }
        var zeroed = this.Add(new IrCmp(IrCmpPred.Slt, masks[i], new IrConstantInt(IrType.I8, 0)));
        var result = this.Add(new IrSelect(zeroed, new IrConstantInt(IrType.I8, 0), picked));
        this.Add(new IrStore(result, this.LaneAddress(destination, i)));
      }
      return true;
    }
  }
}
