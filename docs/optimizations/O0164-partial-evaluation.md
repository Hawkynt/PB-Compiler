# O0164 — Partial evaluation

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program transformation |
| **Related** | [O0025](O0025-pure-function-folding.md), [O0018](O0018-interprocedural-constant-propagation.md), [O0160](O0160-call-site-cloning.md), [O0132](O0132-compile-time-loop-evaluation.md) |

## The idea

[O0025](O0025-pure-function-folding.md) folds a call only when **all** arguments
are constant and the whole body interprets. Partial evaluation is the general
case: specialize the procedure on the arguments that *are* known, execute the
part of the body that depends only on them, and leave the rest as residual code.

```
f(known, unknown)  ->  f_specialized(unknown)   ' with the known part pre-computed
```

## Applies to

```basic
FUNCTION Scale%(BYVAL mode%, BYVAL v%)
  SELECT CASE mode%
    CASE 0 : Scale% = v%
    CASE 1 : Scale% = v% * 2
    CASE 2 : Scale% = v% \ 2
  END SELECT
END FUNCTION

PRINT Scale%(1, n%)          ' mode% known, v% not
```

## Today

[O0018](O0018-interprocedural-constant-propagation.md) specializes the body only
if *every* call passes the same `mode%`; with mixed call sites, nothing happens.

## Planned

A clone specialized on `mode% = 1`, in which the `SELECT` is gone and the body is
one shift:

```basic
FUNCTION Scale_mode1%(BYVAL v%)
  Scale_mode1% = v% * 2
END FUNCTION
```

## What it needs

- **Binding-time analysis** — which values are static, which dynamic — plus the
  cloning machinery of [O0160](O0160-call-site-cloning.md).
- The evaluator from [O0025](O0025-pure-function-folding.md) extended to run
  *partially*: execute static sub-expressions, emit residual code for dynamic
  ones, with the same wrap-exact discipline at every width.
- Termination and code-size budgets, since specialization can cascade.
