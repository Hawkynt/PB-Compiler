# O0085 — Register copy coalescing

| | |
|---|---|
| **Status** | 🟡 Partial (a peephole coalesces the ABI-staging copy when the intermediate register dies immediately; the allocator-driven coalescing over interference info is not wired) |
| **Stage** | Register allocation / assembler peephole |
| **Related** | [O0027](O0027-copy-propagation.md), [O0058](O0058-386-register-allocation.md), [O0072](O0072-register-reassignment.md) |

## The idea

`MOV BX,AX` exists only because the producer and the consumer were assigned
different registers. When their live ranges do not interfere, they can share one
register and the move disappears — the standard coalescing step of a graph
colouring or linear-scan allocator.

This is the register-level counterpart of
[O0027](O0027-copy-propagation.md), which does the same thing for memory cells.

## Applies to

```basic
DIM a%, b%, t%
t% = a% + b%
PRINT t%
```

## Today

The staging discipline routes values through AX and copies them where the callee
or the next operation wants them:

```asm
    mov     ax, [a]
    add     ax, [b]
    mov     [t], ax
    mov     ax, [t]
    mov     bx, ax           ; the copy the ABI shape forced
    ...
```

## Planned

The consumer is allocated AX directly, so no copy is emitted.

## Now

The common shape the doc's "Today" pictures — a value staged through a register
that is then copied to its real home and never read again (`MOV R,SRC … MOV R2,R`
where `R` dies at the copy) — is coalesced to `MOV R2,SRC` by the assembler
peephole (`RunPeephole` in `Asm/Assembler.Peephole.cs`, the copy-forwarding
triple), removing the ABI-staging move. Gated `standalone && !EnableSchedule`
(`CodeGenerator.cs`); covered by `AssemblerPeepholeTests`
(`Peephole_GivenRegisterStagedThenRegisterDies_…`, and the `…IntermediateStillLive…`
decline). This achieves the doc's observable *effect* for the local case without
an allocator.

## Still planned

The doc's actual *mechanism* — the consumer allocated `AX` (or its target) directly
by an allocator coalescing over live-range interference — is not wired. The
graph-colouring allocator (`CodeGen/Ssa/RegisterAllocation.cs`) and `ScalarLiveness`
interference exist and are unit-tested, but `RegisterAllocation.Compute` is
analysis-only and not consumed by the emitter, so coalescing beyond the peephole's
straight-line window (across branches, or a value produced far from its consumer)
does not happen yet.

## What it needs

- A real allocator with **interference information** — coalescing is meaningless
  without live ranges, so this is a sub-item of
  [O0058](O0058-386-register-allocation.md).
- The x86-16 wrinkle from [O0072](O0072-register-reassignment.md): the
  accumulator forms are shorter encodings, so coalescing a value *out of* AX can
  grow the code even as it removes an instruction. The cost model
  ([O0174](O0174-target-cost-models.md)) has to decide.
