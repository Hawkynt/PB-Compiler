using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Syntax;

/// <summary>
/// The statement surface, as data: one entry per spelling of every statement the parser dispatches,
/// including each combination of its optional parameters.
///
/// This exists because a statement is not one thing. <c>LINE</c> is eight grammars - with and without
/// a start point, with and without a colour, with <c>B</c>, with <c>BF</c>, with a style mask, and the
/// combinations where a middle argument is elided but a later one is present (<c>LINE (0,0)-(9,9),,B</c>)
/// - and a parser that handles seven of them is a parser that silently mis-reads real programs. The
/// same is true of <c>CIRCLE</c>'s four trailing options, <c>LOCATE</c>'s five and <c>OPEN</c>'s access
/// and <c>LEN=</c> clauses. Writing them out one at a time is the only way to know.
///
/// Availability is per FAMILY, not per version number, because the two lineages share the
/// <see cref="Dialect"/> value space without sharing an order: PB 3.5 is 35 and QB 4.5 is 145, and
/// neither is "later" than the other. <see cref="Form.MinBorland"/> and <see cref="Form.MinMicrosoft"/>
/// give the first dialect of each lineage that has the form, and <c>null</c> says that lineage never
/// had it at all - <c>INCR</c> is Bob Zale's, <c>DIM SHARED</c> is Microsoft's. This mirrors how the
/// compiler itself gates, with its separate Borland and Microsoft tables in <c>DialectFacts</c>.
///
/// Where a statement's history is not established well enough to assert, the entry stays permissive
/// (available from the oldest dialect of both lineages). Pinning a claim nobody checked is worse than
/// pinning nothing: it makes the claim look verified.
/// </summary>
internal static class StatementSurface {

  /// <param name="Id">A stable name for the form, used in failure messages and the census.</param>
  /// <param name="Body">The statement (or statements) under test, as it appears in a program body.</param>
  /// <param name="MinBorland">First Turbo Basic / PowerBASIC dialect with the form; null if it never had it.</param>
  /// <param name="MinMicrosoft">First BASICA / GW / QB / PDS dialect with the form; null if it never had it.</param>
  /// <param name="Preamble">Declarations the form needs to bind (a file to be open, an array to exist).</param>
  internal sealed record Form(
    string Id,
    string Body,
    Dialect? MinBorland = Dialect.Tb10,
    Dialect? MinMicrosoft = Dialect.Basica,
    string Preamble = "");

  /// <summary>A form only Turbo Basic / PowerBASIC ever had.</summary>
  private static readonly Dialect? _noMicrosoft = null;

  /// <summary>A form only the Microsoft lineage ever had.</summary>
  private static readonly Dialect? _noBorland = null;

  /// <summary>Every dialect the compiler claims to accept, oldest first within each family.</summary>
  internal static readonly Dialect[] AllDialects = [
    Dialect.Tb10, Dialect.Tb11,
    Dialect.Pb20, Dialect.Pb21, Dialect.Pb30, Dialect.Pb31, Dialect.Pb32, Dialect.Pb35, Dialect.Pb36,
    Dialect.Basica, Dialect.Gw,
    Dialect.Qb10, Dialect.Qb20, Dialect.Qb30, Dialect.Qb40, Dialect.Qb45, Dialect.Qbasic,
    Dialect.Pds70, Dialect.Pds71,
  ];

  // ---- assignment and declaration -------------------------------------------------------------

  private static readonly Form[] _core = [
    new("let.implicit", "x% = 1"),
    new("let.explicit", "LET x% = 1"),
    new("let.string", "s$ = \"a\""),
    new("dim.scalar", "DIM d AS INTEGER"),
    new("dim.array.upper", "DIM a%(10)"),
    new("dim.array.range", "DIM b%(1 TO 10)"),
    new("dim.array.2d", "DIM c%(1 TO 3, 1 TO 4)"),
    new("erase", "ERASE e%", Preamble: "DIM e%(4)"),
    new("redim", "REDIM f%(1 TO 4)"),
    new("redim.astype", "REDIM f(1 TO 4) AS LONG"),
    // PRESERVE is PB 3.5 in Bob Zale's line and BASIC PDS 7.0 in Microsoft's; plain QuickBASIC REDIM
    // never preserved anything
    new("redim.preserve", "REDIM f(1 TO 4) AS LONG\nREDIM PRESERVE f(1 TO 8) AS LONG", Dialect.Pb35, Dialect.Pds70),
    new("common", "COMMON c%"),
    // OPTION BASE is in every BASIC there has ever been, so both lineages stay at their oldest
    new("option.base", "OPTION BASE 1\nDIM ob%(4)"),
    new("public", "PUBLIC p%", MinMicrosoft: _noMicrosoft),
    new("ext", "EXT e%", MinMicrosoft: _noMicrosoft),
    // DEFINT / DEFSNG / DEFDBL / DEFSTR are in every BASIC there has ever been
    new("deftype.int", "DEFINT A-C\nav = 1"),
    new("deftype.sng", "DEFSNG D-F\ndv = 1"),
    new("deftype.dbl", "DEFDBL G-I\ngv = 1"),
    new("deftype.str", "DEFSTR J-L\njv = \"x\""),
    // LONG arrived with Turbo Basic on one side and QuickBASIC on the other; GW-BASIC and BASICA
    // have no such type, so they have no DEFLNG either
    new("deftype.lng", "DEFLNG M-O\nmv = 1", Dialect.Tb10, Dialect.Qb10),
    // the extended numeric types (QUAD, EXT, FIX, BCD, FLEX) are PowerBASIC's alone
    new("deftype.qud", "DEFQUD P-R\npv = 1", Dialect.Pb30, _noMicrosoft),   // QUAD is PB 3.0
    new("deftype.ext", "DEFEXT S-T\nsv = 1", MinMicrosoft: _noMicrosoft),
    new("deftype.fix", "DEFFIX U-V\nuv = 1", MinMicrosoft: _noMicrosoft),
    new("deftype.bcd", "DEFBCD W-X\nwv = 1", MinMicrosoft: _noMicrosoft),
    new("deftype.flx", "DEFFLX Y-Z\nyv = \"x\"", MinMicrosoft: _noMicrosoft),
    new("swap", "SWAP x%, y%", Preamble: "x% = 1 : y% = 2"),
    // These six had no form of their own and so no census counted them - MID$ assignment in
    // particular is as common a statement as BASIC has.
    //
    // All six stay PERMISSIVE. EQUATE, ARRAY SORT and BIT SET read as Bob Zale's and FOR EACH and
    // ITERATE as pb36's, but the compiler accepts every one of them in BASICA and QuickBASIC as
    // well, and this table's rule is that an unverified claim is worse than no claim - it makes the
    // claim look checked. Whether that acceptance is a gating hole or the history is wrong needs the
    // oracle to settle, and the census measures the compiler either way.
    new("mid.assign", "s$ = \"abc\"\nMID$(s$, 1, 1) = \"X\""),
    // ten more the statement-kind census found: each is a grammar the parser accepts that no form
    // compiled. Dialect minimums here are what the compiler enforces, measured, not what the history
    // books say - the census reports both and only one of them is this table's business.
    new("asc.assign", "s2$ = \"abc\"\nASC(s2$, 1) = 65", Dialect.Pb35, _noMicrosoft),
    new("chain", "CHAIN \"NEXT.EXE\""),
    new("replace", "s3$ = \"aXa\"\nREPLACE \"X\" WITH \"Y\" IN s3$"),
    new("array.scan", "ARRAY SCAN a4%(), = 5, TO f4%", Preamble: "DIM a4%(4)"),
    new("exit.far", "CALL S9\nEND\nSUB S9\n  EXIT FAR\nEND SUB"),
    new("inline.asm", "! mov ax, 1", Dialect.Pb30, _noMicrosoft),
    new("destructure", "d1%, d2% = (1, 2)", Dialect.Pb36, _noMicrosoft),
    new("static.assert", "$ASSERT 1 = 1, \"ok\"", Dialect.Pb36, _noMicrosoft),
    new("metastatement", "$CPU 8086"),
    new("require", "CALL S8(1)\nEND\nSUB S8(BYVAL n%)\n  REQUIRE n% > 0, \"positive\"\nEND SUB", Dialect.Pb36, _noMicrosoft),
    // and six more: the code-pointer trio, the single-line type alias (TYPE Name AS type - no
    // ALIAS keyword, which a guess would put there), DEFER and a coroutine YIELD
    new("call.dword", "DIM cp AS DWORD\ncp = CODEPTR32(S7)\nCALL DWORD cp\nEND\nSUB S7()\nEND SUB", Dialect.Pb32, _noMicrosoft),
    new("goto.dword", "DIM gp AS DWORD\ngp = CODEPTR32(S6)\nGOTO DWORD gp\nEND\nSUB S6()\nEND SUB", Dialect.Pb32, _noMicrosoft),
    new("gosub.dword", "DIM sp AS DWORD\nsp = CODEPTR32(S5)\nGOSUB DWORD sp\nEND\nSUB S5()\nEND SUB", Dialect.Pb32, _noMicrosoft),
    new("type.alias", "TYPE Small AS INTEGER\nDIM tv AS Small\ntv = 1", Dialect.Pb36, _noMicrosoft),
    new("defer", "CALL S4\nEND\nSUB S4\n  DEFER PRINT \"bye\"\n  PRINT \"hi\"\nEND SUB", Dialect.Pb36, _noMicrosoft),
    new("yield", "FUNCTION G9%()\n  YIELD 1\nEND FUNCTION", Dialect.Pb36, _noMicrosoft),
    new("equate", "%N = 5\nq% = %N"),
    new("array.sort", "ARRAY SORT a2%()", Preamble: "DIM a2%(3)"),
    new("bit.set", "BIT SET bx%, 2", Preamble: "bx% = 0"),
    // FOR EACH is the only one of the six the compiler actually gates: pb36 on Bob Zale's line and
    // nowhere at all on Microsoft's. ITERATE, EQUATE, ARRAY SORT and BIT SET are taken by every
    // dialect there is, BASICA included, so they stay permissive - each reads like a PowerBASIC
    // statement and each is probably a gating hole, but "probably" is not what this table records.
    new("for.each", "FOR EACH ev% IN a3%()\nNEXT ev%", Dialect.Pb36, _noMicrosoft, Preamble: "DIM a3%(3)"),
    new("iterate", "FOR i% = 1 TO 3\n  ITERATE FOR\nNEXT i%"),
    // INCR / DECR are Bob Zale's; no Microsoft BASIC ever had them
    new("incr.bare", "INCR x%", MinMicrosoft: _noMicrosoft),
    new("incr.by", "INCR x%, 2", MinMicrosoft: _noMicrosoft),
    new("decr.bare", "DECR x%", MinMicrosoft: _noMicrosoft),
    new("decr.by", "DECR x%, 2", MinMicrosoft: _noMicrosoft),
  ];

  // ---- control flow ---------------------------------------------------------------------------

  private static readonly Form[] _control = [
    new("if.single", "IF x% = 1 THEN y% = 2"),
    new("if.else.single", "IF x% = 1 THEN y% = 2 ELSE y% = 3"),
    new("if.block", "IF x% = 1 THEN\n  y% = 2\nEND IF"),
    new("if.elseif", "IF x% = 1 THEN\n  y% = 2\nELSEIF x% = 2 THEN\n  y% = 3\nELSE\n  y% = 4\nEND IF"),
    new("for.bare", "FOR i% = 1 TO 3\nNEXT i%"),
    new("for.step", "FOR i% = 1 TO 9 STEP 2\nNEXT i%"),
    new("for.step.negative", "FOR i% = 9 TO 1 STEP -2\nNEXT i%"),
    new("for.next.bare", "FOR i% = 1 TO 3\nNEXT"),
    new("while.wend", "WHILE x% < 3\n  x% = x% + 1\nWEND", Preamble: "x% = 0"),
    new("do.while.top", "DO WHILE x% < 3\n  x% = x% + 1\nLOOP", Preamble: "x% = 0"),
    new("do.until.top", "DO UNTIL x% >= 3\n  x% = x% + 1\nLOOP", Preamble: "x% = 0"),
    new("do.loop.while", "DO\n  x% = x% + 1\nLOOP WHILE x% < 3", Preamble: "x% = 0"),
    new("do.loop.until", "DO\n  x% = x% + 1\nLOOP UNTIL x% >= 3", Preamble: "x% = 0"),
    new("exit.for", "FOR i% = 1 TO 3\n  EXIT FOR\nNEXT i%"),
    new("exit.do", "DO\n  EXIT DO\nLOOP"),
    new("select.case", "SELECT CASE x%\n  CASE 1\n    y% = 1\n  CASE ELSE\n    y% = 2\nEND SELECT"),
    new("select.case.range", "SELECT CASE x%\n  CASE 1 TO 5\n    y% = 1\nEND SELECT"),
    new("select.case.is", "SELECT CASE x%\n  CASE IS > 5\n    y% = 1\nEND SELECT"),
    new("select.case.list", "SELECT CASE x%\n  CASE 1, 3, 5\n    y% = 1\nEND SELECT"),
    new("goto", "GOTO Skip\nSkip:"),
    new("gosub.return", "GOSUB Sub1\nGOTO Past\nSub1:\nRETURN\nPast:"),
    new("on.goto", "ON x% GOTO L1, L2\nL1:\nL2:"),
    new("on.gosub", "ON x% GOSUB L1, L2\nGOTO Past\nL1:\nRETURN\nL2:\nRETURN\nPast:"),
    new("for.nested.next.list", "FOR i% = 1 TO 2\n  FOR j% = 1 TO 2\n  NEXT j%, i%"),
    new("select.case.is.list", "SELECT CASE x%\n  CASE IS < 0, IS > 9\n    y% = 1\nEND SELECT"),
    new("select.case.range.and.value", "SELECT CASE x%\n  CASE 1 TO 5, 9\n    y% = 1\nEND SELECT"),
    new("do.bare.exit", "DO\n  EXIT DO\nLOOP"),
    new("if.then.goto", "IF x% = 1 THEN GOTO Skip\nSkip:"),
    new("on.goto.single", "ON x% GOTO L9\nL9:"),
    new("stop", "STOP"),
    new("end.bare", "END"),
    new("system", "SYSTEM"),
  ];

  // ---- error handling -------------------------------------------------------------------------

  private static readonly Form[] _errors = [
    new("on.error.goto", "ON ERROR GOTO Trap\nGOTO Past\nTrap:\nRESUME Past\nPast:"),
    new("on.error.goto.zero", "ON ERROR GOTO 0"),
    new("on.error.resume.next", "ON ERROR RESUME NEXT"),
    new("resume.next", "ON ERROR GOTO T\nGOTO P\nT:\nRESUME NEXT\nP:"),
    new("resume.same", "ON ERROR GOTO T\nGOTO P\nT:\nRESUME\nP:"),
    new("error.raise", "ON ERROR GOTO T\nERROR 5\nGOTO P\nT:\nRESUME NEXT\nP:"),
    new("err.read", "ON ERROR GOTO T\nGOTO P\nT:\nx% = ERR\nRESUME NEXT\nP:"),
    new("erl.read", "ON ERROR GOTO T\nGOTO P\nT:\nl& = ERL\nRESUME NEXT\nP:"),
    new("errclear", "ERRCLEAR", Dialect.Pb35, _noMicrosoft),
  ];

  // ---- console and file I/O -------------------------------------------------------------------

  private static readonly Form[] _io = [
    new("print.bare", "PRINT"),
    new("print.one", "PRINT x%"),
    new("print.semicolon", "PRINT x%; y%"),
    new("print.comma", "PRINT x%, y%"),
    new("print.trailing.semicolon", "PRINT x%;"),
    new("print.string", "PRINT \"text\""),
    new("print.tab", "PRINT TAB(5); x%"),
    new("print.spc", "PRINT SPC(3); x%"),
    new("print.question", "? x%"),
    new("print.file", "PRINT #1, x%", Preamble: "OPEN \"T.TXT\" FOR OUTPUT AS #1"),
    new("print.file.comma", "PRINT #1, x%, y%", Preamble: "OPEN \"T.TXT\" FOR OUTPUT AS #1"),
    new("write.file", "WRITE #1, x%", Preamble: "OPEN \"T.TXT\" FOR OUTPUT AS #1"),
    new("input.one", "INPUT v%"),
    new("input.prompt", "INPUT \"how many\"; v%"),
    new("input.prompt.comma", "INPUT \"how many\", v%"),
    new("input.many", "INPUT v%, w%"),
    new("line.input", "LINE INPUT s$"),
    new("line.input.prompt", "LINE INPUT \"name\"; s$"),
    new("input.file", "INPUT #1, v%", Preamble: "OPEN \"T.TXT\" FOR INPUT AS #1"),
    new("line.input.file", "LINE INPUT #1, s$", Preamble: "OPEN \"T.TXT\" FOR INPUT AS #1"),
    new("open.output", "OPEN \"T.TXT\" FOR OUTPUT AS #1"),
    new("open.input", "OPEN \"T.TXT\" FOR INPUT AS #1"),
    new("open.append", "OPEN \"T.TXT\" FOR APPEND AS #1"),
    new("open.random.len", "OPEN \"T.DAT\" FOR RANDOM AS #1 LEN = 16"),
    new("open.binary", "OPEN \"T.DAT\" FOR BINARY AS #1"),
    new("close.one", "CLOSE #1", Preamble: "OPEN \"T.TXT\" FOR OUTPUT AS #1"),
    new("close.all", "CLOSE"),
    new("field", "FIELD #1, 8 AS n$, 8 AS v$", Preamble: "OPEN \"T.DAT\" FOR RANDOM AS #1 LEN = 16"),
    new("lset", "LSET n$ = \"a\"", Preamble: "OPEN \"T.DAT\" FOR RANDOM AS #1 LEN = 16\nFIELD #1, 8 AS n$"),
    new("rset", "RSET n$ = \"a\"", Preamble: "OPEN \"T.DAT\" FOR RANDOM AS #1 LEN = 16\nFIELD #1, 8 AS n$"),
    new("get.record", "GET #1, 1", Preamble: "OPEN \"T.DAT\" FOR RANDOM AS #1 LEN = 16"),
    new("put.record", "PUT #1, 1", Preamble: "OPEN \"T.DAT\" FOR RANDOM AS #1 LEN = 16"),
    new("seek", "SEEK #1, 1", Preamble: "OPEN \"T.DAT\" FOR BINARY AS #1"),
    new("data.read", "DATA 1, 2\nREAD x%, y%"),
    new("data.read.restore", "DATA 1, 2\nREAD x%\nRESTORE\nREAD y%"),
    // OPEN's optional clauses, one at a time and together. ACCESS and LOCK may appear in either
    // order and repeat, LEN attaches to any mode, and the shorthand and legacy spellings are whole
    // grammars of their own rather than variations
    new("open.access.read", "OPEN \"T.TXT\" FOR INPUT ACCESS READ AS #1"),
    new("open.access.write", "OPEN \"T.TXT\" FOR OUTPUT ACCESS WRITE AS #1"),
    new("open.access.readwrite", "OPEN \"T.DAT\" FOR RANDOM ACCESS READ WRITE AS #1 LEN = 8"),
    new("open.lock.shared", "OPEN \"T.TXT\" FOR INPUT SHARED AS #1"),
    new("open.lock.read", "OPEN \"T.TXT\" FOR INPUT LOCK READ AS #1"),
    new("open.access.and.lock", "OPEN \"T.DAT\" FOR BINARY ACCESS READ WRITE LOCK SHARED AS #1"),
    new("open.output.len", "OPEN \"T.TXT\" FOR OUTPUT AS #1 LEN = 128"),
    new("open.shorthand.as", "OPEN \"T.DAT\" AS #1"),
    new("open.shorthand.as.len", "OPEN \"T.DAT\" AS #1 LEN = 32"),
    new("open.legacy", "OPEN \"O\", #1, \"T.TXT\""),
    new("open.legacy.reclen", "OPEN \"R\", #1, \"T.DAT\", 16"),
    new("open.filenumber.bare", "OPEN \"T.TXT\" FOR OUTPUT AS 1"),
    // GET / PUT: the record number and the variable are each elidable, including the shape where the
    // record is dropped but the variable is present
    new("get.bare", "GET #1", Preamble: "OPEN \"T.DAT\" FOR RANDOM AS #1 LEN = 2"),
    new("get.record.variable", "GET #1, 1, v%", Preamble: "OPEN \"T.DAT\" FOR RANDOM AS #1 LEN = 2"),
    new("get.elided.record", "GET #1, , v%", Preamble: "OPEN \"T.DAT\" FOR RANDOM AS #1 LEN = 2"),
    new("put.bare", "PUT #1", Preamble: "OPEN \"T.DAT\" FOR RANDOM AS #1 LEN = 2"),
    new("put.record.variable", "PUT #1, 1, v%", Preamble: "OPEN \"T.DAT\" FOR RANDOM AS #1 LEN = 2"),
    new("put.elided.record", "PUT #1, , v%", Preamble: "OPEN \"T.DAT\" FOR RANDOM AS #1 LEN = 2"),
    // WIDTH takes a device or a file as well as a plain column count
    new("width.file", "WIDTH #1, 132", Preamble: "OPEN \"T.TXT\" FOR OUTPUT AS #1"),
    // PRINT's separators in the combinations that decide column behaviour
    new("print.semicolon.trailing.comma", "PRINT x%; y%,"),
    new("print.empty.string", "PRINT \"\""),
    new("print.mixed", "PRINT \"n=\"; x%, \"m=\"; y%"),
    new("print.file.trailing.semicolon", "PRINT #1, x%;", Preamble: "OPEN \"T.TXT\" FOR OUTPUT AS #1"),
    new("write.file.many", "WRITE #1, x%, y%", Preamble: "OPEN \"T.TXT\" FOR OUTPUT AS #1"),
    new("lprint", "LPRINT x%"),
  ];

  // ---- graphics and console control -------------------------------------------------------------

  private static readonly Form[] _graphics = [
    new("cls", "CLS"),
    new("beep", "BEEP"),
    new("color.fg", "COLOR 7"),
    new("color.fg.bg", "COLOR 7, 0"),
    new("color.fg.bg.border", "COLOR 7, 0, 1"),
    new("locate.row.col", "LOCATE 1, 1"),
    new("locate.row", "LOCATE 1"),
    new("locate.row.col.cursor", "LOCATE 1, 1, 1"),
    new("screen.mode", "SCREEN 13"),
    new("screen.mode.switch", "SCREEN 13, 0"),
    new("pset.point", "PSET (10, 20)"),
    new("pset.point.color", "PSET (10, 20), 15"),
    new("preset.point", "PRESET (10, 20)"),
    new("preset.point.color", "PRESET (10, 20), 0"),
    new("line.to", "LINE -(20, 30)"),
    new("line.from.to", "LINE (0, 0)-(20, 30)"),
    new("line.color", "LINE (0, 0)-(20, 30), 15"),
    new("line.box", "LINE (0, 0)-(20, 30), 15, B"),
    new("line.boxfill", "LINE (0, 0)-(20, 30), 15, BF"),
    new("line.box.style", "LINE (0, 0)-(20, 30), 15, B, &HF0F0"),
    new("line.elided.color.box", "LINE (0, 0)-(20, 30), , B"),
    new("line.elided.style", "LINE (0, 0)-(20, 30), 15, , &HF0F0"),
    new("circle.radius", "CIRCLE (10, 10), 5"),
    new("circle.color", "CIRCLE (10, 10), 5, 15"),
    new("circle.arc", "CIRCLE (10, 10), 5, 15, 0.0, 1.5"),
    new("circle.aspect", "CIRCLE (10, 10), 5, 15, 0.0, 1.5, 0.5"),
    new("circle.elided.color", "CIRCLE (10, 10), 5, , 0.0, 1.5"),
    new("paint", "PAINT (10, 10), 15"),
    new("locate.row.col.cursor.start", "LOCATE 1, 1, 1, 0"),
    new("locate.row.col.cursor.start.stop", "LOCATE 1, 1, 1, 0, 7"),
    new("locate.elided.row", "LOCATE , 5"),
    new("color.elided.fg", "COLOR , 1"),
    new("screen.mode.switch.apage", "SCREEN 13, 0, 0"),
    new("screen.mode.switch.apage.vpage", "SCREEN 13, 0, 0, 0"),
    new("paint.border", "PAINT (10, 10), 15, 4"),
    new("sound.bare", "SOUND 440, 1"),
    new("palette.bare", "PALETTE"),
    new("pset.step.free", "PSET (0, 0)"),

    new("draw", "DRAW \"U10 R10 D10 L10\""),
    new("view", "VIEW (0, 0)-(319, 199)"),
    // GET/PUT's graphics form had no entry at all - the six get.*/put.* forms above are the FILE
    // statement of the same name, which is a different grammar reached by the same keyword
    new("get.graphics", "GET (0, 0)-(3, 3), spr%(0)", Preamble: "DIM spr%(64)"),
    new("put.graphics", "PUT (0, 0), spr%(0)", Preamble: "DIM spr%(64)"),
    new("put.graphics.verb", "PUT (0, 0), spr%(0), XOR", Preamble: "DIM spr%(64)"),
    // VIEW PRINT / VIEW TEXT / VIEW SCREEN and PALETTE USING had no forms at all, which is why the
    // census never noticed that VIEW PRINT's own row-range spelling did not parse
    new("view.print", "VIEW PRINT"),
    new("view.print.range", "VIEW PRINT 1 TO 20"),
    new("view.screen", "VIEW SCREEN (0, 0)-(10, 10)"),
    new("view.text", "VIEW TEXT 1, 20"),
    new("palette.using", "PALETTE USING pal%(0)", Preamble: "DIM pal%(16)"),
    new("window", "WINDOW (0, 0)-(319, 199)"),
    new("palette", "PALETTE 1, 2"),
    new("pcopy", "PCOPY 0, 1"),
    new("width", "WIDTH 80"),
    new("sound", "SOUND 440, 3"),
    new("play", "PLAY \"CDE\""),
  ];

  // ---- system, low level ------------------------------------------------------------------------

  private static readonly Form[] _system = [
    new("randomize.bare", "RANDOMIZE"),
    new("randomize.seed", "RANDOMIZE 42"),
    new("shell", "SHELL \"DIR\""),
    new("kill", "KILL \"T.TXT\""),
    new("name", "NAME \"A.TXT\" AS \"B.TXT\""),
    new("chdir", "CHDIR \"\\\""),
    new("mkdir", "MKDIR \"SUB\""),
    new("rmdir", "RMDIR \"SUB\""),
    new("files", "FILES"),
    new("environ", "ENVIRON \"A=B\""),
    new("poke", "DEF SEG = &HB800\nPOKE 0, 65"),
    new("out", "OUT &H61, 0"),
    new("wait", "WAIT &H3DA, 8"),
    new("def.seg", "DEF SEG = &HB800"),
    new("delay", "DELAY 0.01", MinMicrosoft: _noMicrosoft),
    new("sleep", "SLEEP 1"),
    new("bload", "BLOAD \"T.BIN\", 0"),
    new("bsave", "BSAVE \"T.BIN\", 0, 16"),
    new("timer.on", "ON TIMER(1) GOSUB Tick\nTIMER ON\nGOTO Past\nTick:\nRETURN\nPast:"),
    new("key.off", "KEY OFF"),
    new("com.on", "COM(1) ON"),
    new("pen.on", "PEN ON"),
    new("strig.on", "STRIG(0) ON"),
    new("reg", "REG 1, 0", Dialect.Pb30, _noMicrosoft),
    // PB 3.5 additions: DOS-handle I/O and truncation
    new("stdout", "STDOUT \"text\"", Dialect.Pb35, _noMicrosoft),
    new("stdin", "STDIN LINE, s$", Dialect.Pb35, _noMicrosoft),
    new("seteof", "SETEOF #1", Dialect.Pb35, _noMicrosoft,
      Preamble: "OPEN \"T.DAT\" FOR BINARY AS #1"),
    // the SHIFT / ROTATE STATEMENTS are PB 3.0's machine-level wave, alongside REG and BIT - not to
    // be confused with the '<<' / '>>' operators, which are PB 3.6
    new("shift.left", "SHIFT LEFT x%, 1", Dialect.Pb30, _noMicrosoft),
    new("rotate.left", "ROTATE LEFT x%, 1", Dialect.Pb30, _noMicrosoft),

  ];

  // ---- procedures and types ---------------------------------------------------------------------

  private static readonly Form[] _procedures = [
    new("sub.call", "CALL S1(1)\nEND\nSUB S1(BYVAL n%)\nEND SUB"),
    new("sub.call.bare", "S1 1\nEND\nSUB S1(BYVAL n%)\nEND SUB"),
    new("function.call", "x% = F1%(1)\nEND\nFUNCTION F1%(BYVAL n%)\n  F1% = n%\nEND FUNCTION"),
    new("sub.byref", "CALL S2(x%)\nEND\nSUB S2(n%)\n  n% = 1\nEND SUB"),
    new("declare.sub", "DECLARE SUB S3(BYVAL n%)\nCALL S3(1)\nEND\nSUB S3(BYVAL n%)\nEND SUB"),
    // the two lineages spell module-shared storage differently - PowerBASIC puts SHARED in the type
    // clause, QuickBASIC puts it straight after DIM - so each family is asked for its own spelling
    new("shared.global.pb", "DIM g AS SHARED INTEGER\ng = 1", MinMicrosoft: _noMicrosoft),
    // DIM SHARED is Microsoft's spelling. Whether PowerBASIC rejects it is not established here, so
    // the entry stays permissive on the Borland side rather than pinning an unverified claim
    new("shared.global.qb", "DIM SHARED g%\ng% = 1"),
    // SHARED as a statement of its own, inside a procedure - the other two forms declare module-level
    // storage and merely contain the word. History is not established well enough to pin either
    // lineage (BASICA has no procedures to put it in), so the entry stays permissive.
    new("shared.stmt", "DIM h%\nCALL S6\nEND\nSUB S6\n  SHARED h%\n  h% = 1\nEND SUB"),
    new("static.local", "CALL S4\nEND\nSUB S4\n  STATIC s%\n  s% = s% + 1\nEND SUB"),
    new("local.decl", "CALL S5\nEND\nSUB S5\n  LOCAL l%\n  l% = 1\nEND SUB"),
    // TYPE ... END TYPE is PB 3.0 in one line and QuickBASIC 4.0 in the other
    new("type.decl", "TYPE Pt\n  X AS INTEGER\n  Y AS INTEGER\nEND TYPE\nDIM p AS Pt\np.X = 1", Dialect.Pb30, Dialect.Qb40),
    // UNION is PowerBASIC's; QuickBASIC's TYPE has no overlapping variant
    new("union.decl", "UNION U\n  I AS INTEGER\n  L AS LONG\nEND UNION\nDIM u AS U\nu.I = 1", Dialect.Pb30, _noMicrosoft),
    new("enum.decl", "ENUM Colour\n  Red\n  Green\nEND ENUM\nc% = Red", Dialect.Pb36, _noMicrosoft),
    new("with.block", "TYPE Pt2\n  X AS INTEGER\nEND TYPE\nDIM q AS Pt2\nWITH q\n  .X = 1\nEND WITH", Dialect.Pb36, _noMicrosoft),
    new("try.catch", "TRY\n  ERROR 5\nCATCH\n  PRINT \"caught\"\nEND TRY", Dialect.Pb36, _noMicrosoft),
    // USING calls Dispose on the way out, so the type has to have one - that IS the contract
    new("using.block", "TYPE Res\n  H AS INTEGER\n  SUB Dispose()\n  END SUB\nEND TYPE\nUSING r AS Res\n  r.H = 1\nEND USING", Dialect.Pb36, _noMicrosoft),
    new("event.decl", "EVENT Fired AS SUB()", Dialect.Pb36, _noMicrosoft),
    new("deftype", "DEFINT A-Z\nn = 1"),
    new("def.fn", "DEF FnDouble%(n%) = n% * 2\nx% = FnDouble%(3)"),
  ];

  /// <summary>Every form, in one sequence.</summary>
  internal static IEnumerable<Form> All =>
    [.. _core, .. _control, .. _errors, .. _io, .. _graphics, .. _system, .. _procedures];

  /// <summary>The forms grouped by the section they were declared in, for a readable census.</summary>
  internal static IReadOnlyDictionary<string, Form[]> Sections => new Dictionary<string, Form[]> {
    ["core"] = _core,
    ["control"] = _control,
    ["errors"] = _errors,
    ["io"] = _io,
    ["graphics"] = _graphics,
    ["system"] = _system,
    ["procedures"] = _procedures,
  };

  /// <summary>The full program a form is compiled inside: its preamble, the form, then a hard END.</summary>
  internal static string Program(Form form) {
    var lines = new List<string>();
    if (form.Preamble.Length > 0)
      lines.Add(form.Preamble);
    lines.Add(form.Body);
    if (!form.Body.TrimEnd().EndsWith("END SUB", StringComparison.OrdinalIgnoreCase)
        && !form.Body.TrimEnd().EndsWith("END FUNCTION", StringComparison.OrdinalIgnoreCase))
      lines.Add("END");
    return string.Join("\n", lines) + "\n";
  }

  /// <summary>Whether <paramref name="dialect"/> should accept <paramref name="form"/> at all.</summary>
  internal static bool ShouldAccept(Form form, Dialect dialect) {
    var min = dialect.Family() == DialectFamily.Microsoft ? form.MinMicrosoft : form.MinBorland;
    return min is { } floor && dialect >= floor;
  }
}
