# O0247 — Jump-table entry compression

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0029](O0029-select-jump-table.md), [O0101](O0101-jump-table-compression.md), [P0006](P0006-header-squeeze.md) |
| **Split from** | [O0101](O0101-jump-table-compression.md) |

## The idea

A word per target is the general case. Two cheaper encodings:

- **byte offsets** when every arm lies within 256 bytes of a base — half the
  table;
- **an index table** into a smaller address table when many cases share the same
  target, which is common for grouped `CASE` arms.

## Applies to

```basic
SELECT CASE k%
  CASE 0 : ... : CASE 1 : ... : CASE 2 : ...    ' short, adjacent arms
END SELECT
```

## What it needs

- The arm span must be known **before** the table is written, which means a
  second layout pass or a conservative estimate plus relaxation, exactly like
  [O0035](O0035-jump-relaxation.md).
- The extra `ADD` to reconstruct the address costs a cycle per dispatch — a size
  optimization, so it belongs under `$OPTIMIZE SIZE`.
