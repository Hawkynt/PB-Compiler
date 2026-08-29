namespace PowerBasic.Compiler.Asm;

public sealed partial class TextAssembler {
  /// <summary>Typed operand form shared with target-specific inline-asm lowerers.</summary>
  internal abstract record ParsedAsmOperand;
  internal sealed record ParsedAsmRegister(Reg Register) : ParsedAsmOperand;
  internal sealed record ParsedAsmSt(St Register) : ParsedAsmOperand;
  internal sealed record ParsedAsmImmediate(int Value) : ParsedAsmOperand;
  internal sealed record ParsedAsmMemory(Mem Memory) : ParsedAsmOperand;
  internal sealed record ParsedAsmLabel(Label Label) : ParsedAsmOperand;

  /// <summary>
  /// Parses only an operand list, using exactly the same grammar and symbol resolver as normal inline
  /// assembly. This prevents the ISA-emulation layer from acquiring a second subtly different parser.
  /// No bytes are emitted.
  /// </summary>
  internal bool TryParseOperands(string operands, IAsmSymbolResolver? resolver, out IReadOnlyList<ParsedAsmOperand> parsed, out string? error) {
    try {
      var parser = new LineParser("__EMU " + operands, resolver, this._target);
      parsed = parser.ParseOperandsForVirtualization();
      error = null;
      return true;
    } catch (AsmSyntaxException exception) {
      parsed = [];
      error = exception.Message;
      return false;
    } catch (ArgumentException exception) {
      parsed = [];
      error = exception.Message;
      return false;
    }
  }

  private sealed partial class LineParser {
    internal IReadOnlyList<ParsedAsmOperand> ParseOperandsForVirtualization() {
      if (this.Current.Kind != TokenKind.Identifier)
        throw new AsmSyntaxException("Internal operand parser lost its synthetic mnemonic.");
      _ = this.Next();
      var operands = this.ParseOperands();
      if (this.Current.Kind != TokenKind.End)
        throw Unexpected(this.Current);
      return [.. operands.Select(ConvertOperand)];
    }

    private static ParsedAsmOperand ConvertOperand(Operand operand) => operand switch {
      RegisterOperand r => new ParsedAsmRegister(r.Register),
      StOperand s => new ParsedAsmSt(s.Register),
      ImmediateOperand i => new ParsedAsmImmediate(i.Value),
      MemoryOperand m => new ParsedAsmMemory(m.Memory),
      LabelOperand l => new ParsedAsmLabel(l.Label),
      _ => throw new InvalidOperationException($"Unknown inline-asm operand {operand.GetType().Name}."),
    };
  }
}
