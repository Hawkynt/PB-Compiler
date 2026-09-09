# O0276 — Post-link optimization

| | |
|---|---|
| **Status** | ✅ Linker rewriter implemented for O0360-aware routed x86-16 fragments; sampled-profile production/CLI remains separate O0405/O0268 infrastructure |
| **Stage** | After library extraction, before final unit bases/fixups |
| **Source** | `Emit/PostLinkLayout.cs`, `Emit/Linker.cs`, `CodeGen/CodeGenerator.PostLink.cs` |
| **Related** | [O0274](O0274-profile-guided-code-layout.md), [O0360](O0360-basic-block-fragments.md), [O0405](O0405-sample-based-reordering.md) |

## The idea

Reorder and rewrite the **final linked code image** using its actual block map and
a sampled profile — the stage Microsoft's BBT and later Vulcan occupied, and
where BOLT and Propeller operate today. It sees what no SSA pass can: the final
participating unit set, actual code offsets and the branch distances created by
the chosen binary layout.

A general post-link optimizer has to rediscover the CFG from anonymous machine
bytes. PB-Compiler owns its code generator, PBU format and linker, so PBU2 keeps
that information through O0360 and O0276 performs a fragment rewrite rather than
a disassembly pass.

## Implemented pipeline

The linker now performs the following when a `PostLinkProfile` is supplied:

1. Resolve mandatory units and lazily pull PBL/OMF members until the participating
   program is fixed.
2. Validate all imports/signature hashes exactly as the ordinary link does.
3. Validate profile edges against each PBU2 fragment CFG. Stale/nonexistent
   edges fail closed.
4. Build a hot-edge chain order per fragment-aware function, keeping block #0 as
   the entry and accepting a candidate only when sampled weighted fall-through
   strictly improves.
5. Rewrite only those functions. Opaque runtime/direct-emitter/foreign regions
   remain byte-for-byte ordered.
6. Regenerate layout-dependent terminal `JMP`/`Jcc` control from semantic PBU2
   metadata, including condition inversion for a newly selected fall-through.
7. Run O0382 short/near relaxation to a monotone fixpoint.
8. Remap exports, ordinary code-site fixups, `NearCode` targets and retained
   internal PC-relative transfers through the new offsets.
9. Assign final unit/data bases and apply the ordinary linker fixups.
10. Publish the final `LinkedFragment` address map on `LinkedImage` so samples
    from this linked image can be attributed back to stable block IDs.

That is real final-binary rewriting: the bytes consumed by the MZ writer change,
not the SSA block list.

## Main image and units

PBU2 unit compilation records exact routed-function ranges. When the compiler's
main image goes through the linker, `CodeGenerator.PostLink` publishes the same
fragment metadata for routed procedures in `MAIN`; their function end is derived
from the first assembler label following the final machine block. The code
generator exposes `PostLinkProfile` and forwards it to the linker on that path.

The historical standalone path with no link step still calls `Assembler.ToArray`
directly. It is intentionally not silently changed into a linker invocation just
because this optimization exists; future profile-use CLI plumbing can choose the
post-link path explicitly. The public `Linker` API already performs O0276 for a
complete main/unit image when supplied a profile.

## O0360 / O0382 / O0405 status

The three prerequisites that previously blocked this entry are no longer paper
requirements:

- [O0360](O0360-basic-block-fragments.md) preserves routed machine-block ranges,
  stable IDs, successors and internal relative transfers in PBU2.
- [O0382](O0382-post-layout-branch-relaxation.md) regenerates fall-through
  control and relaxes branches after the move.
- [O0405](O0405-sample-based-reordering.md) maps final linked IP samples to
  stable blocks and supplies conservative flow-derived edge weights.

O0405 still needs the DOS timer/PIT collector and profile-file/CLI producer. That
is input acquisition, not a blocker in the binary rewriter itself: callers can
already supply sampled addresses or stable `PostLinkProfile` counts directly.

## Safety boundary

Only code with complete O0360 metadata moves. A reference into terminal bytes
that the new layout removes is an error, as is a retained short relative transfer
that would become out of range and cannot be safely expanded inside an opaque
fragment. O0276 declines those cases rather than guessing instruction
boundaries.

No external dependency is required.
