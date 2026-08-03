# O0034 — Redundant-load elimination (store-to-load forwarding)

| | |
|---|---|
| **Status** | ✅ Implemented (deliberately narrow) |
| **Stage** | Assembler, on the recorded instruction stream |
| **Source** | `Asm/Assembler.LoadForward.cs` |
| **Gate** | `--optimize` |
| **Verified by** | scenario `MaxScanReadsEachElementOnce`, `Asm/LoadForwardingTests` (one test per case it must decline) |
| **Related** | [O0003](O0003-common-subexpression-elimination.md), [O0038](O0038-instruction-scheduling.md), [O0065](O0065-dead-frame-store-elimination.md) |

## What it is

`MOV [BP-8],AX … MOV AX,[BP-8]` leaves AX already holding the value, so the
reload is dead. This is the last thing standing between the emitted code and
hand-written assembly wherever a value passes through a frame slot — a CSE
define feeding its use, a spill feeding its reload.

## Sample

```basic
$OPTIMIZE SPEED
DIM a%(0 TO 99), i%, m%
FOR i% = 0 TO 99
  IF a%(i%) > m% THEN m% = a%(i%)
NEXT
```

## Without the optimizer

The element is read twice, and the CSE slot is written and immediately read
back:

```asm
    mov     ax, [bx]         ; a%(i%)
    mov     [bp-8], ax       ; CSE define
    cmp     ax, di
    jle     Skip
    mov     ax, [bp-8]       ; reload — AX already holds it
    mov     di, ax
Skip:
```

## With the optimizer

```asm
    mov     ax, [bx]
    mov     [bp-8], ax
    cmp     ax, di
    jle     Skip
    mov     di, ax
Skip:
```

## Equivalent BASIC

```basic
FOR i% = 0 TO 99
  t% = a%(i%)              ' read once
  IF t% > m% THEN m% = t%
NEXT
```

## Why it is safe

A mistake here is a miscompile, so the pass is narrow by design. It fires only:

- for `MOV r16,[BP+d]` against a `MOV [BP+d],r16` with the **same register and
  displacement** — BP-relative cells are SS-relative, so unlike a `[label]` cell
  no intervening segment load can re-point them;
- across an unbroken chain of **recorded, byte-adjacent** instructions (a gap
  means a call or inline asm sat between them);
- with **no bound label** in between — something could branch in and reach the
  load without having run the store;
- never past a write to the register or a store that may alias the cell.

Conditional jumps are recorded as reading the flags and clobbering nothing, so
the pass sees *through* them: once no label can enter the range, reaching the
load on a branch's fall-through path is reaching it from the store.

It runs **before** the scheduler, whose window permutation would invalidate the
very records it reads.

## Limits

Removing the now-dead **store** needs whole-procedure knowledge that no other
instruction touches the cell, which the current records do not cover —
[O0065](O0065-dead-frame-store-elimination.md).
