# PowerBASIC/DOS dialect matrix

Sources: PowerBASIC.GER FAQ (pbhq.de, sections 1.6–1.8), the PB 3.5 README
("What's New in PowerBASIC 3.5"), the PB statement reference (manmrk pbs.htm),
and the original PB 3.5 Reference/User guides. The compiler selects a dialect
via `--dialect pb20|pb21|pb30|pb31|pb32|pb35|pb36` (default `pb35`; `pb36` is
the optimizing superset, docs/PB36.md); features below
are rejected with a diagnostic when used under an older dialect, and the
selected dialect also **re-enables that version's documented bugs**
(docs/QUIRKS.md - bug compatibility is part of dialect fidelity).

Related documents: docs/BASIC-FAMILY.md (cross-dialect matrix for GW-BASIC,
QuickBASIC, QBasic, Turbo Basic & friends - groundwork for cross-compiling
those dialects), docs/PB36.md (an envisioned `pb36` successor dialect with
optimizer features).

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

- TYPE/UNION variables comparable directly (`IF a = b THEN` on whole UDTs;
  memcmp semantics, only `=` and `<>`)
- Typed radix literals: suffix on the literal (`&HFFFF??` = 65535, `&HFFFF%` = −1)
- **Radix signedness rule** (verified against PBC 3.50, see QUIRKS): without a
  suffix the value's *bit length* selects the size (16/32/64) and the bits read
  SIGNED at that size — `&HFFFF` = −1 INTEGER, `&O177777` = −1 INTEGER,
  `&HFFFFFFFF` = −1 LONG; a leading zero digit reads unsigned and widens as
  needed (`&H0FFFF` = 65535 LONG); radixes carry up to 64 bits
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
- `$ELSEIF` metastatement (note: real PBC accepts only a bare equate as the
  $IF/$ELSEIF condition — expressions raise Error 477)
- ASC statement (`ASC(s$, n) = code` — the position is mandatory, Error 411
  without it) and ASC/ASCII function start position
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

## PB 3.6 (envisioned superset)

`pb36` is a strict superset of `pb35`: every `pb35` program compiles unchanged
with byte-identical observable behavior, plus opt-in modern syntax (each construct
is rejected below 3.6 with a `requires PowerBASIC 3.6` diagnostic). Optimization is
a separate axis (`--optimize`, on by default for `pb36`). Full detail in
[PB36.md](PB36.md); the object model and generators in
[PB36-TYPES.md](PB36-TYPES.md). Language highlights beyond 3.5:

- Declarations: `DIM x = v` / `DIM x AS T = v`, array-initializer literals
  (`{…}`, `lo..hi`, `..arr`), object initializers (`NEW Udt { .f = v }`), `ENUM` blocks.
- Operators: compound assignment (`+= -= *= /= \= ^= &=`), short-circuit `IF()`
  ternary, `ANDALSO`/`ORELSE`, shift/rotate/bitwise (`<< >> <<< >>> <<> <>> |`),
  scaled pointer arithmetic (`+*`/`-*`), from-end index `arr(^1)`.
- Procedures: expression-bodied `FUNCTION F() AS T = expr`, overloading, default &
  named parameters, nested local SUB/FUNCTION, lambdas/closures (`(a,b) => a+b`),
  typed procedure pointers / named delegate types.
- **Object model** (compile-time, no inheritance/vtables): `SUB`/`FUNCTION`/`PROPERTY
  GET`/`PROPERTY SET` members declared inside the `TYPE` block with the **`THIS`**
  receiver, lifted to procedures that take the instance BYREF. Auto-implemented
  properties (no body → hidden backing field; `FIELD`/`VALUE` keywords; `=>`
  expression bodies). **Anonymous full properties** `PROPERTY Count AS LONG` →
  trivial getter + setter over one field. The optimizer **inlines any trivial method
  body** (accessor or hand-written) through the BYREF `THIS` receiver, purging it when
  every call inlines — no property-specific path. **Constructors**: a `SUB` named like the `TYPE`,
  invoked `p = Point(3, 4)`. **`READONLY` types** (`TYPE Point READONLY …`): fields
  settable only inside the constructor. **`OPERATOR` overloading** (`OPERATOR + (o AS Vec)
  AS Vec` with `THIS` the left operand and `RESULT` the result; resolved at compile time).
  **Bit-field members** (`Mode AS BIT * 3`, 1..16 bits): consecutive bit-fields pack into a
  hidden 16-bit word; reads desugar to shift-and-mask, writes to a neighbour-preserving
  read-modify-write — no new codegen. **Layout control**: `TYPE T PACKED` / `ALIGN n` /
  `SIZE n` and per-field `field AS T AT offset` (explicit field alignment, total size and
  byte-offset placement, with gaps/overlap) — pure binder layout, pb36-only.
- **Nullable types** (pb36): `DIM x AS T?` is a synthesized value + INTEGER `HasValue` flag;
  `x = v` / `x = NOTHING` / `x ?? d` (null-coalescing), with auto-unwrap to `.Value` in value
  contexts. `?`/`??` are disambiguated from the BYTE/WORD suffixes by context (an operand after `??`).
- **Compile-time generics** (monomorphized): generic types `TYPE Stack OF T …` and
  generic procedures `FUNCTION Max OF T (…) AS T` (type argument inferred from the
  call); each instantiation is vivified into concrete code at compile time (no runtime
  type info), so the object model and inliner apply per instantiation. See
  [PB36-GENERICS.md](PB36-GENERICS.md).
- **Generators**: any `SUB`/`FUNCTION` with `YIELD` becomes a first-class generator
  whose call returns an enumerator UDT (`.MoveNext`/`.Current`/`.Reset`, or `FOR
  EACH`); parameters and locals persist across suspensions. `YIELD` is supported in
  `FOR`/`WHILE`/`DO`/`IF`/`SELECT CASE`, a `FOR EACH` over another generator, and
  `TRY`/`CATCH`/`FINALLY` (the handler is saved in enumerator fields and re-armed per
  resume) — all flattened to a resumable state machine; only a `TRY` that yields while
  nested in another yielding `TRY` is not yet supported.
- Exceptions: `TRY` / `CATCH` / `FINALLY` (FINALLY on every path), with **filtered
  CATCH** — `CATCH <errnum>`, `CATCH WHEN <cond>`, `CATCH <errnum> WHEN <cond>` tried
  in order (the WHEN guard short-circuits on the number), an unfiltered `CATCH` as the
  catch-all, and re-raise to the outer handler (after FINALLY) when none match; plus
  **`DEFER`** (scope guards, LIFO, run on the fault path too).
- Functions: a `FUNCTION` may **return a TYPE by value** (`AS Point`) — struct return,
  written straight into the assignment target. **Tuples / multiple return**: `AS (LONG,
  LONG)` returns an anonymous tuple (fields `Item1`…`ItemN`); `q, r = f()` destructures it.
- Memory/control flow: `WITH … END WITH`, `FOR EACH v IN source` (array, `[lo..hi]`
  range, or a generator), XMS/EMS array storage classes.

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

PB-Compiler implementation notes: QUAD values ride the x87 stack; `\`, MOD,
AND/OR/XOR/NOT/EQV/IMP and SHIFT/ROTATE run through memory-based 4-word
routines (DIFF15). FIX stores as a scaled int64, BCD as x87 EXT; both compute
as EXT and print byte-identically to genuine PBC (DIFF16, divergences in
QUIRKS.md). HUGE arrays allocate conventional memory via DOS 48h with
segment-stepping element access, VIRTUAL arrays live in EMS (int 67h, page
pair mapped around each access), ABSOLUTE arrays map `AT segment`, and REDIM
PRESERVE keeps the contents prefix (all DIFF17). Rank is limited to 1 for
HUGE/VIRTUAL; dynamic strings inside them are diagnosed per the spec.

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
- $OPTION SIGNED makes the *PTR/*SEG functions return signed ints
  (implemented in the binder; CODEPTR/CODESEG included).
- $ERROR BOUNDS/NUMERIC/OVERFLOW/STACK insert runtime checks (PBC switches
  -EB/-EN/-EO/-ES preset them): BOUNDS -> error 9 on out-of-range indexes,
  OVERFLOW -> error 6 after signed INTEGER/LONG add/sub/mul, NUMERIC ->
  error 6 when a FOR counter wraps at its type boundary, STACK -> error 201
  from the SP headroom probe at procedure entry ($STACK n sets the probe
  base). Default state is all OFF: overflow wraps silently and FOR loops at
  the type maximum loop forever - see QUIRKS (DIFF18/DIFF19). Genuine PBC
  requires the $ERROR metastatements before executable code (Error 506);
  PB-Compiler additionally allows lexical mid-module toggling (a superset).
- $OPTION CNTLBREAK ON|OFF installs an int 23h handler (OFF ignores
  Ctrl-Break, ON terminates through the runtime exit); $OPTION GOSUB is
  accepted and recorded (the GOSUB stack survives ON ERROR unwinding by
  construction - the handler restores the SP captured at ON ERROR time).
- $OPTIMIZE SIZE|SPEED (-OZF selects SPEED; one per module, duplicates are
  diagnosed). SPEED changes two code shapes: INTEGER multiplies by powers of
  two inline as shifts, and `v = v +/- const` on a direct cell folds into one
  ALU instruction. Both modes are oracle-verified observably identical
  (DIFF18 runs under $OPTIMIZE SPEED; the other batteries under SIZE).
- $STRING n caps the length of one dynamic string at the documented usable
  bytes (1006/2030/4078/8174/16366/32750); exceeding it raises error 15
  (oracle-verified, DIFF20). The storage stays our single far heap - only the
  observable limit follows the segment granularity.
- CHAIN file$ transfers control via DOS EXEC and carries COMMON scalars and
  dynamic strings through a `PBCHAIN.$$$` temp-file handoff in declaration
  order (the stable cross-image layout); RUN file$ transfers without COMMON.
  `$COMPILE CHAIN` emits the same MZ image with a .PBC extension. Targets
  without an extension default to .PBC; genuine PBC also chains to .EXE
  (DIFF21). COMMON arrays across CHAIN are diagnosed as unsupported.
- The MZ header caps MAXALLOC at the actually used paragraphs, so DOS 48h
  allocations (HUGE), EMS handles and SHELL/EXECUTE/CHAIN child processes
  have the rest of conventional memory available.
- FIELD #n binds dynamic-string windows onto RANDOM records: bare GET/PUT
  move the record through them (512-byte record cap, 32 entries); LSET/RSET
  justify into both dynamic and fixed strings (DIFF20/DIFF14).
- ERL returns the most recently executed numeric line label (tracked inside
  error-handling scopes; alphanumeric labels do not count); ERDEV/ERDEV$ are
  stubs (0 / "").
