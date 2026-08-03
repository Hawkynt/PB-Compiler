# O0100 — Perfect-hash dispatch

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0029](O0029-select-jump-table.md), [O0098](O0098-balanced-decision-tree.md), [O0099](O0099-bit-test-dispatch.md) |

## The idea

For a sparse but fixed set of case values, a small collision-free arithmetic
mapping — `(k * a) >> s AND m`, or `k MOD p` for a suitable prime — indexes a
compact table directly, giving constant-time dispatch where the value span is
far too wide for [O0029](O0029-select-jump-table.md)'s dense table and a
decision tree ([O0098](O0098-balanced-decision-tree.md)) would cost log n
compares.

## Applies to

```basic
SELECT CASE scancode%
  CASE 72  : ...      ' up
  CASE 80  : ...      ' down
  CASE 75  : ...      ' left
  CASE 77  : ...      ' right
  CASE 71  : ...      ' home
  CASE 79  : ...      ' end
  CASE ELSE : ...
END SELECT
```

Six values spread over 71..80 — dense enough today, but the same shape with
extended keys (`&H4700`, `&H4B00`, …) is not.

## Planned

```asm
    mov     ax, [scancode]
    mov     bx, ax
    and     ax, 000Fh        ; the chosen perfect hash for this value set
    shl     ax, 1
    mov     si, ax
    cmp     bx, [KeyTable+si]   ; verify: the hash is not injective on all inputs
    jne     Default
    jmp     word ptr [JumpTable+si]
```

## What it needs

- A **hash search** at compile time over a small parameter space
  (mask/multiply/shift, or modulus), with a guaranteed fallback when none is
  found within the budget.
- The **verification compare** is not optional: the hash is perfect only on the
  case values, so any other input must be rejected before the jump.
- A cost model decision against the tree and the table — three lowerings for one
  construct means the choice has to be principled
  ([O0174](O0174-target-cost-models.md)).
