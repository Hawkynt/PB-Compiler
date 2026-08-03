# O0084 — Cross-statement register caching

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0005](O0005-register-residency.md), [O0083](O0083-store-to-load-forwarding.md), [O0058](O0058-386-register-allocation.md) |

## The idea

[O0005](O0005-register-residency.md) keeps a value in SI/DI across a **loop**,
where repetition amortizes the load and the flush. The straight-line case was
deliberately excluded — a single use costs one cell access either way — but a
local read *three or four times* across consecutive statements does pay, and
today it is reloaded every time.

## Applies to

```basic
DIM w%, a%, b%, c%
a% = w% + 1
b% = w% * 2
c% = w% - 3
```

## Today

```asm
    mov     ax, [w]
    inc     ax
    mov     [a], ax
    mov     ax, [w]          ; reload
    shl     ax, 1
    mov     [b], ax
    mov     ax, [w]          ; reload
    sub     ax, 0003h
    mov     [c], ax
```

## Planned

```asm
    mov     si, [w]          ; one load for the run
    mov     ax, si
    inc     ax
    mov     [a], ax
    mov     ax, si
    shl     ax, 1
    mov     [b], ax
    mov     ax, si
    sub     ax, 0003h
    mov     [c], ax
```

## What it needs

- A **profitability rule**: on an 8086 the parked register must save more cell
  accesses than the park costs, which means at least three uses in a barrier-free
  run — and it competes with the loop residency that already owns SI/DI.
- The same clean-region proof [O0005](O0005-register-residency.md) uses (no
  call, no inline asm, no aliasing write), plus a flush at every region exit.
