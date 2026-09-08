# O0276 — Post-link optimization

| | |
|---|---|
| **Status** | 🟨 Partial — IR fragment/profile layout planning implemented; final executable rewriting still needs O0360/O0382/O0405 |
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

## Middle-end foundation implemented

`Ir/Passes/PostLinkOptimization.cs` now defines the part of this contract that
belongs before code generation rather than pretending final addresses exist in
SSA IR:

- every block is exposed as a fragment with a stable **procedure + structural
  block index** identity and stable successor identities;
- sampled edge counts are validated against the current CFG before they may
  influence layout, so a mismatched profile cannot silently optimize the wrong
  edge;
- hot legal edges are greedily chained into a proposed physical order while the
  function entry remains first;
- the candidate is accepted only when it strictly improves sampled weighted
  fall-through over the existing order, and the plan reports both weights plus
  the resulting ratio;
- planning is metadata-only: it does not reorder `IrFunction.Blocks`, because
  SSA/program order is not the final binary layout.

This is deliberately **not** labelled a complete post-link optimizer. The
current linker concatenates already-assembled unit byte blobs, so there is no
safe block-granular object to move yet. The plan is the information O0360 can
preserve into object/link metadata and the eventual O0276 rewriter can consume
without disassembling its own output.

## What it needs

- Relocatable basic-block fragments with stable IDs preserved into the linked
  image ([O0360](O0360-basic-block-fragments.md)).
- Sampled edge counts from the shipped binary
  ([O0405](O0405-sample-based-reordering.md)).
- Re-relaxation and re-fixup after every move
  ([O0382](O0382-post-layout-branch-relaxation.md)).
