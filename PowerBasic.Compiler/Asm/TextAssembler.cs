using System.Globalization;

namespace PowerBasic.Compiler.Asm;

/// <summary>
/// Parses a single PowerBASIC inline-assembly statement (the text after
/// <c>!</c>) and emits it into an <see cref="Assembler"/>. Identifiers that
/// are not registers are resolved through an <see cref="IAsmSymbolResolver"/>;
/// numeric literals use PB notation (decimal, <c>&amp;H</c>, <c>&amp;O</c>,
/// <c>&amp;B</c>); <c>;</c> starts a comment.
/// </summary>
public sealed class TextAssembler(Assembler target) {

  private readonly Assembler _target = target ?? throw new ArgumentNullException(nameof(target));

  /// <summary>Parses and emits one statement; on failure nothing is emitted and <paramref name="error"/> describes the problem.</summary>
  public bool TryParse(string line, IAsmSymbolResolver? resolver, out string? error) {
    ArgumentNullException.ThrowIfNull(line);

    var checkpoint = this._target.Position;
    try {
      var parser = new LineParser(line, resolver, this._target);
      parser.Assemble();
      error = null;
      return true;
    } catch (AsmSyntaxException exception) {
      this._target.Truncate(checkpoint);
      error = exception.Message;
      return false;
    } catch (ArgumentException exception) {
      this._target.Truncate(checkpoint);
      error = exception.Message;
      return false;
    }
  }

  /// <summary>
  /// The 16-bit general-purpose registers one <c>!</c> statement touches: every register its text
  /// NAMES (in any width - <c>AL</c> and <c>AH</c> are <c>AX</c>) plus the ones its mnemonic implies
  /// without spelling them, such as <c>CX</c> for <c>LOOP</c> or <c>DX:AX</c> for <c>MUL</c>.
  ///
  /// <para>
  /// This is the census a register allocator needs to know which registers are the assembly's rather
  /// than its own (see <c>Backend/InlineAsmReservation</c>). It answers with the whole file when the
  /// text does not tokenize, because a statement nobody can read is a statement that could touch
  /// anything - though such a statement will not assemble either.
  /// </para>
  /// </summary>
  public static IReadOnlyCollection<Reg> RegistersUsed(string line) {
    ArgumentNullException.ThrowIfNull(line);
    return LineParser.RegistersUsed(line);
  }

  /// <summary>The 16-bit general-purpose registers, the widest answer <see cref="RegistersUsed"/> can give.</summary>
  public static IReadOnlyCollection<Reg> AllGeneralPurposeRegisters { get; } =
    [Reg.AX, Reg.CX, Reg.DX, Reg.BX, Reg.SP, Reg.BP, Reg.SI, Reg.DI];

  private sealed class AsmSyntaxException(string message) : Exception(message);

  #region operand model

  private abstract record Operand;
  private sealed record RegisterOperand(Reg Register) : Operand;
  private sealed record StOperand(St Register) : Operand;
  private sealed record ImmediateOperand(int Value) : Operand;
  private sealed record MemoryOperand(Mem Memory) : Operand;
  private sealed record LabelOperand(Label Label) : Operand;

  #endregion

  private sealed class LineParser {

    private enum TokenKind { Identifier, Number, Comma, Colon, LBracket, RBracket, LParen, RParen, Plus, Minus, End }

    private readonly record struct Token(TokenKind Kind, string Text, int Value);

    private readonly List<Token> _tokens;
    private readonly IAsmSymbolResolver? _resolver;
    private readonly Assembler _asm;
    private int _index;

    public LineParser(string line, IAsmSymbolResolver? resolver, Assembler target) {
      this._resolver = resolver;
      this._asm = target;
      this._tokens = Tokenize(line);
    }

    #region tokenizer

    /// <summary>
    /// Splits one statement into tokens. Static because the register census
    /// (<see cref="TextAssembler.RegistersUsed"/>) needs the parser's own idea of what an identifier
    /// is without an assembler to emit into - scanning the text a second way is how the two answers
    /// drift apart.
    /// </summary>
    private static List<Token> Tokenize(string line) {
      var tokens = new List<Token>();
      var comment = line.IndexOf(';');
      if (comment >= 0)
        line = line[..comment];

      var i = 0;
      while (i < line.Length) {
        var c = line[i];
        if (char.IsWhiteSpace(c)) {
          ++i;
          continue;
        }

        switch (c) {
          case ',': tokens.Add(new(TokenKind.Comma, ",", 0)); ++i; continue;
          case ':': tokens.Add(new(TokenKind.Colon, ":", 0)); ++i; continue;
          case '[': tokens.Add(new(TokenKind.LBracket, "[", 0)); ++i; continue;
          case ']': tokens.Add(new(TokenKind.RBracket, "]", 0)); ++i; continue;
          case '(': tokens.Add(new(TokenKind.LParen, "(", 0)); ++i; continue;
          case ')': tokens.Add(new(TokenKind.RParen, ")", 0)); ++i; continue;
          case '+': tokens.Add(new(TokenKind.Plus, "+", 0)); ++i; continue;
          case '-': tokens.Add(new(TokenKind.Minus, "-", 0)); ++i; continue;
        }

        if (c == '&') {
          i = TokenizeRadixNumber(tokens, line, i);
          continue;
        }

        if (char.IsAsciiDigit(c)) {
          var start = i;
          while (i < line.Length && char.IsAsciiDigit(line[i]))
            ++i;

          var text = line[start..i];
          if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw new AsmSyntaxException($"Numeric literal '{text}' is out of range.");

          tokens.Add(new(TokenKind.Number, text, value));
          continue;
        }

        if (char.IsAsciiLetter(c) || c == '_') {
          var start = i;
          // dotted QB-style variable names (BR.Char) are one identifier
          while (i < line.Length && (char.IsAsciiLetterOrDigit(line[i]) || line[i] == '_'
                 || (line[i] == '.' && i + 1 < line.Length && (char.IsAsciiLetterOrDigit(line[i + 1]) || line[i + 1] == '_'))))
            ++i;

          // BASIC type suffixes stay part of the operand name (Foff%, x??, d#)
          while (i < line.Length && line[i] is '%' or '&' or '!' or '#' or '?' or '$')
            ++i;

          tokens.Add(new(TokenKind.Identifier, line[start..i], 0));
          continue;
        }

        throw new AsmSyntaxException($"Unexpected character '{c}'.");
      }

      tokens.Add(new(TokenKind.End, "", 0));
      return tokens;
    }

    private static int TokenizeRadixNumber(List<Token> tokens, string line, int i) {
      if (i + 1 >= line.Length)
        throw new AsmSyntaxException("Dangling '&'.");

      var radixChar = char.ToUpperInvariant(line[i + 1]);
      var (radix, isDigit) = radixChar switch {
        'H' => (16, (Func<char, bool>)char.IsAsciiHexDigit),
        'O' => (8, c => c is >= '0' and <= '7'),
        'B' => (2, c => c is '0' or '1'),
        _ => throw new AsmSyntaxException($"Unknown number prefix '&{radixChar}' (use &H, &O or &B)."),
      };

      var start = i + 2;
      var end = start;
      while (end < line.Length && isDigit(line[end]))
        ++end;

      if (end == start)
        throw new AsmSyntaxException($"Number expected after '&{radixChar}'.");

      var text = line[start..end];
      long value;
      try {
        value = Convert.ToInt64(text, radix);
      } catch (OverflowException) {
        throw new AsmSyntaxException($"Numeric literal '&{radixChar}{text}' is out of range.");
      }

      if (value > uint.MaxValue)
        throw new AsmSyntaxException($"Numeric literal '&{radixChar}{text}' is out of range.");

      tokens.Add(new(TokenKind.Number, text, unchecked((int)value)));
      return end;
    }

    #endregion

    #region token stream helpers

    private Token Current => this._tokens[this._index];
    private Token Peek(int offset = 1) => this._tokens[Math.Min(this._index + offset, this._tokens.Count - 1)];
    private Token Next() => this._tokens[this._index++];

    private void Expect(TokenKind kind, string what) {
      if (this.Current.Kind != kind)
        throw new AsmSyntaxException($"Expected {what} but found '{this.Current.Text}'.");

      ++this._index;
    }

    private bool IsKeyword(string keyword) => this.Current.Kind == TokenKind.Identifier && keyword.Equals(this.Current.Text, StringComparison.OrdinalIgnoreCase);

    private static AsmSyntaxException Unexpected(in Token token) => new($"Unexpected token '{token.Text}'.");

    #endregion

    public void Assemble() {
      if (this.Current.Kind == TokenKind.End)
        throw new AsmSyntaxException("Empty statement.");
      if (this.Current.Kind != TokenKind.Identifier)
        throw new AsmSyntaxException($"Mnemonic expected, found '{this.Current.Text}'.");

      var mnemonic = this.Next().Text.ToUpperInvariant();
      switch (mnemonic) {
        case "REP" or "REPE" or "REPZ":
          this._asm.Rep();
          mnemonic = this.RequireStringMnemonic();
          break;
        case "REPNE" or "REPNZ":
          this._asm.Repne();
          mnemonic = this.RequireStringMnemonic();
          break;
      }

      this.Dispatch(mnemonic);
      if (this.Current.Kind != TokenKind.End)
        throw Unexpected(this.Current);
    }

    private string RequireStringMnemonic() {
      if (this.Current.Kind != TokenKind.Identifier)
        throw new AsmSyntaxException("String instruction expected after REP prefix.");

      var mnemonic = this.Next().Text.ToUpperInvariant();
      if (mnemonic is not ("MOVSB" or "MOVSW" or "MOVSD" or "CMPSB" or "CMPSW" or "CMPSD" or "STOSB" or "STOSW" or "STOSD" or "LODSB" or "LODSW" or "LODSD" or "SCASB" or "SCASW" or "SCASD"))
        throw new AsmSyntaxException($"'{mnemonic}' cannot take a REP prefix.");

      return mnemonic;
    }

    #region operand parsing

    private static readonly Dictionary<string, Reg> _REGISTERS = Enum.GetValues<Reg>().ToDictionary(r => r.ToString(), r => r, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The registers a mnemonic uses without naming them. Everything here is architectural: a
    /// <c>MOVSB</c> reads SI and writes DI whether or not the text says so, a <c>REP</c> prefix counts
    /// down CX, and a <c>MUL</c> answers in DX:AX. A register census built only from the text would
    /// call these free and let a later value be put in one.
    /// </summary>
    private static readonly Dictionary<string, Reg[]> _IMPLICIT_REGISTERS = new(StringComparer.OrdinalIgnoreCase) {
      ["MOVSB"] = [Reg.SI, Reg.DI], ["MOVSW"] = [Reg.SI, Reg.DI], ["MOVSD"] = [Reg.SI, Reg.DI],
      ["CMPSB"] = [Reg.SI, Reg.DI], ["CMPSW"] = [Reg.SI, Reg.DI], ["CMPSD"] = [Reg.SI, Reg.DI],
      ["STOSB"] = [Reg.DI, Reg.AX], ["STOSW"] = [Reg.DI, Reg.AX], ["STOSD"] = [Reg.DI, Reg.AX],
      ["LODSB"] = [Reg.SI, Reg.AX], ["LODSW"] = [Reg.SI, Reg.AX], ["LODSD"] = [Reg.SI, Reg.AX],
      ["SCASB"] = [Reg.DI, Reg.AX], ["SCASW"] = [Reg.DI, Reg.AX], ["SCASD"] = [Reg.DI, Reg.AX],
      ["REP"] = [Reg.CX], ["REPE"] = [Reg.CX], ["REPZ"] = [Reg.CX], ["REPNE"] = [Reg.CX], ["REPNZ"] = [Reg.CX],
      ["LOOP"] = [Reg.CX], ["LOOPE"] = [Reg.CX], ["LOOPZ"] = [Reg.CX], ["LOOPNE"] = [Reg.CX], ["LOOPNZ"] = [Reg.CX],
      ["JCXZ"] = [Reg.CX],
      ["MUL"] = [Reg.AX, Reg.DX], ["IMUL"] = [Reg.AX, Reg.DX], ["DIV"] = [Reg.AX, Reg.DX], ["IDIV"] = [Reg.AX, Reg.DX],
      ["CBW"] = [Reg.AX], ["CWD"] = [Reg.AX, Reg.DX],
      ["XLAT"] = [Reg.AX, Reg.BX], ["XLATB"] = [Reg.AX, Reg.BX],
      ["AAA"] = [Reg.AX], ["AAS"] = [Reg.AX], ["AAM"] = [Reg.AX], ["AAD"] = [Reg.AX],
      ["DAA"] = [Reg.AX], ["DAS"] = [Reg.AX],
      ["IN"] = [Reg.AX, Reg.DX], ["OUT"] = [Reg.AX, Reg.DX],
      ["PUSHA"] = [Reg.AX, Reg.CX, Reg.DX, Reg.BX, Reg.SP, Reg.BP, Reg.SI, Reg.DI],
      ["POPA"] = [Reg.AX, Reg.CX, Reg.DX, Reg.BX, Reg.SP, Reg.BP, Reg.SI, Reg.DI],
    };

    /// <summary>
    /// Every 16-bit general-purpose register this statement names or implies - see
    /// <see cref="TextAssembler.RegistersUsed"/>. Identifiers are classified exactly as
    /// <see cref="ParseIdentifierOperand"/> classifies them, so what counts as a register here is what
    /// the assembler will really assemble as one.
    /// </summary>
    public static IReadOnlyCollection<Reg> RegistersUsed(string line) {
      List<Token> tokens;
      try {
        tokens = Tokenize(line);
      } catch (AsmSyntaxException) {
        return AllGeneralPurposeRegisters;
      }

      var used = new HashSet<Reg>();
      foreach (var token in tokens) {
        if (token.Kind != TokenKind.Identifier)
          continue;
        if (_REGISTERS.TryGetValue(token.Text, out var register)) {
          if (WordFormOf(register) is { } word)
            used.Add(word);
          continue;
        }
        // not a register: a mnemonic, a prefix, or an operand name. Only the first two can be in the
        // table, and a variable that happens to be spelled MUL only overstates the answer.
        if (_IMPLICIT_REGISTERS.TryGetValue(token.Text, out var implied))
          used.UnionWith(implied);
      }
      return used;
    }

    /// <summary>The 16-bit register a general-purpose name denotes (AH and EAX are both AX); null for anything else.</summary>
    private static Reg? WordFormOf(Reg register) => register switch {
      _ when register.IsWord() => register,
      // the byte names are numbered AL CL DL BL AH CH DH BH, so the low two bits pick the word register
      _ when register.IsByte() => (Reg)(0x10 | (register.Index() & 3)),
      _ when register.IsDword() => (Reg)(0x10 | register.Index()),
      _ => null,   // a segment, x87, MMX or SSE register is not the integer allocator's business
    };

    private static readonly Dictionary<string, OperandSize> _SIZE_KEYWORDS = new(StringComparer.OrdinalIgnoreCase) {
      ["BYTE"] = OperandSize.Byte,
      ["WORD"] = OperandSize.Word,
      ["DWORD"] = OperandSize.Dword,
      ["QWORD"] = OperandSize.Qword,
      ["TBYTE"] = OperandSize.Tbyte,
      ["TWORD"] = OperandSize.Tbyte,
    };

    private List<Operand> ParseOperands() {
      var result = new List<Operand>();
      if (this.Current.Kind == TokenKind.End)
        return result;

      result.Add(this.ParseOperand());
      while (this.Current.Kind == TokenKind.Comma) {
        ++this._index;
        result.Add(this.ParseOperand());
      }

      return result;
    }

    private Operand ParseOperand() {
      var token = this.Current;
      switch (token.Kind) {
        case TokenKind.Number:
          ++this._index;
          return new ImmediateOperand(token.Value);

        case TokenKind.Minus:
          ++this._index;
          if (this.Current.Kind != TokenKind.Number)
            throw new AsmSyntaxException("Number expected after '-'.");

          return new ImmediateOperand(-this.Next().Value);

        case TokenKind.LBracket:
          return this.ParseMemory(OperandSize.None, null);

        case TokenKind.Identifier:
          return this.ParseIdentifierOperand();

        default:
          throw Unexpected(token);
      }
    }

    private Operand ParseIdentifierOperand() {
      var name = this.Current.Text;

      if (_SIZE_KEYWORDS.TryGetValue(name, out var size)) {
        ++this._index;
        if (this.IsKeyword("PTR"))
          ++this._index;

        return this.ParseSizedOperand(size);
      }

      // "ST" is the FPU stack top - unless a PB variable of that name shadows it
      // and no ST(n) indexing follows
      if (name.Equals("ST", StringComparison.OrdinalIgnoreCase)
          && (this.Peek().Kind == TokenKind.LParen || this._resolver?.TryResolve(name, out _) is not true))
        return new StOperand(this.ParseSt());

      if (_REGISTERS.TryGetValue(name, out var register)) {
        if (register.IsSegment() && this.Peek().Kind == TokenKind.Colon) {
          this._index += 2;
          return this.ParseSizedOperand(OperandSize.None, register);
        }

        ++this._index;
        return new RegisterOperand(register);
      }

      ++this._index;
      var symbol = this.Resolve(name);
      switch (symbol.Kind) {
        case AsmSymbolKind.Constant:
          return new ImmediateOperand(symbol.Value);
        case AsmSymbolKind.Label:
          return new LabelOperand(symbol.Label!);
        case AsmSymbolKind.Memory:
          if (this.Current.Kind == TokenKind.LBracket)
            return this.ParseMemory(OperandSize.None, null, symbol.Memory);

          return new MemoryOperand(symbol.Memory);
        default:
          throw new AsmSyntaxException($"Symbol '{name}' resolved to an unknown kind.");
      }
    }

    private Operand ParseSizedOperand(OperandSize size, Reg? segmentOverride = null) {
      var token = this.Current;
      if (token.Kind == TokenKind.LBracket)
        return this.ParseMemory(size, segmentOverride);

      if (token.Kind == TokenKind.Identifier && !_REGISTERS.ContainsKey(token.Text) && !_SIZE_KEYWORDS.ContainsKey(token.Text)) {
        ++this._index;
        var symbol = this.Resolve(token.Text);
        if (symbol.Kind != AsmSymbolKind.Memory)
          throw new AsmSyntaxException($"Symbol '{token.Text}' is not a memory operand.");

        Mem? seed = symbol.Memory;
        if (this.Current.Kind == TokenKind.LBracket)
          return this.ParseMemory(size, segmentOverride, seed);

        var memory = symbol.Memory;
        if (size != OperandSize.None)
          memory = memory.WithSize(size);
        if (segmentOverride is { } segment)
          memory = memory.Seg(segment);

        return new MemoryOperand(memory);
      }

      if (token.Kind == TokenKind.Identifier && _SIZE_KEYWORDS.TryGetValue(token.Text, out var innerSize)) {
        if (size != OperandSize.None)
          throw new AsmSyntaxException("Duplicate operand size keyword.");

        ++this._index;
        if (this.IsKeyword("PTR"))
          ++this._index;

        return this.ParseSizedOperand(innerSize, segmentOverride);
      }

      throw new AsmSyntaxException($"Memory operand expected, found '{token.Text}'.");
    }

    private St ParseSt() {
      ++this._index; // ST
      if (this.Current.Kind != TokenKind.LParen)
        return St.St0;

      ++this._index;
      if (this.Current.Kind != TokenKind.Number || this.Current.Value is < 0 or > 7)
        throw new AsmSyntaxException("ST(n) needs an index 0..7.");

      var index = this.Next().Value;
      this.Expect(TokenKind.RParen, "')'");
      return new(index);
    }

    private MemoryOperand ParseMemory(OperandSize size, Reg? segmentOverride, Mem? seed = null) {
      var @base = seed?.Base;
      var index = seed?.Index;
      var label = seed?.Label;
      var displacement = seed?.Displacement ?? 0;
      var segment = segmentOverride ?? seed?.Segment;

      this.Expect(TokenKind.LBracket, "'['");
      var expectTerm = true;
      var hasTerms = false;
      while (this.Current.Kind != TokenKind.RBracket) {
        hasTerms = true;
        var token = this.Next();
        switch (token.Kind) {
          case TokenKind.Plus when !expectTerm:
            expectTerm = true;
            continue;

          case TokenKind.Minus:
            if (this.Current.Kind != TokenKind.Number)
              throw new AsmSyntaxException("Number expected after '-'.");

            displacement -= this.Next().Value;
            expectTerm = false;
            continue;

          case TokenKind.Number when expectTerm:
            displacement += token.Value;
            expectTerm = false;
            continue;

          case TokenKind.Identifier when expectTerm: {
            if (_REGISTERS.TryGetValue(token.Text, out var register)) {
              AddAddressRegister(register, ref @base, ref index);
              expectTerm = false;
              continue;
            }

            var symbol = this.Resolve(token.Text);
            switch (symbol.Kind) {
              case AsmSymbolKind.Constant:
                displacement += symbol.Value;
                break;
              case AsmSymbolKind.Memory:
                if (symbol.Memory.Base is { } symbolBase)
                  AddAddressRegister(symbolBase, ref @base, ref index);
                if (symbol.Memory.Index is { } symbolIndex)
                  AddAddressRegister(symbolIndex, ref @base, ref index);
                if (symbol.Memory.Label is { } symbolLabel) {
                  if (label is not null)
                    throw new AsmSyntaxException("Only one label per memory operand.");

                  label = symbolLabel;
                }

                displacement += symbol.Memory.Displacement;
                segment ??= symbol.Memory.Segment;
                break;
              default:
                throw new AsmSyntaxException($"Symbol '{token.Text}' cannot be used inside a memory operand.");
            }

            expectTerm = false;
            continue;
          }

          default:
            throw Unexpected(token);
        }
      }

      ++this._index; // ']'
      if (expectTerm && hasTerms)
        throw new AsmSyntaxException("Term expected after '+' in memory operand.");
      if (!hasTerms && seed is null)
        throw new AsmSyntaxException("Empty memory operand.");

      var memory = (@base, index, label) switch {
        (null, null, null) => Mem.At(displacement),
        (null, null, { } l) => Mem.At(l, displacement),
        ({ } b, null, null) => Mem.At(b, displacement),
        ({ } b, null, { } l) => Mem.At(b, l, displacement),
        ({ } b, { } i, null) => Mem.At(b, i, displacement),
        (null, { } i, null) => Mem.At(i, displacement),
        _ => throw new AsmSyntaxException("Memory operand is too complex (label with base and index)."),
      };

      if (size != OperandSize.None)
        memory = memory.WithSize(size);
      if (segment is { } s)
        memory = memory.Seg(s);

      return new(memory);
    }

    private static void AddAddressRegister(Reg register, ref Reg? @base, ref Reg? index) {
      switch (register) {
        case Reg.BX or Reg.BP:
          if (@base is not null)
            throw new AsmSyntaxException($"Two base registers in memory operand ({@base}+{register}).");

          @base = register;
          return;
        case Reg.SI or Reg.DI:
          if (index is not null)
            throw new AsmSyntaxException($"Two index registers in memory operand ({index}+{register}).");

          index = register;
          return;
        default:
          throw new AsmSyntaxException($"{register} cannot address memory in 16-bit mode.");
      }
    }

    private AsmSymbol Resolve(string name) {
      if (this._resolver is null || !this._resolver.TryResolve(name, out var symbol))
        throw new AsmSyntaxException($"Unknown symbol '{name}'.");

      return symbol;
    }

    #endregion

    #region dispatch

    private void Dispatch(string mnemonic) {
      switch (mnemonic) {
        // no-operand instructions
        case "NOP": this.NoOperands(mnemonic); this._asm.Nop(); return;
        case "HLT": this.NoOperands(mnemonic); this._asm.Hlt(); return;
        case "BSWAP":
          if (this.OneOperand() is RegisterOperand bswap)
            this._asm.Bswap(bswap.Register);
          else
            throw new AsmSyntaxException("BSWAP takes a 32-bit register.");
          return;
        // MMX (Pentium) / SSE2 (XMM) integer SIMD intrinsics for inline asm - same mnemonic,
        // the assembler selects the MMX or 66-prefixed SSE2 encoding by the operand register class
        case "EMMS": this.NoOperands(mnemonic); this._asm.Emms(); return;
        case "MOVD": this.BinaryMovd(); return;
        case "MOVQ": this.BinaryMovq(); return;
        case "MOVDQA": this.BinaryMovdqa(unaligned: false); return;
        case "MOVDQU": this.BinaryMovdqa(unaligned: true); return;
        case "PADDB": this.PackedBinary(0xFC); return;
        case "PADDW": this.PackedBinary(0xFD); return;
        case "PADDD": this.PackedBinary(0xFE); return;
        case "PADDQ": this.PackedBinary(0xD4); return;
        case "PSUBB": this.PackedBinary(0xF8); return;
        case "PSUBW": this.PackedBinary(0xF9); return;
        case "PSUBD": this.PackedBinary(0xFA); return;
        case "PSUBQ": this.PackedBinary(0xFB); return;
        case "PADDSW": this.PackedBinary(0xED); return;
        case "PADDUSW": this.PackedBinary(0xDD); return;
        case "PSUBSW": this.PackedBinary(0xE9); return;
        case "PSUBUSW": this.PackedBinary(0xD9); return;
        case "PMULLW": this.PackedBinary(0xD5); return;
        case "PMULHW": this.PackedBinary(0xE5); return;
        case "PAND": this.PackedBinary(0xDB); return;
        case "PANDN": this.PackedBinary(0xDF); return;
        case "POR": this.PackedBinary(0xEB); return;
        case "PXOR": this.PackedBinary(0xEF); return;
        case "PCMPEQB": this.PackedBinary(0x74); return;
        case "PCMPEQW": this.PackedBinary(0x75); return;
        case "PCMPEQD": this.PackedBinary(0x76); return;
        case "PCMPGTB": this.PackedBinary(0x64); return;
        case "PCMPGTW": this.PackedBinary(0x65); return;
        case "PCMPGTD": this.PackedBinary(0x66); return;
        case "PACKSSWB": this.PackedBinary(0x63); return;
        case "PACKSSDW": this.PackedBinary(0x6B); return;
        case "PACKUSWB": this.PackedBinary(0x67); return;
        case "PUNPCKLBW": this.PackedBinary(0x60); return;
        case "PUNPCKLWD": this.PackedBinary(0x61); return;
        case "PUNPCKLDQ": this.PackedBinary(0x62); return;
        case "PUNPCKHBW": this.PackedBinary(0x68); return;
        case "PUNPCKHWD": this.PackedBinary(0x69); return;
        case "PUNPCKHDQ": this.PackedBinary(0x6A); return;
        case "PSLLW": this.PackedShift(0x71, 6, this._asm.Psllw); return;
        case "PSLLD": this.PackedShift(0x72, 6, this._asm.Pslld); return;
        case "PSLLQ": this.PackedShift(0x73, 6, this._asm.Psllq); return;
        case "PSRLW": this.PackedShift(0x71, 2, this._asm.Psrlw); return;
        case "PSRLD": this.PackedShift(0x72, 2, this._asm.Psrld); return;
        case "PSRLQ": this.PackedShift(0x73, 2, this._asm.Psrlq); return;
        case "PSRAW": this.PackedShift(0x71, 4, this._asm.Psraw); return;
        case "PSRAD": this.PackedShift(0x72, 4, this._asm.Psrad); return;

        // AVX (VEX-encoded, 3-operand) packed-integer intrinsics on XMM/YMM
        case "VMOVDQA": this.VexMove(unaligned: false); return;
        case "VMOVDQU": this.VexMove(unaligned: true); return;
        case "VPADDB": this.VexBinary(0xFC); return;
        case "VPADDW": this.VexBinary(0xFD); return;
        case "VPADDD": this.VexBinary(0xFE); return;
        case "VPADDQ": this.VexBinary(0xD4); return;
        case "VPSUBB": this.VexBinary(0xF8); return;
        case "VPSUBW": this.VexBinary(0xF9); return;
        case "VPSUBD": this.VexBinary(0xFA); return;
        case "VPSUBQ": this.VexBinary(0xFB); return;
        case "VPMULLW": this.VexBinary(0xD5); return;
        case "VPAND": this.VexBinary(0xDB); return;
        case "VPANDN": this.VexBinary(0xDF); return;
        case "VPOR": this.VexBinary(0xEB); return;
        case "VPXOR": this.VexBinary(0xEF); return;
        case "VPCMPEQB": this.VexBinary(0x74); return;
        case "VPCMPEQW": this.VexBinary(0x75); return;
        case "VPCMPEQD": this.VexBinary(0x76); return;
        case "VPCMPGTB": this.VexBinary(0x64); return;
        case "VPCMPGTW": this.VexBinary(0x65); return;
        case "VPCMPGTD": this.VexBinary(0x66); return;

        // 686+ conditional moves (branchless)
        case "CMOVE" or "CMOVZ": this.BinaryCmov(Condition.Equal); return;
        case "CMOVNE" or "CMOVNZ": this.BinaryCmov(Condition.NotEqual); return;
        case "CMOVL": this.BinaryCmov(Condition.Less); return;
        case "CMOVLE": this.BinaryCmov(Condition.LessOrEqual); return;
        case "CMOVG": this.BinaryCmov(Condition.Greater); return;
        case "CMOVGE": this.BinaryCmov(Condition.GreaterOrEqual); return;
        case "CMOVB" or "CMOVC": this.BinaryCmov(Condition.Below); return;
        case "CMOVBE": this.BinaryCmov(Condition.BelowOrEqual); return;
        case "CMOVA": this.BinaryCmov(Condition.Above); return;
        case "CMOVAE" or "CMOVNC": this.BinaryCmov(Condition.AboveOrEqual); return;
        case "CMOVS": this.BinaryCmov(Condition.Sign); return;
        case "CMOVNS": this.BinaryCmov(Condition.NotSign); return;
        case "CMOVO": this.BinaryCmov(Condition.Overflow); return;
        case "CMOVNO": this.BinaryCmov(Condition.NotOverflow); return;

        case "CBW": this.NoOperands(mnemonic); this._asm.Cbw(); return;
        case "CWD": this.NoOperands(mnemonic); this._asm.Cwd(); return;
        case "CWDE": this.NoOperands(mnemonic); this._asm.Cwde(); return;
        case "CDQ": this.NoOperands(mnemonic); this._asm.Cdq(); return;
        case "CLC": this.NoOperands(mnemonic); this._asm.Clc(); return;
        case "STC": this.NoOperands(mnemonic); this._asm.Stc(); return;
        case "CMC": this.NoOperands(mnemonic); this._asm.Cmc(); return;
        case "CLD": this.NoOperands(mnemonic); this._asm.Cld(); return;
        case "STD": this.NoOperands(mnemonic); this._asm.Std(); return;
        case "CLI": this.NoOperands(mnemonic); this._asm.Cli(); return;
        case "STI": this.NoOperands(mnemonic); this._asm.Sti(); return;
        case "LAHF": this.NoOperands(mnemonic); this._asm.Lahf(); return;
        case "SAHF": this.NoOperands(mnemonic); this._asm.Sahf(); return;
        case "XLAT" or "XLATB": this.NoOperands(mnemonic); this._asm.Xlat(); return;
        case "PUSHA": this.NoOperands(mnemonic); this._asm.Pusha(); return;
        case "POPA": this.NoOperands(mnemonic); this._asm.Popa(); return;
        case "PUSHF": this.NoOperands(mnemonic); this._asm.Pushf(); return;
        case "POPF": this.NoOperands(mnemonic); this._asm.Popf(); return;
        case "INT3": this.NoOperands(mnemonic); this._asm.Int3(); return;
        case "INTO": this.NoOperands(mnemonic); this._asm.Into(); return;
        case "IRET": this.NoOperands(mnemonic); this._asm.Iret(); return;
        case "MOVSB": this.NoOperands(mnemonic); this._asm.Movsb(); return;
        case "MOVSW": this.NoOperands(mnemonic); this._asm.Movsw(); return;
        case "MOVSD": this.NoOperands(mnemonic); this._asm.Movsd(); return;
        case "CMPSB": this.NoOperands(mnemonic); this._asm.Cmpsb(); return;
        case "CMPSW": this.NoOperands(mnemonic); this._asm.Cmpsw(); return;
        case "CMPSD": this.NoOperands(mnemonic); this._asm.Cmpsd(); return;
        case "STOSB": this.NoOperands(mnemonic); this._asm.Stosb(); return;
        case "STOSW": this.NoOperands(mnemonic); this._asm.Stosw(); return;
        case "STOSD": this.NoOperands(mnemonic); this._asm.Stosd(); return;
        case "LODSB": this.NoOperands(mnemonic); this._asm.Lodsb(); return;
        case "LODSW": this.NoOperands(mnemonic); this._asm.Lodsw(); return;
        case "LODSD": this.NoOperands(mnemonic); this._asm.Lodsd(); return;
        case "SCASB": this.NoOperands(mnemonic); this._asm.Scasb(); return;
        case "SCASW": this.NoOperands(mnemonic); this._asm.Scasw(); return;
        case "SCASD": this.NoOperands(mnemonic); this._asm.Scasd(); return;

        case "MOV": this.BinaryMov(); return;
        case "XCHG": this.BinaryXchg(); return;
        case "LEA": this.BinaryRegMem(this._asm.Lea); return;
        case "LDS": this.BinaryRegMem(this._asm.Lds); return;
        case "LES": this.BinaryRegMem(this._asm.Les); return;
        case "MOVZX": this.BinaryExtend(this._asm.Movzx, this._asm.Movzx); return;
        case "MOVSX": this.BinaryExtend(this._asm.Movsx, this._asm.Movsx); return;

        case "ADD": this.BinaryAlu(this._asm.Add, this._asm.Add, this._asm.Add, this._asm.Add, this._asm.Add); return;
        case "OR": this.BinaryAlu(this._asm.Or, this._asm.Or, this._asm.Or, this._asm.Or, this._asm.Or); return;
        case "ADC": this.BinaryAlu(this._asm.Adc, this._asm.Adc, this._asm.Adc, this._asm.Adc, this._asm.Adc); return;
        case "SBB": this.BinaryAlu(this._asm.Sbb, this._asm.Sbb, this._asm.Sbb, this._asm.Sbb, this._asm.Sbb); return;
        case "AND": this.BinaryAlu(this._asm.And, this._asm.And, this._asm.And, this._asm.And, this._asm.And); return;
        case "SUB": this.BinaryAlu(this._asm.Sub, this._asm.Sub, this._asm.Sub, this._asm.Sub, this._asm.Sub); return;
        case "XOR": this.BinaryAlu(this._asm.Xor, this._asm.Xor, this._asm.Xor, this._asm.Xor, this._asm.Xor); return;
        case "CMP": this.BinaryAlu(this._asm.Cmp, this._asm.Cmp, this._asm.Cmp, this._asm.Cmp, this._asm.Cmp); return;
        case "TEST": this.BinaryAlu(this._asm.Test, this._asm.Test, this._asm.Test, this._asm.Test, this._asm.Test); return;

        case "NOT": this.UnaryRegMem(this._asm.Not, this._asm.Not); return;
        case "NEG": this.UnaryRegMem(this._asm.Neg, this._asm.Neg); return;
        case "MUL": this.UnaryRegMem(this._asm.Mul, this._asm.Mul); return;
        case "DIV": this.UnaryRegMem(this._asm.Div, this._asm.Div); return;
        case "IDIV": this.UnaryRegMem(this._asm.Idiv, this._asm.Idiv); return;
        case "INC": this.UnaryRegMem(this._asm.Inc, this._asm.Inc); return;
        case "DEC": this.UnaryRegMem(this._asm.Dec, this._asm.Dec); return;
        case "IMUL": this.Imul(); return;

        case "SHL" or "SAL": this.Shift(this._asm.Shl, this._asm.Shl, this._asm.Shl, this._asm.Shl); return;
        case "SHR": this.Shift(this._asm.Shr, this._asm.Shr, this._asm.Shr, this._asm.Shr); return;
        case "SAR": this.Shift(this._asm.Sar, this._asm.Sar, this._asm.Sar, this._asm.Sar); return;
        case "ROL": this.Shift(this._asm.Rol, this._asm.Rol, this._asm.Rol, this._asm.Rol); return;
        case "ROR": this.Shift(this._asm.Ror, this._asm.Ror, this._asm.Ror, this._asm.Ror); return;
        case "RCL": this.Shift(this._asm.Rcl, this._asm.Rcl, this._asm.Rcl, this._asm.Rcl); return;
        case "RCR": this.Shift(this._asm.Rcr, this._asm.Rcr, this._asm.Rcr, this._asm.Rcr); return;

        case "PUSH": this.Push(); return;
        case "POP": this.Pop(); return;

        case "JMP": this.Jump(); return;
        case "CALL": this.CallTarget(); return;
        case "RET" or "RETN": this.Return(this._asm.Ret, this._asm.Ret); return;
        case "RETF": this.Return(this._asm.Retf, this._asm.Retf); return;
        case "LOOP": this._asm.Loop(this.RequireLabel()); return;
        case "LOOPE" or "LOOPZ": this._asm.Loope(this.RequireLabel()); return;
        case "LOOPNE" or "LOOPNZ": this._asm.Loopne(this.RequireLabel()); return;
        case "JCXZ": this._asm.Jcxz(this.RequireLabel()); return;

        case "INT": this.Interrupt(); return;
        case "IN": this.InPort(); return;
        case "OUT": this.OutPort(); return;

        default:
          if (TryGetCondition(mnemonic, out var condition)) {
            this._asm.J(condition, this.RequireLabel());
            return;
          }

          if (this.TryDispatchFpu(mnemonic))
            return;

          throw new AsmSyntaxException($"Unknown mnemonic '{mnemonic}'.");
      }
    }

    private static bool TryGetCondition(string mnemonic, out Condition condition) {
      condition = mnemonic switch {
        "JO" => Condition.Overflow,
        "JNO" => Condition.NotOverflow,
        "JB" or "JC" or "JNAE" => Condition.Below,
        "JAE" or "JNC" or "JNB" => Condition.AboveOrEqual,
        "JE" or "JZ" => Condition.Equal,
        "JNE" or "JNZ" => Condition.NotEqual,
        "JBE" or "JNA" => Condition.BelowOrEqual,
        "JA" or "JNBE" => Condition.Above,
        "JS" => Condition.Sign,
        "JNS" => Condition.NotSign,
        "JP" or "JPE" => Condition.Parity,
        "JNP" or "JPO" => Condition.NotParity,
        "JL" or "JNGE" => Condition.Less,
        "JGE" or "JNL" => Condition.GreaterOrEqual,
        "JLE" or "JNG" => Condition.LessOrEqual,
        "JG" or "JNLE" => Condition.Greater,
        _ => (Condition)0xFF,
      };
      return condition != (Condition)0xFF;
    }

    #endregion

    #region instruction handlers

    private void NoOperands(string mnemonic) {
      if (this.Current.Kind != TokenKind.End)
        throw new AsmSyntaxException($"{mnemonic} takes no operands.");
    }

    private (Operand First, Operand Second) TwoOperands() {
      var operands = this.ParseOperands();
      if (operands.Count != 2)
        throw new AsmSyntaxException($"Two operands expected, found {operands.Count}.");

      return (operands[0], operands[1]);
    }

    private Operand OneOperand() {
      var operands = this.ParseOperands();
      if (operands.Count != 1)
        throw new AsmSyntaxException($"One operand expected, found {operands.Count}.");

      return operands[0];
    }

    private void BinaryMov() {
      var (first, second) = this.TwoOperands();
      switch (first, second) {
        case (RegisterOperand d, RegisterOperand s): this._asm.Mov(d.Register, s.Register); return;
        case (RegisterOperand d, MemoryOperand s): this._asm.Mov(d.Register, SizedLike(s.Memory, d.Register)); return;
        case (RegisterOperand d, ImmediateOperand s): this._asm.Mov(d.Register, s.Value); return;
        case (RegisterOperand d, LabelOperand s): this._asm.Mov(d.Register, Imm.OffsetOf(s.Label)); return;
        case (MemoryOperand d, RegisterOperand s): this._asm.Mov(SizedLike(d.Memory, s.Register), s.Register); return;
        case (MemoryOperand d, ImmediateOperand s): this._asm.Mov(SizedLike(d.Memory, null), s.Value); return;
        case (MemoryOperand d, LabelOperand s): this._asm.Mov(d.Memory, Imm.OffsetOf(s.Label)); return;
        default: throw new AsmSyntaxException("Invalid MOV operand combination.");
      }
    }

    private void BinaryXchg() {
      var (first, second) = this.TwoOperands();
      switch (first, second) {
        case (RegisterOperand a, RegisterOperand b): this._asm.Xchg(a.Register, b.Register); return;
        case (RegisterOperand a, MemoryOperand b): this._asm.Xchg(a.Register, SizedLike(b.Memory, a.Register)); return;
        case (MemoryOperand a, RegisterOperand b): this._asm.Xchg(SizedLike(a.Memory, b.Register), b.Register); return;
        default: throw new AsmSyntaxException("Invalid XCHG operand combination.");
      }
    }

    private void BinaryRegMem(Action<Reg, Mem> emit) {
      var (first, second) = this.TwoOperands();
      if (first is not RegisterOperand register || second is not MemoryOperand memory)
        throw new AsmSyntaxException("Register, memory operands expected.");

      emit(register.Register, memory.Memory);
    }

    // CMOVcc dest, src/mem (686+ conditional move): dest = src when the condition holds
    private void BinaryCmov(Condition condition) {
      var (first, second) = this.TwoOperands();
      if (first is not RegisterOperand d)
        throw new AsmSyntaxException("CMOVcc takes a register destination.");
      switch (second) {
        case RegisterOperand s: this._asm.Cmovcc(condition, d.Register, s.Register); return;
        case MemoryOperand s: this._asm.Cmovcc(condition, d.Register, SizedLike(s.Memory, d.Register)); return;
        default: throw new AsmSyntaxException("CMOVcc takes a register or memory source.");
      }
    }

    private void BinaryExtend(Action<Reg, Reg> emitRegister, Action<Reg, Mem> emitMemory) {
      var (first, second) = this.TwoOperands();
      if (first is not RegisterOperand destination)
        throw new AsmSyntaxException("Register destination expected.");

      switch (second) {
        case RegisterOperand s: emitRegister(destination.Register, s.Register); return;
        case MemoryOperand s: emitMemory(destination.Register, s.Memory); return;
        default: throw new AsmSyntaxException("Register or memory source expected.");
      }
    }

    // packed binary op (0F op /r): destination MM0..MM7 (MMX) or XMM0..XMM7 (SSE2), source the same
    // register class or memory - the assembler selects the MMX or 66-prefixed SSE2 encoding by class
    private void PackedBinary(byte opcode) {
      var (first, second) = this.TwoOperands();
      if (first is not RegisterOperand d || !(d.Register.IsMmx() || d.Register.IsXmm()))
        throw new AsmSyntaxException("an MMX (MM0..MM7) or XMM (XMM0..XMM7) destination register is expected.");
      switch (second) {
        case RegisterOperand s when s.Register.IsMmx() || s.Register.IsXmm(): this._asm.EmitPacked(opcode, d.Register, s.Register); return;
        case MemoryOperand s: this._asm.EmitPacked(opcode, d.Register, s.Memory); return;
        default: throw new AsmSyntaxException("an MMX/XMM register or memory source is expected.");
      }
    }

    // packed shift (0F op /subOp): by a same-class register or an immediate count
    private void PackedShift(byte opcode, int subOp, Action<Reg, Reg> mmxByReg) {
      var (first, second) = this.TwoOperands();
      if (first is not RegisterOperand d || !(d.Register.IsMmx() || d.Register.IsXmm()))
        throw new AsmSyntaxException("an MMX or XMM destination register is expected.");
      switch (second) {
        case ImmediateOperand s: this._asm.EmitPackedShiftImm(opcode, subOp, d.Register, (byte)s.Value); return;
        case RegisterOperand s when s.Register.IsMmx() && d.Register.IsMmx(): mmxByReg(d.Register, s.Register); return;
        default: throw new AsmSyntaxException("an immediate count (or, for MMX, a register count) is expected.");
      }
    }

    private void BinaryMovd() {
      var (first, second) = this.TwoOperands();
      switch (first, second) {
        case (RegisterOperand d, RegisterOperand s) when d.Register.IsXmm() && s.Register.IsGeneralPurpose(): this._asm.MovdX(d.Register, s.Register); return;
        case (RegisterOperand d, RegisterOperand s) when d.Register.IsGeneralPurpose() && s.Register.IsXmm(): this._asm.MovdXStore(d.Register, s.Register); return;
        case (RegisterOperand d, MemoryOperand s) when d.Register.IsXmm(): this._asm.MovdX(d.Register, s.Memory); return;
        case (MemoryOperand d, RegisterOperand s) when s.Register.IsXmm(): this._asm.MovdXStore(d.Memory, s.Register); return;
        case (RegisterOperand d, RegisterOperand s) when d.Register.IsMmx() && !s.Register.IsMmx(): this._asm.Movd(d.Register, s.Register); return;
        case (RegisterOperand d, RegisterOperand s) when !d.Register.IsMmx() && s.Register.IsMmx(): this._asm.MovdStore(d.Register, s.Register); return;
        case (RegisterOperand d, MemoryOperand s) when d.Register.IsMmx(): this._asm.Movd(d.Register, s.Memory); return;
        case (MemoryOperand d, RegisterOperand s) when s.Register.IsMmx(): this._asm.MovdStore(d.Memory, s.Register); return;
        default: throw new AsmSyntaxException("Invalid MOVD operand combination.");
      }
    }

    private void BinaryMovq() {
      var (first, second) = this.TwoOperands();
      switch (first, second) {
        case (RegisterOperand d, RegisterOperand s) when d.Register.IsMmx() && s.Register.IsMmx(): this._asm.Movq(d.Register, s.Register); return;
        case (RegisterOperand d, MemoryOperand s) when d.Register.IsMmx(): this._asm.Movq(d.Register, s.Memory); return;
        case (MemoryOperand d, RegisterOperand s) when s.Register.IsMmx(): this._asm.MovqStore(d.Memory, s.Register); return;
        default: throw new AsmSyntaxException("Invalid MOVQ operand combination.");
      }
    }

    private (Operand First, Operand Second, Operand Third) ThreeOperands() {
      var operands = this.ParseOperands();
      if (operands.Count != 3)
        throw new AsmSyntaxException($"Three operands expected, found {operands.Count}.");
      return (operands[0], operands[1], operands[2]);
    }

    // AVX/AVX-512 VEX/EVEX 3-operand packed op: dest = src1 OP src2 (XMM/YMM = VEX, ZMM = EVEX)
    private void VexBinary(byte opcode) {
      var (a, b, c) = this.ThreeOperands();
      if (a is not RegisterOperand d || !Vec(d.Register))
        throw new AsmSyntaxException("an XMM/YMM/ZMM destination register is expected.");
      if (b is not RegisterOperand s1 || !Vec(s1.Register))
        throw new AsmSyntaxException("an XMM/YMM/ZMM first-source register is expected.");
      var zmm = d.Register.IsZmm();
      switch (c) {
        case RegisterOperand s2 when Vec(s2.Register):
          if (zmm) this._asm.EvexPacked(opcode, d.Register, s1.Register, s2.Register);
          else this._asm.VexPacked(opcode, d.Register, s1.Register, s2.Register);
          return;
        case MemoryOperand s2:
          if (zmm) this._asm.EvexPacked(opcode, d.Register, s1.Register, s2.Memory);
          else this._asm.VexPacked(opcode, d.Register, s1.Register, s2.Memory);
          return;
        default: throw new AsmSyntaxException("an XMM/YMM/ZMM register or memory second source is expected.");
      }
    }

    private static bool Vec(Reg r) => r.IsXmm() || r.IsYmm() || r.IsZmm();

    private void VexMove(bool unaligned) {
      var (first, second) = this.TwoOperands();
      switch (first, second) {
        case (RegisterOperand d, RegisterOperand s) when !unaligned && d.Register.IsZmm() && s.Register.IsZmm(): this._asm.Vmovdqa512(d.Register, s.Register); return;
        case (RegisterOperand d, MemoryOperand s) when d.Register.IsZmm(): if (unaligned) this._asm.Vmovdqu512(d.Register, s.Memory); else this._asm.Vmovdqa512(d.Register, s.Memory); return;
        case (MemoryOperand d, RegisterOperand s) when s.Register.IsZmm(): if (unaligned) this._asm.Vmovdqu512Store(d.Memory, s.Register); else this._asm.Vmovdqa512Store(d.Memory, s.Register); return;
        case (RegisterOperand d, RegisterOperand s) when !unaligned && Vec(d.Register) && Vec(s.Register): this._asm.Vmovdqa(d.Register, s.Register); return;
        case (RegisterOperand d, MemoryOperand s) when Vec(d.Register): if (unaligned) this._asm.Vmovdqu(d.Register, s.Memory); else this._asm.Vmovdqa(d.Register, s.Memory); return;
        case (MemoryOperand d, RegisterOperand s) when Vec(s.Register): if (unaligned) this._asm.VmovdquStore(d.Memory, s.Register); else this._asm.VmovdqaStore(d.Memory, s.Register); return;
        default: throw new AsmSyntaxException($"Invalid {(unaligned ? "VMOVDQU" : "VMOVDQA")} operand combination.");
      }
    }

    private void BinaryMovdqa(bool unaligned) {
      var (first, second) = this.TwoOperands();
      switch (first, second) {
        case (RegisterOperand d, RegisterOperand s) when d.Register.IsXmm() && s.Register.IsXmm() && !unaligned: this._asm.Movdqa(d.Register, s.Register); return;
        case (RegisterOperand d, MemoryOperand s) when d.Register.IsXmm(): if (unaligned) this._asm.Movdqu(d.Register, s.Memory); else this._asm.Movdqa(d.Register, s.Memory); return;
        case (MemoryOperand d, RegisterOperand s) when s.Register.IsXmm(): if (unaligned) this._asm.MovdquStore(d.Memory, s.Register); else this._asm.MovdqaStore(d.Memory, s.Register); return;
        default: throw new AsmSyntaxException($"Invalid {(unaligned ? "MOVDQU" : "MOVDQA")} operand combination.");
      }
    }

    private void BinaryAlu(Action<Reg, Reg> rr, Action<Reg, Mem> rm, Action<Mem, Reg> mr, Action<Reg, Imm> ri, Action<Mem, Imm> mi) {
      var (first, second) = this.TwoOperands();
      switch (first, second) {
        case (RegisterOperand d, RegisterOperand s): rr(d.Register, s.Register); return;
        case (RegisterOperand d, MemoryOperand s): rm(d.Register, SizedLike(s.Memory, d.Register)); return;
        case (RegisterOperand d, ImmediateOperand s): ri(d.Register, s.Value); return;
        case (MemoryOperand d, RegisterOperand s): mr(SizedLike(d.Memory, s.Register), s.Register); return;
        case (MemoryOperand d, ImmediateOperand s): mi(SizedLike(d.Memory, null), s.Value); return;
        default: throw new AsmSyntaxException("Invalid operand combination.");
      }
    }

    private void UnaryRegMem(Action<Reg> register, Action<Mem> memory) {
      switch (this.OneOperand()) {
        case RegisterOperand r: register(r.Register); return;
        case MemoryOperand m: memory(SizedLike(m.Memory, null)); return;
        default: throw new AsmSyntaxException("Register or memory operand expected.");
      }
    }

    private void Imul() {
      var operands = this.ParseOperands();
      switch (operands.Count) {
        case 1:
          switch (operands[0]) {
            case RegisterOperand r: this._asm.Imul(r.Register); return;
            case MemoryOperand m: this._asm.Imul(SizedLike(m.Memory, null)); return;
            default: throw new AsmSyntaxException("Register or memory operand expected.");
          }
        case 2:
          switch (operands[0], operands[1]) {
            case (RegisterOperand d, RegisterOperand s): this._asm.Imul(d.Register, s.Register); return;
            case (RegisterOperand d, MemoryOperand s): this._asm.Imul(d.Register, SizedLike(s.Memory, d.Register)); return;
            case (RegisterOperand d, ImmediateOperand s): this._asm.Imul(d.Register, s.Value); return;
            default: throw new AsmSyntaxException("Invalid IMUL operand combination.");
          }
        case 3:
          if (operands[2] is not ImmediateOperand immediate)
            throw new AsmSyntaxException("IMUL needs an immediate third operand.");

          switch (operands[0], operands[1]) {
            case (RegisterOperand d, RegisterOperand s): this._asm.Imul(d.Register, s.Register, immediate.Value); return;
            case (RegisterOperand d, MemoryOperand s): this._asm.Imul(d.Register, SizedLike(s.Memory, d.Register), immediate.Value); return;
            default: throw new AsmSyntaxException("Invalid IMUL operand combination.");
          }
        default:
          throw new AsmSyntaxException("IMUL takes one, two or three operands.");
      }
    }

    private void Shift(Action<Reg, int> registerImmediate, Action<Reg, Reg> registerCl, Action<Mem, int> memoryImmediate, Action<Mem, Reg> memoryCl) {
      var (first, second) = this.TwoOperands();
      switch (first, second) {
        case (RegisterOperand d, ImmediateOperand c): registerImmediate(d.Register, c.Value); return;
        case (RegisterOperand d, RegisterOperand c): registerCl(d.Register, c.Register); return;
        case (MemoryOperand d, ImmediateOperand c): memoryImmediate(SizedLike(d.Memory, null), c.Value); return;
        case (MemoryOperand d, RegisterOperand c): memoryCl(SizedLike(d.Memory, null), c.Register); return;
        default: throw new AsmSyntaxException("Invalid shift operand combination.");
      }
    }

    private void Push() {
      switch (this.OneOperand()) {
        case RegisterOperand r: this._asm.Push(r.Register); return;
        case MemoryOperand m: this._asm.Push(SizedLike(m.Memory, null, OperandSize.Word)); return;
        case ImmediateOperand i: this._asm.Push(i.Value); return;
        case LabelOperand l: this._asm.Push(Imm.OffsetOf(l.Label)); return;
        default: throw new AsmSyntaxException("Invalid PUSH operand.");
      }
    }

    private void Pop() {
      switch (this.OneOperand()) {
        case RegisterOperand r: this._asm.Pop(r.Register); return;
        case MemoryOperand m: this._asm.Pop(SizedLike(m.Memory, null, OperandSize.Word)); return;
        default: throw new AsmSyntaxException("Invalid POP operand.");
      }
    }

    private void Jump() {
      if (this.IsKeyword("SHORT")) {
        ++this._index;
        this._asm.JmpShort(this.RequireLabel());
        return;
      }

      if (this.IsKeyword("NEAR")) {
        ++this._index;
        this._asm.JmpNear(this.RequireLabel());
        return;
      }

      switch (this.OneOperand()) {
        case LabelOperand l: this._asm.Jmp(l.Label); return;
        case RegisterOperand r: this._asm.Jmp(r.Register); return;
        case MemoryOperand { Memory.Size: OperandSize.Dword } m: this._asm.JmpFar(m.Memory); return;
        case MemoryOperand m: this._asm.Jmp(SizedLike(m.Memory, null, OperandSize.Word)); return;
        default: throw new AsmSyntaxException("Invalid JMP target.");
      }
    }

    private void CallTarget() {
      switch (this.OneOperand()) {
        case LabelOperand l: this._asm.Call(l.Label); return;
        case RegisterOperand r: this._asm.Call(r.Register); return;
        case MemoryOperand { Memory.Size: OperandSize.Dword } m: this._asm.CallFar(m.Memory); return;
        case MemoryOperand m: this._asm.Call(SizedLike(m.Memory, null, OperandSize.Word)); return;
        default: throw new AsmSyntaxException("Invalid CALL target.");
      }
    }

    private void Return(Action plain, Action<ushort> withImmediate) {
      var operands = this.ParseOperands();
      switch (operands.Count) {
        case 0: plain(); return;
        case 1 when operands[0] is ImmediateOperand immediate: withImmediate((ushort)immediate.Value); return;
        default: throw new AsmSyntaxException("RET takes an optional immediate operand.");
      }
    }

    private Label RequireLabel() {
      if (this.OneOperand() is not LabelOperand label)
        throw new AsmSyntaxException("Label target expected.");

      return label.Label;
    }

    private void Interrupt() {
      if (this.OneOperand() is not ImmediateOperand vector || vector.Value is < 0 or > 255)
        throw new AsmSyntaxException("INT needs a vector 0..255.");

      this._asm.Int((byte)vector.Value);
    }

    private void InPort() {
      var (first, second) = this.TwoOperands();
      if (first is not RegisterOperand accumulator)
        throw new AsmSyntaxException("IN needs AL/AX/EAX as destination.");

      switch (second) {
        case ImmediateOperand port when port.Value is >= 0 and <= 255: this._asm.In(accumulator.Register, (byte)port.Value); return;
        case RegisterOperand port: this._asm.In(accumulator.Register, port.Register); return;
        default: throw new AsmSyntaxException("IN needs a port number 0..255 or DX.");
      }
    }

    private void OutPort() {
      var (first, second) = this.TwoOperands();
      if (second is not RegisterOperand accumulator)
        throw new AsmSyntaxException("OUT needs AL/AX/EAX as source.");

      switch (first) {
        case ImmediateOperand port when port.Value is >= 0 and <= 255: this._asm.Out((byte)port.Value, accumulator.Register); return;
        case RegisterOperand port: this._asm.Out(port.Register, accumulator.Register); return;
        default: throw new AsmSyntaxException("OUT needs a port number 0..255 or DX.");
      }
    }

    /// <summary>
    /// Gives a memory operand the size of its register partner (or a default).
    /// An explicit register always wins - PB lets a byte register address the
    /// low byte of a word variable (<c>MOV AL, count%</c>).
    /// </summary>
    private static Mem SizedLike(Mem memory, Reg? partner, OperandSize fallback = OperandSize.None) {
      if (partner is { } register && register.IsGeneralPurpose())
        return memory.WithSize(register.Size());
      if (memory.Size != OperandSize.None)
        return memory;
      if (fallback != OperandSize.None)
        return memory.WithSize(fallback);

      return memory;
    }

    #endregion

    #region FPU dispatch

    private bool TryDispatchFpu(string mnemonic) {
      switch (mnemonic) {
        case "FLD": this.FpuLoadStore(this._asm.Fld, this._asm.Fld); return true;
        case "FST": this.FpuLoadStore(this._asm.Fst, this._asm.Fst); return true;
        case "FSTP": this.FpuLoadStore(this._asm.Fstp, this._asm.Fstp); return true;
        case "FILD": this.FpuMemoryOnly(this._asm.Fild); return true;
        case "FIST": this.FpuMemoryOnly(this._asm.Fist); return true;
        case "FISTP": this.FpuMemoryOnly(this._asm.Fistp); return true;

        case "FADD": this.FpuArithmetic(this._asm.Fadd, this._asm.Fadd); return true;
        case "FMUL": this.FpuArithmetic(this._asm.Fmul, this._asm.Fmul); return true;
        case "FSUB": this.FpuArithmetic(this._asm.Fsub, this._asm.Fsub); return true;
        case "FSUBR": this.FpuArithmetic(this._asm.Fsubr, this._asm.Fsubr); return true;
        case "FDIV": this.FpuArithmetic(this._asm.Fdiv, this._asm.Fdiv); return true;
        case "FDIVR": this.FpuArithmetic(this._asm.Fdivr, this._asm.Fdivr); return true;

        case "FADDP": this.FpuPop(this._asm.Faddp, this._asm.Faddp); return true;
        case "FMULP": this.FpuPop(this._asm.Fmulp, this._asm.Fmulp); return true;
        case "FSUBP": this.FpuPop(this._asm.Fsubp, this._asm.Fsubp); return true;
        case "FSUBRP": this.FpuPop(this._asm.Fsubrp, this._asm.Fsubrp); return true;
        case "FDIVP": this.FpuPop(this._asm.Fdivp, this._asm.Fdivp); return true;
        case "FDIVRP": this.FpuPop(this._asm.Fdivrp, this._asm.Fdivrp); return true;

        case "FIADD": this.FpuMemoryOnly(this._asm.Fiadd); return true;
        case "FIMUL": this.FpuMemoryOnly(this._asm.Fimul); return true;
        case "FISUB": this.FpuMemoryOnly(this._asm.Fisub); return true;
        case "FISUBR": this.FpuMemoryOnly(this._asm.Fisubr); return true;
        case "FIDIV": this.FpuMemoryOnly(this._asm.Fidiv); return true;
        case "FIDIVR": this.FpuMemoryOnly(this._asm.Fidivr); return true;
        case "FICOM": this.FpuMemoryOnly(this._asm.Ficom); return true;
        case "FICOMP": this.FpuMemoryOnly(this._asm.Ficomp); return true;

        case "FCOM": this.FpuCompare(this._asm.Fcom, this._asm.Fcom, this._asm.Fcom); return true;
        case "FCOMP": this.FpuCompare(this._asm.Fcomp, this._asm.Fcomp, this._asm.Fcomp); return true;
        case "FCOMPP": this.NoOperands(mnemonic); this._asm.Fcompp(); return true;
        case "FUCOM": this.FpuStOrNothing(this._asm.Fucom, this._asm.Fucom); return true;
        case "FUCOMP": this.FpuStOrNothing(this._asm.Fucomp, this._asm.Fucomp); return true;
        case "FUCOMPP": this.NoOperands(mnemonic); this._asm.Fucompp(); return true;
        case "FXCH": this.FpuStOrNothing(this._asm.Fxch, this._asm.Fxch); return true;
        case "FFREE": this.FpuStOnly(this._asm.Ffree); return true;

        case "FTST": this.NoOperands(mnemonic); this._asm.Ftst(); return true;
        case "FCHS": this.NoOperands(mnemonic); this._asm.Fchs(); return true;
        case "FABS": this.NoOperands(mnemonic); this._asm.Fabs(); return true;
        case "FSQRT": this.NoOperands(mnemonic); this._asm.Fsqrt(); return true;
        case "FRNDINT": this.NoOperands(mnemonic); this._asm.Frndint(); return true;
        case "FSCALE": this.NoOperands(mnemonic); this._asm.Fscale(); return true;
        case "FPREM": this.NoOperands(mnemonic); this._asm.Fprem(); return true;
        case "FPREM1": this.NoOperands(mnemonic); this._asm.Fprem1(); return true;
        case "FPTAN": this.NoOperands(mnemonic); this._asm.Fptan(); return true;
        case "FPATAN": this.NoOperands(mnemonic); this._asm.Fpatan(); return true;
        case "F2XM1": this.NoOperands(mnemonic); this._asm.F2xm1(); return true;
        case "FYL2X": this.NoOperands(mnemonic); this._asm.Fyl2x(); return true;
        case "FYL2XP1": this.NoOperands(mnemonic); this._asm.Fyl2xp1(); return true;
        case "FSIN": this.NoOperands(mnemonic); this._asm.Fsin(); return true;
        case "FCOS": this.NoOperands(mnemonic); this._asm.Fcos(); return true;
        case "FSINCOS": this.NoOperands(mnemonic); this._asm.Fsincos(); return true;

        case "FLDZ": this.NoOperands(mnemonic); this._asm.Fldz(); return true;
        case "FLD1": this.NoOperands(mnemonic); this._asm.Fld1(); return true;
        case "FLDPI": this.NoOperands(mnemonic); this._asm.Fldpi(); return true;
        case "FLDL2E": this.NoOperands(mnemonic); this._asm.Fldl2e(); return true;
        case "FLDL2T": this.NoOperands(mnemonic); this._asm.Fldl2t(); return true;
        case "FLDLG2": this.NoOperands(mnemonic); this._asm.Fldlg2(); return true;
        case "FLDLN2": this.NoOperands(mnemonic); this._asm.Fldln2(); return true;

        case "FINIT": this.NoOperands(mnemonic); this._asm.Finit(); return true;
        case "FNINIT": this.NoOperands(mnemonic); this._asm.Fninit(); return true;
        case "FCLEX": this.NoOperands(mnemonic); this._asm.Fclex(); return true;
        case "FNCLEX": this.NoOperands(mnemonic); this._asm.Fnclex(); return true;
        case "FINCSTP": this.NoOperands(mnemonic); this._asm.Fincstp(); return true;
        case "FDECSTP": this.NoOperands(mnemonic); this._asm.Fdecstp(); return true;
        case "FWAIT" or "WAIT": this.NoOperands(mnemonic); this._asm.Fwait(); return true;

        case "FSTSW": this.FpuStatusWord(this._asm.FstswAx, this._asm.Fstsw); return true;
        case "FNSTSW": this.FpuStatusWord(this._asm.FnstswAx, this._asm.Fnstsw); return true;
        case "FSTCW": this.FpuMemoryOnly(this._asm.Fstcw); return true;
        case "FNSTCW": this.FpuMemoryOnly(this._asm.Fnstcw); return true;
        case "FLDCW": this.FpuMemoryOnly(this._asm.Fldcw); return true;

        default:
          return false;
      }
    }

    private delegate void MemEmitter(Mem memory);

    private void FpuLoadStore(MemEmitter memory, Action<St> stack) {
      switch (this.OneOperand()) {
        case MemoryOperand m: memory(m.Memory); return;
        case StOperand s: stack(s.Register); return;
        default: throw new AsmSyntaxException("Memory or ST(i) operand expected.");
      }
    }

    private void FpuMemoryOnly(MemEmitter memory) {
      if (this.OneOperand() is not MemoryOperand m)
        throw new AsmSyntaxException("Memory operand expected.");

      memory(m.Memory);
    }

    private void FpuArithmetic(MemEmitter memory, Action<St, St> stack) {
      var operands = this.ParseOperands();
      switch (operands.Count) {
        case 1 when operands[0] is MemoryOperand m: memory(m.Memory); return;
        case 2 when operands[0] is StOperand d && operands[1] is StOperand s: stack(d.Register, s.Register); return;
        default: throw new AsmSyntaxException("Memory operand or ST(i), ST(j) pair expected.");
      }
    }

    private void FpuPop(Action plain, Action<St> stack) {
      var operands = this.ParseOperands();
      switch (operands.Count) {
        case 0: plain(); return;
        case 1 when operands[0] is StOperand d: stack(d.Register); return;
        case 2 when operands[0] is StOperand d && operands[1] is StOperand { Register.Index: 0 }: stack(d.Register); return;
        default: throw new AsmSyntaxException("ST(i) [, ST] operands expected.");
      }
    }

    private void FpuCompare(Action plain, Action<St> stack, MemEmitter memory) {
      var operands = this.ParseOperands();
      switch (operands.Count) {
        case 0: plain(); return;
        case 1 when operands[0] is StOperand s: stack(s.Register); return;
        case 1 when operands[0] is MemoryOperand m: memory(m.Memory); return;
        default: throw new AsmSyntaxException("Memory or ST(i) operand expected.");
      }
    }

    private void FpuStOrNothing(Action plain, Action<St> stack) {
      var operands = this.ParseOperands();
      switch (operands.Count) {
        case 0: plain(); return;
        case 1 when operands[0] is StOperand s: stack(s.Register); return;
        default: throw new AsmSyntaxException("Optional ST(i) operand expected.");
      }
    }

    private void FpuStOnly(Action<St> stack) {
      if (this.OneOperand() is not StOperand s)
        throw new AsmSyntaxException("ST(i) operand expected.");

      stack(s.Register);
    }

    private void FpuStatusWord(Action toAx, MemEmitter toMemory) {
      switch (this.OneOperand()) {
        case RegisterOperand { Register: Reg.AX }: toAx(); return;
        case MemoryOperand m: toMemory(m.Memory); return;
        default: throw new AsmSyntaxException("FSTSW stores to AX or memory.");
      }
    }

    #endregion
  }
}
