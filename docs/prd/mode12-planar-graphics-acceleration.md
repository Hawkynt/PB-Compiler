# PRD — Mode 12 planar graphics acceleration

**Scope:** DOS native x86, `SCREEN 12` (640×480×16, four 1-bit VGA planes). Separate from Mode 13h packed framebuffer optimizations and from arbitrary physical-address store packing.

**Status:** Stage P1 in [PR #334](https://github.com/Hawkynt/PB-Compiler/pull/334). Remaining stages are not yet implemented.

## Observed baseline (current main)

- `rt_pset` and `rt_point` take the BIOS INT 10h AH=0Ch / 0Dh path in every mode except 13h. Their existing Mode 13h path directly addresses `A000:y*320+x`.
- `rt_line`, `rt_linebox`, `rt_circle`, `rt_arc`, `rt_paint` and `rt_gget`/`rt_gput` call the pixel primitives. Their inner loops currently pay for repeated BIOS mode checks and pixel interrupts in Mode 12h.
- Solid horizontal `LINE` and `LINE ... BF` have CPU-width-aware packed Mode 13h paths. Neither was a Mode 12h planar fast path.
- `PRINT` is DOS character I/O by default, not a Mode 12h hardware glyph renderer. The separate `$OPTION VIDEO` (pb36) implementation writes character/attribute pairs to `B800h` in **text** mode; it does not provide a planar graphics font rasterizer.

## VGA semantics that constrain optimization

The Mode 12h aperture is byte-addressed as `80*y + floor(x/8)`, but each byte contains eight pixels **per plane**. Pixel bit `7-(x&7)` selects the dot and the four plane bits together form its 4-bit palette index. A physical `A0000h–BFFFFh` address alone does not prove the active VGA address map, chain/odd-even state, write mode, latch state or plane selection.

- **Write mode 2:** A single 4-bit CPU color byte broadcasts all four color bits into the respective planes. All-plane Map Mask = `0Fh`. Graphics Bit Mask chooses which of the eight pixels to update. Full-byte writes need no preliminary latch read; partial-byte writes **must preload latches** by reading the same destination address.
- **Write mode 0:** Set/Reset and Enable Set/Reset are useful for fonts, patterned spans and per-plane bulk operations. `REP STOSW/STOSD` can be profitable only when the selected VGA write mode and latch behavior justify the effective byte writes.
- **Write mode 1:** Copy latched plane bytes; useful for aligned graphics `GET/PUT`/blits, with particular care about source/destination overlap and clipped/misaligned tails.
- **Write mode 3:** Set/reset color combined with a CPU byte pattern mask can accelerate font glyphs and patterned fills where the current VGA hardware and provenance contract permit it.
- Save and restore all VGA registers and index selectors touched by a self-contained operation. Do not move state changes across `OUT`, `INP`, inline ASM, BIOS calls, interrupts or unknown calls. The sequencer Map Mask, GC registers and latches are observable I/O effects; do not pass them through ordinary scalar DSE/GVN.

## Implementation order

- [x] **P0 — Test oracle:** Provide four actual mode-12 planes in `Cpu8086`; model indexed VGA GC/SQ registers, map masks, write modes, bit masks and read latches; make BIOS pixel services share the same planes as direct `ES:A000` accesses. Add cross-boundary and port restoration tests.
- [x] **P1 — Solid rectangles:** Guard by active BIOS mode 12h, in-bounds 640×480 rectangle and ordinary color 0–15; configure write mode 2 once, use `REP STOSB` for full eight-pixel bytes and latch-preserving masks for edges. Preserve existing cursor semantics and fall back for unknown geometry/colors/modes.
- [ ] **P2 — Horizontal LINE/B/diagonal/CIRCLE/ellipse/DRAW:** Share a graphics-scoped register state contract, batch horizontal spans, cache only unmodified GC/SQ selectors, and retain style-mask rotation and PB cursor behavior. Use known/must mode and port-write invalidation from IR; prefer static state proving to blind runtime assumptions.
- [ ] **P3 — PAINT:** Batch filled contiguous runs by full bytes; preserve flood-fill topology, border color, 128-span stack behavior, returns/traps, and transparent/adjacent colors.
- [ ] **P4 — PSET/POINT:** Specialized planar pixel access with one selected masked RMW or one plane-comparison read; hoist register setup across proven sequences rather than performing per-pixel port switches.
- [ ] **P5 — GET/PUT:** Correct QB-compatible sprite headers and width-per-plane layout, transparent/overlap semantics, and all COPY/PRESET/AND/OR/XOR verbs. Use mode 1 or per-plane blits only when hardware state and alignment allow them.
- [ ] **P6 — Graphics PRINT:** Add a dedicated planar glyph rasterizer only for the relevant BIOS/text semantics, cursor location, colors, font, page and output handle contract. Keep DOS redirected printing and `$OPTION VIDEO` text-mode routing separate.
- [ ] **P7 — Batch scheduling:** Introduce an explicit VGA register-state/effect token or region descriptor in native IR. Reorder only when no intervening read, write, call, `OUT`, ASM, device latch or trap can distinguish the order. Coordinate with independent VRAM-packing directive work, without disabling other optimizations.
- [ ] **P8 — Interoperability/performance:** Cross-check full graphics differential corpus against reference DOSBox/VGABIOS, QEMU/Bochs where independent, malformed/out-of-bounds cases and physical VGA hardware. Record CPU dispatched instructions, instruction bytes, port I/O count and measured 8086/286/386 timings separately.

## Tests and acceptance

- Pixel correctness for all 16 palette values, both byte boundaries and same-byte masks, reversed corners and full 640×480 coverage.
- Preserve unchanged neighbors after masked draws, including an existing non-zero background.
- Ensure lower optimized dispatch count on representative multirow rectangles without claiming an emulator instruction dispatch is a hardware-cycle benchmark.
- Distinguish immutable observed memory from default packed memory: no blind VGA width enlargement by segment number.
- Use `[Category("Performance")]` NUnit advisory tests and normal deterministic regressions. Require Linux/Windows build, PB35/PDS oracles, DOS differential results and real-machine follow-up before merging higher-risk stages.

## References and licensing

- Video7, **V7 VGA Technical Reference Manual** (1988), graphics-controller/sequencer registers: https://www.bitsavers.org/components/video7/700-0242_V7_VGA_Technical_Reference_Manual_Jun88.pdf
- FreeVGA, [Graphics Registers](https://web.eecs.umich.edu/~ryanph/jhu/cs318/fall18/project/specs/freevga/vga/graphreg.htm) and [Sequencer Registers](https://git.koval.net/Imperial-MEng/pintos_22/src/commit/fb268cdef0493b508eaeb44997e29a6da4421/specs/freevga/vga/seqreg.htm)
- Existing PB-Compiler runtime and BIOS differential test vectors. Derive original managed C# implementation from published hardware behavior; do not copy code from different-license emulators. No new external dependencies.
