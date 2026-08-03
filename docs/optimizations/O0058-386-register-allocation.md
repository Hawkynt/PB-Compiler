# O0058 — 386/486 register allocation (the multi-register tier)

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter, on the SSA liveness |
| **Related** | [O0005](O0005-register-residency.md) (the complete 8086 tier), [C0001](C0001-386-codegen.md), [O0072](O0072-register-reassignment.md) |
| **Split into** | [O0243](O0243-byte-register-packing.md) |

## The idea

On the 8086 only SI and DI are callee-stable, and
[O0005](O0005-register-residency.md) has taken that tier as far as it goes. On a
386 the picture changes: six 32-bit GP registers (EAX–EDX, ESI, EDI) exist, and
EBX/ESI/EDI survive the internal ABI, so **several hot LONG/INTEGER locals can
live in registers at once** across a region.

Two sub-items:

- **Multi-register allocation over the SSA live ranges**, paired with C1's
  `DX:AX → EAX` representation change (without it, a LONG in EAX costs four
  scratch moves and is slower than the ADD/ADC pair).
- **8-bit sub-register packing**: two non-escaping `BYTE` locals share one
  16-bit register's halves (`DL`/`DH`, `BL`/`BH`). Sound only when the allocator
  proves neither half is clobbered by an op that writes the whole 16-bit
  register — `MUL`/`DIV`/string ops touch AX, address math touches BX/SI/DI — so
  AX's halves are poor candidates while BX/DX halves work for byte-heavy code.
  Best done *inside* the allocator, which already tracks per-register liveness,
  rather than as a separate pass.

## Applies to

```basic
$CPU 80386
$OPTIMIZE SPEED
DIM i&, sum&, prod&, a&(0 TO 999)
FOR i& = 0 TO 999
  sum& = sum& + a&(i&)
  prod& = prod& XOR a&(i&)
NEXT
```

## Today

`sum&` and `prod&` are 4-byte stack cells; each update is a load pair, an
ADD/ADC or XOR pair, and a store pair.

## Planned

```asm
    xor     esi, esi         ; sum&
    xor     edi, edi         ; prod&
    ...
Top:
    mov     eax, [ebx]
    add     esi, eax
    xor     edi, eax
    ...
```

## What it needs

The hard parts are the ones every real allocator has: **spilling**, the **call
ABI** (calls clobber AX/BX/CX/DX), **`ON ERROR` re-entry** (a handler must see
memory, not registers), and **string/FLEX locals** whose handles the epilogue
must free. Gate conservatively, exactly as the 8086 tier does.
