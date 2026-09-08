# O0274 — Profile-guided code layout

| | |
|---|---|
| **Status** | 🟨 IR basic-block layout consumer implemented; native/linker integration remains in O0268/O0360–O0406 |
| **Stage** | IR middle-end consumer → backend/linker layout integration |
| **Related** | [O0104](O0104-block-placement.md), [O0268](O0268-profile-collection.md), [O0360](O0360-basic-block-fragments.md) |

## The idea

Arrange functions and blocks by observed execution so that the hot path is
contiguous — improving instruction-cache, TLB and branch-predictor behavior on
386-era and later targets, and prefetch-queue and code-fetch traffic on an 8086.

This entry is the *umbrella intent*; the concrete binary-layout work continues in
the [O0360](O0360-basic-block-fragments.md) through
[O0406](O0406-layout-assertion-battery.md) family, where whole-program function
clustering, relocatable fragments, post-link placement and cleanup belong.

## IR middle-end implementation

`ProfileGuidedCodeLayout.Run` consumes observed **current-CFG edge counts** after
the profile layer has resolved its stable identities to `IrBasicBlock` objects. It
then changes only `IrFunction.Blocks` order:

- the entry block remains first;
- the next block is chosen from dominance-ready blocks, preferring the hottest
  observed edge out of the block just placed, then overall block heat;
- equal evidence is resolved by the original order, so output is deterministic;
- unreachable blocks keep their original relative order;
- duplicate edge samples are accumulated with saturating `ulong` arithmetic;
- an empty or all-zero profile is a strict no-op;
- profile edges that are foreign to the function or absent from its current CFG
  are rejected rather than silently applying stale data;
- functions with `ON ERROR`/`RESUME` or inline assembly are left untouched because
  their visible CFG is incomplete or opaque.

The dominance-ready restriction is deliberate. Physical layout is not CFG
semantics, but keeping every dominator before its descendants preserves the
linear def-before-use property expected by simple IR consumers while still
allowing hot joins to move in front of cold sibling arms.

`LlvmEmitter` (and the other emitters that walk `IrFunction.Blocks`) therefore
observes this physical order directly. The native x86-16 `InstructionSelector` is
different: it currently processes and lays out IR blocks in reverse postorder so
SSA definitions are guaranteed to be selected before their uses. This pass does
**not** replace that correctness rule with profile order. Once O0268 supplies an
actual `--profile-use` path, the native backend needs a separate post-selection
machine-block placement step (or equivalent layout-aware realization) before the
fall-through cleanup. Pretending the IR list alone already optimizes native DOS
machine layout would be wrong.

Verified by `ProfileGuidedCodeLayoutTests` for hot diamonds, hot loops, stable
unreachable blocks, no-profile and zero-profile no-ops, opaque-control-flow
no-ops, duplicate/saturating counts, stale/non-CFG edge rejection, IR verification,
and emitted LLVM block order.

## What remains outside this slice

- [O0268](O0268-profile-collection.md): instrumentation/sampling, profile files and
  stable block/edge identity matching. O0274 intentionally consumes resolved
  counts instead of inventing a second profile representation.
- Native x86-16 machine-block realization of the chosen layout after instruction
  selection, so `MachineEmitter` and its fall-through cleanup can exploit it.
- [O0360](O0360-basic-block-fragments.md): relocatable machine-code fragments and
  stable post-codegen block identities.
- Weighted whole-program call-graph placement
  ([O0361](O0361-weighted-call-graph-clustering.md)) and maximum-weighted
  fall-through placement ([O0365](O0365-maximum-weighted-fallthrough.md)).
- A **post-layout cleanup pass** — layout must not be the last machine-code step,
  because it creates new short branches, new fall-throughs and merge opportunities
  ([O0382](O0382-post-layout-branch-relaxation.md)).
