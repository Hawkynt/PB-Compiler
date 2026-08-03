# O0089 — Sign- and zero-extension elimination

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter, on the value facts |
| **Related** | [O0016](O0016-value-fact-analysis.md), [O0090](O0090-demanded-bits.md), [C0001](C0001-386-codegen.md) |

## The idea

Widening conversions are emitted structurally — a `BYTE` read zero-extends, a
`SBYTE` read sign-extends (`CBW`), a 16→32 promotion sign-extends (`CWD`) — even
when the value is already in that form. When the known bits or the interval
prove the high half is already correct, the extension is a no-op.

## Applies to

```basic
DIM b AS BYTE, n%, t&
n% = b                      ' 0..255: the high byte is already zero
t& = n% AND &H7FFF          ' non-negative: CWD would write a zero DX
```

## Today

```asm
    mov     al, [b]
    xor     ah, ah           ; zero-extend, though AH is provably 0 already
    mov     [n], ax
    mov     ax, [n]
    and     ax, 7FFFh
    cwd                      ; sign-extend a provably non-negative value
    mov     [t], ax
    mov     [t+2], dx
```

## Planned

```asm
    mov     al, [b]
    ...
    and     ax, 7FFFh
    xor     dx, dx           ; or nothing at all, if DX is known clear
```

## What it needs

- The **known-bits** domain from [O0016](O0016-value-fact-analysis.md), which
  already answers "is the high byte provably zero" — this is a new consumer of
  an existing analysis, not new analysis.
- Under `$CPU 80386` the same fact chooses between `MOVZX`, `MOVSX` and a plain
  `MOV` ([C0001](C0001-386-codegen.md)).
- Care where the extension is not redundant but *load-bearing*: PB's
  integral-to-float promotion and the wide-use display rules depend on the value
  being widened at the right point.
