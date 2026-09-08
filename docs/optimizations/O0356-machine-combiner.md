# O0356 — Machine combiner

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | After instruction selection, before scheduling/RA |
| **Related** | [O0082](O0082-memory-operand-folding.md), [O0064](O0064-lea-fusion.md), [O0038](O0038-instruction-scheduling.md) |

## The idea

Some target patterns only become visible **after** selection, when the actual
x86 instruction forms and register roles are known. A late combiner can then
replace a sequence whose target-independent IR representation was already lost.

`Backend/MachineCombiner.cs` currently performs two conservative combines:

- `CMP r,0 -> TEST r,r`, preserving the condition-code information consumed by
  ordinary branches/carry chains while avoiding an immediate operand;
- `MOV d,s ; ADD/SUB d,imm -> LEA d,[s +/- imm]` when the arithmetic flags are
  proven dead and using `s` as an address cannot create a new register-class
  constraint.

Optimized instruction selection marks the machine function in `Peephole.Run`.
`MachineScheduler.Schedule` consumes that marker to run O0355/O0356 immediately
before scheduling; unoptimized and hand-built scheduler inputs therefore retain
the scheduler's previous behaviour.

## Applies to

```asm
    mov     si, bx
    add     si, 4
    ; flags overwritten before any read
```

becomes

```asm
    lea     si, [bx+4]
```

when `bx` is already constrained to an address-capable register class.

## Safety and limits

- `CMP r,0 -> TEST r,r` is guarded by auxiliary-flag observability. The two
  instructions agree on the flags used by `Jcc`, `ADC`/`SBB`, and
  `RCL`/`RCR`, but x86 defines AF after `CMP r,0` and leaves AF undefined after
  `TEST r,r`. Inline assembly can observe that difference through `LAHF` or
  `PUSHF`, so O0356 follows CFG successors and declines the combine if a generic
  flag reader can see AF before an instruction that definitely overwrites it.
- `LEA` replacement requires a later flag-writing instruction before any flag
  read. Falling out of the block is not considered proof of dead flags.
- A virtual source is accepted only when some existing memory operand already
  requires that value to be address-capable; otherwise the combine could turn an
  allocatable value into one that only BX/SI/DI can hold.
- The current pass does not duplicate operations, change register pressure, or
  speculate memory accesses.
- More cost-model-driven combines can be added here without teaching the
  target-independent IR about x86 encodings.
