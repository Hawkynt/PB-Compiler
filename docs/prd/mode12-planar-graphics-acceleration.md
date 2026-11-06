# PRD — Mode 12 planar graphics acceleration

**Scope:** DOS native x86, `SCREEN 12` (640×480×16, four 1-bit VGA planes). Separate from Mode 13h packed framebuffer optimizations and from arbitrary physical-address store packing.

**Status:** P0–P2 plus implementations for P3–P5 and partial P8 in [draft PR #334](https://github.com/Hawkynt/PB-Compiler/pull/334). P3–P5 require successful regression and interoperability validation. P6/P7 and physical hardware benchmarking remain open.

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

## Primitive-major versus plane-major raster scheduling

For a single solid-color rectangle, do **not** independently draw four plane passes by default. VGA Write Mode 2 already broadcasts the four color bits across all four planes on one host byte write. Four passes mean four memory traversals and at least four sequencer-mask selections, for no increase in output fidelity.

For other primitives, select the memory schedule according to **data representation and hardware effects**, not source syntax:

| Operation/representation | Preferred scheduling candidate | Reason and proof obligation |
| --- | --- | --- |
| Solid rectangle or horizontal solid run | One broadcast pass, Write Mode 2 with all four plane masks enabled | Color is uniform; one CPU byte changes eight pixels across four planes, with latches preloaded on clipped boundary bytes |
| Monochrome line, circle, ellipse, glyph or repeated color mask | Rasterize a mask per byte, then one-pass Write Mode 3 with Set/Reset and latch-preserving masking | Avoid repeated per-pixel bitmask port writes; determine exact mask before touching video memory; preserve draw ordering when masks overlap |
| Packed source-memory sprite with 16-color pixels | Pretranspose pixels into four plane byte streams, then issue **four plane-major blits**, each with one Map Mask switch | Four passes can beat per-pixel port switches, especially across large sprites; pretranspose must be paid once or reused |
| Already planar source-memory sprite | Four plane-major runs without transposition, if no faster all-plane transfer applies | Hoist plane selection outside the inner pixel/byte loop; handle source and destination bytes and clipping |
| Video-to-video aligned copy | Consider Write Mode 1 and paired source-latch-read/destination-write | All four source planes transfer in one write; do not select four plane passes unless comparison proves a benefit |
| Multi-color individual points or small irregular geometry | Batch consecutive points by byte/color/plane if the legal dependency graph permits; otherwise use safe per-pixel fallback | Sorting raster writes may reorder writes to overlapping pixels, affect X/Y style or expose intermediate results |
| PAINT or framebuffer-reading primitive | Compute flood-fill topology / read-dependent region **first**; only then schedule its output mask | A plane-major write during discovery could change the very pixels the algorithm still has to inspect |

**Candidate plane-major backend**: take a *complete primitive* or a proven side-effect-free group of primitives, render geometry into a compact per-plane/row byte-mask representation once, then issue `for plane in 0..3` outer and `for row/byte` inner. Select Map Mask only once per plane, not once per pixel. The effective plane bits for color `c` are `(c >> plane) & 1`; zero bits **must be written as zero** for overwriting fills, not merely skipped. Partial-byte outputs require a destination latch read before every masked write. Preserve any overlaps, per-plane logical action (COPY/XOR/AND/OR/PRESET), graphics style phase, cursor state, and exception/interrupt/BIOS effects. A single pass through hardware write mode 2/3 may dominate this plan; choose by measured cost instead of applying plane-major universally.

**Cross-primitive grouping is a stronger transformation than reordering inside one primitive.** It is allowed only when the compiler can prove the same result *and intermediate read observations* (including POINT, GET, PAINT, PEEK, INP/OUT, inline ASM, calls that may draw/read video, and screen/page/mode changes). For example, moving an entire later rectangle before an earlier POINT is illegal even when their final pixels would match. No whole-program grouping is implied merely by consecutive `LINE` syntax. Consider an explicit VGA state/effect analysis and a bounded raster batch as a future IR lowering stage.

**Selection / performance acceptance**: for each candidate count (a) VGA indexed-port writes, (b) bytes and width of VRAM transactions, (c) frame-buffer reads and latch reloads, (d) total CPU instructions per supported tier, (e) temporary mask memory/pretranspose cost, (f) physical display-bus throughput where hardware evidence exists. Reject an alternative that loses to Write Mode 2 in uniform fills or requires unbounded intermediates. Add emulated pixel readback and independent DOS VGA oracle vectors before enabling it.

## Styled LINE mask semantics

The `LINE (x1,y1)-(x2,y2),color,,style` operation interprets `style`
as a 16-bit **phase-rotated stroke pattern**, not a stationary repeating
bitmap anchored at screen coordinate zero. For the existing runtime, bit 15
is consumed at the current raster position; `ROL style,1` happens even if
the pixel is omitted; the first visited point uses the incoming bit 15.
Traversing a line in the reverse direction traverses the style in reverse
*screen-coordinate* order. The final value of `rt_gstyle`, cursor position,
and every skipped pixel are observable via following drawing commands.

**Horizontal Mode 12h initial kernel**: while iterating in exactly the
original traversal order, accumulate selected positions into an 8-bit mask
indexed by `80*y + (x >> 3)`, with bit `7 - (x & 7)`. On a byte boundary
(or at the endpoint) flush the previous mask once using VGA Write Mode 3:
`Set/Reset = color`, `Bit Mask = FFh`, CPU byte = accumulated mask.
Read the destination VRAM byte first to load VGA latches. Empty masks
produce **no write**, which is essential to preserving existing background
colors and device I/O observability. Program Set/Reset, sequencer Map Mask,
rotation and write mode **once per line**, saving/restoring every changed
index selector and data register, rather than switching per pixel.

For a styled `LINE ...,B,style`, each of the four edges inherits the
same style phase via the existing `rt_gstyle` state. Do not reset the pattern
between edges, omit repeated corners, normalize the start/end order or
reorder the four edges unless tests prove all resulting observations match.
`LINE ...,BF` still ignores the style and remains a solid fill.

**Next**: generalize the pixel-selection stream into byte masks for
diagonal/vertical Bresenham runs and for circle/ellipse point sequences
without changing their pixel visiting order. Choose Write Mode 3 for
constant-color masks, Write Mode 2 for full-color uniform spans and
plane-major writes when the source is already planar. Never assume that
vectorizing styled `LINE` means repeating the same 8-bit sequence
independently on each byte: the modulo-16 phase and non-horizontal step
order are part of its semantics.

## Shared ordered-mask raster for diagonals, curves and DRAW

A follow-up on the same PR adds the common `rt_planar_raster_plot` /
`rt_planar_raster_flush` contract for non-horizontal primitives. Each raster
point arrives in its **existing** order, with the same algorithmic decision
variable, style phase and X/Y coordinates as before. The accumulator keeps
at most one pending VGA byte:

1. Compute `byteOffset = 80*y + (x >> 3)` and `bit = 80h >> (x & 7)`.
2. If this is the same byte as the pending one, OR the bit into its mask.
3. On an address change, **read the old destination byte to populate VGA
   latches**, then apply one Write Mode 3 masked-color store before collecting
   the next byte.
4. At primitive exit, flush the final byte and restore the previously saved
   GC mode, bit mask, data rotate, set/reset, sequencer map mask, and selector
   indices.

This gives horizontal runs substantial byte fusion. Vertical and diagonal
lines usually touch another byte every step, so their main gain is eliminating
per-pixel BIOS mode queries and VGA register switches, not magically merging
nonadjacent VRAM stores. Bit-level operations from different bytes are never
reordered. All fast paths are guarded by actual BIOS SCREEN 12, **positive indexed-register
hardware certification** (GC 06h A000 64K map, SEQ 04h odd/even disabled
and chain-four off, GC 05h neither odd/even nor packed shift mode), normal
non-XOR colors and proven in-bounds output. Mere BIOS SCREEN 12 does not
suffice after a user `OUT` reprograms VGA: if any register fails inspection,
the runtime restores both port index selectors and uses the original
BIOS graphics path. Other screen states fall back to the original runtime.

- **LINE / B / DRAW**: the existing Bresenham loop and `rt_gstyle` rotate are
  shared. For Mode 12, all in-bounds directions use the ordered-mask writer;
  horizontal lines continue to use their dedicated simpler byte-mask writer.
  `DRAW` reuses `rt_line` for its strokes rather than inventing a separate
  rasterizer; string parsing, scale/angle state and cursor rules are preserved.
- **CIRCLE**: eight mirror points feed that same ordered-mask stream. Each
  point is clipped to the real 640×480 Mode 12 screen; the earlier 320×200
  clipping error has also been fixed in the nonoptimized fallback.
- **Elliptical arcs / CIRCLE aspect**: a conservative subset uses the cache
  after checking radius and a whole-shape bounding box, with finite nonnegative
  aspect strictly below 1.0 and both angles in [0,8) radians. These constraints
  keep x87 FSIN/FCOS in their defined trigonometric domain and avoid signed
  coordinate overflows before any direct VRAM write. Unproved arcs and aspect
  ratios retain the original x87 and `PSET` path. This is intentionally not a claim to have
  optimized every ellipse.
- **Mode 13h**: horizontal styled strokes use a direct precomputed
  `A000:y*320+x` writer, advancing in the original drawing direction.
  Other in-bounds lines retain Bresenham geometry and style rotation but
  write selected pixels directly to the packed framebuffer with one mode
  check and ES selection per primitive. No blind word/dword widening of
  partially masked output is applied.

Further possible improvements: keep multiple pending bytes for circles
without changing the visit order, hoist the row stride for vertical runs,
specialize constant style words at compile time and compare mode-13
word/dword read-modify-write masks against byte stores on 8086/286/386.
None may cross `POINT`, `GET`, `PAINT`, port accesses or unknown calls.

## Implementation order

- [x] **P0 — Test oracle:** Provide four actual mode-12 planes in `Cpu8086`; model indexed VGA GC/SQ registers, map masks, write modes, bit masks and read latches; make BIOS pixel services share the same planes as direct `ES:A000` accesses. Add cross-boundary and port restoration tests.
- [x] **P1 — Solid rectangles:** Guard by active BIOS mode 12h, in-bounds 640×480 rectangle and ordinary color 0–15; configure write mode 2 once, use `REP STOSB` for full eight-pixel bytes and latch-preserving masks for edges. Preserve existing cursor semantics and fall back for unknown geometry/colors/modes.
- [~] **P2 — Patterned LINE/B/diagonal/CIRCLE/ellipse/DRAW:** In-bounds Mode 12h horizontal lines use the direct Write Mode 3 byte-mask kernel; vertical and diagonal Bresenham lines, `LINE ...,B,style` and `DRAW` strokes now use the ordered-byte accumulator. CIRCLE's eight symmetric mirror points use that same shared accumulator. A conservative in-bounds subset of elliptical arcs/aspects (radius ≤230, aspect in [0,1), angle in [0,8) radians) also participates. Unproved shapes, out-of-bounds and special colors preserve fallback. Remaining work: arbitrary-aspect ellipse specialization, multi-byte buffering where order permits, stricter VGA port-state contracts, and CPU-specific speed experiments.
- [~] **P3 — PAINT:** Mode 12 uses the real 640×480 bounds in the flood-fill search and dispatches proven complete scanline runs through the certified planar BF kernel. Preserve flood-fill topology, border-color semantics, the 128-span stack, error propagation and graphics cursor. New optimized/unoptimized tests are present; CI confirmation remains required.
- [~] **P4 — PSET/POINT:** For certified in-bounds Mode 12 pixels, PSET uses a four-plane Write Mode 2 bitmask read-modify-write and POINT reads the four selected plane bits, restoring modified register values and selectors. Unknown VGA state, special XOR colors and unproved bounds use BIOS. Register setup still occurs per isolated pixel: cross-primitive hoisting requires the P7 effect proof. New regressions require successful CI.
- [~] **P5 — GET/PUT:** Implement four-plane QuickBASIC sprite headers, row/plane ordering, eight-pixel grouping, padded edge bytes, and COPY/PSET, PRESET, AND, OR and XOR. Ordinary cases reconstruct colors in original pixel order. Certified fully aligned GET and PUT use plane-major memory passes and hardware logical operations; remaining cases stay on the original ordered pixel fallback. Verify malformed buffers, overlapping/aliased source, clipping, interoperation with a genuine QuickBASIC oracle and CI before marking complete.
- [~] **P6 — Graphics PRINT:** SCREEN 12/13 handle-1 output now distinguishes a console-like DOS device from redirection before leaving the DOS byte path. For a proven graphics console, BIOS teletype is the semantic fallback so the current foreground color reaches text while BIOS retains control-character/wrap/scroll behavior. With explicit `$OPTION VIDEO`, SCREEN 12 additionally has a certified planar glyph fastpath: page 0, 80 columns, current BIOS graphics font (`INT 10h/1130h BH=1`), printable 7-bit run, no wrap/scroll, valid four-bit foreground and certified planar VGA mapping. It clears the glyph rectangle to attribute 0 and uses VGA Write Mode 3 for foreground masks, restoring all touched GC/SEQ state and selectors. Redirected output, file handles, extended/control characters, unsafe VGA state and wrap/scroll cases fall back. DOSBox pixel-equivalence and redirection regressions are included; VIEW PRINT/WIDTH/custom device-driver behavior and independent VGA-emulator validation remain open.
- [~] **P7 — Primitive/plane-major batch scheduling:** Native `IrStore.IsPackedMode13Write` and `IrLoad.IsDeviceRead` are volatile I/O observations in `IrEffects`. `IrVgaEffects` and per-basic-block `IrVgaRegionAnalysis` distinguish **controller configuration**, **read-latch changes** and **observable pixel traffic** with separate ordering epochs. Unknown port I/O, BIOS display changes, opaque calls/ASM and unmarked far-pointer loads/stores invalidate relevant proofs. Neither an A000h pointer nor a known BIOS mode alone enables wider or reordered stores. Inside already-certified primitive regions, adjacent VGA index/data writes whose complete value is known are now folded to the 8086 `OUT DX,AX` form (AL=index, AH=data), including register restoration, Bit Mask updates and graphics-text Set/Reset. Save-and-read sequences remain byte I/O because the old data value must be observed before modification. **Cross-primitive** register hoisting and plane-major scheduling remain disabled until exact register-value facts are carried through the existing epochs.
- [~] **P8 — Interoperability/performance:** Added deterministic emulated VGA-plane readback, **independently derived register truth-table vectors** for VGA Write Modes 0/1/2/3 and Read Mode 1, and an advisory benchmark fixture recording CPU dispatches, VGA port writes and A000 reads/writes with 8086/80286/80386 CPU targets. These vectors use the public hardware specification, not another path through the same emulator as the expected output. DOSBox/VGABIOS/QEMU/Bochs independent graphics readback, malformed-buffer tests, emitted instruction-byte accounting and physical 8086/286/386 timing remain open. Instruction dispatch is not hardware cycles.

## VGA controller proof and transaction epochs

The native IR now has two different guarantees that MUST NOT be conflated:

- `IrEffects` keeps a certified `IsPackedMode13Write` as a volatile, observable device store and an `IsDeviceRead` as a volatile device observation; ordinary stores are not automatically video stores.
- `IrVgaRegionAnalysis` creates **three separate monotonically increasing tokens per basic block** for controller configuration, read-latch freshness and observed pixel traffic. It never carries a token across CFG edges. An opaque port/BIOS/ASM action invalidates the controller and latch tokens. Pixel reads and writes advance the observation token, and reads advance the latch token.

An unchanged controller epoch permits **considering** setup reuse, not moving or coalescing memory writes. Equal observation and latch epochs still require concrete address, write-mode, byte-width, overlap, alias and trapping proofs. Once an optimization can prove these, register-state hoisting can be introduced one runtime primitive at a time; the analysis itself intentionally emits no new I/O transformations.

## P6 graphics PRINT decision

`rt_print_str` already sends a **contiguous byte run** through DOS `INT 21h/AH=40h`, preserving stdout handle selection, redirection and per-run accounting. Its existing explicit `$OPTION VIDEO` optimization targets `B800h` **text mode**, not Mode 12h.

The explicit `$OPTION VIDEO` shortcut is now additionally guarded by BIOS Mode 3 (80-column color text); other screen modes fall back to the existing DOS path, with screen-mode and handle regressions.

The graphics path now uses a stronger handle-1 proof than the character-device bit alone: DOS IOCTL must report character-device + standard-input + standard-output capability and not NUL. This is evaluated only for SCREEN 12/13; failed proof leaves the general DOS handle path untouched. The direct SCREEN-12 path remains explicit `$OPTION VIDEO` opt-in and intentionally refuses any run requiring wrap/scroll or extended/control-character semantics. The current graphics font pointer and height come from BIOS rather than a baked ROM table. Normal PRINT is opaque: attribute 0 is written as the cell background before foreground glyph masks. Unsupported cases go through BIOS teletype only after the console proof, preserving redirection. P6 remains partial until VIEW PRINT/WIDTH/custom-device behavior and independent emulator/oracle coverage are complete.

## CI and historical DOS oracles

The November 4, 2026 Linux core result reported 2,484 passed tests with zero failures but `Test Run Aborted` and 13 not-executed entries; this is not a green run. Its CI log also contained extensive ALSA startup errors. The historical Turbo BASIC 1.0 differential `DIFF01` artifact showed DOSBox-X repeatedly waiting on a nested `Are you sure to quit anyway now? y/n` prompt and never creating `RESULT.TXT`. It is **an oracle-runner failure**, not an observed pixel output mismatch.

For headless Linux jobs the CI environment now sets `SDL_AUDIODRIVER=dummy` (the documented SDL2 audio hint, also accepted as an SDL3 fallback), reducing noisy ALSA attempts without weakening expected outputs or skipping a failing test. Full CI must still succeed on the new branch head; a dummy audio driver does not prove the historic IDE autotype sequence can always exit.

## Remaining safety and compatibility gates

- **P3/P4/P5 CI:** Latest changes require a new all-green Linux/Windows NUnit and DOS-oracle run. Earlier green P0–P2 runs do not validate later commits. The runtime trimmer must include optimized-only dependencies such as PAINT → `rt_linefill`.
- **P5 buffer fidelity:** Compare actual bytes (including plane ordering, padding, width header and unsigned overflow) against a genuine Microsoft QuickBASIC image rather than relying solely on GET→PUT round trips. Cover unknown/malformed dimensions and overlapping aliases before enabling further optimizations.
- **P6 printing semantics:** The SCREEN-12 direct glyph path is limited to a positively identified console-like handle 1, page 0, 80 columns, printable ASCII, no wrap/scroll and certified planar VGA state. Other graphics-console cases use BIOS teletype; redirected/file output remains DOS handle output. VIEW PRINT/WIDTH and nonstandard DOS character drivers still require dedicated compatibility tests.
- **P7 I/O effects:** Configuration, latch and observation epochs now exist and must remain separate. The indexed-port `OUT DX,AX` folding changes only the instruction spelling of an already ordered index+data transaction; it does not permit moving that transaction across a pixel observation or unknown I/O. Cross-primitive setup reuse still requires exact GC/SEQ value facts, mode/page proof and address/overlap legality. No batching may be inferred from physical segment A000h alone.
- **P8 hardware:** The test CPU is a behavioral model, not independent proof. Verify timing and exact screen bytes using DOSBox-X and Bochs/QEMU or a genuine BIOS implementation, then physical VGA hardware if available, before claiming target-cycle speedups.

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
