# O0102 — Return-value forwarding

| | |
|---|---|
| **Status** | 🟡 Partial (numeric results whose slot `Mem2Reg` promotes are returned straight from their SSA value, on every exit; string results are not covered) |
| **Stage** | IR middle end + x86 back end (instruction selection) |
| **Source** | `Ir/IrLowering.cs` — `ReturnFromFunction`; `Ir/Passes/Mem2Reg.cs`; `Backend/InstructionSelector.cs` — `SelectRet` |
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

The lowering gives the result pseudo-variable an ordinary alloca and ends every
exit (`EXIT FUNCTION`/`EXIT DEF` included, via `ReturnFromFunction`) with a load
of it followed by `ret`. `Mem2Reg` promotes that alloca when
every use is a direct, same-width load or store, so each `ret` names the SSA value
the last assignment on its path produced (a phi where paths join), and the slot
store and reload disappear. `SelectRet` then moves that value straight into the
return channel: `AX` for a word or byte, `DX:AX` for a `LONG`, an `FLD` onto the
x87 stack for SINGLE/DOUBLE. Multi-exit functions need no special case because
each exit is its own `ret`.

MBF results (BASICA/GW floats) are never promoted: `Mem2Reg.IsPromotable` refuses
an MBF cell, so they keep the slot and the conversion on return.

## Still planned

- **String** results, which carry an owned handle and so need an ownership rule
  of their own before the store/reload pair can go.
