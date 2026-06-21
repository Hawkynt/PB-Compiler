namespace PowerBasic.Compiler.Syntax.Ast;

/// <summary>Base of all statement nodes.</summary>
public abstract record Statement(SourcePosition Position);

/// <summary>A whole compilation unit (main program, unit, or include-expanded module).</summary>
public sealed record CompilationUnit(string FileName, IReadOnlyList<Statement> Statements);

#region types

/// <summary>Built-in scalar type names usable in an <c>AS</c> clause.</summary>
public enum BuiltinType { None, Byte, Word, Dword, Integer, Long, Quad, Single, Double, Ext, Fix, Bcd, String, FixedString, Asciiz, Flex, Any }

/// <summary>
/// An <c>AS</c>-clause type: builtin, fixed string (<c>STRING * n</c>), ASCIIZ
/// (<c>ASCIIZ * n</c>), a user-defined TYPE/UNION name, or - when
/// <see cref="PointerTarget"/> is set - a pointer to that type (<c>... PTR</c>).
/// </summary>
public sealed record TypeName(SourcePosition Position, BuiltinType Builtin, string? UserTypeName = null, Expression? FixedLength = null, TypeName? PointerTarget = null,
    IReadOnlyList<TypeName>? ProcParameterTypes = null, TypeName? ProcReturnType = null, bool IsProcPtr = false) {
  public bool IsUserDefined => this.UserTypeName != null;
  public bool IsPointer => this.PointerTarget != null;
}

#endregion

#region declarations

public enum Visibility { Default, Public, Private }

/// <summary>
/// Calling convention of a SUB/FUNCTION/DECLARE. <see cref="Basic"/> is PB's
/// default (arguments left to right, BYREF unless BYVAL, callee cleans via RET n);
/// <see cref="Cdecl"/> pushes right to left and the caller cleans; <see cref="Stdcall"/>
/// pushes right to left but the callee cleans; <see cref="Pascal"/> pushes left to
/// right and the callee cleans.
/// </summary>
public enum CallConvention { Basic, Cdecl, Stdcall, Pascal, Fastcall, Watcall }

/// <summary>
/// Formal parameter: <c>BYVAL</c>/<c>SEG</c> modifiers, optional <c>AS</c> type, optional <c>()</c>
/// array marker (a dimension count inside the parens is accepted: <c>arr(1) AS LONG</c>);
/// <see cref="Optional"/> marks CDECL bracket parameters (<c>[, BYVAL x]</c>).
/// </summary>
public sealed record Parameter(SourcePosition Position, string Name, TypeSuffix Suffix, TypeName? Type, bool ByVal, bool Seg, bool IsArray, bool Optional = false, Expression? DefaultValue = null);

/// <summary>SUB definition.</summary>
public sealed record SubDecl(SourcePosition Position, string Name, IReadOnlyList<Parameter> Parameters, bool IsStatic, Visibility Visibility, string? Alias, CallConvention Convention, IReadOnlyList<Statement> Body) : Statement(Position) {
  /// <summary>Back-compat shorthand for the CDECL convention.</summary>
  public bool Cdecl => this.Convention == CallConvention.Cdecl;
}

/// <summary>FUNCTION definition; return type from name suffix or <c>AS</c> clause.</summary>
public sealed record FunctionDecl(SourcePosition Position, string Name, TypeSuffix Suffix, TypeName? ReturnType, IReadOnlyList<Parameter> Parameters, bool IsStatic, Visibility Visibility, string? Alias, CallConvention Convention, IReadOnlyList<Statement> Body) : Statement(Position) {
  /// <summary>Back-compat shorthand for the CDECL convention.</summary>
  public bool Cdecl => this.Convention == CallConvention.Cdecl;
}

/// <summary>DECLARE SUB/FUNCTION prototype.</summary>
public sealed record DeclareStmt(SourcePosition Position, bool IsFunction, string Name, TypeSuffix Suffix, TypeName? ReturnType, IReadOnlyList<Parameter>? Parameters, string? Alias = null, CallConvention Convention = CallConvention.Basic) : Statement(Position) {
  /// <summary>Back-compat shorthand for the CDECL convention.</summary>
  public bool Cdecl => this.Convention == CallConvention.Cdecl;
}

/// <summary>One field inside TYPE/UNION; array fields carry bounds (lower TO upper | upper).</summary>
public sealed record TypeField(SourcePosition Position, string Name, TypeName Type, IReadOnlyList<(Expression? Lower, Expression Upper)>? ArrayBounds);

/// <summary>The four member shapes a PB 3.6 TYPE block can declare alongside its fields.</summary>
public enum TypeMemberKind { Sub, Function, PropertyGet, PropertySet }

/// <summary>
/// A member declared inside a TYPE block (PB 3.6): a SUB/FUNCTION method or a
/// PROPERTY GET/SET accessor. Each lifts to an ordinary procedure that takes the
/// instance BYREF as an implicit first parameter named THIS (the receiver), fully
/// resolved at compile time from the static type - no inheritance, no virtual
/// dispatch. <see cref="ReturnType"/> applies to FUNCTION / PROPERTY GET only.
/// </summary>
public sealed record TypeMember(SourcePosition Position, TypeMemberKind Kind, string Name, TypeSuffix Suffix, IReadOnlyList<Parameter> Parameters, TypeName? ReturnType, IReadOnlyList<Statement> Body);

/// <summary>TYPE ... END TYPE. <see cref="Members"/> is empty unless the block declares methods/properties (pb36).</summary>
public sealed record TypeDecl(SourcePosition Position, string Name, IReadOnlyList<TypeField> Fields) : Statement(Position) {
  public IReadOnlyList<TypeMember> Members { get; init; } = [];
}

/// <summary>UNION ... END UNION.</summary>
public sealed record UnionDecl(SourcePosition Position, string Name, IReadOnlyList<TypeField> Fields) : Statement(Position);

/// <summary>DEF FN single-line or block form.</summary>
public sealed record DefFnDecl(SourcePosition Position, string Name, TypeSuffix Suffix, IReadOnlyList<Parameter> Parameters, Expression? Body, IReadOnlyList<Statement>? BlockBody) : Statement(Position);

/// <summary>DEFINT/DEFLNG/DEFSNG/DEFDBL/DEFEXT/DEFSTR letter-range default typing.</summary>
public sealed record DefTypeStmt(SourcePosition Position, BuiltinType Type, IReadOnlyList<(char From, char To)> Ranges) : Statement(Position);

/// <summary>Named-constant (equate) definition: <c>%NAME = const-expr</c>.</summary>
public sealed record EquateStmt(SourcePosition Position, string Name, Expression Value) : Statement(Position);

/// <summary>
/// PB 3.6 <c>ENUM Name [AS type] : A [= v], B, ... : END ENUM</c>: a group of named
/// integer constants (auto-incrementing from 0 / last+1, or explicit values). The
/// name is usable as an integer type alias.
/// </summary>
public sealed record EnumDecl(SourcePosition Position, string Name, TypeName? UnderlyingType, IReadOnlyList<(string Name, Expression? Value)> Members) : Statement(Position);

#endregion

#region variable declarations

/// <summary>
/// One declared entity inside DIM/LOCAL/STATIC/SHARED/PUBLIC: optional bounds (lower TO upper | upper).
/// <paramref name="Initializer"/> is the fused declare-and-initialize value (PB 3.6:
/// <c>DIM x = value</c> / <c>DIM x AS type = value</c>); the binder infers the type from it
/// when no explicit type is given and lowers the init to a real assignment after the declaration.
/// </summary>
public sealed record VariableDecl(SourcePosition Position, string Name, TypeSuffix Suffix, IReadOnlyList<(Expression? Lower, Expression Upper)>? ArrayBounds, TypeName? Type, Expression? Initializer = null);

public enum StorageClass { Dim, Local, Static, Shared, Public, Ext, Common }

/// <summary>Array allocation class selected on DIM (see docs/DIALECTS.md). PB 3.6 adds Ems/Xms (external-memory backed).</summary>
public enum ArrayClass { Default, Static, Dynamic, Huge, Virtual, Absolute, Ems, Xms }

/// <summary>
/// DIM/LOCAL/STATIC/SHARED/PUBLIC/EXT/COMMON declaration; DIM may carry an extra SHARED flag
/// (<c>DIM x AS SHARED WORD</c>); <c>COMMON /blockname/</c> carries the block name;
/// <c>DIM HUGE/VIRTUAL/DYNAMIC/STATIC</c> select the array class, <c>AT segment</c> maps ABSOLUTE.
/// </summary>
public sealed record DimStmt(SourcePosition Position, StorageClass Storage, bool SharedFlag, IReadOnlyList<VariableDecl> Variables, string? CommonBlock = null, ArrayClass Class = ArrayClass.Default, Expression? AtAddress = null, bool StaticFlag = false) : Statement(Position);

/// <summary>REDIM (re-dimension a $DYNAMIC array); PRESERVE (3.5) keeps existing contents.</summary>
public sealed record RedimStmt(SourcePosition Position, IReadOnlyList<VariableDecl> Variables, bool Preserve = false) : Statement(Position);

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

/// <summary>
/// PB 3.6 statement-form member call: <c>receiver.Member(args)</c> / <c>receiver.Member args</c>.
/// The binder resolves it against the receiver's static type and desugars it to a call on the
/// lifted member procedure with the receiver passed as the BYREF THIS first argument.
/// </summary>
public sealed record MemberCallStmt(SourcePosition Position, Expression Receiver, string Member, IReadOnlyList<Expression> Arguments) : Statement(Position);

/// <summary>Far call through a 32-bit pointer: <c>CALL DWORD ptr [BDECL|CDECL|SDECL] (args)</c>.</summary>
public sealed record CallPtrStmt(SourcePosition Position, Expression Pointer, string? Convention, IReadOnlyList<Expression> Arguments) : Statement(Position);

/// <summary>MID$(s$, start [, len]) = value$ statement form.</summary>
public sealed record MidAssignStmt(SourcePosition Position, Expression Target, Expression Start, Expression? Length, Expression Value) : Statement(Position);

/// <summary>ASC(s$ [, position]) = code statement form (PB 3.5).</summary>
public sealed record AscAssignStmt(SourcePosition Position, Expression Target, Expression? Index, Expression Value) : Statement(Position);

/// <summary>STDOUT [s$] [;] - writes to DOS handle 1 (redirectable); trailing ';' suppresses the newline (PB 3.5).</summary>
public sealed record StdOutStmt(SourcePosition Position, Expression? Value, bool NoNewline) : Statement(Position);

/// <summary>STDIN n, s$ (read n bytes) / STDIN LINE, s$ (read a line) from DOS handle 0 (PB 3.5).</summary>
public sealed record StdInStmt(SourcePosition Position, bool Line, Expression? Count, Expression Target) : Statement(Position);

/// <summary>LSET/RSET str-or-field = value.</summary>
public sealed record LsetRsetStmt(SourcePosition Position, bool IsLeft, Expression Target, Expression Value) : Statement(Position);

/// <summary>SWAP a, b.</summary>
public sealed record SwapStmt(SourcePosition Position, Expression Left, Expression Right) : Statement(Position);

/// <summary>REPLACE find$ WITH with$ IN target$ - replaces every occurrence.</summary>
public sealed record ReplaceStmt(SourcePosition Position, Expression Find, Expression With, Expression Target) : Statement(Position);

public enum BitOp { Set, Reset, Toggle }

/// <summary>BIT SET/RESET/TOGGLE var, bit-number (PB 3.0).</summary>
public sealed record BitStmt(SourcePosition Position, BitOp Op, Expression Target, Expression Bit) : Statement(Position);

/// <summary>
/// ARRAY SORT arr([start]) [FOR count] [, FROM x TO y] [, COLLATE c$] [, ASCEND|DESCEND] [, TAGARRAY tag()].
/// FROM/TO limit string comparison to a character-position range.
/// </summary>
public sealed record ArraySortStmt(SourcePosition Position, CallOrIndexExpr Array, Expression? Count, Expression? FromPos, Expression? ToPos, Expression? Collate, bool Descend, CallOrIndexExpr? TagArray) : Statement(Position);

/// <summary>
/// ARRAY SCAN arr([start]) [FOR count] [, FROM x TO y] [, COLLATE c$], relop expr, TO var -
/// var receives the 1-based position relative to the start element, 0 when not found.
/// </summary>
public sealed record ArrayScanStmt(SourcePosition Position, CallOrIndexExpr Array, Expression? Count, Expression? FromPos, Expression? ToPos, Expression? Collate, CaseComparison Op, Expression Match, Expression Target) : Statement(Position);

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

/// <summary>
/// <c>EXIT FAR AT label</c> records the unwind point (stack mark + target);
/// a bare <c>EXIT FAR</c> unwinds all nested procedures/GOSUBs back to it.
/// </summary>
public sealed record ExitFarStmt(SourcePosition Position, string? AtLabel) : Statement(Position);

/// <summary>ITERATE [FOR|DO|LOOP|WHILE] - continue with the next loop pass.</summary>
public sealed record IterateStmt(SourcePosition Position, ExitKind Kind) : Statement(Position);

/// <summary>Label definition (identifier label or numeric line number).</summary>
public sealed record LabelStmt(SourcePosition Position, string Name) : Statement(Position);

public sealed record GotoStmt(SourcePosition Position, string Target) : Statement(Position);

public sealed record GosubStmt(SourcePosition Position, string Target) : Statement(Position);

/// <summary>GOTO DWORD ptr32 (PB 3.2): far jump through a 32-bit code pointer.</summary>
public sealed record GotoPtrStmt(SourcePosition Position, Expression Pointer) : Statement(Position);

/// <summary>GOSUB DWORD ptr32 (PB 3.2): far call through a 32-bit code pointer.</summary>
public sealed record GosubPtrStmt(SourcePosition Position, Expression Pointer) : Statement(Position);

/// <summary>RETURN [label].</summary>
public sealed record ReturnStmt(SourcePosition Position, string? Target) : Statement(Position);

/// <summary>ON expr GOTO/GOSUB label-list.</summary>
public sealed record OnGotoStmt(SourcePosition Position, Expression Selector, bool IsGosub, IReadOnlyList<string> Targets) : Statement(Position);

/// <summary>CHAIN file$ (COMMON carries over) / RUN file$ (fresh start).</summary>
public sealed record ChainStmt(SourcePosition Position, Expression Target, bool IsRun) : Statement(Position);

/// <summary>END / STOP / SYSTEM program termination (END SUB etc. are structural, not this).</summary>
public sealed record EndStmt(SourcePosition Position, Expression? ExitCode) : Statement(Position);

/// <summary>
/// PB 3.6 <c>YIELD &lt;expression&gt;</c>: suspends the enclosing coroutine SUB/FUNCTION,
/// surfacing <see cref="Value"/> to the resumer, and continues from the next statement
/// when resumed. A pure pb36 extension (no PBC 3.50 equivalent); see docs/PB36-COROUTINES.md.
/// </summary>
public sealed record YieldStmt(SourcePosition Position, Expression Value) : Statement(Position);

#endregion

#region error handling & events

/// <summary>ON ERROR GOTO label|0 / RESUME NEXT-style registration.</summary>
public sealed record OnErrorStmt(SourcePosition Position, string? Target, bool ResumeNext) : Statement(Position);

public enum ResumeKind { SameStatement, Next, Label }

public sealed record ResumeStmt(SourcePosition Position, ResumeKind Kind, string? Target) : Statement(Position);

/// <summary>ERROR n - raise a runtime error.</summary>
public sealed record ErrorStmt(SourcePosition Position, Expression Code) : Statement(Position);

/// <summary>
/// PB 3.6 structured exception handling: TRY / [CATCH] / [FINALLY] / END TRY.
/// Lowers onto the existing ON ERROR trap machinery (no RESUME semantics): a
/// fault in <see cref="Body"/> transfers to <see cref="Catch"/> with ERR set;
/// <see cref="Finally"/> always runs (normal, caught, or pre-propagation path).
/// </summary>
public sealed record TryStmt(SourcePosition Position, IReadOnlyList<Statement> Body, IReadOnlyList<Statement>? Catch, IReadOnlyList<Statement>? Finally) : Statement(Position);

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

/// <summary>WRITE [#n,] expr-list - comma-delimited output, strings quoted.</summary>
public sealed record WriteStmt(SourcePosition Position, Expression? FileNumber, IReadOnlyList<Expression> Items) : Statement(Position);

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
