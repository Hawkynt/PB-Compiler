# PRD extension — address-pinned arrays, fixed strings and effect-aware store scheduling

**Status:** Incremental implementation. This branch delivers private-buffer byte packing and safe local reordering. Address-pinned memory batching remains gated on a separate memory-region proof.

## Semantic facts

PowerBASIC 3.5 supports `DIM DYNAMIC a(...) AT segment` (a fixed-bound `DIM ... AT` is rejected by the reference compiler), typed pointers, fixed-length `STRING * n`, ASCIIZ buffers, dynamic-string handles, `STRPTR32` and `VARPTR32`. These are *different* storage contracts:

- A non-escaping fixed byte array, fixed string, or ASCIIZ buffer in a compiler-owned stack object is ordinary RAM. Distinct byte writes may be reordered subject to all dependency and observation checks.
- `DIM DYNAMIC ... AT segment`, a pointer to an arbitrary numeric address, or a string location exposed through `STRPTR32` may alias existing RAM, DOS control structures, VRAM, BIOS memory or memory-mapped devices. The numeric address alone does not authorize store merging or removal.
- A dynamic STRING variable generally contains a **handle**, not inline character bytes. Do not treat `VARPTR32(s$)` as a pointer to its characters, or assume a stable string buffer across reassignment/reallocation.
- An ASCIIZ buffer has NUL-termination semantics. Final bytes and all relevant reads must remain identical, and stores that alter its terminator cannot be moved past `LEN`/`PRINT`/ASCIIZ-consuming operations.

## Reordering legality

A group of independent byte stores may be scheduled in address order and packed into little-endian word/dword stores only when:

1. The containing control-flow block and the stores' execution multiplicity are unchanged. No transformation moves a write across a branch, loop boundary, trap, handler, interrupt, call or inline ASM.
2. All candidate writes identify disjoint bytes of the **same** normal RAM object, with known offsets and a complete in-bounds wider access (including no 16-bit offset wrap). Multiple writes to a single byte stay ordered unless normal DSE has already legally removed earlier writes.
3. Every intervening read is proven **NoAlias** with the bytes moved earlier, using the IR's width-aware `IrAliasAnalysis` and `IrPointerIdentityAnalysis`. A `MayAlias` result is a barrier, not a hint.
4. Every intervening write is proven independent of each moved write, including partial overlaps and byte ranges. Unknown offsets, complex calls, device reads, volatile regions and unknown effects are barriers.
5. The object's address does not escape through `VARPTR`, BYREF, `STRPTR`, inline ASM, returned pointers, pointer casts or another unknown observer.
6. Target instructions and profitability are explicit: 8086/286 word-width, 386+ optional dword-width, byte tails left intact. Alignment and target cost still matter; a cycle benefit is not assumed.

Example (ordinary private byte array, no intervening observers):

```basic
DIM data(0 TO 3) AS BYTE
data(2) = 3
data(0) = 1
data(3) = 4
data(1) = 2
```

With private storage and in-bounds addresses proven, the four independent writes may be coalesced into the little-endian dword `04030201h` on a 386, or two words on an 8086. The **first implementation** recognizes the equivalent late IR pattern; the BASIC front-end's bounds checks and higher-level operations may still act as barriers.

Contrast:

```basic
DIM DYNAMIC screen(0 TO 7) AT &HB800
```

This is **not** automatically a normal RAM array. It may be a text-framebuffer alias; byte-store count, width and order can be externally observable. The first pass deliberately leaves such `IrFarPtr` operations unchanged.

## Implementation stages

- [x] Reuse existing pointer-root, width-aware alias, pointer-escape and target-cost analyses.
- [x] Implement constant-byte coalescing for non-escaping, bounded `IrAlloca` byte buffers. Permit out-of-order writes across only proven-disjoint loads/stores and pure pointer calculations.
- [x] Keep `IrFarPtr` and uncertain memory as barriers, with explicit regression cases for pinned `B800h` memory.
- [x] Lower certified private 386 dwords as one machine instruction rather than splitting under BALANCED.
- [ ] Prove persistent pinned RAM ownership at the source/runtime boundary (segment, offset, extent, lifetime, mutability, alias registry and no MMIO). Do not infer from a conventional-memory number.
- [ ] Extend `IrPointerIdentityAnalysis` with safe physical-alias reasoning for real-mode `segment*16 + offset`, including 20-bit wrap, alternate segment:offset aliases, and far-pointer bounds.
- [ ] Introduce a target-specific memory-region descriptor or equivalent verified effect contract; distinguish private RAM, pinned ordinary RAM, packed VRAM, planar VRAM, BIOS/DOS/unknown MMIO.
- [ ] Allow pinned-memory store packetization only with positive read/write observer proofs; optionally batch certified framebuffer spans using the separate graphics provenance contract.
- [ ] Extend from constant bytes to provably invariant pattern fills, fixed-string writes, constant loops, UDT field layouts and array slices.
- [ ] Benchmark 8086/286/386 on DOS emulators and hardware; compare byte counts, machine instructions, segment setup and speed, not just SSA instruction counts.

## References and licensing

- Existing PB-Compiler `docs/QUIRKS.md` / DIFF17: vendor-oracle evidence for `DIM DYNAMIC ... AT segment`; `docs/DIALECTS.md` for fixed and ASCIIZ storage
- PowerBASIC.GER 3.5 notes: https://www.pbhq.de/pbfaq/english/pbfaq01.html
- PowerBASIC pointer/string handling FAQ: https://www.pbhq.de/pbfaq/pbfaq06.html
- LLVM alias classifications: https://llvm.org/docs/AliasAnalysis.html
- LLVM volatile memory and pointer aliasing rules: https://llvm.org/docs/LangRef.html#volatile-memory-accesses

Normative language/hardware facts and specification-style contracts only; independent C# implementation, no copied third-party source and no new NuGet dependency.
