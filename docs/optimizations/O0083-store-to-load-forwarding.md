# O0083 — Statement-level store-to-load forwarding

| | |
|---|---|
| **Status** | ⬜ Planned (the frame-slot case exists — [O0034](O0034-redundant-load-elimination.md); the variable case does not) |
| **Stage** | Emitter |
| **Related** | [O0034](O0034-redundant-load-elimination.md), [O0084](O0084-cross-statement-register-caching.md), [O0027](O0027-copy-propagation.md) |

## The idea

The exact pattern:

```asm
    mov     [n], ax          ; store
    mov     ax, [n]          ; reload — AX already holds it
```

After `x = <expression>`, the value is still in the accumulator. An immediately
following read of `x` should use the register instead of reloading the cell.
The same shape appears whenever consecutive statements touch the same variable,
which in generated BASIC code is most of them — `n% = n% + 1 : IF n% = 0 …`
stores and reloads `n%` for no reason at all.

[O0034](O0034-redundant-load-elimination.md) does exactly this for `[BP+d]`
frame cells at the assembler level. Direct variable cells (`[label]`) are
excluded there because a segment load could re-point them — which means the
*emitter*, which knows no segment change happened, is the right place for the
general case.

## Applies to

```basic
DIM x%, y%
x% = a% + b%
y% = x% * 2
```

## Today

```asm
    mov     ax, [a]
    add     ax, [b]
    mov     [x], ax
    mov     ax, [x]          ; AX already holds it
    shl     ax, 1
    mov     [y], ax
```

## Planned

```asm
    mov     ax, [a]
    add     ax, [b]
    mov     [x], ax
    shl     ax, 1
    mov     [y], ax
```

## What it needs

- A small **accumulator-contents** model in the emitter: which variable's value
  AX currently holds, invalidated by any call, branch, label, `POKE`, inline
  asm, or write to that variable (directly or through an alias).
- It composes with [O0084](O0084-cross-statement-register-caching.md), which is
  the same idea generalized to a register that is *kept* rather than
  opportunistically reused.
