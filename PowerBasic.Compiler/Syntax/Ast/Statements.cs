namespace PowerBasic.Compiler.Syntax.Ast;

/// <summary>Base of all statement nodes.</summary>
public abstract record Statement(SourcePosition Position);

/// <summary>A whole compilation unit (main program, unit, or include-expanded module).</summary>
public sealed record CompilationUnit(string FileName, IReadOnlyList<Statement> Statements);

#region types

/// <summary>Built-in scalar type names usable in an <c>AS</c> clause.</summary>
public enum BuiltinType { None, Byte, Word, Dword, Integer, Long, Quad, Single, Double, Ext, String, FixedString, Flex, Any }

/// <summary>An <c>AS</c>-clause type: builtin, fixed string (<c>STRING * n</c>) or a user-defined TYPE/UNION name.</summary>
public sealed record TypeName(SourcePosition Position, BuiltinType Builtin, string? UserTypeName = null, Expression? FixedLength = null) {
  public bool IsUserDefined => this.UserTypeName != null;
}

#endregion

#region declarations

public enum Visibility { Default, Public, Private }

/// <summary>Formal parameter: <c>BYVAL</c>/<c>SEG</c> modifiers, optional <c>AS</c> type, optional <c>()</c> array marker.</summary>
public sealed record Parameter(SourcePosition Position, string Name, TypeSuffix Suffix, TypeName? Type, bool ByVal, bool Seg, bool IsArray);

/// <summary>SUB definition.</summary>
public sealed record SubDecl(SourcePosition Position, string Name, IReadOnlyList<Parameter> Parameters, bool IsStatic, Visibility Visibility, string? Alias, bool Cdecl, IReadOnlyList<Statement> Body) : Statement(Position);

/// <summary>FUNCTION definition; return type from name suffix or <c>AS</c> clause.</summary>
public sealed record FunctionDecl(SourcePosition Position, string Name, TypeSuffix Suffix, TypeName? ReturnType, IReadOnlyList<Parameter> Parameters, bool IsStatic, Visibility Visibility, string? Alias, bool Cdecl, IReadOnlyList<Statement> Body) : Statement(Position);

/// <summary>DECLARE SUB/FUNCTION prototype.</summary>
public sealed record DeclareStmt(SourcePosition Position, bool IsFunction, string Name, TypeSuffix Suffix, TypeName? ReturnType, IReadOnlyList<Parameter>? Parameters) : Statement(Position);

/// <summary>One field inside TYPE/UNION.</summary>
public sealed record TypeField(SourcePosition Position, string Name, TypeName Type, IReadOnlyList<Expression>? ArrayBounds);

/// <summary>TYPE ... END TYPE.</summary>
public sealed record TypeDecl(SourcePosition Position, string Name, IReadOnlyList<TypeField> Fields) : Statement(Position);

/// <summary>UNION ... END UNION.</summary>
public sealed record UnionDecl(SourcePosition Position, string Name, IReadOnlyList<TypeField> Fields) : Statement(Position);

/// <summary>DEF FN single-line or block form.</summary>
public sealed record DefFnDecl(SourcePosition Position, string Name, TypeSuffix Suffix, IReadOnlyList<Parameter> Parameters, Expression? Body, IReadOnlyList<Statement>? BlockBody) : Statement(Position);

/// <summary>DEFINT/DEFLNG/DEFSNG/DEFDBL/DEFEXT/DEFSTR letter-range default typing.</summary>
public sealed record DefTypeStmt(SourcePosition Position, BuiltinType Type, IReadOnlyList<(char From, char To)> Ranges) : Statement(Position);

/// <summary>Named-constant (equate) definition: <c>%NAME = const-expr</c>.</summary>
public sealed record EquateStmt(SourcePosition Position, string Name, Expression Value) : Statement(Position);

#endregion

#region variable declarations

/// <summary>One declared entity inside DIM/LOCAL/STATIC/SHARED/PUBLIC: optional bounds (lower TO upper | upper).</summary>
public sealed record VariableDecl(SourcePosition Position, string Name, TypeSuffix Suffix, IReadOnlyList<(Expression? Lower, Expression Upper)>? ArrayBounds, TypeName? Type);

public enum StorageClass { Dim, Local, Static, Shared, Public, Ext, Common }

/// <summary>DIM/LOCAL/STATIC/SHARED/PUBLIC/EXT/COMMON declaration; DIM may carry an extra SHARED flag (<c>DIM x AS SHARED WORD</c>).</summary>
public sealed record DimStmt(SourcePosition Position, StorageClass Storage, bool SharedFlag, IReadOnlyList<VariableDecl> Variables) : Statement(Position);

/// <summary>REDIM (re-dimension a $DYNAMIC array).</summary>
public sealed record RedimStmt(SourcePosition Position, IReadOnlyList<VariableDecl> Variables) : Statement(Position);

/// <summary>ERASE array, ...</summary>
public sealed record EraseStmt(SourcePosition Position, IReadOnlyList<NameExpr> Arrays) : Statement(Position);

#endregion

#region assignment & calls

/// <summary>Assignment, incl. LET form. Target is a name, array element or member access.</summary>
public sealed record AssignStmt(SourcePosition Position, Expression Target, Expression Value) : Statement(Position);

/// <summary>INCR/DECR x [, amount].</summary>
public sealed record IncrDecrStmt(SourcePosition Position, bool Increment, Expression Target, Expression? Amount) : Statement(Position);

/// <summary>SUB invocation: <c>CALL Name(args)</c> or bare <c>Name args</c>.</summary>
public sealed record CallStmt(SourcePosition Position, string Name, IReadOnlyList<Expression> Arguments, bool UsedCallKeyword) : Statement(Position);

/// <summary>MID$(s$, start [, len]) = value$ statement form.</summary>
public sealed record MidAssignStmt(SourcePosition Position, Expression Target, Expression Start, Expression? Length, Expression Value) : Statement(Position);

/// <summary>LSET/RSET str-or-field = value.</summary>
public sealed record LsetRsetStmt(SourcePosition Position, bool IsLeft, Expression Target, Expression Value) : Statement(Position);

/// <summary>SWAP a, b.</summary>
public sealed record SwapStmt(SourcePosition Position, Expression Left, Expression Right) : Statement(Position);

#endregion

#region control flow

/// <summary>IF in block or single-line form; ElseIfs are (condition, body) pairs.</summary>
public sealed record IfStmt(SourcePosition Position, Expression Condition, IReadOnlyList<Statement> Then, IReadOnlyList<(Expression Condition, IReadOnlyList<Statement> Body)> ElseIfs, IReadOnlyList<Statement>? Else) : Statement(Position);

public enum CaseComparison { Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual }

/// <summary>One CASE selector: a value, a range (<c>x TO y</c>) or a relation (<c>IS &gt; x</c>).</summary>
public sealed record CaseSelector(SourcePosition Position, Expression? Value, Expression? RangeUpper, CaseComparison? IsComparison);

/// <summary>SELECT CASE arm; empty Selectors = CASE ELSE.</summary>
public sealed record CaseArm(SourcePosition Position, IReadOnlyList<CaseSelector> Selectors, IReadOnlyList<Statement> Body);

public sealed record SelectStmt(SourcePosition Position, Expression Subject, IReadOnlyList<CaseArm> Arms) : Statement(Position);

public sealed record ForStmt(SourcePosition Position, Expression Variable, Expression From, Expression To, Expression? Step, IReadOnlyList<Statement> Body) : Statement(Position);

public enum LoopTestKind { None, While, Until }

/// <summary>DO/LOOP with optional pre- or post-test; also covers WHILE/WEND (pre-test While).</summary>
public sealed record DoLoopStmt(SourcePosition Position, LoopTestKind PreTest, Expression? PreCondition, LoopTestKind PostTest, Expression? PostCondition, IReadOnlyList<Statement> Body) : Statement(Position);

public enum ExitKind { For, Do, Loop, Sub, Function, Def, Select, If }

public sealed record ExitStmt(SourcePosition Position, ExitKind Kind) : Statement(Position);

/// <summary>Label definition (identifier label or numeric line number).</summary>
public sealed record LabelStmt(SourcePosition Position, string Name) : Statement(Position);

public sealed record GotoStmt(SourcePosition Position, string Target) : Statement(Position);

public sealed record GosubStmt(SourcePosition Position, string Target) : Statement(Position);

/// <summary>RETURN [label].</summary>
public sealed record ReturnStmt(SourcePosition Position, string? Target) : Statement(Position);

/// <summary>ON expr GOTO/GOSUB label-list.</summary>
public sealed record OnGotoStmt(SourcePosition Position, Expression Selector, bool IsGosub, IReadOnlyList<string> Targets) : Statement(Position);

/// <summary>END / STOP / SYSTEM program termination (END SUB etc. are structural, not this).</summary>
public sealed record EndStmt(SourcePosition Position, Expression? ExitCode) : Statement(Position);

#endregion

#region error handling & events

/// <summary>ON ERROR GOTO label|0 / RESUME NEXT-style registration.</summary>
public sealed record OnErrorStmt(SourcePosition Position, string? Target, bool ResumeNext) : Statement(Position);

public enum ResumeKind { SameStatement, Next, Label }

public sealed record ResumeStmt(SourcePosition Position, ResumeKind Kind, string? Target) : Statement(Position);

/// <summary>ERROR n - raise a runtime error.</summary>
public sealed record ErrorStmt(SourcePosition Position, Expression Code) : Statement(Position);

/// <summary>ON KEY(n)/TIMER(n)/COM(n)... GOSUB label event registration.</summary>
public sealed record OnEventStmt(SourcePosition Position, string EventKind, Expression? Index, string Target) : Statement(Position);

/// <summary>KEY(n) ON/OFF/STOP, TIMER ON/... event arming.</summary>
public sealed record EventControlStmt(SourcePosition Position, string EventKind, Expression? Index, string Mode) : Statement(Position);

#endregion

#region I/O

public enum PrintSeparator { Newline, Comma, Semicolon }

/// <summary>One PRINT list item with its trailing separator; SPC(n)/TAB(n) appear as expressions.</summary>
public sealed record PrintItem(Expression? Value, PrintSeparator Separator);

/// <summary>PRINT/LPRINT [#n,] [USING fmt;] items.</summary>
public sealed record PrintStmt(SourcePosition Position, Expression? FileNumber, bool IsLPrint, Expression? UsingFormat, IReadOnlyList<PrintItem> Items) : Statement(Position);

/// <summary>INPUT/LINE INPUT [#n,] ["prompt",|;] var-list.</summary>
public sealed record InputStmt(SourcePosition Position, bool IsLineInput, Expression? FileNumber, string? Prompt, bool PromptSemicolon, IReadOnlyList<Expression> Targets) : Statement(Position);

public enum FileMode { Input, Output, Append, Random, Binary }

/// <summary>OPEN file$ FOR mode [ACCESS ...] [LOCK ...] AS [#]n [LEN = reclen].</summary>
public sealed record OpenStmt(SourcePosition Position, Expression FileName, FileMode Mode, string? Access, string? Lock, Expression FileNumber, Expression? RecordLength) : Statement(Position);

/// <summary>CLOSE [[#]n, ...]; empty = close all.</summary>
public sealed record CloseStmt(SourcePosition Position, IReadOnlyList<Expression> FileNumbers) : Statement(Position);

/// <summary>GET/PUT #n [, record [, var]] - file form.</summary>
public sealed record GetPutFileStmt(SourcePosition Position, bool IsGet, Expression FileNumber, Expression? RecordNumber, Expression? Variable) : Statement(Position);

/// <summary>SEEK #n, position.</summary>
public sealed record SeekStmt(SourcePosition Position, Expression FileNumber, Expression Target) : Statement(Position);

/// <summary>FIELD #n, width AS strvar, ...</summary>
public sealed record FieldStmt(SourcePosition Position, Expression FileNumber, IReadOnlyList<(Expression Width, Expression Target)> Fields) : Statement(Position);

#endregion

#region DATA

public sealed record DataStmt(SourcePosition Position, IReadOnlyList<string> Items) : Statement(Position);

public sealed record ReadStmt(SourcePosition Position, IReadOnlyList<Expression> Targets) : Statement(Position);

public sealed record RestoreStmt(SourcePosition Position, string? Target) : Statement(Position);

#endregion

#region low level & misc

/// <summary>One raw inline-assembly statement (the text after <c>!</c>).</summary>
public sealed record InlineAsmStmt(SourcePosition Position, string Text) : Statement(Position);

/// <summary>DEF SEG [= expr].</summary>
public sealed record DefSegStmt(SourcePosition Position, Expression? Segment) : Statement(Position);

/// <summary>
/// Catch-all for keyword statements taking a plain expression list (BEEP, CLS, POKE,
/// OUT, WAIT, LOCATE, COLOR, SOUND, RANDOMIZE, SHELL, KILL, CHDIR, DELAY, REG, ...).
/// Keyword is upper-case; omitted positional arguments are null.
/// </summary>
public sealed record CommandStmt(SourcePosition Position, string Keyword, IReadOnlyList<Expression?> Arguments) : Statement(Position);

/// <summary>Graphics LINE [(x1,y1)]-(x2,y2) [,[color][,B[F][,style]]].</summary>
public sealed record LineStmt(SourcePosition Position, (Expression X, Expression Y)? From, (Expression X, Expression Y) To, Expression? Color, bool Box, bool Fill, Expression? Style) : Statement(Position);

/// <summary>Graphics CIRCLE (x,y), r [,color [,start [,end [,aspect]]]].</summary>
public sealed record CircleStmt(SourcePosition Position, (Expression X, Expression Y) Center, Expression Radius, Expression? Color, Expression? Start, Expression? End, Expression? Aspect) : Statement(Position);

/// <summary>Graphics PSET/PRESET (x,y) [,color].</summary>
public sealed record PsetStmt(SourcePosition Position, bool IsPreset, (Expression X, Expression Y) Point, Expression? Color) : Statement(Position);

/// <summary>Graphics GET/PUT (x1,y1)-(x2,y2), array / PUT (x,y), array [,verb].</summary>
public sealed record GetPutGraphicsStmt(SourcePosition Position, bool IsGet, (Expression X, Expression Y) From, (Expression X, Expression Y)? To, Expression Array, string? Verb) : Statement(Position);

/// <summary>A metastatement kept for the driver ($CPU, $STACK, $COMPILE, $LINK, $ERROR, ...), arguments as raw tokens.</summary>
public sealed record MetaStmt(SourcePosition Position, string Command, IReadOnlyList<Token> Arguments) : Statement(Position);

#endregion
