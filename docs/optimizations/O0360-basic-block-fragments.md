# O0360 — Relocatable basic-block fragments

| | |
|---|---|
| **Status** | ⬜ Planned — **the prerequisite for the whole layout family** |
| **Stage** | Code generation → linker |
| **Related** | [O0274](O0274-profile-guided-code-layout.md), [O0276](O0276-post-link-optimization.md), [O0268](O0268-profile-collection.md) |

## The idea

Layout optimization needs to move code around. That is only possible if the
code is emitted as **independently placeable fragments** with stable identities
and complete relocation information — one fragment per basic block, each
carrying its ID, its successors, and its fixups.

The rest of this family is then a matter of *choosing an order*; without it,
every one of them is blocked.

## Why this compiler is well placed

A general post-link optimizer must rediscover the control-flow graph from
anonymous machine bytes — the hard, fragile part of BOLT-class tools. This
compiler owns its code generator, its object format (`.PBU`/`.PBL`) and its
linker, so it can simply **keep** the CFG rather than reconstruct it.
Rediscovering one's own control-flow graph from one's own output would be a
theatrical waste of engineering time.

## What it needs

- Block-granular emission with stable IDs (procedure name + structural index),
  preserved through the peephole, the scheduler and the linker.
- Relocations expressed against fragment IDs rather than absolute offsets, so a
  move is a re-layout rather than a rewrite.
- Re-relaxation after every move
  ([O0382](O0382-post-layout-branch-relaxation.md)) — layout must not be the
  last step of the pipeline.
