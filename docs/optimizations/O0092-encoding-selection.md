# O0092 — Encoding selection: size, micro-ops and decode width

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter / assembler |
| **Related** | [O0035](O0035-jump-relaxation.md), [O0038](O0038-instruction-scheduling.md), [O0174](O0174-target-cost-models.md) |
| **Split into** | [O0244](O0244-microop-selection.md), [O0245](O0245-decode-width-scheduling.md), [O0246](O0246-move-elimination-aware.md) |

## The idea

x86 offers several encodings for the same operation, and which one is best is a
*target* question, not a universal one:

- the accumulator short forms (`ADD AX,imm` = `05 iw`) against the general modrm
  forms (`81 /0`) — fewer bytes, but they pin the value to AX
  ([O0072](O0072-register-reassignment.md));
- `INC` versus `ADD r,1`; `LOOP` versus `DEC CX`/`JNZ` (the 486 prefers the
  latter, [C0002](C0002-486-codegen.md));
- **micro-op count** rather than instruction count on decoded cores: a single
  microcoded instruction may cost more than three simple ones;
- **decode width and boundaries** on superscalar cores, where an awkward
  instruction mix throttles the front end;
- **move elimination** on cores that resolve register-to-register moves in
  rename, which changes the cost of coalescing
  ([O0085](O0085-copy-coalescing.md)).

On an 8086 exactly one of these matters, and it matters a lot: **fewer
instruction bytes** keep the 4-byte prefetch queue full while the bus interface
unit is the bottleneck.

## Applies to

Every emitted instruction — this is a selection policy, not a pattern.

## What it needs

- [O0174](O0174-target-cost-models.md), with a per-target table of encoding
  costs, and a size-versus-speed knob wired to `$OPTIMIZE SIZE`/`SPEED`.
- Length-changing selection forces re-layout of everything after it (the same
  problem [O0072](O0072-register-reassignment.md) documents), so it belongs in
  the assembler's fixup pass rather than in an after-the-fact peephole.
