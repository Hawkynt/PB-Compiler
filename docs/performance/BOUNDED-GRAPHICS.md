# Bounded-memory graphics acceleration (8086/286/386)

This document separates implemented optimizations from potential ones. All
changes must preserve BASIC's observable pixel order, 16-bit style phase,
graphics cursor, VGA controller state and palette behavior.

## Implemented: scanline PAINT without an extra frame bitmap

The existing PAINT is a **nonrecursive scanline seed fill** with at most 128
entries, each holding a 16-bit coordinate pair. Its 512-byte work stack is
allocated in SS only for the duration of PAINT. Filled framebuffer pixels
are the visited markers, so there is no separate bitmap, no recursion and
no unbounded dynamic allocation. Reaching capacity still raises error 7
and does not silently discard seeds.

- **SCREEN 13:** when the video mode is positively identified, scanline
  fillability probes read a bounds-checked byte from `A000:(320*y+x)`
  directly. The old path would query BIOS video mode for *every* tested
  pixel before performing exactly the same packed read. Only one byte of
  state (`rt_paint_mode13`) is added; non-13 modes retain the old path.
- **SCREEN 12:** a single positive VGA-map/controller certification per
  PAINT call lets scanline probes use the existing four-plane pixel-read
  routine without repeating three hardware register certifications and
  the BIOS mode query per visited pixel. This adds one state byte and
  retains the original routine for noncertified configurations. The
  existing PSET/BF helpers preserve changed VGA controller registers.

Correctness gates include optimized-vs-BIOS tests of borders, nested
regions, equal border/paint colors, screen-edge seeds, and original image
readback. Performance fixtures count dynamic 8086 emulator instructions
and VGA port transactions; these are not hardware cycles.

## Candidate optimizations (not enabled)

| Candidate | Extra RAM bound | Expected gain | Key legality checks |
|---|---:|---|---|
| Mode-12 color-compare row masks | 80 bytes for one 640-pixel scanline | Test 8 fillability pixels per two VRAM byte reads using VGA Read Mode 1 | Save/restore GC 02/05/07 and selectors, comparison semantics, stale data after painting |
| Plane-row mask accumulator | 80 bytes | Batch multiple spans into already-certified one-row masks | Overlap, paint topology, last VGA latch address, sequential observability |
| Byte-run skip over fully blocked spans | At most 80 bytes | Avoid repeated per-pixel plane reads | Must distinguish border, already-painted and unfilled colors |
| Pending-span coalescing | A few stack entries (at most existing 512-byte budget) | Reduce stack pushes on comb/maze shapes | Original error-7 behavior and identical 4-connectivity |
| Adaptive CPU-specific cost selector | No additional framebuffer storage | Select BIOS fallback for short runs and byte-kernel for long runs | Predict actual VGA bus waits and controller setup on 8086/286/386 |

A 640×480 4-bit plane image takes **153,600 bytes** of video storage;
copying it into conventional DOS memory just to fill a region is
unreasonable. Even a 1-bit visited bitmap costs 38,400 bytes and can
change behavior if the source image is modified mid-fill. The proposed
80-byte row mask is preferable, but must be proven worthwhile against
the current per-pixel certified read and controller switching costs
before it is enabled.

## References and licensing

- Microsoft BASIC/QB45 CIRCLE aspect documentation: when aspect exceeds
  one, the argument radius is the Y radius. The earlier parametric arc
  used the opposite interpretation; corrected independently.
- Paul Heckbert, *A Seed Fill Algorithm*, Graphics Gems (1990), and
  publicly described scanline/span coherence. The original Graphics
  Gems code has a separate Graphics Gems EULA, **not** LGPL-3.0.
  No expressive code, comments or control-flow structure has been copied.
- FreeVGA Graphics Controller reference: read mode 1 color compare,
  per-plane read maps, bit masks, and VGA latch semantics.

The implemented fastpaths are original assembly emitted from the
repository's existing runtime helpers and public hardware semantics.
No new build/runtime packages are required. Test-only QEMU/NASM remain
outside the distributed compiler.
