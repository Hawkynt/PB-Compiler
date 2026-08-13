namespace PowerBasic.Compiler.Asm;

/// <summary>
/// Reading one inline-assembly statement's REGISTER EFFECT out of its text (see
/// <see cref="AsmRegisterEffect"/>), through the same tokenizer and operand parser that emits it.
///
/// It is deliberately the parser rather than a scan of the characters: only the parser knows which
/// identifier is a register, which is a mnemonic, which is a variable, and which register a memory
/// operand names as its base. A scan that guessed those would be wrong about exactly the statements
/// that matter - <c>MOV AL, ES:[BX]</c> reads <c>BX</c> and writes half of <c>AX</c>, and neither
/// fact is in the spelling of a name.
/// </summary>
public sealed partial class TextAssembler {

  /// <summary>
  /// The register effect of one statement, or <see cref="AsmRegisterEffect.Opaque"/> when the text is
  /// not understood - an unlisted mnemonic, an operand shape the entry does not cover, or a line that
  /// does not parse at all. "Not understood" is a legitimate answer here and never an exception: this
  /// runs on text a back end is DECIDING whether it can honour.
  /// </summary>
  public static AsmRegisterEffect Analyze(string line, IAsmSymbolResolver? resolver) {
    ArgumentNullException.ThrowIfNull(line);
    try {
      return new LineParser(line, resolver, new Assembler()).Effect();
    } catch (Exception) {
      return AsmRegisterEffect.Opaque;
    }
  }

  private sealed partial class LineParser {

    /// <summary>Parses the statement for its effect only - nothing is emitted into the target.</summary>
    public AsmRegisterEffect Effect() {
      if (this.Current.Kind != TokenKind.Identifier)
        return AsmRegisterEffect.Opaque;

      var mnemonic = this.Next().Text.ToUpperInvariant();
      var repeated = mnemonic is "REP" or "REPE" or "REPZ" or "REPNE" or "REPNZ";
      if (repeated) {
        if (this.Current.Kind != TokenKind.Identifier)
          return AsmRegisterEffect.Opaque;

        mnemonic = this.Next().Text.ToUpperInvariant();
      }

      var operands = this.ParseOperands();
      if (this.Current.Kind != TokenKind.End)
        return AsmRegisterEffect.Opaque;

      var effect = new EffectBuilder();
      return Describe(mnemonic, operands, repeated, effect) ? effect.Build() : AsmRegisterEffect.Opaque;
    }

    /// <summary>
    /// The per-mnemonic entry. Returns false for anything it does not model, which is the conservative
    /// answer and the reason the table may stay short: the corpus's inline assembly is a dozen 8086
    /// mnemonics, and a family that is missing costs a decline rather than a wrong answer.
    ///
    /// <para>
    /// Two families are opaque ON PURPOSE rather than for want of an entry. An <c>INT</c> is whatever
    /// its handler reads and writes, which is a property of the interrupt number and the function code
    /// in <c>AH</c> and not of the instruction; a <c>CALL</c> is whatever the callee does. Naming a
    /// plausible register set for either would be the guess this whole model exists to avoid.
    /// </para>
    /// </summary>
    private static bool Describe(string mnemonic, List<Operand> operands, bool repeated, EffectBuilder e) {
      if (repeated) {
        // the prefix counts CX down to zero, and REPE/REPNE also re-test ZF each iteration
        e.Read(Reg.CX);
        e.Define(Reg.CX);
        e.ReadsFlags = true;
      }

      switch (mnemonic) {
        // ---- no operands ----------------------------------------------------------------------
        case "NOP" or "HLT":
          return operands.Count == 0;
        case "CLC" or "STC" or "CLD" or "STD" or "CLI" or "STI":
          e.WritesFlags = true;
          return operands.Count == 0;
        case "CMC":
          e.ReadsFlags = e.WritesFlags = true;
          return operands.Count == 0;
        case "PUSHF":
          e.ReadsFlags = true;
          return operands.Count == 0;
        case "POPF":
          e.WritesFlags = true;
          return operands.Count == 0;
        case "LAHF":
          e.ReadsFlags = true;
          e.Define(Reg.AH);                        // a byte half: AX is defined, not killed
          return operands.Count == 0;
        case "SAHF":
          e.Read(Reg.AH);
          e.WritesFlags = true;
          return operands.Count == 0;
        case "CBW" or "CWDE":
          e.Read(Reg.AX);
          e.Define(Reg.AX);
          return operands.Count == 0;
        case "CWD" or "CDQ":
          e.Read(Reg.AX);
          e.Define(Reg.DX);
          return operands.Count == 0;
        case "XLAT" or "XLATB":
          e.Read(Reg.AL);
          e.Read(Reg.BX);
          e.Define(Reg.AL);
          return operands.Count == 0;

        // ---- string operations ----------------------------------------------------------------
        case "MOVSB" or "MOVSW" or "MOVSD":
          e.Read(Reg.SI);
          e.Read(Reg.DI);
          e.Define(Reg.SI);
          e.Define(Reg.DI);
          return operands.Count == 0;
        case "CMPSB" or "CMPSW" or "CMPSD":
          e.Read(Reg.SI);
          e.Read(Reg.DI);
          e.Define(Reg.SI);
          e.Define(Reg.DI);
          e.WritesFlags = true;
          return operands.Count == 0;
        case "STOSB" or "STOSW" or "STOSD":
          e.Read(Reg.AX);
          e.Read(Reg.DI);
          e.Define(Reg.DI);
          return operands.Count == 0;
        case "SCASB" or "SCASW" or "SCASD":
          e.Read(Reg.AX);
          e.Read(Reg.DI);
          e.Define(Reg.DI);
          e.WritesFlags = true;
          return operands.Count == 0;
        case "LODSB" or "LODSW" or "LODSD":
          e.Read(Reg.SI);
          e.Define(Reg.SI);
          e.Define(mnemonic == "LODSB" ? Reg.AL : Reg.AX);
          return operands.Count == 0;

        // ---- data movement --------------------------------------------------------------------
        case "MOV" or "MOVZX" or "MOVSX" or "LEA" or "LDS" or "LES":
          if (operands.Count != 2)
            return false;

          e.Write(operands[0]);                    // LEA/LDS/LES take only the ADDRESS of operand 1,
          e.Read(operands[1]);                     // which is what Read of a memory operand yields
          return true;
        case "XCHG":
          if (operands.Count != 2)
            return false;

          e.ReadWrite(operands[0]);
          e.ReadWrite(operands[1]);
          return true;
        case "PUSH":
          if (operands.Count != 1)
            return false;

          e.Read(operands[0]);
          return true;
        case "POP":
          if (operands.Count != 1)
            return false;

          e.Write(operands[0]);
          return true;

        // ---- arithmetic and logic --------------------------------------------------------------
        case "ADD" or "SUB" or "AND" or "OR" or "XOR":
          if (operands.Count != 2)
            return false;

          e.ReadWrite(operands[0]);
          e.Read(operands[1]);
          e.WritesFlags = true;
          return true;
        case "ADC" or "SBB":
          if (operands.Count != 2)
            return false;

          e.ReadWrite(operands[0]);
          e.Read(operands[1]);
          e.ReadsFlags = e.WritesFlags = true;
          return true;
        case "CMP" or "TEST":
          if (operands.Count != 2)
            return false;

          e.Read(operands[0]);
          e.Read(operands[1]);
          e.WritesFlags = true;
          return true;
        case "INC" or "DEC" or "NEG":
          if (operands.Count != 1)
            return false;

          e.ReadWrite(operands[0]);
          e.WritesFlags = true;
          return true;
        case "NOT":
          if (operands.Count != 1)
            return false;

          e.ReadWrite(operands[0]);
          return true;
        case "SHL" or "SAL" or "SHR" or "SAR" or "ROL" or "ROR":
          if (operands.Count != 2)
            return false;

          e.ReadWrite(operands[0]);
          e.Read(operands[1]);                     // the count, which is CL when it is not a literal
          e.WritesFlags = true;
          return true;
        case "RCL" or "RCR":
          if (operands.Count != 2)
            return false;

          e.ReadWrite(operands[0]);
          e.Read(operands[1]);
          e.ReadsFlags = e.WritesFlags = true;
          return true;
        // the one-operand forms: the other factor is AX and the answer is DX:AX. AX is written at
        // every width and DX only at sixteen bits, so DX is DEFINED without being killed.
        case "MUL" or "IMUL" or "DIV" or "IDIV":
          if (operands.Count != 1)
            return false;                          // IMUL also has 186+ two- and three-operand forms

          e.Read(operands[0]);
          e.Read(Reg.AX);
          e.Read(Reg.DX);
          e.Define(Reg.AX);
          e.DefinePartial(Reg.DX);
          e.WritesFlags = true;
          return true;

        // ---- transfers of control ---------------------------------------------------------------
        case "JMP":
          if (operands.Count != 1)
            return false;

          e.Read(operands[0]);                     // a label reads nothing; an indirect jump reads its address
          return true;
        case "JCXZ":
          e.Read(Reg.CX);
          return operands.Count == 1;
        case "LOOP" or "LOOPE" or "LOOPZ" or "LOOPNE" or "LOOPNZ":
          e.Read(Reg.CX);
          e.Define(Reg.CX);
          e.ReadsFlags = mnemonic is not "LOOP";
          return operands.Count == 1;

        // ---- ports ------------------------------------------------------------------------------
        case "IN":
          if (operands.Count != 2)
            return false;

          e.Write(operands[0]);
          e.Read(operands[1]);
          return true;
        case "OUT":
          if (operands.Count != 2)
            return false;

          e.Read(operands[0]);
          e.Read(operands[1]);
          return true;

        default:
          // a conditional jump, or nothing this table models: INT, CALL, RET, the FPU, the SIMD families
          if (!TryGetCondition(mnemonic, out _) || operands.Count != 1)
            return false;

          e.ReadsFlags = true;
          e.Read(operands[0]);
          return true;
      }
    }

    /// <summary>
    /// Accumulates one statement's effect, canonicalizing every register to the word one the allocator
    /// hands out and dropping the classes it never hands out at all.
    /// </summary>
    private sealed class EffectBuilder {

      private readonly HashSet<Reg> _reads = [];
      private readonly HashSet<Reg> _defines = [];
      private readonly HashSet<Reg> _kills = [];

      public bool ReadsFlags { get; set; }
      public bool WritesFlags { get; set; }

      public void Read(Reg register) {
        if (Tracked(register) is { } word)
          this._reads.Add(word);
      }

      /// <summary>Records a write: a definition always, and a kill only when the whole register goes.</summary>
      public void Define(Reg register) {
        if (Tracked(register) is not { } word)
          return;

        this._defines.Add(word);
        if (!register.IsByte())
          this._kills.Add(word);
      }

      /// <summary>
      /// Records a write that may leave the old value in place - the <c>DX</c> of a byte-wide
      /// <c>MUL</c>, which only the sixteen-bit form touches. A definition, never a kill.
      /// </summary>
      public void DefinePartial(Reg register) {
        if (Tracked(register) is { } word)
          this._defines.Add(word);
      }

      public void Read(Operand operand) {
        switch (operand) {
          case RegisterOperand register:
            this.Read(register.Register);
            return;
          case MemoryOperand memory:
            this.Address(memory);
            return;
        }
      }

      /// <summary>A written operand: a register is defined, and a memory destination still READS its address.</summary>
      public void Write(Operand operand) {
        switch (operand) {
          case RegisterOperand register:
            this.Define(register.Register);
            return;
          case MemoryOperand memory:
            this.Address(memory);
            return;
        }
      }

      public void ReadWrite(Operand operand) {
        this.Read(operand);
        this.Write(operand);
      }

      public AsmRegisterEffect Build()
        => new(this._reads, this._defines, this._kills, this.ReadsFlags, this.WritesFlags, IsOpaque: false);

      private void Address(MemoryOperand memory) {
        if (memory.Memory.Base is { } @base)
          this.Read(@base);
        if (memory.Memory.Index is { } index)
          this.Read(index);
      }

      /// <summary>
      /// The word register a name contends for, or null for a class this back end never allocates.
      /// <c>AH</c>, <c>AL</c> and <c>EAX</c> are all <c>AX</c> - one resource under three spellings -
      /// while a segment, x87, MMX or SSE register is none of the allocator's business.
      /// </summary>
      private static Reg? Tracked(Reg register) {
        if (register.IsWord())
          return register;
        if (register.IsByte())
          return (Reg)(0x10 | (register.Index() & 0x03));
        if (register.IsDword())
          return (Reg)(0x10 | register.Index());
        return null;
      }
    }
  }
}
