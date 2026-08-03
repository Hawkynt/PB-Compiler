# O0244 — Micro-op count selection

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter / assembler |
| **Related** | [O0092](O0092-encoding-selection.md), [O0174](O0174-target-cost-models.md), [C0002](C0002-486-codegen.md) |
| **Split from** | [O0092](O0092-encoding-selection.md) |

## The idea

On a decoded core, the unit of cost is the **micro-op**, not the instruction. A
single microcoded instruction can cost more than three simple ones — which
inverts the usual "fewer instructions is better" rule:

| Instruction | Simple alternative | Better on |
|---|---|---|
| `LOOP` | `DEC CX` / `JNZ` | 486+ |
| `ENTER`/`LEAVE` | explicit frame code | 486+ |
| `XLAT` | `MOV AL,[BX+SI]` | 486+ |
| `PUSHA`/`POPA` | individual pushes | P6+ (when only some registers are live) |
| `REP MOVSB` (short counts) | an unrolled move | P6+ |

## Applies to

Every selection decision; the source is unchanged.

## What it needs

- A per-target micro-op table in [O0174](O0174-target-cost-models.md). Some of
  these rules are already applied opportunistically for the 486 gate
  ([C0002](C0002-486-codegen.md)) — the missing piece is that they are *rules*
  rather than *data*.
- The 8086 has no decoder to speak of and the opposite preference: the shortest
  encoding wins, because the bus is the bottleneck.
