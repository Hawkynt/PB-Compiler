# O0275 — Cold-code outlining

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end + layout |
| **Related** | [O0105](O0105-hot-cold-splitting.md), [O0367](O0367-exception-handler-outlining.md), [O0402](O0402-layout-aware-outlining.md) |

## The idea

Extract error paths, rare cases and exceptional cleanup **out of** a hot
procedure into a separate cold procedure, so the hot body shrinks — which makes
it cheaper to inline, easier to keep in one cache line or page, and denser to
fetch.

Where [O0105](O0105-hot-cold-splitting.md) *relocates* a block, outlining
*extracts* it into its own callable unit, which is what allows the hot remainder
to be treated as a small function.

## Applies to

```basic
SUB Parse(s$)
  IF LEN(s$) = 0 THEN
    PRINT "empty input"      ' cold: several statements and two literals
    PRINT "usage: ..."
    EXIT SUB
  END IF
  ...                        ' hot
END SUB
```

## What it needs

- A cost model that counts the *hot* body's size, not the procedure's
  ([O0402](O0402-layout-aware-outlining.md)).
- Live-value analysis at the extraction boundary: the outlined fragment needs
  whatever the block read, passed or shared — which is why outlining is easiest
  exactly where the cold path is a dead end (`EXIT SUB`, `END`, an error).
