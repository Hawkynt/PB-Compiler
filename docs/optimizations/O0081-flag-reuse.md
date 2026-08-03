# O0081 — Flag reuse and `TEST` instead of `CMP …,0`

| | |
|---|---|
| **Status** | ⬜ Planned (the `OR AX,AX` form of the zero compare exists — [O0008](O0008-peephole-zero-idiom.md); reusing *earlier* flags does not) |
| **Stage** | Assembler peephole |
| **Related** | [O0008](O0008-peephole-zero-idiom.md), [O0031](O0031-branch-fusion.md), [O0038](O0038-instruction-scheduling.md) |

## The idea

Two related rewrites:

1. `CMP r,0` → `TEST r,r` (or the existing `OR r,r`) — two bytes instead of
   three or four, same ZF/SF.
2. **Redundant compare elimination**: `ADD`, `SUB`, `AND`, `OR`, `XOR`, `INC`,
   `DEC` and `NEG` already set ZF/SF from their result. A `CMP result,0` that
   follows one of them with nothing in between that writes flags is pure
   redundancy.

## Applies to

```basic
DIM n%
n% = n% - 1
IF n% = 0 THEN PRINT "done"
```

## Today

```asm
    mov     ax, [n]
    dec     ax
    mov     [n], ax
    mov     ax, [n]
    or      ax, ax           ; the flags DEC already set
    jnz     Skip
```

## Planned

```asm
    mov     ax, [n]
    dec     ax
    mov     [n], ax          ; MOV does not touch flags
    jnz     Skip
```

## What it needs

- The assembler's existing **def/use records** (`Asm/Assembler.Schedule.cs`)
  already track `readsFlags`/`writesFlags` per instruction; the pass is a scan
  for a flag-setting instruction whose flags survive to the compare.
- The same narrowness rules as [O0034](O0034-redundant-load-elimination.md): an
  unbroken chain of recorded, byte-adjacent instructions and **no bound label**
  in between, because something could branch in with different flags.
- `INC`/`DEC` do **not** write CF, so a following unsigned condition (`JB`/`JA`)
  may not reuse them — only the ZF/SF conditions qualify.
