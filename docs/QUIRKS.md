# PowerBASIC quirks and bug-compatibility notes

Documented oddities of the real compilers (PowerBASIC.GER FAQ, pbhq.de,
sections 2.x), and what PB-Compiler does about each. Policy: we implement the
**PB 3.5 fixed behavior** for things PowerBASIC itself later fixed, and we
**replicate semantics programs actually depend on** (number formatting, radix
signedness, wrap-around arithmetic, FOR boundary behavior under $ERROR).

| # | Quirk (versions) | Our behavior |
|---|------------------|--------------|
| 2.1/2.2 | `w?? = &HA000` overflowed in 3.0 (radix read as signed); 3.1+ honors a leading zero (`&H0A000`) as unsigned | Radix literals follow the documented 3.1+ rule: signed by default, unsigned with leading zero or typed suffix (`&HFFFF??`) |
| 2.3 | DWORD never gets an overflow test, any version | Same: no overflow checks on DWORD arithmetic |
| 2.8 | "Fixup Overflow" when >64 KiB code from units in one segment | Same diagnostic at link time (64 KiB guard); $CODE SEG planned for multi-segment |
| 2.9 | ASCII-154 after a remark inside inline asm breaks the parser (3.0) | Not replicated (parses fine) |
| 2.21 | Inline-asm operand semantics changed 3.0→3.1 (variable references) | We implement 3.1+ semantics (variable = memory cell, BYREF param = pointer slot) |
| 2.24 | PRINT bug in 3.2 | Not replicated; 3.5 behavior |
| 2.26 | Constant folding of e.g. `%k = -20-4` wrong in 3.0–3.2 | Not replicated; correct folding |
| 2.27 | ROTATE on QUAD wrong in 3.0/3.1 (fixed 3.2) | Fixed behavior |
| 2.28 | FOR/NEXT increments **then** tests: a loop `FOR b? = 1 TO 255` wraps and never exits unless $ERROR NUMERIC is on | Replicated faithfully: counter wraps after the final iteration; with `$ERROR NUMERIC ON` the overflow raises error 6 instead |
| 2.29 | `STEP -1` with unsigned counters underflows the same way | Same rule as 2.28 |
| 2.30 | `VARPTR32(x) + n` miscomputed in 3.2 (fixed 3.5) | Fixed behavior |
| 2.31 | KEY ON line-25 protection missing since 3.x | Row 25 not specially protected (matches 3.x) |
| 2.34 | SWAP of UDT array elements with variable index swaps wrong cells (3.0–3.2) | Fixed behavior |
| — | PRINT goes directly to video memory; only STDOUT/PRINT# redirect | Our PRINT currently writes via DOS (redirectable, like QB). Divergence documented; STDOUT/STDIN provided for the portable path. Differential tests compare via files. |
| — | Compiled EXEs embed environment-dependent bytes at 0x9C/0xA0 (3.x) | Not replicated; our images are deterministic |
| — | *PTR/*SEG functions return unsigned 0–64k unless `$OPTION SIGNED` | Same |

## Number formatting facts (verified against real PBC 3.5 in DOSBox)

- `PRINT n` for numerics: leading space (or `-`), digits, **trailing space**.
- `STR$(n)`: leading space/sign, **no** trailing space.
- `7 \ 2` → ` 3 `, `10 / 4` → ` 2.5 `, `2 ^ 10` → ` 1024 ` (integral powers
  print without decimal point), `HEX$(255)` → `FF`.
- Differential test `T2.BAS` (file-output battery) produces byte-identical
  results between PB-Compiler and genuine PBC 3.50.
