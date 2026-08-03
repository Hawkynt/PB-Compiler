# O0059 — Scalar replacement of aggregates (SROA)

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end, on escape analysis |
| **Related** | [O0015](O0015-udt-zero-cost.md), [O0058](O0058-386-register-allocation.md), [O0011](O0011-literal-overlap-pooling.md) (shared escape analysis) |

## The idea

Escape analysis classifies `TYPE`/`UNION` values. A **non-escaping** one — no
`VARPTR`/`VARSEG`, no whole-value BYREF to an unproven callee, no inline-asm
reference, not in an array or a file record — is not really an aggregate at all:
it is a bundle of independent locals. Decomposing it gives:

- **Scalar replacement** — each field becomes an ordinary local that register
  allocates like any other, so field access costs exactly a scalar access and
  the abstraction is genuinely free;
- **Copy elision** — a whole-UDT assignment between non-escaping values becomes
  per-live-field moves, or nothing at all when the source can simply be
  forwarded. `REP MOVS` block copies remain only for escaping values;
- **Compare lowering** — the PB 3.1 whole-value `=`/`<>` memcmp becomes
  field-wise compares with early-out, which then fold further when fields are
  constant.

## Applies to

```basic
TYPE Vec
  x AS INTEGER
  y AS INTEGER
END TYPE

FUNCTION Dot%(BYVAL ax%, BYVAL ay%, BYVAL bx%, BYVAL by%)
  Dot% = ax% * bx% + ay% * by%
END FUNCTION

DIM a AS Vec, b AS Vec, d%
a.x = 1 : a.y = 2
b = a
d% = Dot%(a.x, a.y, b.x, b.y)
```

## Today

`b = a` is a `REP MOVSW`, and every field access is a memory reference.

## Planned

`a` and `b` decompose into four scalars; `b = a` becomes two register moves (or
nothing, if `a`'s values can be forwarded), and the fields participate in
register allocation like plain locals.

## What it needs

- A field-granular escape analysis shared with the literal packer.
- Register allocation worth allocating into — the 8086 tier has two registers,
  so the pay-off really begins at [O0058](O0058-386-register-allocation.md).
- `UNION`s decompose only when a single member is ever touched; `LSET`, `FIELD`
  and file `GET`/`PUT` force materialization back to the packed layout.
