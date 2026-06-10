namespace PowerBasic.Compiler.Asm;

/// <summary>What a non-register identifier in inline assembly resolved to.</summary>
public enum AsmSymbolKind {
  /// <summary>A numeric constant (e.g. a PB named constant).</summary>
  Constant,
  /// <summary>A memory operand (e.g. a PB variable).</summary>
  Memory,
  /// <summary>A code label (jump/call target).</summary>
  Label,
}

/// <summary>The resolution result for an identifier inside an inline-assembly statement.</summary>
public readonly struct AsmSymbol {

  private AsmSymbol(AsmSymbolKind kind, int value, Mem memory, Label? label) {
    this.Kind = kind;
    this.Value = value;
    this.Memory = memory;
    this.Label = label;
  }

  public AsmSymbolKind Kind { get; }
  public int Value { get; }
  public Mem Memory { get; }
  public Label? Label { get; }

  public static AsmSymbol Constant(int value) => new(AsmSymbolKind.Constant, value, default, null);
  public static AsmSymbol OfMemory(in Mem memory) => new(AsmSymbolKind.Memory, 0, memory, null);
  public static AsmSymbol OfLabel(Label label) => new(AsmSymbolKind.Label, 0, default, label ?? throw new ArgumentNullException(nameof(label)));
}

/// <summary>
/// Maps identifiers found in inline-assembly statements (variables, named
/// constants, labels) to their operand representation.
/// </summary>
public interface IAsmSymbolResolver {

  /// <summary>Tries to resolve <paramref name="name"/>; returns false when unknown.</summary>
  bool TryResolve(string name, out AsmSymbol symbol);
}
