# PRD: Runtime-aware optimization and graphics store fusion

**Repository:** Hawkynt/PB-Compiler  
**Targets:** DOS real-mode x86 (8086 through newer feature-gated CPUs), plus portable back ends where behavior is supported  
**Status:** Active; first reviewable slice implements guarded Mode 13h `LINE ... BF` widened span fills  
**Baseline:** Existing `PixelRuntimeInlining`, `IrScreenModeAnalysis`, `IrEffects`, MemorySSA/GVN/DSE, `RuntimeTarget`, `TargetCost`, `PortableRuntime`, `RuntimeTrimmer`

## Goal

Expose selected runtime implementations as optimizable IR without losing language semantics, then specialize them to known backend, CPU, memory model and proven runtime state. Recognize adjacent byte stores through *semantically equivalent* graphics and memory operations and lower legal regions to efficient scalar stores, word/dword groups, repeated fills or blits. Eliminate the now-unreachable runtime routines.

Examples (under proven `SCREEN 13` and appropriate state/effect rules):

- `PSET (160,100),15` -> offset `100*320+160 = &H7DA0`, ultimately one `ES:[7DA0]` byte store with `ES=A000h` established.
- Four consecutive different-color pixels -> two 8086 word stores or one 386 dword store, after a *graphics-specific* proof that batching is legal.
- `LINE (0,100)-(319,100),15,BF` -> a scanline fill using `REP STOSW` on 8086 or `REP STOSD` on 386, with appropriate byte/word tails.
- Horizontal spans emitted by `PSET`, `LINE`, `PAINT`, constant `DRAW` and proven framebuffer `POKE` converge on a common operation if observable state and intervening effects permit it.
- Constant expressions within runtime functions fold; loop-invariant mode, segment, address and pattern setup moves out of loops; dead helper bodies become eligible for trimming.

This PRD covers the larger program of work. The accompanying code changes implement **Milestone 1** only, not the entire cross-primitive optimizer.

## Non-goals

- No assumption that arbitrary `POKE` or `DIM AT` is ordinary RAM. Generic memory-mapped device accesses must preserve individual writes and ordering.
- No implicit merging of LLVM `volatile` memory operations; LLVM explicitly forbids changing their number, width or order. Hardware-certified graphics batching requires its own positive semantic contract before widening.
- No generic conversion of planar VGA modes or Mode X to Mode 13h addressing.
- No new third-party packages, native dependencies or blanket rewrite of the DOS runtime.
- No MMX or x87 aliasing assumptions; consider MMX only after target-state and profitability analysis.

## Existing foundations to reuse

- `IrScreenModeAnalysis` computes forward must-facts, invalidated by uncertain calls/assembly/memory or port activity.
- `PixelRuntimeInlining` exposes `y*320+x` as regular SSA IR while `rt_pset_mode13_offset` remains a byte-write helper. Preserve this behavior until the memory-write semantics are modeled.
- `IrEffects` and `FunctionSummaries` are authoritative for traps, ownership, memory and IO; use MemorySSA, pointer identity, ranges and existing proof infrastructure.
- `MemoryRoutineSpecialization`/`TargetCost` already choose legal, profitable widths for ordinary memory. Extend policy rather than introducing independent CPU-width tables.
- `DosRuntime.Graphics.cs` contains mode-agnostic pixel-loop implementations; never delete the correct fallback.

## Semantic requirements (blocking)

1. Prove the active mode, pixel layout, and video segment at each transformed operation. A `SCREEN` call only establishes mode on the successful normal path; unknown calls, `OUT`, inline assembly, graphics reprogramming and opaque memory operations invalidate relevant facts.
2. Preserve BASIC evaluation order, coordinates, clipping and out-of-bounds behavior, style, palette semantics, page selection, and error behavior. Preserve final graphics-cursor cells even if pixel writes are fused.
3. Distinguish RAM/private backbuffer, proven packed VGA framebuffer, planar VGA, and unknown/IO memory. No default inference from numeric addresses alone.
4. Preserve ordered and width-sensitive writes to unknown device memory. When batching VGA framebuffer accesses, document the stronger target-specific hardware contract and require explicit proof; keep reads, externally visible calls and interrupt-sensitive boundaries as barriers until proven safe.
5. Respect runtime calling conventions, register/segment/flags preservation, CPU ISA gating, direction flag and pointer/address overflow. A 386 `STOSD` remains valid in 16-bit real mode only with the correct operand-size encoding.
6. Keep DOS runtime and portable runtime behavior compatible. Feature-specific IR should not silently leak into targets that cannot represent far pointers.
7. Separate legality from profitability. A wider store is not automatically faster on an 8088, virtualized DOS machine or a late CPU.

## Milestones / agent work packages

### M0 — Establish baseline (prerequisite)

- [x] Inspect SSA optimization, existing pixel specialization, runtime ABI, CPU gates and graphics implementation.
- [x] Consult official Intel STOS ISA and LLVM volatile rules; use published VGA framebuffer addressing as hardware facts.
- [ ] Capture release-mode baseline size, DOSBox runtime/cycle proxy and code shape for single pixels, 320-pixel lines, rectangles, sprite rows, and constant `POKE` sequences.
- [ ] Record target/profile combinations and relevant conformance oracles.

### M1 — Mode 13h span fast path (in this PR)

- [x] Add optimized `rt_linefill` path only when target optimization is enabled, actual BIOS mode is 13h, and the entire normalized rectangle is within 320x200.
- [x] Compute first byte address, set `ES`, repeat color pattern and width once; fill each row using 8086 `REP STOSW` and optional `STOSB`, or 386 `REP STOSD` plus `STOSW`/`STOSB` tails.
- [x] Keep previous fallback for every other case, with one shared final graphics-cursor update.
- [x] Add regression tests across widths 1–5, reversed full-width bottom-edge rectangles, cursor continuation, and 8086/386.
- [ ] Run `dotnet build PB-Compiler.slnx -c Release`, `dotnet test PowerBasic.Compiler.Tests -c Release --filter TestCategory!=Performance`, and DOSBox checks on CI.
- [ ] Confirm generated 8086 image contains no 386-only instructions and compare selected span code shape against the baseline.
- [ ] Benchmark optimized vs faithful selection with matched CPU profiles.

### M2 — Direct pixel access (separate PR)

- [ ] Retire the final opaque `rt_pset_mode13_offset` call at a late target-specific stage, replacing it with an explicit segmented byte write whose **volatile/effect contract** prevents accidental DSE and widening.
- [ ] Combine duplicate segment initialization across safe regions without changing `ES` live-state assumptions.
- [ ] Preserve `POINT` readback and graphics-cursor tracking; prove direct byte output on emulator and physical-target-compatible images.
- [ ] Verify unknown-mode, mode-change, callback, raw port, and inline-assembly barriers.

### M3 — Graphics memory provenance and span IR (separate PR)

- [ ] Define an explicit graphics write/read/span IR operation or equivalent verified semantic descriptor; do **not** initially map it to generic `IrStore`.
- [ ] Add must-fact queries for framebuffer format, page, clip/viewport state, segment and memory region, with CFG join/backedge and effect invalidation.
- [ ] Introduce a typed target-specific lowering path, sharing `RuntimeAbi`, `IrEffects`, `TargetCost`, and existing analyses; avoid a second pass manager.
- [ ] Add verifier, analysis invalidation and negative tests for unknown device memory and raw `POKE`.

### M4 — Store fusion and renderer recognition (separate PRs)

- [ ] Canonicalize direct `PSET`, eligible `POKE`, horizontal `LINE`, contiguous `PAINT` runs and statically parsed `DRAW` sequences to a shared graphics operation while preserving cursor/style/side effects.
- [ ] Pack adjacent constant colors little-endian into word/dword stores only within a certified batchable region, with no observer/barrier between writes.
- [ ] Transform constant-color span runs into fills, clipped or peeled as required; select 8086 words, 386 dwords and byte/word remainders based on cost/ISA.
- [ ] Improve rectangle and sprite row lowering; preserve GET/PUT action semantics (COPY, XOR, PRESET, etc.) and screen page behavior.
- [ ] Ensure DSE removes dead **RAM** writes, never an observable framebuffer/device write without an explicit semantics proof.

### M5 — More runtime functions, post-specialization trimming (separate PRs)

- [ ] Expose small format, array, string, number and memory helpers as IR selectively, with complete ownership, trap, allocation, error and alias contracts.
- [ ] Run SCCP/GVN, specialization, bounded inlining, range checks, LICM, DSE and final DCE in the existing fixed-point framework.
- [ ] Prune leftover runtime sections through `RuntimeTrimmer`; measure code-size deltas.
- [ ] Add comparative tests for hosted backends and dialect-specific semantics.

## Acceptance criteria

- M1 is accepted only when CPU-specific compiled images execute correctly for tested boundaries and widths; the generic path still works for unknown modes and out-of-range coordinates.
- Every transformation carries a concrete safety gate; verifier errors, register/segment corruption, changed graphics cursor, wrong page/color or unexpected runtime error are blockers.
- Assert at least one machine-code/code-size difference that demonstrates the intended optimization, not solely equivalent screen output.
- Require unit tests, DOS CPU emulator tests and the existing Linux/Windows CI. Advisory performance data must not be used as a correctness gate.
- Track each milestone independently with its own tests, documentation and focused PR; never mark unimplemented milestones complete.
- Preserve compatibility with the repository's LGPL-3.0 license. ISA rules, volatile constraints and layout formulas are used as factual specifications; implementation is independently written, not copied from reference implementations.

## Reference material and license decision

- Intel, *80386 Programmer's Reference Manual*, STOS/REP and real-mode operand sizes — ISA specification, factual reference: https://read.seas.harvard.edu/~kohler/class/aosref/i386.pdf
- LLVM, *Language Reference*, volatile memory accesses — semantics reference; LLVM is Apache-2.0 WITH LLVM-exception, no implementation code adopted: https://llvm.org/docs/LangRef.html#volatile-memory-accesses
- VGA Mode 13h layout, A000h, 320*200 bytes — hardware fact: https://media.gdcvault.com/GD_Mag_Archives/GDM_OctNov_1995.pdf
- Existing project `docs/X86-BACKEND.md`, `docs/MIDDLE-END-VISION.md`, `docs/optimizations/O0250-adjacent-store-merging.md` and `docs/optimizations/O0339-memory-routine-by-size.md`.

**Implementation provenance:** clean-room project-native C#/assembler generation. No external source implementation copied or adapted.
