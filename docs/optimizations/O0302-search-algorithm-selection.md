# O0302 — Search algorithm selection by pattern

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter + runtime |
| **Related** | [O0154](O0154-swar-search.md), [O0330](O0330-library-call-recognition.md), [R0003](R0003-string-engine.md) |

## The idea

`INSTR` should not pay for the same general substring probe when the compiler
already knows enough about the needle to select a better search:

| Pattern | Strategy |
|---|---|
| one byte | `REPNE SCASB` byte scan |
| 2–4 byte constant | `REPNE SCASB` candidate scan + `REPE CMPSB` verification |
| 5+ byte constant | Boyer-Moore-Horspool with a compile-time-generated skip table |
| runtime pattern | the general per-position `REPE CMPSB` probe |

A constant pattern also means the needle need not be materialized as a dynamic
string. The long-pattern skip table is compile-time data rather than work repeated
at every call.

## Applies to

```basic
DIM s$, p%, k%
p% = INSTR(s$, "x")          ' byte scan
p% = INSTR(s$, "AB")         ' short candidate scan + verify
p% = INSTR(k%, s$, "BEGIN")  ' Horspool, explicit start retained
```

## Implementation

The existing single-byte path remains `rt_scanchar` (`EmitScanChar`), using
`REPNE SCASB` and passing the byte in `DL`, so a one-byte needle never allocates.

For a foldable 2–4 byte needle the emitter reads the needle directly from the
literal pool. `REPNE SCASB` advances between occurrences of the first byte and a
candidate is verified with `REPE CMPSB`. Positions that cannot contain the whole
needle are excluded from the scan count up front.

For a foldable needle of five or more bytes the emitter generates a 256-byte
Boyer-Moore-Horspool bad-character table. The search compares the candidate's
last byte first, verifies a possible match with `REPE CMPSB`, and indexes the
precomputed shift table with `XLAT`. A distance greater than 255 is saturated to
255; this can only shorten a legal shift, so patterns longer than 255 bytes remain
correct without growing the table to 512 bytes.

All three specialized paths preserve `INSTR` semantics: the optional start is
1-based and clamps to 1, a start past the last possible match returns 0, matches
return their 1-based position, and the owned haystack temporary is consumed just
like `rt_instr`. Empty needles, non-byte compile-time strings, runtime needles,
`VERIFY`, and `INSTR ... ANY` keep their existing generic paths so dialect-specific
or set-search semantics are not changed.

## References and licensing

The implementation was derived clean-room from the public algorithm description
and the compiler's own string ABI; no third-party implementation code was copied.

- R. Nigel Horspool, *Practical fast searching in strings*, Software: Practice and
  Experience 10(6), 1980, DOI `10.1002/spe.4380100608`. The paper motivates the
  bad-character search and notes that very short needles are a case where dedicated
  hardware string-search instructions can be preferable.
- NIST Dictionary of Algorithms and Data Structures, *Boyer-Moore-Horspool
  algorithm*, for the end-oriented comparison and bad-character shift definition.

## Verification

- `SearchAlgorithmSelectionTests` pins the 2–4 byte candidate-scan instruction
  shape, the long-pattern skip table and `XLAT` path, explicit-start selection,
  and the runtime-needle fallback.
- `tests/diff/DIFF122.BAS` covers one-byte, short and long constants, explicit
  starts, repeated prefixes, misses, a too-long remaining needle, and a runtime
  needle against the differential DOS battery.
