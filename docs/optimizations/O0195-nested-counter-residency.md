# O0195 — Nested FOR counter residency

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR middle end + x86 back end (register allocation) |
| **Source** | `Ir/Passes/Mem2Reg.cs` (the variable becomes SSA values); `Backend/LinearScanAllocator.cs` — `TryCoalesced` (under SPEED, SI/DI are offered first to what `LivenessAnalysis.LoopCarried` reports); `Backend/CopyCoalescer.cs` |
| **Gate** | `--optimize`; the SI/DI preference needs `$OPTIMIZE SPEED` |
| **Verified by** | `tests/diff/DIFF90.BAS` |
| **Split from** | [O0005](O0005-register-residency.md) |

## What it is

Both counters of a nested INTEGER `FOR` stay in registers, and neither spills
to the stack.

Nothing here is specific to counters or to nesting: each counter is an SSA value
with its own loop-header phi, and the linear-scan allocator assigns registers
from `AX BX CX DX SI DI` by live range. Under `$OPTIMIZE SPEED` SI and DI are
offered first to loop-carried values. There is no fixed two-level limit; a deeper
nest keeps its counters in registers as long as the live values fit the register
file, and the spiller decides what goes to memory when they do not.

## Sample

```basic
$OPTIMIZE SPEED
DIM i%, j%, t%
FOR i% = 1 TO 100
  FOR j% = 1 TO 100
    t% = t% + 1
  NEXT
NEXT
```

## With the optimizer

```asm
    mov     si, 0001h        ; outer counter
OuterTop:
    mov     di, 0001h        ; inner counter
InnerTop:
    ...
    inc     di
    cmp     di, 0064h
    jle     InnerTop
    mov     [j], di          ; flushed: a post-loop read sees the end value
    inc     si
```

The listing shows the shape the retired direct emitter produced. The allocator
now picks the registers per function, and a counter that is not read after the
loop has no cell to flush.

## Why it is safe

A post-loop read of the counter reads the SSA value that left the loop, which
is the increment-then-test end value (QUIRK 2.28). A counter whose variable is
observable elsewhere is not promoted and stays in memory.
