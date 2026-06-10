namespace PowerBasic.Compiler.Syntax;

/// <summary>A single lexical token.</summary>
public readonly record struct Token(TokenKind Kind, string Text, SourcePosition Position, TypeSuffix Suffix = TypeSuffix.None, long IntegerValue = 0, double FloatValue = 0, string? StringValue = null) {
  public override string ToString() => $"{this.Kind} '{this.Text}' @{this.Position}";
}
