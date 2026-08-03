# O0276 — Post-link optimization

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | After linking |
| **Related** | [O0274](O0274-profile-guided-code-layout.md), [O0360](O0360-basic-block-fragments.md), [O0405](O0405-sample-based-reordering.md) |

## The idea

Reorder and rewrite the **final executable** using its actual addresses and a
sampled profile — the stage Microsoft's BBT and later Vulcan occupied, and where
BOLT and Propeller operate today. It sees what no earlier stage can: the real
layout, the real branch distances, and a profile taken from the optimized binary
rather than from an instrumented one.

## Why it is cheap *here*

A general post-link optimizer has to **rediscover** the control-flow graph from
anonymous machine bytes, which is the hard and fragile part. This compiler owns
its own object format and linker, so it can simply *keep* the CFG, the block
boundaries and the relocations in the `.PBU`/`.PBL` metadata — and the rewriter
becomes a fragment-reordering pass rather than a disassembler.

## What it needs

- Relocatable basic-block fragments with stable IDs preserved into the linked
  image ([O0360](O0360-basic-block-fragments.md)).
- Sampled edge counts from the shipped binary
  ([O0405](O0405-sample-based-reordering.md)).
- Re-relaxation and re-fixup after every move
  ([O0382](O0382-post-layout-branch-relaxation.md)).
