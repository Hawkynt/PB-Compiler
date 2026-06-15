namespace PowerBasic.Compiler.Syntax;

/// <summary>Lexical token categories of PowerBASIC 3.5 source.</summary>
public enum TokenKind {
  EndOfFile,
  EndOfLine,

  /// <summary>Identifier or keyword (keywords are resolved by the parser, case-insensitively).</summary>
  Identifier,

  /// <summary>Named-constant reference or definition, e.g. <c>%SVGA_MODEX</c>; <see cref="Token.Text"/> holds the bare name.</summary>
  NamedConstant,

  /// <summary>Metastatement, e.g. <c>$INCLUDE</c>; <see cref="Token.Text"/> holds the bare command name.</summary>
  MetaCommand,

  /// <summary>Raw inline-assembly statement body following <c>!</c>.</summary>
  InlineAsm,

  IntegerLiteral,
  FloatLiteral,
  StringLiteral,

  Plus,
  Minus,
  Star,
  Slash,
  Backslash,
  Caret,
  Equals,
  Less,
  Greater,
  LessEquals,
  GreaterEquals,
  NotEquals,
  LParen,
  RParen,
  Comma,
  Semicolon,
  Colon,
  Period,
  Hash,
  Question,
  /// <summary>Standalone <c>&amp;</c>: string concatenation operator (PB 3.5).</summary>
  Ampersand,
  /// <summary>Standalone <c>@</c>: pointer dereference (PB 3.2).</summary>
  At,
  LBracket,
  RBracket,
  /// <summary><c>{</c> / <c>}</c>: object-initializer braces (PB 3.6).</summary>
  LBrace,
  RBrace,
  // PB 3.6 shift/rotate/bitwise operators
  /// <summary><c>&lt;&lt;</c> arithmetic/logical shift left.</summary>
  ShiftLeft,
  /// <summary><c>&lt;&lt;&lt;</c> logical shift left (same as <c>&lt;&lt;</c>).</summary>
  ShiftLeftLogical,
  /// <summary><c>&gt;&gt;</c> arithmetic (signed) shift right.</summary>
  ShiftRight,
  /// <summary><c>&gt;&gt;&gt;</c> logical (unsigned) shift right.</summary>
  ShiftRightLogical,
  /// <summary><c>&lt;&lt;&gt;</c> rotate left.</summary>
  RotateLeft,
  /// <summary><c>&lt;&gt;&gt;</c> rotate right.</summary>
  RotateRight,
  /// <summary><c>|</c> bitwise OR.</summary>
  Pipe,
  /// <summary><c>+*</c> scaled pointer add (index times target size, PB 3.6).</summary>
  PlusStar,
  /// <summary><c>-*</c> scaled pointer subtract (PB 3.6).</summary>
  MinusStar,
}
