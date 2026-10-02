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
/// Flags are not modelled yet, and neither are jumps, the stack or addressing through registers: an
/// instruction that needs any of them is declined by name, never compiled into something else. The
/// set lifted so far: <c>MOV</c>, <c>ADD</c>, <c>SUB</c>, <c>AND</c>, <c>OR</c>, <c>XOR</c>, <c>NOT</c>,
/// <c>NEG</c>, <c>INC</c>, <c>DEC</c>, <c>SHL</c>/<c>SAL</c>, <c>SHR</c>, <c>SAR</c>, <c>NOP</c>; MMX
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
        if (!node.Routable) {
          declined = $"'{function.Name}': inline assembly naming something that is not a variable: {node.Text.Trim()}";
          return false;
        }
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
      symbol = AsmSymbol.OfMemory(Mem.At(label));
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
        case "NOT": return this.Unary(operands, (v, type) => new IrBinary(IrBinaryOp.Xor, v, new IrConstantInt(type, -1)));
        case "NEG": return this.Unary(operands, (v, type) => new IrBinary(IrBinaryOp.Sub, new IrConstantInt(type, 0), v));
        case "INC": return this.Unary(operands, (v, type) => new IrBinary(IrBinaryOp.Add, v, new IrConstantInt(type, 1)));
        case "DEC": return this.Unary(operands, (v, type) => new IrBinary(IrBinaryOp.Sub, v, new IrConstantInt(type, 1)));
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
    private bool Scalar(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, IrBinaryOp? op) {
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
      var result = op is { } binary
        ? this.Add(new IrBinary(binary, this.Read(destination, bytes), value))
        : value;
      this.Write(destination, result);
      return true;
    }

    private bool Unary(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, Func<IrValue, IrType, IrInstruction> apply) {
      if (operands.Count != 1)
        throw new NotLiftableException("expects one operand");
      var place = this.PlaceOf(operands[0], 0);
      if (place.Bytes == 0 || place is VectorPlace)
        throw new NotLiftableException("the operand size is not stated");
      var result = this.Add(apply(this.Read(place, place.Bytes), Integer(place.Bytes)));
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
