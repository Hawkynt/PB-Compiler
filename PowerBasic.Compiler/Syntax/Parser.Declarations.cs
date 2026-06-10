using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Syntax;

/// <summary>Declarations: SUB/FUNCTION/DECLARE, TYPE/UNION, DEF FN/DEFtype/DEF SEG, DIM family, equates.</summary>
public sealed partial class Parser {

  private Statement ParseSub() {
    var pos = this.Advance().Position;
    var name = this.Expect(TokenKind.Identifier, "SUB name");
    var parameters = this.Current.Kind == TokenKind.LParen ? this.ParseParameterList() : [];
    var (isStatic, visibility, alias, cdecl) = this.ParseProcedureModifiers();
    var body = this.ParseBody("END SUB");
    this.Advance();
    this.Advance();
    return new SubDecl(pos, name.Text, parameters, isStatic, visibility, alias, cdecl, body);
  }

  private Statement ParseFunction() {
    var pos = this.Advance().Position;
    var name = this.Expect(TokenKind.Identifier, "FUNCTION name");
    var parameters = this.Current.Kind == TokenKind.LParen ? this.ParseParameterList() : (List<Parameter>)[];

    TypeName? returnType = null;
    var isStatic = false;
    var visibility = Visibility.Default;
    string? alias = null;
    var cdecl = false;
    for (;;) {
      if (this.TryMatchKeyword("AS")) {
        returnType = this.ParseTypeName();
        continue;
      }
      if (!this.TryParseProcedureModifier(ref isStatic, ref visibility, ref alias, ref cdecl))
        break;
    }

    var body = this.ParseBody("END FUNCTION");
    this.Advance();
    this.Advance();
    return new FunctionDecl(pos, name.Text, name.Suffix, returnType, parameters, isStatic, visibility, alias, cdecl, body);
  }

  private (bool IsStatic, Visibility Visibility, string? Alias, bool Cdecl) ParseProcedureModifiers() {
    var isStatic = false;
    var visibility = Visibility.Default;
    string? alias = null;
    var cdecl = false;
    while (this.TryParseProcedureModifier(ref isStatic, ref visibility, ref alias, ref cdecl)) { }
    return (isStatic, visibility, alias, cdecl);
  }

  private bool TryParseProcedureModifier(ref bool isStatic, ref Visibility visibility, ref string? alias, ref bool cdecl) {
    if (this.TryMatchKeyword("STATIC")) {
      isStatic = true;
      return true;
    }
    if (this.TryMatchKeyword("PUBLIC")) {
      visibility = Visibility.Public;
      return true;
    }
    if (this.TryMatchKeyword("PRIVATE")) {
      visibility = Visibility.Private;
      return true;
    }
    if (this.TryMatchKeyword("CDECL")) {
      cdecl = true;
      return true;
    }
    if (this.TryMatchKeyword("ALIAS")) {
      alias = this.Expect(TokenKind.StringLiteral, "ALIAS name").StringValue;
      return true;
    }
    return false;
  }

  private Statement ParseDeclare() {
    var pos = this.Advance().Position;
    var isFunction = this.TryMatchKeyword("FUNCTION");
    if (!isFunction)
      this.ExpectKeyword("SUB");
    var name = this.Expect(TokenKind.Identifier, "procedure name");

    // CDECL/ALIAS are legal on prototypes but not represented in DeclareStmt - parse and discard
    var isStatic = false;
    var visibility = Visibility.Default;
    string? alias = null;
    var cdecl = false;
    while (this.TryParseProcedureModifier(ref isStatic, ref visibility, ref alias, ref cdecl)) { }

    IReadOnlyList<Parameter>? parameters = this.Current.Kind == TokenKind.LParen ? this.ParseParameterList() : null;
    TypeName? returnType = this.TryMatchKeyword("AS") ? this.ParseTypeName() : null;
    return new DeclareStmt(pos, isFunction, name.Text, name.Suffix, returnType, parameters);
  }

  private List<Parameter> ParseParameterList() {
    this.Expect(TokenKind.LParen, "'('");
    var result = new List<Parameter>();
    if (this.Match(TokenKind.RParen))
      return result;

    do
      result.Add(this.ParseParameter());
    while (this.Match(TokenKind.Comma));
    this.Expect(TokenKind.RParen, "')'");
    return result;
  }

  private Parameter ParseParameter() {
    var byVal = this.TryMatchKeyword("BYVAL");
    var seg = this.TryMatchKeyword("SEG");
    var name = this.Expect(TokenKind.Identifier, "parameter name");
    var isArray = false;
    if (this.Current.Kind == TokenKind.LParen && this.Peek().Kind == TokenKind.RParen) {
      this.Advance();
      this.Advance();
      isArray = true;
    }
    var type = this.TryMatchKeyword("AS") ? this.ParseTypeName() : null;
    return new(name.Position, name.Text, name.Suffix, type, byVal, seg, isArray);
  }

  private TypeName ParseTypeName() {
    var token = this.Expect(TokenKind.Identifier, "type name");
    var builtin = token.Text.ToUpperInvariant() switch {
      "BYTE" => BuiltinType.Byte,
      "WORD" => BuiltinType.Word,
      "DWORD" => BuiltinType.Dword,
      "INTEGER" => BuiltinType.Integer,
      "LONG" => BuiltinType.Long,
      "QUAD" => BuiltinType.Quad,
      "SINGLE" => BuiltinType.Single,
      "DOUBLE" => BuiltinType.Double,
      "EXT" => BuiltinType.Ext,
      "STRING" => BuiltinType.String,
      "FLEX" => BuiltinType.Flex,
      "ANY" => BuiltinType.Any,
      _ => BuiltinType.None,
    };

    if (builtin == BuiltinType.None)
      return new(token.Position, BuiltinType.None, token.Text);
    if (builtin == BuiltinType.String && this.Match(TokenKind.Star))
      return new(token.Position, BuiltinType.FixedString, null, this.ParseExpression());
    return new(token.Position, builtin);
  }

  private Statement ParseTypeDecl(bool isUnion) {
    var pos = this.Advance().Position;
    var name = this.Expect(TokenKind.Identifier, "type name");
    var end = isUnion ? "END UNION" : "END TYPE";
    var fields = new List<TypeField>();
    for (;;) {
      this.SkipSeparators();
      if (this.IsAtTerminator(end)) {
        this.Advance();
        this.Advance();
        break;
      }
      if (this.Current.Kind == TokenKind.EndOfFile)
        throw this.Error($"unexpected end of file, expected {end}");
      fields.Add(this.ParseTypeField());
    }
    return isUnion ? new UnionDecl(pos, name.Text, fields) : new TypeDecl(pos, name.Text, fields);
  }

  private TypeField ParseTypeField() {
    var name = this.Expect(TokenKind.Identifier, "field name");
    List<(Expression? Lower, Expression Upper)>? bounds = null;
    if (this.Match(TokenKind.LParen)) {
      bounds = [];
      do
        bounds.Add(this.ParseArrayBound());
      while (this.Match(TokenKind.Comma));
      this.Expect(TokenKind.RParen, "')'");
    }
    this.ExpectKeyword("AS");
    return new(name.Position, name.Text, this.ParseTypeName(), bounds);
  }

  private Statement ParseDef() {
    var pos = this.Advance().Position;
    if (this.TryMatchKeyword("SEG"))
      return new DefSegStmt(pos, this.Match(TokenKind.Equals) ? this.ParseExpression() : null);

    var name = this.Expect(TokenKind.Identifier, "FN name");
    if (!name.Text.StartsWith("FN", StringComparison.OrdinalIgnoreCase))
      throw new ParserException($"expected SEG or FN-name after DEF, found '{name.Text}'", name.Position);

    var parameters = this.Current.Kind == TokenKind.LParen ? this.ParseParameterList() : (List<Parameter>)[];
    if (this.Match(TokenKind.Equals))
      return new DefFnDecl(pos, name.Text, name.Suffix, parameters, this.ParseExpression(), null);

    var body = this.ParseBody("END DEF");
    this.Advance();
    this.Advance();
    return new DefFnDecl(pos, name.Text, name.Suffix, parameters, null, body);
  }

  private Statement ParseDefType(BuiltinType type) {
    var pos = this.Advance().Position;
    var ranges = new List<(char From, char To)>();
    do {
      var from = this.ParseRangeLetter();
      var to = this.Match(TokenKind.Minus) ? this.ParseRangeLetter() : from;
      ranges.Add((from, to));
    } while (this.Match(TokenKind.Comma));
    return new DefTypeStmt(pos, type, ranges);
  }

  private char ParseRangeLetter() {
    var token = this.Expect(TokenKind.Identifier, "letter");
    if (token.Text.Length != 1)
      throw new ParserException($"expected single letter, found '{token.Text}'", token.Position);
    return char.ToUpperInvariant(token.Text[0]);
  }

  private Statement ParseDim(StorageClass storage) {
    var pos = this.Advance().Position;
    var shared = false;
    string? commonBlock = null;
    if (storage == StorageClass.Common) {
      shared = this.TryMatchKeyword("SHARED");
      if (this.Match(TokenKind.Slash)) {
        commonBlock = this.Expect(TokenKind.Identifier, "COMMON block name").Text;
        this.Expect(TokenKind.Slash, "'/'");
      }
    }

    var variables = new List<VariableDecl>();
    do
      variables.Add(this.ParseVariableDecl(ref shared));
    while (this.Match(TokenKind.Comma));
    return new DimStmt(pos, storage, shared, variables, commonBlock);
  }

  private Statement ParseRedim() {
    var pos = this.Advance().Position;
    var shared = false;
    var variables = new List<VariableDecl>();
    do
      variables.Add(this.ParseVariableDecl(ref shared));
    while (this.Match(TokenKind.Comma));
    return new RedimStmt(pos, variables);
  }

  private Statement ParseErase() {
    var pos = this.Advance().Position;
    var arrays = new List<NameExpr>();
    do {
      var name = this.Expect(TokenKind.Identifier, "array name");
      arrays.Add(new(name.Position, name.Text, name.Suffix));
    } while (this.Match(TokenKind.Comma));
    return new EraseStmt(pos, arrays);
  }

  private VariableDecl ParseVariableDecl(ref bool shared) {
    var name = this.Expect(TokenKind.Identifier, "variable name");
    List<(Expression? Lower, Expression Upper)>? bounds = null;
    if (this.Match(TokenKind.LParen)) {
      bounds = [];
      if (!this.Match(TokenKind.RParen)) {
        do
          bounds.Add(this.ParseArrayBound());
        while (this.Match(TokenKind.Comma));
        this.Expect(TokenKind.RParen, "')'");
      }
    }

    TypeName? type = null;
    if (this.TryMatchKeyword("AS")) {
      shared |= this.TryMatchKeyword("SHARED");
      type = this.ParseTypeName();
    }
    return new(name.Position, name.Text, name.Suffix, bounds, type);
  }

  private (Expression? Lower, Expression Upper) ParseArrayBound() {
    var first = this.ParseExpression();
    return this.TryMatchKeyword("TO") ? (first, this.ParseExpression()) : (null, first);
  }
}
