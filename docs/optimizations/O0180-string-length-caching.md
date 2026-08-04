# O0180 — String length caching

| | |
|---|---|
| **Status** | ✅ Done |
| **Stage** | Emitter |
| **Related** | [O0003](O0003-common-subexpression-elimination.md), [O0028](O0028-loop-invariant-code-motion.md), [O0181](O0181-empty-string-comparison.md), [R0003](R0003-string-engine.md) |

## The idea

`LEN(s$)` reads the string descriptor through the handle — a double
indirection through the descriptor table. Repeated in one expression, in a loop
condition, or in successive statements, it is recomputed every time even though
nothing changed the string.

The CSE machinery ([O0003](O0003-common-subexpression-elimination.md)) already
caches pure integer subexpressions; a `LEN` over an unmodified string variable
should be one of them.

## Applies to

```basic
DIM s$, i%, n%
FOR i% = 1 TO LEN(s$)            ' evaluated once by FOR, but...
  IF MID$(s$, i%, 1) = "x" THEN n% = n% + 1
NEXT
IF LEN(s$) > 0 AND LEN(s$) < 100 THEN PRINT "ok"    ' twice
```

## Now

The first `LEN(s$)` defines a CSE slot; every later read of the *same,
unmodified* string reloads it. `LEN(s) + LEN(s) + LEN(s)` calls `rt_len` **once**
instead of three times.

`CacheableLenSymbol` (in `OptCommonSubexpr`) classifies `LEN(bareStringVar)` over
a plain, non-static dynamic string as a cacheable integer leaf — keyed by the
string symbol, exactly the treatment the array-element read gets
(`CacheableArrayReadSymbol`). `LEN` is also made **barrier-free** so a condition
or statement holding it is scanned. The `LEN` result is a `LONG`, so it reuses
the existing wide (4-byte) CSE slot machinery — no emitter change was needed.

### Loop-condition hoisting (LICM)

The block-local CSE above collapses `LEN(s$)` repeats *within one straight-line
run*, but a `DO`/`WHILE` **condition** is re-evaluated every iteration and lives
outside the body block — so `WHILE i% <= LEN(s$)` recomputed the length on each
pass. The LICM preheader ([O0028](O0028-loop-invariant-code-motion.md),
`AnalyzeLicm`) now also scans the loop's pre/post condition: a `LEN` of a string
the body never writes is hoisted into the preheader as a single descriptor read,
and both the condition *and* any body use reload the one slot (keyed by the
string symbol, so they share it). `IsLicmCacheable` accepts a `CacheableLenSymbol`
node; `IsHoistableSafely` is trivially true for it (a `LEN` cannot trap).

The invariance test is the body write-set: if the loop mutates the string
(`s$ = s$ + …`) its length is *not* invariant, `s$` is in `written`, and the
length stays a per-iteration read. Verified by a self-differential DOSBox run of
`WHILE i% <= LEN(s$)` (optimized output identical to the golden-faithful
build) plus two regression tests — the invariant form collapses the condition and
body reads to **one** `rt_len` call, the string-mutating form keeps them separate.

### Invalidation

- A **write to the string** (`s$ = …`) drops the slot: `InvalidateAfterWrite`
  and `CollectWrites` (for cross-block / loop retention) both recognize a
  dynamic-string target now, so a length cached before a reassignment — or one
  inherited into a loop body that reassigns the string — recomputes.
- Any **barrier** (a call, `INPUT`/`LINE INPUT`, `MID$`/`LSET` statement, pointer
  write) ends the straight-line run and clears the cache, so nothing that could
  change a length survives unseen.
- **Heap compaction is not a hazard**: it moves a string's data but never its
  *length*, which is what makes the length safe to cache where the address would
  not be.

Verified by a self-differential run (optimized == the golden-faithful
unoptimized build) over the tricky cases — cache-then-reassign, a branch that
rewrites the string past an `IF` merge, and a loop body that grows the string
each pass — plus a regression test asserting the repeated-`LEN` image shrinks.

`LEN` over a **fixed-length or ASCIIZ** buffer is already a compile-time constant
(`EmitIntrinsic` emits `mov ax, <size>`), so it never reaches this path.

Native-only, in the `CodeGen` CSE. The IR back ends lower `LEN` to an `rt_len`
call the host C compiler's own CSE/GVN collapses when the string is provably
unchanged, so no dedicated IR pass is needed.
