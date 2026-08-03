# O0368 — Unlikely `CASE` arm outlining

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0029](O0029-select-jump-table.md), [O0366](O0366-hot-cold-function-splitting.md), [O0393](O0393-jump-table-near-dispatch.md) |

## The idea

A `SELECT CASE` with many arms spreads its rare arms through the middle of the
dispatch region. Moving them out compacts the frequently taken arms around the
dispatch — so the table, the range check and the hot arms share a page.

## Applies to

```basic
SELECT CASE key%
  CASE 27 : ...              ' Escape: rare
  CASE 13 : ...              ' Enter: common
  CASE 32 : ...              ' Space: very common
  ...
END SELECT
```

## What it needs

- Per-arm counts ([O0268](O0268-profile-collection.md)); arms that end in an
  error or an `END` are statically classifiable as cold without one.
- The jump table's entries are absolute or base-relative, so moving an arm is
  free — the table just points further away
  ([O0393](O0393-jump-table-near-dispatch.md) keeps the *table* near instead).
