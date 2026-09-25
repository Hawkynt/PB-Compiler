# O0180 — String length caching

| | |
|---|---|
| **Status** | ✅ Done |
| **Stage** | IR middle end |
| **Source** | `Ir/Passes/StringConstantFold.cs` — `FoldLength` (`rt_str_len_borrow`); `Ir/Passes/Gvn.cs` — `IsDeterministicRead`; `Ir/Passes/Licm.cs` — `IsHoistableRead`; `Ir/Analysis/IrEffects.cs` |
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

The lowering reads `LEN(s$)` of a variable as `rt_str_len` over a copy made by
`rt_str_dup`. `StringConstantFold.FoldLength` rewrites that pair into one
non-consuming `rt_str_len_borrow` of the variable's handle (and a `LEN` of a
literal into its byte count). `IrEffects` classifies `rt_str_len_borrow` as a
deterministic read: no writes, no trap.

`Gvn` numbers such a call like a load, keyed by callee, operands and the MemorySSA
version of memory it reads (`IsDeterministicRead`). Two `LEN(s$)` with no
intervening memory definition get the same number and the second is replaced by
the first, so `LEN(s) + LEN(s) + LEN(s)` makes **one** descriptor read.

### Loop-condition hoisting (LICM)

`Licm` hoists a deterministic read (`IsHoistableRead`) out of a loop whose body
writes no memory, releases or allocates nothing and cannot throw. Reading it
once in the preheader is harmless even for a zero-trip loop, because its operand
is live there. `WHILE i% <= LEN(s$)` therefore reads the length once when the body
leaves memory alone; a body with any store keeps the per-iteration read. See
[O0028](O0028-loop-invariant-code-motion.md).

### Invalidation

- A **write to the string** (`s$ = …`) is a memory definition, so a later `LEN`
  reads a different MemorySSA version and gets a new value number.
- Any other memory definition between the two reads (a call that may write, a
  store, an input statement) does the same; the rule is conservative rather than
  string-specific.
- **Heap compaction is not a hazard**: it moves a string's data but never its
  *length*, which is what makes the length safe to cache where the address would
  not be.

`LEN` over a **fixed-length** string or a record is a compile-time constant, so it
never reaches this path; an ASCIIZ buffer calls `rt_asciiz_len` instead.

Covered by `OptimizerTests.Emit_GivenRepeatedLenOfSameString_WhenPb36_ThenCachedSmallerImage`.
The C and LLVM back ends receive the same folded IR.
