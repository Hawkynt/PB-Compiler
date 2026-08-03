# O0101 — Jump-table sharing and compression

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0029](O0029-select-jump-table.md), [O0067](O0067-if-chain-jump-table.md), [P0006](P0006-header-squeeze.md) |
| **Split into** | [O0247](O0247-jump-table-entry-compression.md) |

## The idea

Two refinements to the existing jump table:

1. **Shared range checks.** Adjacent or nested dispatches over the same subject
   each emit their own `SUB`/`CMP`/`JA` guard. When the second dispatch is
   dominated by the first's in-range path, its guard is redundant.
2. **Compressed entries.** A word per target is the general case, but a table
   whose targets all lie within 256 bytes of a base can use **byte offsets**,
   halving the table; a table with few distinct targets can index a second,
   smaller table of addresses.

## Applies to

```basic
SELECT CASE k%
  CASE 0 TO 7
    SELECT CASE k%          ' the subject is already proven in 0..7
      CASE 0 : ...
      CASE 1 : ...
      ...
    END SELECT
END SELECT
```

## Today

```asm
    ; outer guard
    cmp     ax, 0007h
    ja      Default
    ; inner guard, on a value already proven in range
    cmp     ax, 0007h
    ja      Default2
    shl     ax, 1
    jmp     word ptr [Table+bx]
Table:
    dw      Arm0, Arm1, ...   ; 2 bytes per entry
```

## Planned

```asm
    cmp     ax, 0007h
    ja      Default
    mov     bx, ax
    mov     bl, [Table+bx]    ; 1 byte per entry
    add     bx, offset Base
    jmp     bx
Table:
    db      Arm0-Base, Arm1-Base, ...
```

## What it needs

- The **range fact** for the guard sharing comes from
  [O0016](O0016-value-fact-analysis.md), which already narrows a `SELECT`
  subject per arm — this is a new consumer of an existing refinement.
- Byte-offset tables need the arm span to be known before the table is written,
  which means a second layout pass (or a conservative estimate plus relaxation,
  like [O0035](O0035-jump-relaxation.md)).
