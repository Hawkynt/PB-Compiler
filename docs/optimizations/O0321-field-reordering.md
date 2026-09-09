# O0321 — Field reordering

| | |
|---|---|
| **Status** | ✅ Implemented for private packed scalar records |
| **Stage** | Binder layout |
| **IR** | ✅ `Ir/Passes/DataLayoutTransforms.cs` — infers complete non-overlapping field coverage, estimates access frequency from static references plus exact counted-loop trip counts, and rewrites all affine field offsets without changing record stride |
| **Related** | [O0322](O0322-hot-cold-field-splitting.md), [O0163](O0163-dead-field-elimination.md), [O0320](O0320-aos-to-soa.md) |

## The idea

Place frequently accessed fields **together** — ideally within one cache line or
one 16-bit displacement — and order fields to minimize padding under
`TYPE T ALIGN n`.

PB's default layout is **packed**, so padding is not usually the issue; the
locality of the hot fields is.

## Applies to

```basic
TYPE Entity
  name AS STRING * 64        ' cold: touched on display only
  x AS INTEGER               ' hot: touched every frame
  y AS INTEGER
END TYPE
```

Reordering puts `x` and `y` first, so the hot pair shares a line and sits at
small displacements.

## What it needs

- Access frequency, statically estimated or profiled
  ([O0268](O0268-profile-collection.md)).
- The **layout must not be observable**: `pb36`'s explicit
  `PACKED`/`ALIGN n`/`SIZE n`/`AT` controls exist precisely so a program can pin
  a layout for hardware registers and file formats, and any type using them —
  or written to a file, `FIELD`ed, or shared with an external unit — is off
  limits ([O0260](O0260-escape-analysis.md)).

The IR implementation uses a static execution estimate rather than profile
data. A reference outside a recognized counted loop has weight one; a reference
inside a counted loop is multiplied by its exact static trip count, and nested
recognized loops multiply those weights. Stores retain the existing higher
memory-traffic weight. Equal-hotness fields keep their current packed order, so
the pass does not reshuffle a record when it has no locality evidence for doing
so.

The complete packed record must be represented by analyzable scalar
loads/stores. Any opaque/escaping use makes the pass decline.
