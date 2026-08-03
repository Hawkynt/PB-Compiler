# O0367 — Exception-handler outlining

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0366](O0366-hot-cold-function-splitting.md), [O0275](O0275-cold-code-outlining.md), [O0105](O0105-hot-cold-splitting.md) |

## The idea

`ON ERROR` handlers, `TRY`/`CATCH`/`FINALLY` bodies, `DEFER` guards, bounds and
overflow error stubs, and assertion failures are **cold by construction**: they
run when something has gone wrong. Placing them on cold pages keeps them out of
the hot instruction stream entirely.

In `pb36` this is a larger win than it sounds, because structured exception
handling lowers to real code interleaved with the protected region
(`docs/PB36.md`).

## What it needs

- Placeable fragments ([O0360](O0360-basic-block-fragments.md)) — and the
  observation that these blocks need **no profile** to be classified: their
  coldness is a property of what they are.
- The handler must remain reachable by the runtime's error dispatch, which
  addresses it by label, so relocation is transparent.
- `RESUME` semantics: a handler that resumes into the protected region must
  still find it, which constrains how far apart the two may be placed.
