# O0293 — Copy-on-write elision

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end + runtime |
| **Related** | [O0291](O0291-handle-ownership-elision.md), [O0296](O0296-string-move-instead-of-copy.md), [O0297](O0297-substring-view.md) |

## The idea

A copy made only because two names might both be live is unnecessary when
**ownership is provably exclusive**: the source is dead, or neither party ever
mutates the value. Sharing the storage — with a copy only if a mutation actually
happens — removes the copy entirely in the common case.

For arrays passed BYREF and never written, and for strings assigned and then only
read, this is a whole memcpy avoided per operation.

## Applies to

```basic
DIM a$, b$
a$ = LoadFile$("x.txt")      ' a large string
b$ = a$                      ' copied today; nothing ever mutates either
PRINT LEN(b$)
```

## What it needs

- Either a **proof of no mutation** on both sides (static, no runtime support
  needed), or a shared-with-copy-on-write representation in the string manager —
  which is a much larger change, since the descriptor table has no sharing
  concept today.
- Interaction with the in-place append paths
  ([O0208](O0208-inplace-literal-append.md)): a shared block is not safely
  growable in place, so the two optimizations must agree about which handles are
  exclusive.
