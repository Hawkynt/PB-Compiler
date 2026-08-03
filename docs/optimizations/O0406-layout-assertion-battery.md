# O0406 — Executable-layout assertion battery

| | |
|---|---|
| **Status** | ⬜ Planned (test infrastructure, not a compiler pass) |
| **Stage** | Test architecture |
| **Related** | [O0177](O0177-cycle-estimate-battery.md), [O0374](O0374-hot-page-packing.md), [O0375](O0375-working-set-minimization.md) |

## Why it belongs in this list

The existing battery model — compile one `NOINLINE SUB`, inspect its bytes —
cannot express a single claim in the layout family. "These two procedures are
adjacent" and "the hot set fits two pages" are properties of the **whole image**,
not of one procedure's instruction sequence.

Without assertions for them, every layout optimization would be unfalsifiable.

## The shape

A separate `LAYOUT.BAS` suite with profile inputs and image-level assertions:

```basic
' @scenario FrequentlyPairedFunctionsAreAdjacent
' @profile   MainToParse 100000
' @profile   ParseToDecode 95000
' @profile   ParseToError 3
' @assert    adjacent-functions Parse Decode
' @assert    cold-section ParseError
' @assert    hot-pages-at-most 2
' @assert    weighted-fallthrough-ratio-at-least 0.90
' @assert    weighted-branch-distance-less-than-unoptimized
' @assert    startup-pages-less-than-unoptimized
```

## The metrics worth reporting

weighted fall-through ratio · weighted branch distance · hot cache lines
touched · hot pages touched · startup working-set pages · estimated
instruction-cache misses · estimated iTLB misses · hot-to-cold transfers ·
short-versus-long branch count · padding bytes inside the hot region.

## What it needs

- The image map the compiler already produces for `--list` (`Emit/Listing.cs`),
  extended with block boundaries and temperatures.
- A profile input format ([O0268](O0268-profile-collection.md)) and a replay
  that computes the metrics from a trace plus a layout.
- The same separation of concerns as
  [O0177](O0177-cycle-estimate-battery.md): these metrics judge **quality**; the
  differential oracle judges **correctness**, and neither substitutes for the
  other.
