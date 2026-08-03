# P0004 — Right-sized memory footprint

| | |
|---|---|
| **Status** | ✅ Implemented (demand-driven heap segments, MinAlloc from actual use) |
| **Stage** | MZ image layout |
| **Source** | `Emit/MzExeWriter.cs`, the runtime's segment setup |
| **Gate** | `--optimize` |
| **Related** | [P0003](P0003-bss.md), [P0001](P0001-runtime-trimming.md) |

## What it is

The classic PB layout reserves a 64 KiB main segment **plus** two more 64 KiB
segments for the string and array heaps — always, whether the program uses them
or not. That is ~192 KiB resident for a hello world, on a machine with 640 KiB.

The layout becomes demand-driven instead: no string usage, no string segment; no
dynamic arrays, no array segment; `$STACK` honored downward; and `MinAlloc`
computed from the program's actual use rather than a fixed worst case.

## Sample

```basic
PRINT "Hello, World!"
```

## Without the optimizer

```
Resident: ~192 KiB
  64 KiB  main segment (code + data + stack)
  64 KiB  string heap        (never touched)
  64 KiB  array heap         (never touched)
```

## With the optimizer

```
Resident: 64 KiB
  the single main segment
```

For the [P0007](P0007-trivial-io-lowering.md) fast path the image is a 25-byte
raw file whose entire footprint is one paragraph plus the DOS PSP.

## Equivalent BASIC

Unchanged. A program that *does* use strings still gets its heap — the
difference is that the reservation follows the program instead of the
worst case.

## Why it is safe

A heap segment is omitted only when nothing in the trimmed image can allocate
from it, which is the same reachability fact [P0001](P0001-runtime-trimming.md)
already establishes: no string runtime linked ⇒ no string heap needed. `MinAlloc`
is computed from the summed BSS, stack and heap requirements, so the loader
still guarantees every byte the program can touch.
