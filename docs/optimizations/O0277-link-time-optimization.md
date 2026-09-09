# O0277 — Link-time optimization

| | |
|---|---|
| **Status** | 🟡 Partial — `Ir/IrModuleLinker.cs` thin-links separately lowered modules so the whole-program passes work across former unit boundaries; persisting the IR in `.PBU`/`.PBL` and feeding it from `$LINK` is still open |
| **Stage** | Linker |
| **IR** | ✅ `Ir/IrModuleLinker.cs` — thin-links separately lowered `IrModule`s before the middle-end: BASIC declarations/definitions resolve to one symbol, module-private globals remain distinct, bodies are deep-cloned through `IrCloner`, and signature/duplicate-definition/runtime-dialect conflicts are rejected. The resulting module is an ordinary whole-program IR, so the existing IPCP, inliner, read-only/global localization and `GlobalDce` passes work across former unit boundaries. `IrModuleLinkerTests` covers cross-module constant propagation + dead-code removal, global isolation and malformed links. Persisting the IR/summary in `.PBU`/`.PBL` and feeding it from `$LINK` is still the linker-format half of O0277. |
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

The IR middle-end now has the missing **thin-link primitive**: once separately
lowered modules are available in memory, `IrModuleLinker` resolves their function
symbols into one canonical graph and clones the bodies/globals into a fresh,
verified `IrModule`. No optimization is duplicated in the linker — the normal
module passes simply see a whole program and become inter-module automatically.
The remaining work is persistence/plumbing: a `.PBU`/`.PBL` still carries only
native code, data, fixups and symbols, so a normal `$LINK` compile cannot yet
reconstruct those IR modules from disk.

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

- The bound `SemanticModel` or, preferably now that the target-neutral middle-end exists,
  a serialized `IrModule`/IR summary in the unit format alongside code, fixups and exports.
  `IrModuleLinker` is the consumer once those modules have been recovered.
- A rule for what an **exported** entry point still guarantees, so a unit
  compiled for linking by a foreign tool keeps its ABI.
