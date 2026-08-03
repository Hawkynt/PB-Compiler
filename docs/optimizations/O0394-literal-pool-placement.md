# O0394 — Literal pool placement

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0011](O0011-literal-overlap-pooling.md), [O0393](O0393-jump-table-near-dispatch.md), [O0285](O0285-constant-data-merging.md) |

## The idea

Constants are placed near their **hot consumers**, while cold constants are
pooled and deduplicated more aggressively elsewhere. A hot loop's mask,
multiplier or format string should share a page with the loop; a diagnostic
message should not.

The packing itself already exists ([O0011](O0011-literal-overlap-pooling.md));
what is missing is *where* the pool goes, which today is one place.

## What it needs

- Per-literal use temperature ([O0268](O0268-profile-collection.md)) or the
  static approximation "referenced from inside a loop".
- Splitting the single pool into hot and cold regions — which interacts with
  overlap packing, since two literals can only share bytes if they are in the
  same region. Hot/cold separation therefore costs some sharing, and the trade
  needs measuring.
