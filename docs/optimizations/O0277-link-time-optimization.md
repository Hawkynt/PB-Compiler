# O0277 — Link-time optimization

| | |
|---|---|
| **Status** | ⬜ Planned (a self-contained main is already whole-program; `$LINK`ed units are not) |
| **Stage** | Linker |
| **Related** | [O0022](O0022-dead-procedure-elimination.md), [O0023](O0023-dead-global-elimination.md), [O0018](O0018-interprocedural-constant-propagation.md), [docs/LINKER.md](../LINKER.md) |

## The idea

Most of this compiler's interprocedural passes are restricted to a
**self-contained main** — they switch off the moment a `$LINK`ed unit or library
is present, because an external caller could exist. Carrying the semantic model
(not just the object code) into `.PBU`/`.PBL` files removes that restriction:
reachability, IPCP, register parameters, dead globals and inlining would work
across the whole program rather than one compilation unit.

The compiler already links the objects itself, so this is not the usual
"convince the linker to run the optimizer" problem — the pieces are in the same
process.

## Applies to

```basic
' UNIT.BAS -> UNIT.PBU
FUNCTION Scale%(BYVAL v%, BYVAL k%)
  Scale% = v% * k%
END FUNCTION

' MAIN.BAS
$LINK "UNIT.PBU"
PRINT Scale%(3, 4)           ' constant-foldable, but not across the unit boundary
```

## What it needs

- The bound `SemanticModel` (or an IR summary) serialized into the unit format
  alongside the code — a format decision, since `.PBU` today carries code,
  fixups and exports.
- A rule for what an **exported** entry point still guarantees, so a unit
  compiled for linking by a foreign tool keeps its ABI.
