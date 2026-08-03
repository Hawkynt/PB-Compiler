# O0217 — Bounds-check elimination by range

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter, on the value lattice |
| **Source** | `CodeGen/CodeGenerator.cs` — `IndexRangeOf` |
| **Gate** | `--optimize` + `$ERROR BOUNDS ON` |
| **Verified by** | `tests/diff/DIFF37/77/89/92/93.BAS` |
| **Split from** | [O0016](O0016-value-fact-analysis.md) (which is now the three-domain lattice itself) |

## What it is

An array index whose proven `[lo,hi]` lies inside the array's static bounds
cannot raise Error 9, so its check is not emitted. `IndexRangeOf` proves the
range for:

- a constant index, a `FOR` counter, or an affine counter expression
  (`a(i)`, `a(i±k)`);
- the sum or difference of two range-known operands (`a(i+j)`, `a(i-j)`);
- `x AND m` → `[0,m]` for any `x` (m a non-negative constant);
- `x MOD k` → `[-(|k|-1), |k|-1]`, or `[0,|k|-1]` when `x >= 0` is provable;
- `x \ k` by dividing the endpoints (truncated divide is monotonic in the
  dividend).

So masked, modular and scaled-down indexing — `a(h AND mask)`, `a(i MOD n)`,
`a(i \ 2)` — all drop their checks.

## Sample

```basic
$ERROR BOUNDS ON
DIM a%(0 TO 99), i%, h%
FOR i% = 0 TO 99
  a%(i%) = i%                ' counter range [0,99] fits [0,99]
NEXT
a%(h% AND 63) = 1            ' masked to [0,63]
```

## Why it is safe

A check that *could* fire is kept — the range must lie **entirely** inside the
bounds. Every node's range is validated against its own type, so a wrapped
intermediate yields "unknown" rather than a false proof
([O0016](O0016-value-fact-analysis.md)). A constant out-of-range index is
already a compile error in the genuine compiler.
