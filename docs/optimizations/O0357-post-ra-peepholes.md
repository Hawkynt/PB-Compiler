# O0357 — Post-register-allocation peepholes

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | After register allocation |
| **Related** | [O0085](O0085-copy-coalescing.md), [O0034](O0034-redundant-load-elimination.md), [O0356](O0356-machine-combiner.md) |

## The idea

Once **physical** registers are assigned, patterns appear that no earlier pass
could see: a `MOV` whose source and destination turned out to be the same
register, two spills to the same slot, an addressing mode that became available
because the index landed in BX.

The load-forwarding pass ([O0034](O0034-redundant-load-elimination.md)) is
already a member of this family — it runs on the final stream and reasons about
concrete registers.

## Applies to

```asm
    mov     ax, ax           ; became a no-op after allocation
    mov     [bp-4], ax
    mov     ax, [bp-4]       ; already removed by O0034
```

## What it needs

- An allocator to run after ([O0058](O0058-386-register-allocation.md)); with
  today's fixed SI/DI residency the opportunities are few, which is why the
  existing pass targets frame slots rather than registers.
- The same narrowness discipline as
  [O0034](O0034-redundant-load-elimination.md): recorded, adjacent, label-free
  ranges only — a mistake at this level is a miscompile.
