# O0110 — General induction-variable strength reduction

| | |
|---|---|
| **Status** | ⬜ Planned (specific array shapes are done — [O0030](O0030-induction-variable-strength-reduction.md)) |
| **Stage** | Mid-end + emitter |
| **Related** | [O0030](O0030-induction-variable-strength-reduction.md), [O0111](O0111-redundant-induction-variables.md), [O0004](O0004-strength-reduction.md) |

## The idea

[O0030](O0030-induction-variable-strength-reduction.md) recognizes named loop
shapes: an element read, an element store, an accumulate. A **general** pass
would instead recognize the algebraic property — any expression of the form
`base + i * stride` where `i` is a loop induction variable is itself an
induction variable — and replace it with an incrementally updated value.

That covers what the shape recognizers miss: several arrays in one body,
multi-dimensional addressing, UDT-element strides that are not powers of two,
and expressions that are not array addresses at all.

## Applies to

```basic
TYPE Item
  a AS INTEGER
  b AS INTEGER
  c AS INTEGER
END TYPE
DIM list(0 TO 99) AS Item, i%, s%
FOR i% = 0 TO 99
  s% = s% + list(i%).b            ' stride 6, offset 2 — not a recognized shape
NEXT
```

## Today

Each iteration recomputes `base + i * 6 + 2`, and 6 is not a power of two, so
the multiply survives strength reduction.

## Planned

```asm
    lea     bx, [list+2]     ; base + field offset
Top:
    add     di, [bx]
    add     bx, 0006h        ; the whole address computation, per iteration
    ...
```

## What it needs

- **Induction-variable detection** over the SSA form (a phi whose back-edge
  input is `phi ± constant`), plus derived-IV classification — the standard
  SCEV-style analysis.
- The loop-exit fix-ups the existing pass already handles: the counter's final
  value must be exactly what the rolled loop leaves.
- Alias safety: a stepped pointer is only valid while nothing can reallocate the
  array under it ([O0060](O0060-memory-ssa.md) makes that provable in general;
  the current pass gates on "no calls" instead).
