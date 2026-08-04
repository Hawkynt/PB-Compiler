# O0101 — Jump-table sharing and compression

| | |
|---|---|
| **Status** | 🟡 Partial (the byte-index-into-address-table compression is emitted under `$OPTIMIZE SIZE`; the shared range check and the byte-*offset* table are not) |
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

## Now

The **few-distinct-targets** compression ships (`TryEmitSelectJumpTable`,
`CodeGenerator.cs`). The dispatch builds the span→target map as usual, but when the
distinct targets `K` are few enough that a byte index table plus a `K`-entry address
table is smaller than the word table (`span + 2K < 2·span`, i.e. `span > 2K`) and
`$OPTIMIZE SIZE` is on, it emits `MOV BL, [ByteTable+BX]` (the byte slot, `BH`
stays 0 since the span is ≤ 256) then `JMP [AddrTable+BX*2]`. The byte table is one
byte per span slot; the address table one word per distinct arm (plus the default).
Under `$OPTIMIZE SPEED` (or no size pressure) the plain word table stays — the extra
per-dispatch load is not worth the bytes. The word-table path is byte-for-byte the
same as before (the golden gate and `DIFF62.BAS` differential are unmoved), so only
the size-directed output changes; verified by a self-differential DOSBox run of a
wide-span, three-arm SELECT identical to `$OPTIMIZE OFF`, plus a regression test
that the byte-index load appears under SIZE and not under SPEED.

## Still planned

- **Shared range checks.** Adjacent or nested dispatches over the same subject each
  emit their own `SUB`/`CMP`/`JA` guard; the range fact from
  [O0016](O0016-value-fact-analysis.md) (which already narrows a `SELECT` subject
  per arm) would let a dominated second guard drop.
- **Byte-offset tables** (targets all within 256 bytes of a base → a `db` of
  offsets), which need the arm span known before the table is written — a second
  layout pass, or a conservative estimate plus relaxation like
  [O0035](O0035-jump-relaxation.md).
