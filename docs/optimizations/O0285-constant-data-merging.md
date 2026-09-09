# O0285 — Program-wide constant data merging

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Linker / image writer |
| **IR** | ✅ `Ir/Passes/ConstantDataMerging.cs` — module-level exact, containment and prefix/suffix pooling for provably non-escaping read-only byte blobs; cross-unit pooling still awaits [O0277](O0277-link-time-optimization.md) |
| **Gate** | `--optimize` |
| **Related** | [O0011](O0011-literal-overlap-pooling.md), [O0040](O0040-identical-code-folding.md), [P0006](P0006-header-squeeze.md) |

## The idea

[O0011](O0011-literal-overlap-pooling.md) packs *string* literals within one
compilation. The same argument applies to every other block of initialized,
read-only data — `DATA` pools, lookup tables, `TYPE` initializers, format
strings — and, with [O0277](O0277-link-time-optimization.md), across linked
units.

Identical blobs share one copy; a blob contained in another shares its bytes.
Prefix/suffix overlap can likewise store a common run once and rebase the second
blob to a byte offset in the merged storage.

## IR implementation

`ConstantDataMerging` operates over initialized byte globals after lookup-table
formation/elimination has settled. It repeatedly chooses the legal pair with the
largest byte saving, with deterministic symbol-name tie breaks, and handles:

- exact duplicates;
- arbitrary containment;
- prefix/suffix overlap.

A non-zero alias is represented as a byte-offset `IrGep`, so existing typed or
byte-indexed accesses continue from the correct slice without copying data.

The legality rule is intentionally strict. A candidate's complete IR pointer-use
tree must consist only of `IrGep` derivations ending in `IrLoad`. A call,
pointer comparison, cast, store, return, phi/select, detached use, or any other
consumer makes the address observable/escaping and keeps the blob private. If
any function contains inline assembly, the module pass declines entirely because
asm can name a symbol without producing an IR use.

String globals (`.str*`) stay with O0011's dedicated literal representation and
the special `.data` symbol stays private because the current direct/x86 bridge
recognizes it by name. Generic byte constants and generated lookup tables are
eligible.

Alignment is preserved by construction in the current IR: byte blobs carry no
stronger alignment fact and generic LLVM memory operations are emitted with
`align 1`. If the IR later gains explicit per-global alignment, this pass must
include that fact in its placement constraints before accepting non-zero offsets.

## Applies to

```basic
' two units, each with its own copy of the same table
DATA 0, 1, 4, 9, 16, 25, 36, 49
```

The shown cross-unit `DATA` case still requires O0277 plus link/image-level
constant metadata. The implemented middle-end layer performs the same operation
for eligible byte constants visible together in one `IrModule`.

## Remaining link-time work

- Carry read-only/escape/alignment facts into linked-unit constant sections.
- Pool eligible constants across units rather than only inside one `IrModule`.
- Teach the linker/image writer to preserve symbolic aliases and required
  alignment when a constant becomes a slice of another section.
