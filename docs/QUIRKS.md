# PowerBASIC quirks and bug-compatibility notes

Documented oddities of the real compilers (PowerBASIC.GER FAQ, pbhq.de,
sections 2.x), and what PB-Compiler does about each.

**Policy (since the dialect wave): quirks are a function of `--dialect`.**
Under the default `pb35` the compiler behaves like genuine PBC 3.50
(byte-exact, oracle-verified). Under an older `--dialect` it additionally
**replicates that compiler's documented bugs**, so old sources behave exactly
as they did — version-specific bugs are *features* of the selected dialect.
Quirks whose precise wrong behavior is not documented well enough to clone
safely are listed as **pending oracle**: they activate once a genuine binary
of that version lands in `tools/<dialect>/` and a `tests/diff/<dialect>/`
battery pins the behavior (the harness picks both up automatically;
`tests/diff/pb30/QUIRK30.BAS` runs against a genuine PBC 3.0c in
tools/pb30/ - byte-identical as of the first oracle run).

| # | Quirk (versions) | Our behavior |
|---|------------------|--------------|
| 2.1/2.2 | `w?? = &HA000` overflowed in 3.0 (radix read as signed); 3.1+ honors a leading zero (`&H0A000`) as unsigned | **Dialect-emulated, oracle-verified vs genuine PBC 3.0c**: `--dialect pb30` and older read every radix literal signed (`&H0A000` = −24576, `&H0FFFF` = −1); pb31+ honor the leading zero (`&H0A000` = 40960 LONG) |
| 2.3 | DWORD never gets an overflow test, any version | Same: no overflow checks on DWORD arithmetic (all dialects) |
| 2.8 | "Fixup Overflow" when >64 KiB code from units in one segment | Same diagnostic at link time (64 KiB guard); $CODE SEG planned for multi-segment (PB36.md C1) |
| 2.9 | ASCII-154 after a remark inside inline asm breaks the parser (3.0) | **Pending oracle** (currently parses fine in all dialects) |
| 2.21 | Inline-asm operand semantics changed 3.0→3.1 (variable references) | 3.1+ semantics in all dialects (variable = memory cell, BYREF param = pointer slot); `--dialect pb30` emits a once-per-program warning that 3.0 semantics are not replicated — **pending oracle** |
| 2.24 | PRINT bug in 3.2 | **Pending oracle** (wrong behavior not specified precisely enough); 3.5 behavior in all dialects |
| 2.26 | Constant folding of `%k = -20-4` wrong in 3.0–3.2 | **Dialect-emulated, oracle-verified vs genuine PBC 3.0c**: a leading unary minus binds the whole additive chain, `%k = -20-4` = −16 (= −(20−4)); pb35/pb2x fold correctly (−24) |
| 2.27 | ROTATE on QUAD wrong in 3.0/3.1 (fixed 3.2) | Fixed (3.2+) behavior generated for all dialects (DIFF15); the 3.0/3.1 wrong rotation is **pending oracle** |
| 2.28 | FOR/NEXT increments **then** tests: a loop `FOR b? = 1 TO 255` wraps and never exits unless $ERROR NUMERIC is on | Replicated faithfully (DIFF18): counter arithmetic runs at the counter's own width, so BYTE/WORD counters wrap; with `$ERROR NUMERIC ON` the wrap raises error 6 (DIFF19) |
| 2.29 | `STEP -1` with unsigned counters underflows the same way | Oracle-corrected (DIFF18): an unsigned counter reads `STEP -1` as its unsigned bit pattern (65535), making the loop ascending - `FOR w?? = 2 TO 0 STEP -1` never enters the body |
| 2.30 | `VARPTR32(x) + n` miscomputed in 3.2 (fixed 3.5) | Fixed behavior; **pending oracle** for pb32 emulation (wrong result undocumented) |
| 2.31 | KEY ON line-25 protection missing since 3.x | Row 25 not specially protected (matches 3.x) |
| 2.34 | SWAP of UDT array elements with variable index swaps wrong cells (3.0–3.2) | Fixed behavior; **pending oracle** for pb30–pb32 emulation (which wrong cells is undocumented) |
| 3.1 | BIN$/HEX$/OCT$ accept 32-bit LONG values only since 3.1 | **Dialect-emulated, oracle-verified vs genuine PBC 3.0c**: pb30 and older render 16 bits (`HEX$(-1)` = `FFFF`, `OCT$(-1)` = `177777`); pb31+ render 32 |
| — | PRINT goes directly to video memory; only STDOUT/PRINT# redirect | Our PRINT currently writes via DOS (redirectable, like QB). Divergence documented; STDOUT/STDIN provided for the portable path. Differential tests compare via files. |
| — | Compiled EXEs embed environment-dependent bytes at 0x9C/0xA0 (3.x) | Not replicated; our images are deterministic |
| — | *PTR/*SEG functions return unsigned 0–64k unless `$OPTION SIGNED` | Same |

Unit coverage for the emulated rows: `PowerBasic.Compiler.Tests/Syntax/QuirkEmulationTests.cs`.

## Discoveries from the differential harness (verified against genuine PBC 3.50)

- **LONG `+`/`-` overflow wraps; LONG `*` overflow traps.** PB's float-promotion of
  integer `+ - *` is NOT uniform across widths. A 2-byte sum promotes to SINGLE (`32767+1`
  prints `32768`, not `-32768`), and a 4-byte *product* uses the FPU because it can exceed
  32 bits — but a 4-byte **add or subtract runs in the native 32-bit ALU and wraps**:
  genuine PBC prints `2147483000 + 1000` as `-2147483296` (mod 2³²), not the x87
  integer-indefinite sentinel a promoted `FISTP` store would give. (Fixed in
  `Binder.ArithmeticResultType`: Double-wide `+`/`-` stays integral; only `*` promotes.
  Battery `DIFF113`, now byte-identical.) A LONG **multiply** whose product is narrowed into
  a LONG store, by contrast, **raises Error 6** — it goes to the handler under
  `ON ERROR`, and halts the program without one; a *wide use* of the same product
  (`PRINT a& * b&`) shows the full value with no trap, and a DWORD multiply wraps silently.
  PB-Compiler matches this (`DIFF105`, byte-identical). The narrowing store RANGE-CHECKS the
  value against the LONG limits before FISTP rather than reading the x87 Invalid-Operation
  flag afterwards: FISTP does store `8000_0000h` for an out-of-range value, but the IE bit it
  should set alongside does not survive to a `FSTSW` under emulation, so the flag-based form
  stored the sentinel and trapped nothing. Comparing first also keeps `8000_0000h` a legal
  value a program may store. DWORD multiply matches too (wraps, never traps).
- **Radix sizing is by value bit length, not digit count**: `&O177777` (6 octal
  digits, but a 16-bit value) is `-1` INTEGER, exactly like `&HFFFF`. A leading
  zero *digit* switches to unsigned interpretation and widens as needed
  (`&H0FFFF` = 65535 LONG, `&O0177777` = 65535 LONG). Typed suffixes
  reinterpret the raw bits at the suffix size (`&HFFFF??` = 65535 WORD).
- **QUAD prints through the 15-digit float formatter**: `PRINT q&&` and
  `STR$(q&&)` of large values appear in E notation
  (`-9223372036854775807` prints as `-9.22337203685478E+18`); values of up to
  15 digits print as plain integers. PB-Compiler replicates this byte-for-byte
  (QUAD values ride the x87 stack).
- **The ASC statement requires the position argument**: `ASC(s$) = code` is
  rejected by PBC 3.50 with `Error 411: "," expected`; only
  `ASC(s$, n) = code` compiles. Replicated.
- **$IF/$ELSEIF take only a bare equate**: PBC 3.50 rejects expressions
  (`$IF %X = 1` → `Error 477: Syntax error`); the condition is one equate,
  true when nonzero. PB-Compiler additionally accepts constant expressions
  (a superset) — programs valid for the real compiler behave identically.
- **DECLARE SUB requires an explicit parameter list**: PBC 3.50 rejects
  `DECLARE SUB Name` without parentheses (`Error 426: Variable expected`);
  parameterless prototypes must read `DECLARE SUB Name ()`. PB-Compiler
  accepts both (superset); batteries use the strict form.
- **Narrowing QUAD/DWORD stores wrap silently** (e.g. `d??? = 3000000000`
  keeps 3000000000; a saturating FISTP would yield 2147483648) — matching the
  documented "no overflow checking" rule; PB-Compiler stores through a 64-bit
  scratch and takes the low bits.
- **BIT() needs an integer variable**: `BIT(8, 3)` is rejected by PBC 3.50
  with `Error 430: Integer variable expected`; only variables work.
- **$ERROR must precede executable statements**: a mid-module `$ERROR NUMERIC
  ON` raises `Error 506: Declaration must precede statements`. PB-Compiler is
  a superset here (it toggles the check state lexically); programs valid for
  the real compiler behave identically (DIFF18/DIFF19).
- **Error codes**: string-too-long is **15** (not 14); the `$ERROR STACK ON`
  probe raises **201** (verified via runaway recursion, DIFF19/DIFF20).
- **`DIM x(0 TO 7) AT seg` needs a dynamic array**: with constant bounds PBC
  3.50 raises `Error 489: Array is already static`; `DIM DYNAMIC x(...) AT seg`
  works (DIFF17 uses the B800 text page for the round trip).
- **FIX (@) facts** (DIFF16): `pbvFixDigits` defaults to 2; literal stores
  round decimally (`f@ = 2.555` gives 2.56 even though the binary value is
  below .555); arithmetic on FIX/BCD operands runs as EXT (`g@/h@` prints
  3.33333333333333); CBCD of an unsuffixed literal carries the SINGLE noise
  (`CBCD(2.7)` prints 2.70000004768372). Unsuffixed float literals are SINGLE -
  PB-Compiler quantizes them so the noise propagates identically.
- **CHAIN accepts .EXE targets** and carries COMMON across the transfer; the
  parent never resumes (DIFF21). PB-Compiler moves the COMMON blob through a
  `PBCHAIN.$$$` temp file - observably identical.
- **GetStrLoc ABI**: the handle is pushed on the stack (callee cleans, RET 2);
  returns DX:AX = far data pointer, CX = length (DIFF20 exercises it from
  inline assembly).

### Implementation divergences (documented, observably benign)

- FIX (@) is stored as a scaled 64-bit integer and BCD (@@) as an 80-bit x87
  EXT in their 8/10-byte cells - not as packed BCD nibbles. PRINT/STR$ run
  through the 15-significant-digit formatter, so results are byte-identical
  (DIFF16); only PEEKing the raw cell bytes could tell the difference.
  Runtime (non-literal) FIX stores round binary-to-nearest where genuine PBC
  rounds the value's decimal text; values within 14 significant digits agree.
- `REDIM PRESERVE` copies into a fresh block; the old block stays in the bump
  allocator until the program ends (the heap is reclaimed wholesale at exit).
- FIELD records are capped at 512 bytes and 32 field entries per program.
- VIRTUAL (EMS) arrays allocate one spare 16 KiB page so elements straddling a
  page boundary always map; `FRE(-11)` reflects that allocation.
- ARRAY SORT/SCAN cover dynamic-string arrays (the vendor corpus's usage);
  numeric element sorting and TAGARRAY are diagnosed as unsupported.

## Number formatting facts (verified against real PBC 3.5 in DOSBox)

- `PRINT n` for numerics: leading space (or `-`), digits, **trailing space**.
- `STR$(n)`: leading space/sign, **no** trailing space.
- `7 \ 2` → ` 3 `, `10 / 4` → ` 2.5 `, `2 ^ 10` → ` 1024 ` (integral powers
  print without decimal point), `HEX$(255)` → `FF`.
- Differential batteries `tests/diff/DIFF01–21.BAS` (numerics, radix rules,
  suffixes, `&` concat, QUAD incl. \/MOD/bitwise/SHIFT/ROTATE, ASCIIZ, PB 3.5
  surface, data pointers, UDT comparison/$ELSEIF, code pointers, the vendor
  string surface (INSTR ANY/VERIFY/EXTRACT$/TALLY/REPLACE/BIT/dotted names),
  ARRAY SORT/SCAN + LSET/RSET + USING$, FIX/BCD arithmetic, memory models
  (HUGE/VIRTUAL/ABSOLUTE/REDIM PRESERVE), $ERROR defaults and traps,
  $OPTIMIZE SPEED, FIELD/ERL/$STRING/GetStrLoc, and CHAIN with COMMON) produce
  byte-identical RESULT.TXT between PB-Compiler and genuine PBC 3.50
  (`scripts/run-diff-tests.sh`).

## Known unreplicated edge

- `MININT& \ -1` displays `2147483648` on genuine PBC 3.50 (the true quotient,
  unrepresentable in LONG) while PB-Compiler's long divide wraps to
  `-2147483648` - the genuine runtime appears to carry the quotient wide into
  PRINT. Everything else about `\`/`MOD` (truncation, signs, DWORD unsigned
  forms, error 11 on zero divisors) is oracle-verified; this single edge is
  excluded from the batteries and recorded here.
