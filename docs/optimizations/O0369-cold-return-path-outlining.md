# O0369 — Cold return-path outlining

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0103](O0103-shared-epilogue.md), [O0366](O0366-hot-cold-function-splitting.md), [O0104](O0104-block-placement.md) |

## The idea

Early returns — the argument-validation `EXIT SUB`, the "nothing to do" guard,
the failure return — sit at the top of a procedure, right in front of the code
that actually runs. Moving them out leaves the guard as a forward branch to a
distant block and the hot body as the fall-through.

## Applies to

```basic
SUB Process(BYVAL n%)
  IF n% <= 0 THEN EXIT SUB   ' rare, but occupies the entry path
  IF busy% THEN EXIT SUB
  ...                        ' the hot body
END SUB
```

## What it needs

- Placeable fragments ([O0360](O0360-basic-block-fragments.md)) and a coldness
  signal — a profile, or the static heuristic that an early `EXIT` guarded by a
  validity test is unlikely ([O0104](O0104-block-placement.md)).
- The epilogue those paths reach may be shared
  ([O0103](O0103-shared-epilogue.md)), so the outlined block usually contains a
  jump rather than a copy of the teardown.
