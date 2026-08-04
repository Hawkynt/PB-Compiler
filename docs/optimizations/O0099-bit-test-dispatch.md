# O0099 — Bit-test dispatch for small constant sets

| | |
|---|---|
| **Status** | 🟡 Partial (both spellings — a `SELECT` arm value list and an `IF … OR …` equality chain — lower to a 16-bit mask test; the 32-bit/386 mask and `LONG` subjects remain) |
| **Stage** | Emitter |
| **Related** | [O0029](O0029-select-jump-table.md), [O0098](O0098-balanced-decision-tree.md), [O0032](O0032-short-circuit-conditions.md) |

## The idea

Membership in a small constant set — `CASE 1, 3, 5, 9` or
`IF k% = 1 OR k% = 3 OR k% = 5 THEN` — is a **bit mask** test, not a chain of
comparisons: build the constant mask at compile time, shift it by the value, and
test bit 0. On a 386+ the `BT` instruction does it directly.

## Applies to

```basic
DIM k%
IF k% = 1 OR k% = 3 OR k% = 5 OR k% = 9 THEN PRINT "odd-ish"
```

## Today

Four compares and four branches (after
[O0032](O0032-short-circuit-conditions.md) short-circuits them, which already
helps, but the worst case is still four).

## Planned

```asm
    mov     ax, [k]
    cmp     ax, 000Fh        ; range guard: the mask covers 0..15
    ja      NotMember
    mov     cx, ax
    mov     ax, 022Ah        ; bits 1,3,5,9 set
    shr     ax, cl
    test    ax, 0001h
    jz      NotMember
```

Constant time, six instructions, no branches until the answer.

## Equivalent BASIC

```basic
IF k% >= 0 AND k% <= 15 THEN
  IF ((&H022A \ 2 ^ k%) AND 1) THEN PRINT "odd-ish"
END IF
```

## Now

Both spellings the doc names lower to the mask, gated on `$OPTIMIZE SPEED` and
INTEGER subjects:

- **`SELECT CASE` arm value list** (`TryEmitArmBitMask`, `CodeGenerator.cs`): an
  arm listing **≥ 3** single-constant point values (no ranges, no `IS`) whose
  window fits a 16-bit mask (`max − min ≤ 15`), once the dense jump table
  ([O0029](O0029-select-jump-table.md)) and the balanced tree
  ([O0098](O0098-balanced-decision-tree.md)) have declined — exactly the
  sparse-small-window shape neither of those covers.
- **`IF k = 1 OR k = 8 OR k = 15 THEN` equality chain** (`TryEmitOrChainBitMask`,
  in `EmitConditionalBranch` before the short-circuit lowering): the `OR` tree is
  flattened; every leaf must be `k = const` for the same 16-bit variable. It reads
  `k` **once** (a bare variable, no side effect), so it is equivalent to the
  short-circuited compare chain, and branches to the `THEN`/`ELSE` target on the
  requested truth value — both `whenFalse` polarities, so it composes inside an
  outer `AND`/`OR`.

Each normalizes the subject to the minimum (so a window not starting at zero, or
with negatives, still fits), emits one unsigned range guard, loads the compile-time
mask, and does `SHR AX, CL` + a bit-0 `TEST`. The same arm/branch runs as the
compare chain — verified by self-differential DOSBox runs over the whole subject
range (members `1, 8, 15`, non-members, negatives, boundaries, and an `OR` chain
nested in an `AND`) identical to `$OPTIMIZE OFF`, plus regression tests pinning the
`MOV AX, 4081h` / `SHR AX, CL` shape for both spellings (and its decline below
three values, and for a mixed-variable chain). Golden gate 250/250.

## Still planned

- The 32-bit mask under `$CPU 80386` for windows up to 31 wide, and `LONG`
  subjects.
- A cost-model call against the jump table where both apply (the mask needs no
  table bytes, so it wins on a size-constrained target even where a table fits).
