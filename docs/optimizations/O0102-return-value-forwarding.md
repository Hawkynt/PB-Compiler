# O0102 — Return-value forwarding

| | |
|---|---|
| **Status** | 🟡 Partial (single-exit integer/LONG/SINGLE/DOUBLE functions forward the result; multi-exit and string results still reload) |
| **Stage** | Emitter |
| **Related** | [O0006](O0006-inlining.md), [O0027](O0027-copy-propagation.md), [O0070](O0070-leaf-frame-elision.md) |

## The idea

A `FUNCTION`'s result is written to the result slot (the pseudo-variable named
like the function) and loaded again by the epilogue. When the final assignment
to the result is the last statement on its path, the expression should be
computed **directly into the return register**, and the slot never written.

## Applies to

```basic
FUNCTION Scale%(BYVAL v%)
  LOCAL t%
  t% = v% * 3
  Scale% = t% + 1
END FUNCTION
```

## Today

```asm
    mov     ax, [bp-2]       ; t%
    inc     ax
    mov     [bp-4], ax       ; the result slot
    ...
    mov     ax, [bp-4]       ; epilogue reload
    mov     sp, bp
    pop     bp
    ret     2
```

## Planned

```asm
    mov     ax, [bp-2]
    inc     ax               ; already in the return register
    mov     sp, bp
    pop     bp
    ret     2
```

## Now

`ResultForwardable` (`CodeGenerator.Procs.cs`) fires when the function has an
integer/`LONG` result, a **single exit** (no `EXIT SUB`/`FUNCTION`/`DEF` anywhere —
those reach the epilogue with `AX` unset), no `ON ERROR` restore and no string
teardown between the last store and the reload (both clobber `AX`), and its **final
top-level statement assigns the result**. In that case the body's last statement is
emitted as its RHS *expression* straight into the return register (`AX`/`DX:AX`) —
not as an assignment, which sidesteps the in-place / remainder-reuse `EmitAssign`
paths that leave the value in memory or `DX` — and both the slot store and the
epilogue reload are skipped, exactly the "Planned" listing above. The slot is on the
torn-down frame, so nothing reads it afterward; a final RHS that reads the result
pseudo-variable (`Scale% = Scale% + 1`) still reads its prior slot value correctly,
since earlier assignments stored normally. Optimize-gated, so the faithful epilogue
keeps its reload byte-for-byte (golden gate 250/250). Verified by a self-differential
DOSBox run over seven function shapes — simple, result-reading, `LONG`, multi-exit
(declines), a local, a string local (declines), recursive (last statement is an
`IF`, declines) — all identical to `$OPTIMIZE OFF`, and a regression test that the
epilogue reload is gone for a single-exit function and kept for a multi-exit one.

## Still planned

- **Multi-exit** functions: forwarding must hold on every path reaching the
  epilogue (compose with [O0103](O0103-shared-epilogue.md)); today any `EXIT`
  declines the whole function.
- **String** results, which carry an owned handle and so need an ownership rule
  of their own before the store/reload pair can go.

SINGLE and DOUBLE now forward as well. A float returns on the x87 stack rather
than in a register, but the shape is the same: the epilogue's job is an `FLD`
from the slot, so a value the last statement already left in `ST(0)` is where the
caller expects it and both the `FSTP` and the `FLD` go. The stack stays balanced
because the exchange is one-for-one. `MbfType` (BASICA/GW floats) is a separate
`PbType` rather than a float `ScalarType` and so cannot match — correctly, since
its epilogue *converts* MBF to IEEE, and a conversion is not a load that can be
skipped on the grounds that the value is already in place.
