using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Syntax;

/// <summary>Declarations: SUB/FUNCTION/DECLARE, TYPE/UNION, DEF FN/DEFtype/DEF SEG, DIM family, equates.</summary>
public sealed partial class Parser {

  private Statement ParseSub() {
    var pos = this.Advance().Position;
    var name = this.Expect(TokenKind.Identifier, "SUB name");
    var typeParams = this.ParseProcTypeParameters();   // pb36 generics: SUB Name OF T

    // modifiers may precede or follow the parameter list (SUB X CDECL (a, b) PUBLIC)
    List<Parameter>? parameters = null;
    var isStatic = false;
    var visibility = Visibility.Default;
    string? alias = null;
    var convention = CallConvention.Basic;
    for (;;) {
      if (parameters == null && this.Current.Kind == TokenKind.LParen) {
        parameters = this.ParseParameterList();
        continue;
      }
      if (!this.TryParseProcedureModifier(ref isStatic, ref visibility, ref alias, ref convention))
        break;
    }

    var body = this.ParseBody("END SUB");
    this.Advance();
    this.Advance();
    return new SubDecl(pos, name.Text, parameters ?? [], isStatic, visibility, alias, convention, body) { TypeParameters = typeParams };
  }

  private Statement ParseFunction() {
    var pos = this.Advance().Position;
    var name = this.Expect(TokenKind.Identifier, "FUNCTION name");
    var typeParams = this.ParseProcTypeParameters();   // pb36 generics: FUNCTION Name OF T

    List<Parameter>? parameters = null;
    TypeName? returnType = null;
    var isStatic = false;
    var visibility = Visibility.Default;
    string? alias = null;
    var convention = CallConvention.Basic;
    for (;;) {
      if (parameters == null && this.Current.Kind == TokenKind.LParen) {
        parameters = this.ParseParameterList();
        continue;
      }
      if (this.TryMatchKeyword("AS")) {
        returnType = this.ParseTypeName();
        continue;
      }
      if (!this.TryParseProcedureModifier(ref isStatic, ref visibility, ref alias, ref convention))
        break;
    }

    // expression-bodied function (PB 3.6): FUNCTION F(...) [AS T] = expression
    // desugars to a single FUNCTION-result assignment, mirroring DEF FN's '= expr'.
    if (this.Current.Kind == TokenKind.Equals) {
      this.Require(LanguageFeature.ExpressionBodiedProc);
      var eq = this.Advance();
      var result = new NameExpr(eq.Position, "FUNCTION", TypeSuffix.None);
      IReadOnlyList<Statement> exprBody = [new AssignStmt(eq.Position, result, this.ParseExpression())];
      return new FunctionDecl(pos, name.Text, name.Suffix, returnType, parameters ?? [], isStatic, visibility, alias, convention, exprBody) { TypeParameters = typeParams };
    }

    var body = this.ParseBody("END FUNCTION");
    this.Advance();
    this.Advance();
    return new FunctionDecl(pos, name.Text, name.Suffix, returnType, parameters ?? [], isStatic, visibility, alias, convention, body) { TypeParameters = typeParams };
  }

  /// <summary>pb36 generics: an optional <c>OF T</c> / <c>OF (T1, T2)</c> type-parameter list right after a SUB/FUNCTION name. Empty when no <c>OF</c> follows.</summary>
  private List<string> ParseProcTypeParameters() {
    var typeParams = new List<string>();
    if (!this.IsKeyword(0, "OF"))
      return typeParams;
    this.Require(LanguageFeature.Generics);
    this.Advance(); // OF
    if (this.Match(TokenKind.LParen)) {
      do
        typeParams.Add(this.Expect(TokenKind.Identifier, "type parameter").Text);
      while (this.Match(TokenKind.Comma));
      this.Expect(TokenKind.RParen, "')'");
    } else {
      typeParams.Add(this.Expect(TokenKind.Identifier, "type parameter").Text);
    }
    return typeParams;
  }

  private bool TryParseProcedureModifier(ref bool isStatic, ref Visibility visibility, ref string? alias, ref CallConvention convention) {
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
      convention = CallConvention.Cdecl;
      return true;
    }
    if (this.TryMatchKeyword("STDCALL")) {
      convention = CallConvention.Stdcall;
      return true;
    }
    if (this.TryMatchKeyword("PASCAL")) {
      convention = CallConvention.Pascal;
      return true;
    }
    if (this.TryMatchKeyword("FASTCALL")) {
      convention = CallConvention.Fastcall;
      return true;
    }
    if (this.TryMatchKeyword("WATCALL")) {
      convention = CallConvention.Watcall;
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

    // CDECL/ALIAS on a prototype name the external (link) symbol and convention -
    // carried through so $LINK'd objects/libraries (incl. OMF C/asm) resolve by alias
    var isStatic = false;
    var visibility = Visibility.Default;
    string? alias = null;
    var convention = CallConvention.Basic;
    while (this.TryParseProcedureModifier(ref isStatic, ref visibility, ref alias, ref convention)) { }

    IReadOnlyList<Parameter>? parameters = this.Current.Kind == TokenKind.LParen ? this.ParseParameterList() : null;
    TypeName? returnType = this.TryMatchKeyword("AS") ? this.ParseTypeName() : null;
    return new DeclareStmt(pos, isFunction, name.Text, name.Suffix, returnType, parameters, alias, convention);
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

  /// <summary>The pb36 natural type-name aliases (alternative spellings of existing types); they require <see cref="LanguageFeature.TypeAliases"/>.</summary>
  private static readonly HashSet<string> _typeAliasSpellings = new(StringComparer.OrdinalIgnoreCase) {
    "INT8", "SBYTE", "INT16", "SHORT", "INT32", "INT64", "UINT8", "UINT16", "UINT32", "UINT64", "QWORD",
    "DQUAD", "QQUAD", "OQUAD", "DQWORD", "QQWORD", "OWORD",
  };

  private int _anonymousParameters;

  private Parameter ParseParameter(bool optional = false) {
    var byVal = this.TryMatchKeyword("BYVAL");
    if (!byVal)
      this.TryMatchKeyword("BYREF");   // explicit spelling of the default (the back-emitter always writes one of the two)
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

    // default parameter value (PB 3.6): SUB Foo(x AS INTEGER = 10). A defaulted
    // parameter is optional - a call site may omit it (trailing).
    Expression? defaultValue = null;
    if (!isArray && this.Match(TokenKind.Equals)) {
      this.Require(LanguageFeature.DefaultParameters);
      defaultValue = this.ParseExpression();
      optional = true;
    }
    return new(name.Position, name.Text, name.Suffix, type, byVal, seg, isArray, optional, defaultValue);
  }

  private TypeName ParseTypeName() {
    // PB 3.6 typed procedure pointer: FUNCTION(types) AS ret / SUB(types)
    if ((this.IsKeyword(0, "FUNCTION") || this.IsKeyword(0, "SUB")) && this.Peek().Kind == TokenKind.LParen)
      return this.ParseProcPtrType();

    // pb36 tuple type: (T1, T2, ...) - an anonymous value aggregate
    if (this.Current.Kind == TokenKind.LParen) {
      this.Require(LanguageFeature.Tuples);
      var tuplePos = this.Advance().Position; // (
      var elements = new List<TypeName>();
      do
        elements.Add(this.ParseTypeName());
      while (this.Match(TokenKind.Comma));
      this.Expect(TokenKind.RParen, "')'");
      return new TypeName(tuplePos, BuiltinType.None, TupleElements: elements);
    }

    var token = this.Expect(TokenKind.Identifier, "type name");
    var upper = token.Text.ToUpperInvariant();
    var builtin = upper switch {
      "BYTE" or "BYT" => BuiltinType.Byte,
      "WORD" or "WRD" => BuiltinType.Word,
      "DWORD" or "DWD" => BuiltinType.Dword,
      "INTEGER" or "INT" => BuiltinType.Integer,
      "LONG" or "LNG" => BuiltinType.Long,
      "QUAD" or "QUD" => BuiltinType.Quad,
      // pb36 natural type-name aliases (gated below): explicit-width and friendly spellings of the
      // existing types. INTEGER stays 16-bit and LONG 32-bit (classic), so SHORT/INT16 = INTEGER and
      // INT32 = LONG. The wide tiers mirror QUAD/QWORD: D(ouble)/Q(uad)/O(cta) prefixes for 128/256/512.
      "INT8" or "SBYTE" => BuiltinType.SByte,
      "INT16" or "SHORT" => BuiltinType.Integer,
      "INT32" => BuiltinType.Long,
      "INT64" => BuiltinType.Quad,
      "UINT8" => BuiltinType.Byte,
      "UINT16" => BuiltinType.Word,
      "UINT32" => BuiltinType.Dword,
      "UINT64" or "QWORD" => BuiltinType.QWord,
      "DQUAD" => BuiltinType.Int128,
      "QQUAD" => BuiltinType.Int256,
      "OQUAD" => BuiltinType.Int512,
      "DQWORD" => BuiltinType.UInt128,
      "QQWORD" => BuiltinType.UInt256,
      "OWORD" => BuiltinType.UInt512,
      "INT128" => BuiltinType.Int128,
      "INT256" => BuiltinType.Int256,
      "INT512" => BuiltinType.Int512,
      "UINT128" => BuiltinType.UInt128,
      "UINT256" => BuiltinType.UInt256,
      "UINT512" => BuiltinType.UInt512,
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

    // the pb36 alias spellings require the TypeAliases feature (the classic keywords keep their own gates)
    if (_typeAliasSpellings.Contains(upper))
      this.Require(LanguageFeature.TypeAliases);

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
      case BuiltinType.Int128 or BuiltinType.Int256 or BuiltinType.Int512
        or BuiltinType.UInt128 or BuiltinType.UInt256 or BuiltinType.UInt512:
        this.Require(LanguageFeature.WideIntegers);
        break;
    }

    var result = builtin switch {
      BuiltinType.None => new TypeName(token.Position, BuiltinType.None, token.Text) { TypeArguments = this.TryParseTypeArguments() },
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
    // pb36 nullable type T? - the value type plus a presence flag. The '?' glues to the type
    // keyword as a lexed BYTE suffix (LONG?), or stands apart as a Question token (LONG ?).
    if (token.Suffix == TypeSuffix.Byte || this.Match(TokenKind.Question)) {
      this.Require(LanguageFeature.NullableTypes);
      result = result with { IsNullable = true };
    }
    return result;
  }

  /// <summary>Consumes an overloadable operator token after OPERATOR and returns its lifted method name (op_Add, op_Eq, ...).</summary>
  private string ParseOperatorName() {
    var kind = this.Current.Kind;
    var byToken = kind switch {
      TokenKind.Plus => "op_Add", TokenKind.Minus => "op_Sub", TokenKind.Star => "op_Mul",
      TokenKind.Slash => "op_Div", TokenKind.Backslash => "op_IDiv", TokenKind.Caret => "op_Pow",
      TokenKind.Equals => "op_Eq", TokenKind.NotEquals => "op_Ne",
      TokenKind.Less => "op_Lt", TokenKind.Greater => "op_Gt",
      TokenKind.LessEquals => "op_Le", TokenKind.GreaterEquals => "op_Ge",
      _ => null,
    };
    if (byToken != null) {
      this.Advance();
      return byToken;
    }
    var byKeyword = this.Current.Kind == TokenKind.Identifier ? this.Current.Text.ToUpperInvariant() switch {
      "MOD" => "op_Mod", "AND" => "op_And", "OR" => "op_Or", "XOR" => "op_Xor", _ => null,
    } : null;
    if (byKeyword != null) {
      this.Advance();
      return byKeyword;
    }
    throw this.Error("expected an overloadable operator after OPERATOR (+ - * / \\ ^ = <> < > <= >= MOD AND OR XOR)");
  }

  /// <summary>pb36 generics use site: an optional <c>OF</c> type-argument list after a user type name - <c>OF LONG</c> (single) or <c>OF (LONG, STRING)</c> (several). Null when no <c>OF</c> follows.</summary>
  private IReadOnlyList<TypeName>? TryParseTypeArguments() {
    if (!this.IsKeyword(0, "OF"))
      return null;
    this.Require(LanguageFeature.Generics);
    this.Advance(); // OF
    var args = new List<TypeName>();
    if (this.Match(TokenKind.LParen)) {
      do
        args.Add(this.ParseTypeName());
      while (this.Match(TokenKind.Comma));
      this.Expect(TokenKind.RParen, "')'");
    } else {
      args.Add(this.ParseTypeName());
    }
    return args;
  }

  private TypeName ParseProcPtrType() {
    this.Require(LanguageFeature.ProcPointers);
    var isFunction = this.IsKeyword(0, "FUNCTION");
    var pos = this.Advance().Position; // FUNCTION / SUB
    this.Expect(TokenKind.LParen, "'('");
    var paramTypes = new List<TypeName>();
    if (this.Current.Kind != TokenKind.RParen)
      do
        paramTypes.Add(this.ParseTypeName());
      while (this.Match(TokenKind.Comma));
    this.Expect(TokenKind.RParen, "')'");
    var returnType = isFunction && this.TryMatchKeyword("AS") ? this.ParseTypeName() : null;
    return new TypeName(pos, BuiltinType.None, IsProcPtr: true, ProcParameterTypes: paramTypes, ProcReturnType: returnType);
  }

  private TypeName ParseAsciiz(Token token) {
    this.Require(LanguageFeature.AsciizType);
    this.Expect(TokenKind.Star, "'*' after ASCIIZ");
    return new(token.Position, BuiltinType.Asciiz, null, this.ParseExpression());
  }

  // PB 3.6 WITH expr ... END WITH: a leading '.member' inside the body refers to
  // expr.member. Pure parser desugar - the body's dots are rewritten to member
  // accesses on the subject and the block is spliced inline (StatementGroup), so
  // no WITH node reaches the binder/codegen. The subject is re-read per access
  // (use a simple subject if that matters).
  private Statement ParseWith() {
    this.Require(LanguageFeature.WithBlock);
    var pos = this.Advance().Position; // WITH
    var subject = this.ParseExpression();
    this._withSubjects.Add(subject);
    var body = this.ParseBody("END WITH");
    this._withSubjects.RemoveAt(this._withSubjects.Count - 1);
    this.Advance(); // END
    this.Advance(); // WITH
    return new StatementGroup(pos, body);
  }

  /// <summary>EVENT name AS delegate - declares a multicast event (pb36).</summary>
  private Statement ParseEventDecl() {
    this.Require(LanguageFeature.Events);
    var pos = this.Advance().Position; // EVENT
    var name = this.Expect(TokenKind.Identifier, "event name");
    this.ExpectKeyword("AS");
    return new EventDeclStmt(pos, name.Text, this.ParseTypeName());
  }

  /// <summary>
  /// <c>USING v AS Type[(ctor args)]</c> (pb36): declares <paramref name="v"/>, optionally runs the
  /// TYPE's constructor, and schedules <c>v.Dispose()</c> for scope exit via DEFER - which the block
  /// close wraps in TRY ... FINALLY, so disposal runs on the fault path too. The TYPE must expose a
  /// <c>Dispose</c> method (binding the deferred call fails otherwise).
  /// </summary>
  private Statement ParseUsing() {
    this.Require(LanguageFeature.UsingStatement);
    var pos = this.Advance().Position; // USING
    var name = this.Expect(TokenKind.Identifier, "variable name");
    this.ExpectKeyword("AS");
    var typeToken = this.Expect(TokenKind.Identifier, "TYPE name");
    var statements = new List<Statement> {
      new DimStmt(pos, StorageClass.Dim, false, [new VariableDecl(name.Position, name.Text, name.Suffix, null, new TypeName(typeToken.Position, BuiltinType.None, typeToken.Text))]),
    };
    if (this.Current.Kind == TokenKind.LParen)   // constructor arguments: v = Type(args)
      statements.Add(new AssignStmt(pos, new NameExpr(name.Position, name.Text, name.Suffix),
        new CallOrIndexExpr(typeToken.Position, typeToken.Text, TypeSuffix.None, this.ParseArgumentList())));
    statements.Add(new DeferStmt(pos, new MemberCallStmt(pos, new NameExpr(name.Position, name.Text, name.Suffix), "Dispose", [])));
    return new StatementGroup(pos, statements);
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
    // pb36 type alias: TYPE Name AS type (single line, no END TYPE)
    if (!isUnion && this.IsKeyword(0, "AS")) {
      this.Require(LanguageFeature.TypeAlias);
      this.Advance(); // AS
      return new TypeAliasDecl(pos, name.Text, this.ParseTypeName());
    }
    // pb36 generics: TYPE Name OF T (or OF (K, V)) - a template monomorphized per instantiation
    var typeParams = new List<string>();
    if (!isUnion && this.IsKeyword(0, "OF")) {
      this.Require(LanguageFeature.Generics);
      this.Advance(); // OF
      if (this.Match(TokenKind.LParen)) {
        do
          typeParams.Add(this.Expect(TokenKind.Identifier, "type parameter").Text);
        while (this.Match(TokenKind.Comma));
        this.Expect(TokenKind.RParen, "')'");
      } else {
        typeParams.Add(this.Expect(TokenKind.Identifier, "type parameter").Text);
      }
    }
    // pb36: TYPE Name READONLY - fields are write-once (settable only in the constructor)
    var isReadonly = !isUnion && this.TryMatchKeyword("READONLY");
    if (isReadonly)
      this.Require(LanguageFeature.TypeMethods);
    // pb36 layout control: TYPE Name [PACKED | ALIGN n] [SIZE n] - explicit field alignment / total size
    var alignment = 0;
    Expression? explicitSize = null;
    if (!isUnion)
      for (;;) {
        if (this.IsKeyword(0, "PACKED")) {
          this.Require(LanguageFeature.TypeLayout);
          this.Advance();
          alignment = 1;
        } else if (this.IsKeyword(0, "ALIGN")) {
          this.Require(LanguageFeature.TypeLayout);
          this.Advance();
          var n = (int)this.Expect(TokenKind.IntegerLiteral, "alignment").IntegerValue;
          if (n is not (1 or 2 or 4 or 8 or 16))
            throw this.Error("an ALIGN value must be 1, 2, 4, 8 or 16");
          alignment = n;
        } else if (this.IsKeyword(0, "SIZE")) {
          this.Require(LanguageFeature.TypeLayout);
          this.Advance();
          explicitSize = this.ParseExpression();
        } else
          break;
      }
    var end = isUnion ? "END UNION" : "END TYPE";
    var fields = new List<TypeField>();
    var members = new List<TypeMember>();
    for (;;) {
      this.SkipSeparators();
      if (this.IsAtTerminator(end)) {
        this.Advance();
        this.Advance();
        break;
      }
      if (this.Current.Kind == TokenKind.EndOfFile)
        throw this.Error($"unexpected end of file, expected {end}");
      // pb36: a TYPE block may carry SUB/FUNCTION/PROPERTY members (UNION stays fields-only)
      if (!isUnion && (this.IsKeyword(0, "SUB") || this.IsKeyword(0, "FUNCTION") || this.IsKeyword(0, "PROPERTY") || this.IsKeyword(0, "OPERATOR"))) {
        members.AddRange(this.ParseTypeMember());
        continue;
      }
      fields.Add(this.ParseTypeField());
    }
    return isUnion ? new UnionDecl(pos, name.Text, fields) : new TypeDecl(pos, name.Text, fields) { Members = members, IsReadonly = isReadonly, TypeParameters = typeParams, Alignment = alignment, ExplicitSize = explicitSize };
  }

  /// <summary>
  /// A member inside a TYPE block (PB 3.6): <c>SUB</c>/<c>FUNCTION</c> method or
  /// <c>PROPERTY GET</c>/<c>PROPERTY SET</c> accessor. Gated to pb36; the receiver
  /// is the implicit <c>THIS</c> added when the member is lifted to a procedure.
  /// </summary>
  private IReadOnlyList<TypeMember> ParseTypeMember() {
    this.Require(LanguageFeature.TypeMethods);
    var pos = this.Current.Position;
    // pb36 operator overloading: OPERATOR <op> (rhs AS T) AS RetType ... END OPERATOR. THIS is the
    // left operand; the body assigns the result via the RESULT keyword. Lifts to Type.op_<Name>.
    if (this.TryMatchKeyword("OPERATOR")) {
      this.Require(LanguageFeature.OperatorOverloading);
      var opName = this.ParseOperatorName();
      var opParams = this.Current.Kind == TokenKind.LParen ? this.ParseParameterList() : [];
      var opReturn = this.TryMatchKeyword("AS") ? this.ParseTypeName() : null;
      var opBody = this.ParseBody("END OPERATOR");
      this.Advance();
      this.Advance();
      return [new TypeMember(pos, TypeMemberKind.Operator, opName, TypeSuffix.None, opParams, opReturn, opBody)];
    }
    if (this.TryMatchKeyword("SUB")) {
      var name = this.Expect(TokenKind.Identifier, "method name");
      var parameters = this.Current.Kind == TokenKind.LParen ? this.ParseParameterList() : [];
      var body = this.ParseBody("END SUB");
      this.Advance();
      this.Advance();
      return [new TypeMember(pos, TypeMemberKind.Sub, name.Text, name.Suffix, parameters, null, body)];
    }
    if (this.TryMatchKeyword("FUNCTION")) {
      var name = this.Expect(TokenKind.Identifier, "method name");
      var parameters = this.Current.Kind == TokenKind.LParen ? this.ParseParameterList() : [];
      var returnType = this.TryMatchKeyword("AS") ? this.ParseTypeName() : null;
      var body = this.ParseBody("END FUNCTION");
      this.Advance();
      this.Advance();
      return [new TypeMember(pos, TypeMemberKind.Function, name.Text, name.Suffix, parameters, returnType, body)];
    }

    this.ExpectKeyword("PROPERTY");
    var isGet = this.TryMatchKeyword("GET");
    var isSet = !isGet && this.TryMatchKeyword("SET");

    // anonymous full property: PROPERTY Name AS Type (no GET/SET) -> an auto getter AND auto setter
    // over one synthesized backing field (the trivial accessors bind straight to that field)
    if (!isGet && !isSet) {
      var autoName = this.Expect(TokenKind.Identifier, "property name");
      this.ExpectKeyword("AS");
      var autoType = this.ParseTypeName();
      return [
        new TypeMember(pos, TypeMemberKind.PropertyGet, autoName.Text, autoName.Suffix, [], autoType, [], IsAuto: true),
        new TypeMember(pos, TypeMemberKind.PropertySet, autoName.Text, autoName.Suffix, [], autoType, [], IsAuto: true),
      ];
    }

    var propName = this.Expect(TokenKind.Identifier, "property name");
    var kind = isGet ? TypeMemberKind.PropertyGet : TypeMemberKind.PropertySet;
    var propParams = this.Current.Kind == TokenKind.LParen ? this.ParseParameterList() : [];
    var propReturn = this.TryMatchKeyword("AS") ? this.ParseTypeName() : null;   // GET: result type; SET: value type

    // expression body: PROPERTY GET P() AS T => expr  /  PROPERTY SET P() => FIELD = expr
    if (this.Match(TokenKind.FatArrow)) {
      IReadOnlyList<Statement> arrowBody = isGet
        ? [new AssignStmt(pos, new NameExpr(propName.Position, propName.Text, propName.Suffix), this.ParseExpression())]
        : [this.ParseArrowAssignment()];
      return [new TypeMember(pos, kind, propName.Text, propName.Suffix, propParams, propReturn, arrowBody)];
    }

    // auto-implemented property: no body block (the compiler synthesizes a backing field)
    this.SkipSeparators();
    if (this.IsKeyword(0, "SUB") || this.IsKeyword(0, "FUNCTION") || this.IsKeyword(0, "PROPERTY")
        || (this.IsKeyword(0, "END") && this.IsKeyword(1, "TYPE")))
      return [new TypeMember(pos, kind, propName.Text, propName.Suffix, propParams, propReturn, [], IsAuto: true)];

    var propBody = this.ParseBody("END PROPERTY");
    this.Advance();
    this.Advance();
    return [new TypeMember(pos, kind, propName.Text, propName.Suffix, propParams, propReturn, propBody)];
  }

  /// <summary>Parses the <c>lvalue = expr</c> after a PROPERTY SET <c>=&gt;</c> arrow into one assignment.</summary>
  private Statement ParseArrowAssignment() {
    var target = this.ParseLValue();
    var eq = this.Expect(TokenKind.Equals, "'='");
    return new AssignStmt(eq.Position, target, this.ParseExpression());
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
    // pb36 bit-field: field AS BIT [* width] - packs into a hidden storage word, accessed by shift/mask
    if (bounds == null && this.IsKeyword(0, "BIT")) {
      this.Require(LanguageFeature.BitFields);
      this.Advance(); // BIT
      var width = 1;
      if (this.Match(TokenKind.Star)) {
        var widthTok = this.Expect(TokenKind.IntegerLiteral, "bit-field width");
        width = (int)widthTok.IntegerValue;
        if (width is < 1 or > 16)
          throw this.Error("a bit-field width must be 1..16");
      }
      return new(name.Position, name.Text, new TypeName(name.Position, BuiltinType.Word), null, width);
    }
    var type = this.ParseTypeName();
    if (type is { IsPointer: true, PointerTarget.Builtin: BuiltinType.String })
      this.Require(LanguageFeature.StringPtrInType); // STRING PTR fields arrived only in 3.5
    // pb36 layout control: field AS T AT offset - place this field at an explicit byte offset (allows gaps/overlap)
    Expression? explicitOffset = null;
    if (this.IsKeyword(0, "AT")) {
      this.Require(LanguageFeature.TypeLayout);
      this.Advance();
      explicitOffset = this.ParseExpression();
    }
    return new(name.Position, name.Text, type, bounds, ExplicitOffset: explicitOffset);
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
      case "STACK": // pb36: frame-resident local array
        this.Require(LanguageFeature.StackArrays);
        this.Advance();
        return ArrayClass.Stack;
      case "EMS": // PB 3.6 external-memory arrays
        this.Require(LanguageFeature.XmsEmsArrays);
        this.Advance();
        return ArrayClass.Ems;
      case "XMS":
        this.Require(LanguageFeature.XmsEmsArrays);
        this.Advance();
        return ArrayClass.Xms;
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

    // fused declare-and-initialize (PB 3.6): DIM x = value / DIM x AS type = value
    // (scalar), or DIM a(...) = { ... } / DIM a() = { ... } (array initializer).
    Expression? initializer = null;
    if (this.Match(TokenKind.Equals)) {
      initializer = this.ParseExpression();
      this.Require(initializer is ArrayLiteralExpr ? LanguageFeature.ArrayInitializer
        : bounds != null ? LanguageFeature.ArrayInitializer
        : LanguageFeature.DimInitializer);
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
