# O0304 — Guarded specialization

| | |
|---|---|
| **Status** | ⬜ Planned — the general shape behind the speculative family |
| **Stage** | Mid-end + emitter |
| **Related** | [O0130](O0130-trip-count-versioning.md), [O0152](O0152-vector-alias-versioning.md), [O0270](O0270-value-profile-specialization.md), [O0310](O0310-side-exit-deoptimization.md) |

## The idea

Check a profitable assumption **once**, then execute a version compiled under
it:

```text
if arrays do not overlap and count >= 32 and both aligned then
    vector fast path
else
    general path
end
```

Ahead-of-time compilation can do most of what people think requires a JIT. It
merely needs guards and fallback paths — the assumption does not have to be
provable, only *checkable*.

## Applies to

Every optimization on this list that is currently blocked by an unprovable
precondition: aliasing, alignment, trip count, argument value, indirect target,
overflow-freedom, narrow ranges.

## What it needs

- A **uniform guard mechanism** so that several conditions combine into one test
  block and one fallback, instead of each pass inventing its own versioning.
- A code-size budget, since every specialization is a second copy of the body.
- A rule that the fallback is always **correct on its own**, so a wrong guess
  costs speed and never correctness — which is what makes the whole family safe
  for a compiler whose bar is byte-identical output.
