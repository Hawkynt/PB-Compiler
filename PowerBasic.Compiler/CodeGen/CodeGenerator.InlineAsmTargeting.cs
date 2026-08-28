using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  /// <summary>
  /// Handles an inline instruction whose native encoding is unavailable on the selected target.
  /// Returns true when the line was either lowered or diagnosed; false means native emission is legal.
  /// </summary>
  private bool TryEmitTargetedInlineAsm(string line, InlineAsmResolver resolver, RuntimeTarget target, out string? error) {
    error = null;
    var parsed = InlineInstruction.Parse(line);
    if (parsed.Mnemonic.Length == 0)
      return false;

    var required = RequiredFeature(parsed);
    if (required == RuntimeCpuFeatures.None || target.Has(required))
      return false;

    if (parsed.Mnemonic == "MOVSD") {
      var helper = this.EnsureInlineAsmMovsdCompat(parsed.RepPrefix != null);
      this._asm.Call(helper);
      return true;
    }

    if (parsed.Mnemonic.StartsWith("CMOV", StringComparison.Ordinal)) {
      if (this.TryLowerCmov(parsed, resolver, target, out error))
        return true;
      error ??= $"{parsed.Mnemonic} requires {target.DescribeMissing(required)} and has no safe fallback for these operands";
      return true;
    }

    if (parsed.Mnemonic == "BSWAP" && target.Has32BitGeneralPurpose) {
      if (this.TryLowerBswap(parsed.Operands, out error))
        return true;
      error ??= "BSWAP fallback requires a 32-bit GP register other than ESP";
      return true;
    }

    if (parsed.Mnemonic is "MOVZX" or "MOVSX" && !target.Has32BitGeneralPurpose) {
      if (this.TryLowerExtend16(parsed, resolver, out error))
        return true;
      error ??= $"{parsed.Mnemonic} requires 80386 for this operand width";
      return true;
    }

    var missing = target.DescribeMissing(required);
    error = $"{parsed.Mnemonic} requires {missing}; target is {TargetName(target)}. "
      + ArchitecturalFallbackNote(parsed, required);
    return true;
  }

  private static RuntimeCpuFeatures RequiredFeature(InlineInstruction instruction) {
    var mnemonic = instruction.Mnemonic;
    var text = instruction.Operands.ToUpperInvariant();

    if (mnemonic == "BSWAP")
      return RuntimeCpuFeatures.I486;
    if (mnemonic.StartsWith("CMOV", StringComparison.Ordinal))
      return RuntimeCpuFeatures.P6;

    if (mnemonic is "MOVZX" or "MOVSX" or "CWDE" or "CDQ" or "MOVSD" or "CMPSD" or "STOSD" or "LODSD" or "SCASD")
      return RuntimeCpuFeatures.GeneralPurpose32;

    if (!mnemonic.StartsWith('F') && !IsVectorMnemonic(mnemonic)
        && (ContainsDwordGp(text) || text.Contains("DWORD PTR", StringComparison.Ordinal)))
      return RuntimeCpuFeatures.GeneralPurpose32;

    if (mnemonic == "EMMS")
      return RuntimeCpuFeatures.Mmx;

    if (mnemonic is "MOVDQA" or "MOVDQU")
      return RuntimeCpuFeatures.Sse2;

    if (IsLegacyPackedInteger(mnemonic))
      return text.Contains("XMM", StringComparison.Ordinal) ? RuntimeCpuFeatures.Sse2 : RuntimeCpuFeatures.Mmx;

    if (IsSsse3(mnemonic))
      return RuntimeCpuFeatures.Ssse3;
    if (IsSse41(mnemonic))
      return RuntimeCpuFeatures.Sse41;
    if (IsSse42(mnemonic))
      return RuntimeCpuFeatures.Sse42;

    if (mnemonic.StartsWith('V')) {
      if (text.Contains("ZMM", StringComparison.Ordinal))
        return RuntimeCpuFeatures.Avx512F;
      if (text.Contains("YMM", StringComparison.Ordinal) && mnemonic is not ("VMOVDQA" or "VMOVDQU"))
        return RuntimeCpuFeatures.Avx2;
      return RuntimeCpuFeatures.Avx;
    }

    return RuntimeCpuFeatures.None;
  }

  private bool TryLowerCmov(InlineInstruction instruction, InlineAsmResolver resolver, RuntimeTarget target, out string? error) {
    error = null;
    if (!TryCmovCondition(instruction.Mnemonic, out var condition))
      return false;
    if (!TrySplitBinaryOperands(instruction.Operands, out var destinationText, out var sourceText)) {
      error = "CMOVcc expects two operands";
      return false;
    }
    if (!Enum.TryParse<Reg>(destinationText.Trim(), true, out var destination)
        || (!destination.IsWord() && !destination.IsDword()) || destination == Reg.SP || destination == Reg.ESP) {
      error = "CMOVcc fallback needs a 16- or 32-bit GP destination other than SP/ESP";
      return false;
    }
    if (destination.IsDword() && !target.Has32BitGeneralPurpose) {
      error = "32-bit CMOVcc cannot be represented on a pre-386 target";
      return false;
    }

    var scratch = ScratchFor(destination);
    var widthScratch = destination.IsDword() ? DwordForm(scratch) : scratch;
    var sourceLoad = $"MOV {widthScratch}, {sourceText}";
    var destinationMove = $"MOV {destination}, {widthScratch}";
    if (!ProbeInline(sourceLoad, resolver, out error) || !ProbeInline(destinationMove, resolver, out error))
      return false;

    // Intel CMOV conceptually reads its source even when the condition is false. Load into a saved
    // scratch register before branching so a memory source keeps that behaviour. MOV/PUSH/POP/Jcc do
    // not alter flags, therefore the incoming flags remain the outgoing flags exactly as for CMOV.
    this._asm.Push(widthScratch);
    this._textAssembler ??= new(this._asm);
    _ = this._textAssembler.TryParse(sourceLoad, resolver, out _);
    var skip = this._asm.DefineLabel();
    this._asm.J(Invert(condition), skip);
    _ = this._textAssembler.TryParse(destinationMove, resolver, out _);
    this._asm.MarkLabel(skip);
    this._asm.Pop(widthScratch);
    return true;
  }

  private bool TryLowerBswap(string operands, out string? error) {
    error = null;
    if (!Enum.TryParse<Reg>(operands.Trim(), true, out var destination)
        || !destination.IsDword() || destination == Reg.ESP) {
      error = "BSWAP fallback needs EAX/EBX/ECX/EDX/EBP/ESI/EDI";
      return false;
    }

    this._asm.Pushf();
    if (destination != Reg.EAX) {
      this._asm.Push(Reg.EAX);
      this._asm.Mov(Reg.EAX, destination);
    }
    this._asm.Xchg(Reg.AL, Reg.AH);
    this._asm.Ror(Reg.EAX, 16);
    this._asm.Xchg(Reg.AL, Reg.AH);
    if (destination != Reg.EAX) {
      this._asm.Mov(destination, Reg.EAX);
      this._asm.Pop(Reg.EAX);
    }
    this._asm.Popf();
    return true;
  }

  private bool TryLowerExtend16(InlineInstruction instruction, InlineAsmResolver resolver, out string? error) {
    error = null;
    if (!TrySplitBinaryOperands(instruction.Operands, out var destinationText, out var sourceText)
        || !Enum.TryParse<Reg>(destinationText.Trim(), true, out var destination)
        || !destination.IsWord() || destination == Reg.SP) {
      error = $"{instruction.Mnemonic} pre-386 fallback supports a 16-bit GP destination other than SP";
      return false;
    }

    var load = $"MOV AL, {sourceText}";
    if (!ProbeInline(load, resolver, out error)) {
      error = $"{instruction.Mnemonic} pre-386 fallback requires an 8-bit source: {error}";
      return false;
    }

    this._asm.Pushf();
    if (destination != Reg.AX)
      this._asm.Push(Reg.AX);
    this._textAssembler ??= new(this._asm);
    _ = this._textAssembler.TryParse(load, resolver, out _);
    if (instruction.Mnemonic == "MOVSX")
      this._asm.Cbw();
    else
      this._asm.Xor(Reg.AH, Reg.AH);
    this._asm.Mov(destination, Reg.AX);
    if (destination != Reg.AX)
      this._asm.Pop(Reg.AX);
    this._asm.Popf();
    return true;
  }

  /// <summary>
  /// Lazily emits an embedded runtime replacement for MOVSD / REP MOVSD. Two MOVSW operations are
  /// bit-for-bit equivalent to one MOVSD for either DF direction and do not alter flags. The REP form
  /// loops on CX directly, so even CX &gt; 32767 is handled without a lossy "CX *= 2" transformation.
  /// </summary>
  private Label EnsureInlineAsmMovsdCompat(bool repeated) {
    var helper = this._asm.Lbl(repeated ? "rt_asm_compat_rep_movsd" : "rt_asm_compat_movsd");
    if (helper.IsBound)
      return helper;

    var over = this._asm.DefineLabel();
    this._asm.Jmp(over);
    this._asm.MarkLabel(helper);
    if (!repeated) {
      this._asm.Movsw();
      this._asm.Movsw();
      this._asm.Ret();
    } else {
      var loop = this._asm.DefineLabel();
      var done = this._asm.DefineLabel();
      this._asm.Jcxz(done);
      this._asm.MarkLabel(loop);
      this._asm.Movsw();
      this._asm.Movsw();
      this._asm.Loop(loop);
      this._asm.MarkLabel(done);
      this._asm.Ret();
    }
    this._asm.MarkLabel(over);
    return helper;
  }

  private bool ProbeInline(string text, InlineAsmResolver resolver, out string? error) {
    var probe = new Assembler();
    return new TextAssembler(probe).TryParse(text, resolver, out error);
  }

  private static Reg ScratchFor(Reg destination) {
    Reg[] candidates = [Reg.AX, Reg.CX, Reg.DX, Reg.BX, Reg.SI, Reg.DI, Reg.BP];
    var wordDestination = destination.IsDword() ? WordForm(destination) : destination;
    return candidates.First(r => r != wordDestination);
  }

  private static Reg WordForm(Reg dword) => dword switch {
    Reg.EAX => Reg.AX, Reg.ECX => Reg.CX, Reg.EDX => Reg.DX, Reg.EBX => Reg.BX,
    Reg.ESP => Reg.SP, Reg.EBP => Reg.BP, Reg.ESI => Reg.SI, Reg.EDI => Reg.DI,
    _ => dword,
  };

  private static Reg DwordForm(Reg word) => word switch {
    Reg.AX => Reg.EAX, Reg.CX => Reg.ECX, Reg.DX => Reg.EDX, Reg.BX => Reg.EBX,
    Reg.SP => Reg.ESP, Reg.BP => Reg.EBP, Reg.SI => Reg.ESI, Reg.DI => Reg.EDI,
    _ => word,
  };

  private static bool TryCmovCondition(string mnemonic, out Condition condition) {
    condition = mnemonic switch {
      "CMOVE" or "CMOVZ" => Condition.Equal,
      "CMOVNE" or "CMOVNZ" => Condition.NotEqual,
      "CMOVL" => Condition.Less,
      "CMOVLE" => Condition.LessOrEqual,
      "CMOVG" => Condition.Greater,
      "CMOVGE" => Condition.GreaterOrEqual,
      "CMOVB" or "CMOVC" => Condition.Below,
      "CMOVBE" => Condition.BelowOrEqual,
      "CMOVA" => Condition.Above,
      "CMOVAE" or "CMOVNC" => Condition.AboveOrEqual,
      "CMOVS" => Condition.Sign,
      "CMOVNS" => Condition.NotSign,
      "CMOVO" => Condition.Overflow,
      "CMOVNO" => Condition.NotOverflow,
      _ => (Condition)0xFF,
    };
    return condition != (Condition)0xFF;
  }

  private static Condition Invert(Condition condition) => condition switch {
    Condition.Equal => Condition.NotEqual,
    Condition.NotEqual => Condition.Equal,
    Condition.Less => Condition.GreaterOrEqual,
    Condition.LessOrEqual => Condition.Greater,
    Condition.Greater => Condition.LessOrEqual,
    Condition.GreaterOrEqual => Condition.Less,
    Condition.Below => Condition.AboveOrEqual,
    Condition.BelowOrEqual => Condition.Above,
    Condition.Above => Condition.BelowOrEqual,
    Condition.AboveOrEqual => Condition.Below,
    Condition.Sign => Condition.NotSign,
    Condition.NotSign => Condition.Sign,
    Condition.Overflow => Condition.NotOverflow,
    Condition.NotOverflow => Condition.Overflow,
    _ => throw new ArgumentOutOfRangeException(nameof(condition)),
  };

  private static bool ContainsDwordGp(string operands) {
    var tokens = operands.Split([' ', '\t', ',', '[', ']', '+', '-', ':', '(', ')'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return tokens.Any(t => t.ToUpperInvariant() is "EAX" or "ECX" or "EDX" or "EBX" or "ESP" or "EBP" or "ESI" or "EDI");
  }

  private static bool TrySplitBinaryOperands(string operands, out string first, out string second) {
    var comma = operands.IndexOf(',');
    if (comma < 0) {
      first = second = string.Empty;
      return false;
    }
    first = operands[..comma].Trim();
    second = operands[(comma + 1)..].Trim();
    return first.Length > 0 && second.Length > 0;
  }

  private static bool IsVectorMnemonic(string mnemonic) =>
    mnemonic == "EMMS" || IsLegacyPackedInteger(mnemonic) || mnemonic is "MOVDQA" or "MOVDQU"
    || mnemonic.StartsWith('V') || IsSsse3(mnemonic) || IsSse41(mnemonic) || IsSse42(mnemonic);

  private static bool IsLegacyPackedInteger(string mnemonic) => mnemonic is
    "MOVD" or "MOVQ" or
    "PADDB" or "PADDW" or "PADDD" or "PADDQ" or
    "PSUBB" or "PSUBW" or "PSUBD" or "PSUBQ" or
    "PADDSW" or "PADDUSW" or "PSUBSW" or "PSUBUSW" or
    "PMULLW" or "PMULHW" or "PAND" or "PANDN" or "POR" or "PXOR" or
    "PCMPEQB" or "PCMPEQW" or "PCMPEQD" or "PCMPGTB" or "PCMPGTW" or "PCMPGTD" or
    "PACKSSWB" or "PACKSSDW" or "PACKUSWB" or
    "PUNPCKLBW" or "PUNPCKLWD" or "PUNPCKLDQ" or "PUNPCKHBW" or "PUNPCKHWD" or "PUNPCKHDQ" or
    "PSLLW" or "PSLLD" or "PSLLQ" or "PSRLW" or "PSRLD" or "PSRLQ" or "PSRAW" or "PSRAD";

  private static bool IsSsse3(string mnemonic) => mnemonic is
    "PABSB" or "PABSW" or "PABSD" or "PSHUFB" or "PHADDW" or "PHADDD" or "PHADDSW" or
    "PHSUBW" or "PHSUBD" or "PHSUBSW" or "PMADDUBSW" or "PMULHRSW" or
    "PSIGNB" or "PSIGNW" or "PSIGND" or "PALIGNR";

  private static bool IsSse41(string mnemonic) => mnemonic is
    "PBLENDW" or "PMULLD" or "PMINSB" or "PMAXSB" or "PMINUW" or "PMAXUW" or
    "PMINUD" or "PMAXUD" or "PCMPEQQ" or "PACKUSDW" or "PHMINPOSUW";

  private static bool IsSse42(string mnemonic) => mnemonic is "PCMPGTQ" or "PCMPESTRI" or "PCMPESTRM" or "PCMPISTRI" or "PCMPISTRM" or "CRC32";

  private static string TargetName(RuntimeTarget target) => target.CpuLevel switch {
    <= 86 => "8086",
    186 => "80186",
    286 => "80286",
    386 => "80386",
    486 => "80486",
    586 => "80586/Pentium",
    _ => "P6/686+",
  };

  private static string ArchitecturalFallbackNote(InlineInstruction instruction, RuntimeCpuFeatures required) {
    if ((required & (RuntimeCpuFeatures.Mmx | RuntimeCpuFeatures.Sse | RuntimeCpuFeatures.Sse2 | RuntimeCpuFeatures.Sse3
        | RuntimeCpuFeatures.Ssse3 | RuntimeCpuFeatures.Sse41 | RuntimeCpuFeatures.Sse42 | RuntimeCpuFeatures.Avx
        | RuntimeCpuFeatures.Avx2 | RuntimeCpuFeatures.Avx512F)) != 0)
      return "No scalar fallback is emitted because the instruction exposes architectural vector-register state across inline-asm statements.";
    if (ContainsDwordGp(instruction.Operands))
      return "The instruction uses 32-bit architectural register state which does not exist on this target.";
    return "No semantics-preserving compatibility lowering is registered for this opcode/operand form.";
  }

  private readonly record struct InlineInstruction(string? RepPrefix, string Mnemonic, string Operands) {
    public static InlineInstruction Parse(string line) {
      var comment = line.IndexOf(';');
      if (comment >= 0)
        line = line[..comment];
      line = line.Trim();
      if (line.Length == 0)
        return new(null, string.Empty, string.Empty);

      var firstSpace = line.IndexOfAny([' ', '\t']);
      var first = (firstSpace < 0 ? line : line[..firstSpace]).ToUpperInvariant();
      var rest = firstSpace < 0 ? string.Empty : line[(firstSpace + 1)..].TrimStart();
      if (first is "REP" or "REPE" or "REPZ" or "REPNE" or "REPNZ") {
        var secondSpace = rest.IndexOfAny([' ', '\t']);
        var mnemonic = (secondSpace < 0 ? rest : rest[..secondSpace]).ToUpperInvariant();
        var operands = secondSpace < 0 ? string.Empty : rest[(secondSpace + 1)..].Trim();
        return new(first, mnemonic, operands);
      }
      return new(null, first, rest.Trim());
    }
  }
}
