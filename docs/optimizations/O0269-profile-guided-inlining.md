# O0269 — Profile-guided inlining

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program |
| **Related** | [O0006](O0006-inlining.md), [O0268](O0268-profile-collection.md), [O0401](O0401-layout-aware-inlining.md) |

## The idea

Inline **hot** calls aggressively and leave cold ones alone. The static gate
today is structural — a small leaf body, no error handling
([O0006](O0006-inlining.md)) — which is safe but blind: it inlines a helper
called once at startup and declines one called a million times in a loop because
its body is two statements too long.

With edge counts the budget follows the payoff: a hot call site earns a much
larger inline budget, and a cold one earns none.

## Applies to

```basic
FUNCTION Pixel%(BYVAL x%, BYVAL y%)      ' called 64 000 times: inline
  ...
END FUNCTION

SUB ShowHelp                              ' called once: never inline
  ...
END SUB
```

## What it needs

- [O0268](O0268-profile-collection.md) for the call-edge counts.
- A cost function combining call frequency, callee size and the **code growth**
  it causes — which on a paged or cached target also has a layout cost
  ([O0401](O0401-layout-aware-inlining.md)).
- All of [O0006](O0006-inlining.md)'s correctness gates still apply; the profile
  changes the *budget*, never the legality.
