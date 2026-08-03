# O0404 — Stale profile matching

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Profile ingestion |
| **Related** | [O0268](O0268-profile-collection.md), [O0405](O0405-sample-based-reordering.md), [O0360](O0360-basic-block-fragments.md) |

## The idea

A profile is collected from one build and used by the next. In between, the
source changes. Without a matching strategy the profile becomes worthless after
the first edit — which is the practical reason PGO goes unused in most projects
that adopt it.

Mapping counts from an older build onto a changed one requires **stable
identities**: a procedure name plus a structural block index survives an edit
elsewhere in the file, where a byte offset does not.

## What it needs

- Structural IDs assigned at code generation and preserved through every pass
  ([O0360](O0360-basic-block-fragments.md)) — the same requirement the layout
  family has.
- A matching policy for the blocks that genuinely changed: inherit from the
  enclosing procedure, or fall back to the static estimate
  ([O0104](O0104-block-placement.md)) rather than to zero, since "no data" and
  "never executed" are very different claims.
- A staleness report, so a profile that no longer matches is visible rather than
  silently degrading the build.
