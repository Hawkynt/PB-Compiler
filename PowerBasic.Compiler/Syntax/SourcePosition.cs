namespace PowerBasic.Compiler.Syntax;

/// <summary>A location in PowerBASIC source, 1-based.</summary>
public readonly record struct SourcePosition(string File, int Line, int Column) {
  public override string ToString() => $"{this.File}({this.Line},{this.Column})";
}
