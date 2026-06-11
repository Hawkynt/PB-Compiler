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
    if (this.IsKeyword(0, "ALIAS")) {
      this.Require(LanguageFeature.AliasClause);
      this.Advance();
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
      "FIX" => BuiltinType.Fix,
      "BCD" => BuiltinType.Bcd,
      "STRING" => BuiltinType.String,
      "ASCIIZ" => BuiltinType.Asciiz,
      "FLEX" => BuiltinType.Flex,
      "ANY" => BuiltinType.Any,
      _ => BuiltinType.None,
    };

    switch (builtin) {
      case BuiltinType.Byte or BuiltinType.Word or BuiltinType.Dword:
        this.Require(LanguageFeature.UnsignedTypes);
        break;
      case BuiltinType.Quad:
        this.Require(LanguageFeature.QuadType);
        break;
      case BuiltinType.Any:
        this.Require(LanguageFeature.AnyParameter);
        break;
    }

    var result = builtin switch {
      BuiltinType.None => new TypeName(token.Position, BuiltinType.None, token.Text),
      BuiltinType.String when this.Match(TokenKind.Star) => new(token.Position, BuiltinType.FixedString, null, this.ParseExpression()),
      BuiltinType.Asciiz => this.ParseAsciiz(token),
      _ => new(token.Position, builtin),
    };

    // <type> PTR (PB 3.2), nestable: INTEGER PTR PTR
    while (this.IsKeyword(0, "PTR")) {
      this.Require(LanguageFeature.Pointers);
      this.Advance();
      result = new(token.Position, BuiltinType.None, null, null, result);
    }
    return result;
  }

  private TypeName ParseAsciiz(Token token) {
    this.Require(LanguageFeature.AsciizType);
    this.Expect(TokenKind.Star, "'*' after ASCIIZ");
    return new(token.Position, BuiltinType.Asciiz, null, this.ParseExpression());
  }

  private Statement ParseTypeDecl(bool isUnion) {
    this.Require(LanguageFeature.TypeUnion);
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
    var type = this.ParseTypeName();
    if (type is { IsPointer: true, PointerTarget.Builtin: BuiltinType.String })
      this.Require(LanguageFeature.StringPtrInType); // STRING PTR fields arrived only in 3.5
    return new(name.Position, name.Text, type, bounds);
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

    var arrayClass = this.ParseArrayClass(storage);

    var variables = new List<VariableDecl>();
    do
      variables.Add(this.ParseVariableDecl(ref shared));
    while (this.Match(TokenKind.Comma));

    // DIM x(...) [AS type] AT segment - ABSOLUTE array mapped at a fixed address
    Expression? atAddress = null;
    if (this.TryMatchKeyword("AT")) {
      atAddress = this.ParseExpression();
      arrayClass = ArrayClass.Absolute;
    }
    return new DimStmt(pos, storage, shared, variables, commonBlock, arrayClass, atAddress);
  }

  /// <summary>Optional DIM array-class keyword: STATIC/DYNAMIC/HUGE/VIRTUAL/ABSOLUTE.</summary>
  private ArrayClass ParseArrayClass(StorageClass storage) {
    if (storage != StorageClass.Dim || this.Current.Kind != TokenKind.Identifier || this.Peek().Kind != TokenKind.Identifier)
      return ArrayClass.Default;

    switch (this.Current.Text.ToUpperInvariant()) {
      case "STATIC":
        this.Advance();
        return ArrayClass.Static;
      case "DYNAMIC":
        this.Advance();
        return ArrayClass.Dynamic;
      case "HUGE":
        this.Require(LanguageFeature.HugeArrays);
        this.Advance();
        return ArrayClass.Huge;
      case "VIRTUAL":
        this.Require(LanguageFeature.VirtualArrays);
        this.Advance();
        return ArrayClass.Virtual;
      case "ABSOLUTE":
        this.Advance();
        return ArrayClass.Absolute;
      default:
        return ArrayClass.Default;
    }
  }

  private Statement ParseRedim() {
    var pos = this.Advance().Position;
    var preserve = false;
    if (this.IsKeyword(0, "PRESERVE")) {
      this.Require(LanguageFeature.RedimPreserve);
      this.Advance();
      preserve = true;
    }
    var shared = false;
    var variables = new List<VariableDecl>();
    do
      variables.Add(this.ParseVariableDecl(ref shared));
    while (this.Match(TokenKind.Comma));
    return new RedimStmt(pos, variables, preserve);
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
