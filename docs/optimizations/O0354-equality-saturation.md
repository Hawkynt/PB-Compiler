# O0354 — Equality saturation

| | |
|---|---|
| **Status** | ✅ Implemented (bounded local saturation) |
| **Stage** | Mid-end |
| **IR** | `PowerBasic.Compiler/Ir/Passes/EqualitySaturation.cs` |
| **Related** | [O0043](O0043-ir-instcombine.md), [O0061](O0061-reassociation.md), [O0355](O0355-superoptimized-peepholes.md), [O0174](O0174-target-cost-models.md) |

## The idea

Rewrite rules applied in sequence are **order-dependent**: applying one can
destroy the opportunity for another, and the peephole pass has to guess a good
order. Equality saturation avoids that commitment by retaining equivalent forms
long enough to explore competing rewrite paths and then extracting a cheapest
representative.

PB-Compiler implements the useful bounded form of that idea in
`Ir/Passes/EqualitySaturation.cs`: pure integer expression trees are imported
into a local immutable expression graph, rewrites are explored in every subtree
for at most 8 rounds / 256 candidates, and the lowest-operation-count form is
rebuilt only when it is strictly cheaper. Shared SSA expressions are leaves, so
the extractor never prices an instruction as removable while another user still
needs it.

Associative/commutative regions (`ADD`, `MUL`, `AND`, `OR`, `XOR`) are normalized
as whole regions rather than one binary pair at a time: they are flattened,
deterministically sorted, and rebuilt into one canonical association before a
candidate is hashed. That collapses equivalent parenthesizations onto the same
representative and, importantly, exposes repeated terms such as
`(a OR b) OR a` to idempotence instead of normalizing reassociation back into the
shape it started with.

The rule set currently covers wrap-correct integer identities, cancellation,
absorption, associative/commutative canonicalization, and both distributive
factoring directions. Floating point, division/remainder, memory, calls, and
other side-effecting IR are outside the saturation domain.

## Applies to

```basic
DIM a%, b%, c%, r%
r% = (a% AND b%) OR (a% AND c%)
' becomes a% AND (b% OR c%)
```

The AC canonicalization also makes rewrite chains independent of how the source
was parenthesized; for example `(a% OR b%) OR a%` reduces to `a% OR b%`.

## Safety and limits

- Integer constant evaluation uses the IR type width and two's-complement wrap.
- Candidate/round budgets make compile time deterministic and prevent rewrite
  cycles from exploding.
- AC canonicalization removes reassociation permutations from the candidate
  budget instead of exploring them as distinct trees.
- Only single-use nested `IrBinary` nodes are imported; shared nodes remain SSA
  leaves.
- Extraction currently minimizes IR operation count, not a target-specific
  instruction cost. Target-specific combining remains O0356.
- The implementation is self-contained; no e-graph or SMT package is required.
