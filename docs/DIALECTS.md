# PowerBASIC/DOS dialect matrix

Sources: PowerBASIC.GER FAQ (pbhq.de, sections 1.6–1.8), the PB 3.5 README
("What's New in PowerBASIC 3.5"), the PB statement reference (manmrk pbs.htm),
and the original PB 3.5 Reference/User guides. The compiler selects a dialect
via `--dialect pb20|pb21|pb30|pb31|pb32|pb35` (default `pb35`); features below
are rejected with a diagnostic when used under an older dialect.

## Baseline (PB 2.0/2.1, Turbo Basic lineage)

Core BASIC: suffixes `% & ! # $`, control flow, GOSUB, DEF FN, static/dynamic
arrays, sequential/random/binary files, inline `$INLINE` byte lists, units
(.PBU) and chains (.PBC), EXT (80-bit) floats, FLEX strings, BCD types
(`@`/`@@` — FIX/BCD), event trapping, $-metastatements (COM, COMPILE, CPU
8086/286, DEBUG, DIM, DYNAMIC/STATIC, ERROR, EVENT, FLOAT, IF/ELSE/ENDIF,
INCLUDE, LIB, LINK, OPTIMIZE, OPTION, SEGMENT, SOUND, STACK, STRING).
PB 2.x has **no** inline `!` assembler statements ($INLINE bytes only), no
unsigned types, no QUAD, no pointers, no UNION, no 80386 codegen.

## PB 3.0 (1993)

- Inline assembler (`!` statements), 80386 codegen ($CPU 80386)
- Unsigned types BYTE `?`, WORD `??`, DWORD `???`; QUAD `&&` (8-byte signed)
- TYPE/UNION user-defined types, arrays as fields
- HUGE arrays (dynamic, > 64 KiB conventional memory)
- DECLARE with parameter lists; SHARED/LOCAL/STATIC discipline; $DIM ALL/ARRAY
- VARPTR/VARSEG/STRPTR/STRSEG/CODEPTR/CODESEG (unsigned results; see $OPTION SIGNED)
- REG/CALL INTERRUPT, SHIFT/ROTATE, BIT manipulation, MIN/MAX, VERIFY etc.

## PB 3.1

- TYPE/UNION variables comparable directly (`IF a = b THEN` on whole UDTs)
- Typed radix literals: suffix on the literal (`&HFFFF??` = 65535, `&HFFFF%` = −1)
- **Radix signedness rule**: leading zero ⇒ unsigned (`&HFFFF` = −1 INTEGER,
  `&H0FFFF` = 65535 LONG); radixes carry up to 64 bits
- Equates (%CONST) widened to signed 64-bit range
- BIN$/HEX$/OCT$ accept 32-bit LONG values
- `ALIAS "external_name"` on SUB/FUNCTION (for .OBJ interop)
- `ANY` parameter type
- Inline-asm operand semantics tightened (see QUIRKS: 3.0 vs 3.1 asm difference)

## PB 3.2

- Data pointers: `DIM p AS INTEGER PTR`, dereference `@p`, pointer targets of
  any type incl. TYPE structures; `BYVAL p` override passes the target
- Code pointers: `CALL DWORD`, `GOTO DWORD`, `GOSUB DWORD`
- STRPTR32 / VARPTR32 / CODEPTR32 (32-bit seg:offset results)
- LEN() of user-defined TYPE variables
- Underscores allowed in labels and variable names
- 16550 UART support ($COM buffers)

## PB 3.5 (12/1997)

- ASCIIZ strings: `DIM s AS ASCIIZ * n` (NUL-terminated fixed buffer)
- Arrays as UDT fields may have 1–2 static dimensions
- `&` as string concatenation operator
- `STRING PTR` legal inside TYPE/UNION
- `$ELSEIF` metastatement
- ASC statement (`ASC(s$, n) = code`) and ASC/ASCII function start position
- REDIM PRESERVE (outermost bound only; not for VIRTUAL arrays)
- RND() and RND(a, z) → LONG in [a, z]
- TRIM$()
- Indexed pointers: `@p[i]` (zero-based, ignores OPTION BASE)
- **VIRTUAL arrays**: `DIM VIRTUAL x(…)` stored in **EMS**; LONG bounds; no
  dynamic/flex strings inside; `FRE(-11)` = free EMS bytes
- HUGE/VIRTUAL arrays take LONG indexes
- ERRCLEAR statement/function (synonym of old ERRTEST)
- CVI/CVL/CVS/CVD/CVE… optional start offset: `CVL(x$, 3)`
- SIZEOF(var) (storage size; 2 for dynamic strings = the handle)
- STDIN n, s$ / STDIN LINE, s$ / STDOUT s$ [;] (redirectable standard I/O)
- CONSIN / CONSOUT (redirection status, −1/0)
- SETEOF #n (truncate at current position)

## Data types (PB 3.5 full set)

| Type | Keyword | Suffix | Size | Notes |
|------|---------|--------|------|-------|
| Byte | BYTE | `?` | 1 | unsigned |
| Word | WORD | `??` | 2 | unsigned |
| Integer | INTEGER | `%` | 2 | |
| Double word | DWORD | `???` | 4 | unsigned, **no overflow checking ever** |
| Long | LONG | `&` | 4 | |
| Quad | QUAD | `&&` | 8 | signed 64-bit |
| Single | SINGLE | `!` | 4 | |
| Double | DOUBLE | `#` | 8 | |
| Extended | EXT | `##` | 10 | x87 native |
| BCD fixed | FIX | `@` | 8 | ±9.99e±63, `pbvFixDigits` fraction digits |
| BCD float | BCD | `@@` | 10 | |
| Pointer | <type> PTR | | 4 | seg:off, `@p` deref, `@p[i]` index |
| Dynamic string | STRING | `$` | 2 (handle) | ≤ 32750 bytes, see $STRING |
| Flex string | FLEX | `$$` | | dynamic structure |
| Fixed string | STRING * n | | n | |
| ASCIIZ | ASCIIZ * n | | n | NUL-terminated (3.5) |

DEFtype forms: DEFINT, DEFLNG, DEFQUD, DEFSNG, DEFDBL, DEFEXT, DEFFIX,
DEFBCD, DEFSTR, DEFFLX. Conversions: CINT, CLNG, CQUD, CSNG, CDBL, CEXT,
CFIX, CBCD, CBYT, CWRD, CDWD.

## Array allocation classes (DIM [class] a(bounds) [AS type] [AT seg])

- **STATIC** — compile-time, ≤ 64 KiB, constant bounds
- **DYNAMIC** — run-time, ≤ 64 KiB; implied by non-constant bounds, COMMON,
  LOCAL, flex element type, REDIM, or multiple DIMs
- **HUGE** — dynamic, any amount of conventional memory, LONG bounds
- **VIRTUAL** — dynamic, stored in **EMS**, LONG bounds (3.5)
- **ABSOLUTE** — mapped at a fixed address (`AT segment`), e.g. video memory

Bounds: 1–8 dimensions, `lower TO upper` (or `lower:upper`), INTEGER range
except HUGE/VIRTUAL (LONG).

## PBC.EXE switches (3.5)

```
-CE *compile to .EXE   -CU .PBU   -CC .PBC(chain)
-ODA declare arrays    -ODV declare vars/arrays
-OD attach PBD info    -OG gosub preserve  -OM map file
-OP path in unit dbg   -OU unit full debug -OZF *optimize faster
-RExxx find rt error   -ES stack test  -EB bounds  -EO overflow  -EN numeric
-FEMU *emulated float  -FNPX 87 float  -FP procedural float
-G86 *8086  -G286  -G386
-LS *serial  -LP *printer  -LB *ctrl-break  -LG *graphics  -LC *CGA
-LE *EGA  -LV *VGA  -LH *Hercules  -LA all  -LI interpreted print
-LF full float emulate
-DExxx exe dir  -DUxxx unit dir  -DSxxx source dirs  -DLxxx link dirs
```

## Notable runtime architecture facts (from the reference)

- PRINT writes **directly to video memory**, not DOS handles; only STDOUT/
  STDIN/PRINT# are redirectable (differential tests must compare via files).
- $STRING n selects string-segment granularity: 1006/2030/4078/8174/16366/
  32750 usable bytes per allocated segment (default 32750).
- Internal procedures callable from inline asm (string manager ABI):
  GetStrAlloc/GET$ALLOC, GetStrLen/GET$LEN, GetStrLoc/GET$LOC,
  RlsStrAlloc/RLS$ALLOC, ArrayCalc, ArrayInfo, SetOnExit, SetUevent.
  String variables hold 16-bit handles (`! mov AX, x$` loads the handle).
- Internal variables (read via name): pbvScrnCols, pbvScrnRows?, pbvHost,
  pbvBinBase, pbvDefSeg, pbvScrnBuff, pbvSwitch, pbvVTxtX1/Y1/X2/Y2,
  pbvRestore, pbvFixDigits.
- $OPTION SIGNED makes the *PTR/*SEG functions return signed ints.
- $ERROR BOUNDS/NUMERIC/OVERFLOW/STACK insert runtime checks; without
  NUMERIC, overflow wraps silently (and FOR loops at the type maximum loop
  forever - see QUIRKS).
