# O0353 — String capacity check hoisting

| | |
|---|---|
| **Status** | ✅ Implemented (exact-trip side-effect-free builders) |
| **Stage** | Mid-end + runtime |
| **IR** | `PowerBasic.Compiler/Ir/Passes/StringCapacityHoisting.cs` |
| **Related** | [O0294](O0294-string-builder-recognition.md), [O0208](O0208-inplace-literal-append.md), [O0292](O0292-ownership-batching.md) |

## The idea

Every append checks whether the block can grow — the topmost-block test and the
`$STRING` cap check in `rt_strcatlit`/`rt_strcatvar`. When the final size is
known or boundable in advance, doing the allocation decision once before the loop removes the
per-append check.

## Applies to

```basic
DIM i%, out$
FOR i% = 1 TO 1000
  out$ = out$ + "x"          ' 1 000 capacity checks for a known 1 000 bytes
NEXT
```

## IR implementation

The first design implied adding spare capacity to every runtime string block. That is unnecessary for
the exact counted-builder shape the IR can prove completely. If a loop has a compile-time trip count,
contains exactly one append, has no observable work between appends, and the appended value is a
literal or loop-invariant string, then

```text
out = out + piece    repeated N times
```

is equivalently built as one `rt_str_repeat(N, piece)` suffix followed by one `rt_str_concat(out,
suffix)` in the preheader. The loop carries that final handle unchanged. The existing runtime therefore
performs the length/$STRING/heap decision once instead of once per iteration, and no new ABI or capacity
field is required.

The pass reuses the shared `CountedLoop` matcher for the trip-count proof. That matters for narrow
integer induction variables: the proof models the counter at its actual machine width, including wrap,
instead of applying an unbounded-integer formula that can silently produce the wrong repeat count.
O0353 then adds its own stricter straight-line-builder requirement on top of that shared loop model.

The matcher rejects early exits, extra calls, loads/stores, trapping arithmetic, multiple appends, a
loop-variant source, or any other observation of the partially built string. The safety scan covers the
whole natural-loop region, including the header, because prebuilding in the preheader also reorders the
allocation relative to header work. Those are precisely the cases where prebuilding the final value
would be visible. More general builders can still justify a future explicit capacity representation;
this port does not pretend those cases are solved.

## What it needs

- An exact, machine-width-correct trip count and a known or loop-invariant appended value. The current
  matcher recognizes the canonical FOR-shaped induction variable through the shared `CountedLoop`
  analysis; broader trip-count analysis can widen applicability later.
- Ownership-normalized append calls from [O0208](O0208-inplace-literal-append.md), which is why this
  module pass runs immediately after `StringAppendInPlace`.
- The transformation must be unobservable. It therefore runs only on side-effect-free, no-early-exit
  loops and leaves every other builder untouched.
