# O0360 — Relocatable basic-block fragments

| | |
|---|---|
| **Status** | ✅ Implemented for routed x86-16 machine functions — PBU2 preserves block boundaries, CFG successors and internal relative control-flow records through the linker |
| **Stage** | Code generation → linker |
| **Source** | `Asm/Assembler.PostLink.cs`, `CodeGen/CodeGenerator.PostLink.cs`, `Emit/PbuFile.cs`, `Emit/PostLinkLayout.cs` |
| **Related** | [O0274](O0274-profile-guided-code-layout.md), [O0276](O0276-post-link-optimization.md), [O0268](O0268-profile-collection.md) |

## The idea

Layout optimization needs to move code around. That is only possible if the
code is emitted as **independently placeable fragments** with stable identities
and complete relocation information — one fragment per basic block, each
carrying its ID, its successors, and its fixups.

A general post-link optimizer must rediscover that graph from anonymous machine
bytes. PB-Compiler owns the code generator, object format and linker, so PBU2
keeps it instead.

## Implemented representation

`Assembler.ToPostLinkRelocatable()` runs the same late assembler pipeline that
produces the stored bytes, then retains two things ordinary relocation no longer
needs once assembly is complete:

- every non-constant bound label, including anonymous machine-block labels;
- every resolved internal `CALL`, `JMP` and `Jcc`, with its semantic kind,
  condition, encoded length and target offset.

`CodeGenerator.PostLink` combines those records with routed `MFunction` block
boundaries and writes one `PbuFragment` per machine block. A fragment carries:

- procedure name + function-local block ID;
- original byte range;
- the body length before layout-dependent terminal branches;
- CFG successor IDs;
- semantic terminal control (`Preserve`, one-way, or conditional with its two
  targets and x86 condition nibble).

PBU version 2 serializes that metadata and the internal-relative table. Version
1 remains readable and simply has no fragment information.

The main image uses the same representation when it passes through the linker.
Unit emission records exact function end labels; the main path derives the end
of a routed function from the first assembler label following its final machine
block. Routed functions exclude inline assembly, so this stays label-based
metadata recovery rather than instruction-byte disassembly.

## Safety boundary

Only routed x86-16 functions are movable. Runtime code, direct-emitter
procedures, foreign OMF code and other regions without complete machine-block
metadata remain opaque and keep their bytes in order. O0276 therefore optimizes
the regions for which the compiler can prove all relocation/control-flow facts
instead of guessing boundaries in the rest.

The linker rewriter remaps ordinary code offsets, exports, `NearCode` targets and
retained internal PC-relative instructions through the new layout. References
into removed terminal-branch bytes fail closed rather than being silently
retargeted.

Post-layout regeneration and relaxation are implemented by
[O0382](O0382-post-layout-branch-relaxation.md).
