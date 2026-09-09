# O0405 — Sample-based binary reordering

| | |
|---|---|
| **Status** | 🟡 Partial — final-address block attribution, sampled block weights, edge-flow inference and relink consumption implemented; DOS sampler/CLI producer remains |
| **Stage** | Post-link |
| **Source** | `Emit/PostLinkLayout.cs` (`PostLinkSampleAttribution`, `PostLinkProfile`, `PostLinkLayoutPlanner`) |
| **Related** | [O0276](O0276-post-link-optimization.md), [O0268](O0268-profile-collection.md), [O0404](O0404-stale-profile-matching.md) |

## The idea

Consume **sampled** execution data — a timer interrupt recording the instruction
pointer, or hardware branch history where it exists — taken from the final
optimized executable, rather than counts from an instrumented build.

Two advantages over instrumentation: no instrumentation bias (the counters
themselves change the layout and the timing they measure), and the profile
describes the binary that ships.

On DOS the eventual producer is concrete: hook `INT 08h` or reprogram the PIT,
record CS:IP into a buffer, write it out at exit. This entry now implements the
post-link half of that contract; target-runtime collection remains in the
profile-generation infrastructure rather than inside the linker.

## Implemented attribution

Every `LinkedImage` now exposes the final O0360 fragment address map. A
`PostLinkIpSample` is an offset in that final code image plus a count.
`PostLinkSampleAttribution.Attribute`:

- binary-searches the final non-overlapping fragment ranges;
- attaches each mapped sample to stable `(function, block-id)` identity;
- ignores samples in deliberately opaque runtime/direct-emitter/foreign regions;
- rejects sample offsets outside the linked code image and malformed overlapping
  block maps.

The resulting `PostLinkProfile` stores saturating `ulong` block and edge counts,
independent of the original addresses. It can therefore be consumed by a later
link of the same fragment identities.

## Point-sample edge inference

A point sample gives block heat directly, not the exact traversed CFG edge. The
implemented inference is deliberately explicit about that information loss:

- one-successor block: its observed outgoing flow belongs to that edge exactly;
- multiple successors: distribute the source block count in proportion to the
  observed successor block heat;
- if none of the successors was sampled, split the source flow evenly;
- the allocated outgoing edge counts always sum back to the sampled source count.

This is a conservative source-side flow-conservation estimate, not a claim that
point sampling recovered branch history it never observed. Hardware branch
records can later populate `PostLinkProfile` edge counts directly without
changing O0276.

`PostLinkLayoutPlanner` validates every sampled edge against the PBU2 CFG and
fails closed on stale/nonexistent block edges before those counts can change
layout.

## Still planned

- the DOS timer/PIT sampling hook and bounded target-side sample buffer;
- profile file/CLI plumbing that turns captured CS:IP values into
  `PostLinkIpSample` input automatically;
- richer global flow reconstruction for under-determined joins and loop flow;
- stale-profile structural matching across changed binaries
  ([O0404](O0404-stale-profile-matching.md)).
