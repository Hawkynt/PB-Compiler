using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Syntax;

/// <summary>Declarations: SUB/FUNCTION/DECLARE, TYPE/UNION, DEF FN/DEFtype/DEF SEG, DIM family, equates.</summary>
public sealed partial class Parser {

  private Statement ParseSub() {
    var pos = this.Advance().Position;
    var name = this.Expect(TokenKind.Identifier, "SUB name");

    // modifiers may precede or follow the parameter list (SUB X CDECL (a, b) PUBLIC)
    List<Parameter>? parameters = null;
    var isStatic = false;
    var visibility = Visibility.Default;
    string? alias = null;
    var cdecl = false;
    for (;;) {
      if (parameters == null && this.Current.Kind == TokenKind.LParen) {
        parameters = this.ParseParameterList();
        continue;
      }
      if (!this.TryParseProcedureModifier(ref isStatic, ref visibility, ref alias, ref cdecl))
        break;
    }

    var body = this.ParseBody("END SUB");
    this.Advance();
    this.Advance();
    return new SubDecl(pos, name.Text, parameters ?? [], isStatic, visibility, alias, cdecl, body);
  }

  private Statement ParseFunction() {
    var pos = this.Advance().Position;
    var name = this.Expect(TokenKind.Identifier, "FUNCTION name");

    List<Parameter>? parameters = null;
    TypeName? returnType = null;
    var isStatic = false;
    var visibility = Visibility.Default;
    string? alias = null;
    var cdecl = false;
    for (;;) {
      if (parameters == null && this.Current.Kind == TokenKind.LParen) {
        parameters = this.ParseParameterList();
        continue;
      }
      if (this.TryMatchKeyword("AS")) {
        returnType = this.ParseTypeName();
        continue;
      }
      if (!this.TryParseProcedureModifier(ref isStatic, ref visibility, ref alias, ref cdecl))
        break;
    }

    // expression-bodied function (PB 3.6): FUNCTION F(...) [AS T] = expression
    // desugars to a single FUNCTION-result assignment, mirroring DEF FN's '= expr'.
    if (this.Current.Kind == TokenKind.Equals) {
      this.Require(LanguageFeature.ExpressionBodiedProc);
      var eq = this.Advance();
      var result = new NameExpr(eq.Position, "FUNCTION", TypeSuffix.None);
      IReadOnlyList<Statement> exprBody = [new AssignStmt(eq.Position, result, this.ParseExpression())];
      return new FunctionDecl(pos, name.Text, name.Suffix, returnType, parameters ?? [], isStatic, visibility, alias, cdecl, exprBody);
    }

    var body = this.ParseBody("END FUNCTION");
    this.Advance();
    this.Advance();
    return new FunctionDecl(pos, name.Text, name.Suffix, returnType, parameters ?? [], isStatic, visibility, alias, cdecl, body);
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

    // CDECL optional parameters arrive in brackets: (a, b [, c [, d]])
    var optionalDepth = 0;
    do {
      while (this.Match(TokenKind.LBracket)) {
        ++optionalDepth;
        this.Expect(TokenKind.Comma, "','");
      }
      result.Add(this.ParseParameter(optionalDepth > 0));
    } while (this.Match(TokenKind.Comma) || this.Current.Kind == TokenKind.LBracket);
    while (optionalDepth-- > 0)
      this.Expect(TokenKind.RBracket, "']'");
    this.Expect(TokenKind.RParen, "')'");
    return result;
  }

  /// <summary>Builtin type keywords usable as anonymous DECLARE parameters (<c>DECLARE SUB S(BYVAL STRING, INTEGER)</c>).</summary>
  private static readonly HashSet<string> _typeKeywords = new(StringComparer.OrdinalIgnoreCase) {
    "BYTE", "WORD", "DWORD", "INTEGER", "LONG", "QUAD", "SINGLE", "DOUBLE", "EXT",
    "FIX", "BCD", "STRING", "ASCIIZ", "FLEX", "ANY",
  };

  private int _anonymousParameters;

  private Parameter ParseParameter(bool optional = false) {
    var byVal = this.TryMatchKeyword("BYVAL");
    var seg = this.TryMatchKeyword("SEG");

    // anonymous type-only parameter (DECLARE prototypes): BYVAL STRING / INTEGER / STRING * 4
    if (this.Current is { Kind: TokenKind.Identifier, Suffix: TypeSuffix.None } typeToken
        && _typeKeywords.Contains(typeToken.Text)
        && this.Peek().Kind is TokenKind.Comma or TokenKind.RParen or TokenKind.RBracket or TokenKind.Star) {
      var anonType = this.ParseTypeName();
      return new(typeToken.Position, $"__param{++this._anonymousParameters}", TypeSuffix.None, anonType, byVal, seg, IsArray: false, optional);
    }

    var name = this.Expect(TokenKind.Identifier, "parameter name");
    var isArray = false;
    if (this.Current.Kind == TokenKind.LParen && this.Peek().Kind == TokenKind.RParen) {
      this.Advance();
      this.Advance();
      isArray = true;
    } else if (this.Current.Kind == TokenKind.LParen && this.Peek().Kind == TokenKind.IntegerLiteral && this.Peek(2).Kind == TokenKind.RParen) {
      // array parameter declared with its dimension count: arr(1) AS LONG
      this.Advance();
      this.Advance();
      this.Advance();
      isArray = true;
    }
    var type = this.TryMatchKeyword("AS") ? this.ParseTypeName() : null;
    return new(name.Position, name.Text, name.Suffix, type, byVal, seg, isArray, optional);
  }

  private TypeName ParseTypeName() {
    var token = this.Expect(TokenKind.Identifier, "type name");
    var builtin = token.Text.ToUpperInvariant() switch {
      "BYTE" or "BYT" => BuiltinType.Byte,
      "WORD" or "WRD" => BuiltinType.Word,
      "DWORD" or "DWD" => BuiltinType.Dword,
      "INTEGER" or "INT" => BuiltinType.Integer,
      "LONG" or "LNG" => BuiltinType.Long,
      "QUAD" or "QUD" => BuiltinType.Quad,
      "SINGLE" or "SNG" => BuiltinType.Single,
      "DOUBLE" or "DBL" => BuiltinType.Double,
      "EXT" or "EXTENDED" => BuiltinType.Ext,
      "FIX" => BuiltinType.Fix,
      "BCD" => BuiltinType.Bcd,
      "STRING" => BuiltinType.String,
      "ASCIIZ" => BuiltinType.Asciiz,
      "FLEX" or "FLX" => BuiltinType.Flex,
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

  private Statement ParseEnum() {
    this.Require(LanguageFeature.EnumType);
    var pos = this.Advance().Position; // ENUM
    var name = this.Expect(TokenKind.Identifier, "enum name");
    var underlying = this.TryMatchKeyword("AS") ? this.ParseTypeName() : null;
    var members = new List<(string Name, Expression? Value)>();
    for (;;) {
      this.SkipSeparators();
      if (this.IsAtTerminator("END ENUM")) {
        this.Advance();
        this.Advance();
        break;
      }
      if (this.Current.Kind == TokenKind.EndOfFile)
        throw this.Error("unexpected end of file, expected END ENUM");
      var member = this.Expect(TokenKind.Identifier, "enum member name");
      var value = this.Match(TokenKind.Equals) ? this.ParseExpression() : null;
      members.Add((member.Text, value));
      this.Match(TokenKind.Comma); // members may be comma- and/or newline-separated
    }
    return new EnumDecl(pos, name.Text, underlying, members);
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

    var statik = false;
    var variables = new List<VariableDecl>();
    do
      variables.Add(this.ParseVariableDecl(ref shared, ref statik));
    while (this.Match(TokenKind.Comma));

    // DIM x(...) [AS type] AT segment - ABSOLUTE array mapped at a fixed address
    Expression? atAddress = null;
    if (this.TryMatchKeyword("AT")) {
      atAddress = this.ParseExpression();
      arrayClass = ArrayClass.Absolute;
    }
    return new DimStmt(pos, storage, shared, variables, commonBlock, arrayClass, atAddress, statik);
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
      if (this.Current.Kind == TokenKind.LParen && this.Peek().Kind == TokenKind.RParen) { // optional ERASE a()
        this.Advance();
        this.Advance();
      }
      arrays.Add(new(name.Position, name.Text, name.Suffix));
    } while (this.Match(TokenKind.Comma));
    return new EraseStmt(pos, arrays);
  }

  private VariableDecl ParseVariableDecl(ref bool shared) {
    var statik = false;
    return this.ParseVariableDecl(ref shared, ref statik);
  }

  private VariableDecl ParseVariableDecl(ref bool shared, ref bool statik) {
    var name = this.Expect(TokenKind.Identifier, "variable name");
    var (fullName, suffix) = this.ParseDottedNameRest(name);
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
      for (;;) { // AS [SHARED|STATIC] type
        if (this.TryMatchKeyword("SHARED")) {
          shared = true;
          continue;
        }
        if (this.TryMatchKeyword("STATIC")) {
          statik = true;
          continue;
        }
        break;
      }
      type = this.ParseTypeName();
    }

    // fused declare-and-initialize (PB 3.6): DIM x = value / DIM x AS type = value.
    // Only on scalar declarations (an array carries bounds, not an initializer).
    Expression? initializer = null;
    if (bounds == null && this.Match(TokenKind.Equals)) {
      this.Require(LanguageFeature.DimInitializer);
      initializer = this.ParseExpression();
    }
    return new(name.Position, fullName, suffix, bounds, type, initializer);
  }

  /// <summary>
  /// QB-style dotted variable names (<c>DIM TL.Char AS BYTE</c>): consumes
  /// <c>.ident</c> chains following <paramref name="name"/> into one flat name;
  /// the suffix of the last segment wins.
  /// </summary>
  private (string Name, TypeSuffix Suffix) ParseDottedNameRest(Token name) {
    var fullName = name.Text;
    var suffix = name.Suffix;
    while (suffix == TypeSuffix.None && this.Current.Kind == TokenKind.Period && this.Peek().Kind == TokenKind.Identifier) {
      this.Advance();
      var part = this.Advance();
      fullName += "." + part.Text;
      suffix = part.Suffix;
    }
    return (fullName, suffix);
  }

  /// <summary>One array bound: <c>upper</c>, <c>lower TO upper</c>, or <c>lower:upper</c> (colon synonym).</summary>
  private (Expression? Lower, Expression Upper) ParseArrayBound() {
    var first = this.ParseExpression();
    return this.TryMatchKeyword("TO") || this.Match(TokenKind.Colon)
      ? (first, this.ParseExpression())
      : (null, first);
  }
}
