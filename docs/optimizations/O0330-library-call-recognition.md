# O0330 — Library call recognition

| | |
|---|---|
| **Status** | 🟡 Partial — canonical byte fill/copy loops become `memset`/`memcpy`; the wider library catalog remains planned |
| **Stage** | Mid-end |
| **Source** | `Ir/Passes/LibraryCallRecognition.cs` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Verified by** | `LibraryAndMemoryIdiomTests` |
| **Related** | [O0020](O0020-idiom-replacement.md), [O0073](O0073-algorithmic-idiom-catalog.md), [O0339](O0339-memory-routine-by-size.md) |

## The idea

Hand-written loops that reimplement a runtime primitive are replaced by the
primitive: `memcpy`, `memset`, `memcmp`, `strlen`, a search, a math routine. The
runtime version is written once, tuned once
([R0003](R0003-string-engine.md)), and widened per target
([O0241](O0241-dword-string-copy.md)) — which no open-coded loop will ever be.

## Implemented v1

`LibraryCallRecognition` recognizes canonical positive-unit-stride counted loops
that touch exactly one byte per iteration and have no extra observable work.

- A loop storing one invariant byte becomes `llvm.memset.p0.i32`.
- A loop loading one byte and storing it to a distinct proven storage object
  becomes `llvm.memcpy.p0.p0.i32`.
- `memcpy` is formed only for storage pairs the IR can prove disjoint (distinct
  allocas/globals); a possibly-overlapping copy is deliberately left alone.
- The memory operation must execute exactly once per counted iteration. Its block
  must dominate the latch, it may not live in the test header, and after removing
  the latch-to-header edge the matched loop body must be acyclic.
- The matched region must be closed: no side entrance, `EXIT LOOP`, early return,
  unreachable terminator, or other side exit may disappear when the loop is
  replaced. The header's normal false edge is the only permitted exit.
- For `memcpy`, the source load must likewise belong to the counted body rather
  than the test header and dominate the store.
- Declined matches are mutation-free: they do not even mint an intrinsic
  declaration.

The exact-execution checks mirror the legality boundary used by LLVM's
`LoopIdiomRecognize`: a store is only promotable when its block is unconditionally
executed for the loop exits. PB-Compiler keeps its own narrower clean-room matcher
rather than porting LLVM implementation code. LLVM's Language Reference is also
the semantic reference for the non-overlap requirement of `llvm.memcpy`.

The resulting intrinsic is then available to [O0339](O0339-memory-routine-by-size.md)
and the existing target/runtime memory-copy policy.

## Applies to

```basic
DIM i%, n%, a$(0 TO 99)
' hand-written length scan over a fixed buffer
i% = 1
DO WHILE MID$(buf$, i%, 1) <> CHR$(0)
  i% = i% + 1
LOOP                         ' this is strlen
```

The example above is intentionally still future work; v1 covers the byte
fill/copy members of the catalog rather than `strlen`.

## Still planned

- `memcmp`, `strlen`, search and math idioms.
- Overlap-aware `memmove` recognition.
- Wider element widths/strides and less canonical loop shapes when dependence
  and bounds proofs are available.
- The exactness proof for each additional primitive, including empty input,
  overlap and `$ERROR BOUNDS` behaviour.
