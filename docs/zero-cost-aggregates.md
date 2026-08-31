# Zero-cost aggregate lowering

PowerBASIC source abstractions should not imply runtime machinery merely because the source used a higher-level spelling. The compiler therefore treats generic specialization, type aliases, records, and unions as compile-time structure wherever their language semantics permit it.

## Contract

### Must

- PB/CC 3.6 generic procedures and generic `TYPE`s are monomorphized before IR lowering. Emitted IR contains concrete specializations, not runtime generic dispatch, boxing, type descriptors, or generic dictionaries.
- Type aliases have no runtime representation of their own.
- Non-escaping packed `TYPE` storage may be scalar-replaced when every observed field access names a statically known, independent byte region. The resulting typed scalar slots are then eligible for `mem2reg`, so ordinary field reads and writes can become SSA values.
- `mem2reg` must prove that every direct load/store has the promoted slot's storage type. Direct use alone is insufficient with opaque pointers: a packed `alloca i8, N` can be the offset-zero address of an `INTEGER`, `LONG`, or overlapping `UNION` view.
- Small-array scalar replacement must prove that every access has the array element's storage type. An `alloca i8, N` is not sufficient evidence that the object is a `BYTE[N]`; packed UDTs use the same backing representation.
- Overlapping aggregate regions must remain shared storage. This is required for `UNION` aliasing and type-punning semantics and prevents scalar replacement from inventing independent values for bytes that are intentionally the same bytes.
- Aggregate scalar replacement must refuse storage whose address escapes, whose offset is dynamic or out of bounds, or whose users require whole-object identity.

### Should

- Nested generic `TYPE` instances and member bodies should reach the same concrete aggregate lowering path as handwritten concrete `TYPE`s.
- Scalar replacement should expose concrete fields early enough that the existing SSA/value passes can optimize through the source abstraction without aggregate-specific runtime support.

### Could

- Later passes may remove redundant whole-record copies when ordinary data-flow proof makes the copy unnecessary.
- A future target-aware aggregate analysis may safely scalarize additional pointer-containing layouts once pointer widths and address-space constraints are explicit in the proof.

### Won't

- Eliminate semantically required `BYVAL` record copies merely to satisfy a benchmark-shaped definition of "zero cost".
- Split overlapping union views into independent values.
- Scalarize escaping objects or accesses with unknown offsets.
- Remove runtime operations that implement actual language semantics, such as dynamic-string ownership/conversion, merely because the containing object is a `TYPE`.

## Meaning of zero cost

"Zero cost" here means that the abstraction itself adds no runtime representation or dispatch when a concrete representation is known at compile time. It does not mean that observable language semantics become free. A `BYVAL` aggregate still denotes a copy; a union still denotes aliased storage; and a dynamic string still has its normal runtime semantics.

The optimization boundary is deliberately conservative: when the compiler cannot prove that aggregate regions are independent, it retains the packed byte backing rather than changing aliasing semantics.
