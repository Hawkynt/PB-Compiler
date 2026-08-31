# Zero-cost aggregate lowering

PowerBASIC source abstractions should not imply runtime machinery merely because the source used a higher-level spelling. The compiler therefore treats generic specialization, type aliases, records, and unions as compile-time structure wherever their language semantics permit it.

## Contract

### Must

- PB/CC 3.6 generic procedures and generic `TYPE`s are monomorphized before IR lowering. Emitted IR contains concrete specializations, not runtime generic dispatch, boxing, type descriptors, or generic dictionaries.
- Type aliases have no runtime representation of their own.
- Non-escaping packed `TYPE` storage may be scalar-replaced when every observed field access names a statically known, independent byte region. The resulting typed scalar slots are then eligible for `mem2reg`, so ordinary field reads and writes can become SSA values.
- Exact-size whole-record copies may be decomposed into typed scalar loads/stores only when observed field accesses prove a complete, gap-free, non-overlapping byte partition of the copied extent. The scalar operations occur at the original copy point, preserving the copy/snapshot semantics before later SSA/data-flow passes remove values that are genuinely redundant.
- Whole-record `=`/`<>` may replace `rt_mem_compare` only when the same complete-layout proof holds and every region has integer storage. Integer equality is raw bit equality, so conjunction of region equality is equivalent to byte equality. Floating regions stay byte-compared because IEEE numeric equality is not raw-bit equality for signed zero and NaNs.
- `mem2reg` must prove that every direct load/store has the promoted slot's storage type. Direct use alone is insufficient with opaque pointers: a packed `alloca i8, N` can be the offset-zero address of an `INTEGER`, `LONG`, or overlapping `UNION` view.
- Small-array scalar replacement must prove that every access has the array element's storage type. An `alloca i8, N` is not sufficient evidence that the object is a `BYTE[N]`; packed UDTs use the same backing representation.
- Overlapping aggregate regions must remain shared storage. This is required for `UNION` aliasing and type-punning semantics and prevents scalar replacement from inventing independent values for bytes that are intentionally the same bytes.
- Aggregate scalar replacement must refuse storage whose address escapes, whose offset is dynamic or out of bounds, whose layout contains unproved padding/gaps, or whose remaining users require whole-object identity.

### Should

- Nested generic `TYPE` instances and member bodies should reach the same concrete aggregate lowering path as handwritten concrete `TYPE`s.
- Scalar replacement should expose concrete fields early enough that the existing SSA/value passes can optimize through the source abstraction without aggregate-specific runtime support.
- BYVAL copies whose complete scalar snapshot is observable only through independent fields should become entry-point scalar loads and SSA values rather than retaining a block-copy temporary.

### Could

- A future raw-bit cast representation may scalarize floating record equality without changing byte semantics.
- Field-granular liveness may decompose copies even when some record bytes have no typed observation, provided it proves those bytes are dead and cannot later be observed through a whole-object operation or escape.
- A future target-aware aggregate analysis may safely scalarize additional pointer-containing layouts once pointer widths and address-space constraints are explicit in the proof.

### Won't

- Eliminate semantically required `BYVAL` snapshot/copy behavior merely to satisfy a benchmark-shaped definition of "zero cost". The block copy may disappear only when equivalent scalar data flow preserves the same snapshot.
- Replace bytewise whole-record equality with language-level numeric field equality where the two disagree.
- Split overlapping union views into independent values.
- Scalarize escaping objects or accesses with unknown offsets.
- Remove runtime operations that implement actual language semantics, such as dynamic-string ownership/conversion, merely because the containing object is a `TYPE`.

## Meaning of zero cost

"Zero cost" here means that the abstraction itself adds no runtime representation or dispatch when a concrete representation is known at compile time. It does not mean that observable language semantics become free. A `BYVAL` aggregate still denotes a snapshot/copy, a union still denotes aliased storage, and a dynamic string still has its normal runtime semantics.

A whole-record operation can therefore disappear from final optimized IR without disappearing semantically. For a proven complete scalar layout, `llvm.memcpy` becomes loads at the copy point plus stores into independent fields; `ScalarReplaceAggregates` exposes those fields and `mem2reg` turns the destination into SSA. If later value propagation removes the stores, it is because the scalar data flow already carries the same copied values.

The optimization boundary is deliberately conservative: when the compiler cannot prove that aggregate regions are independent and cover every observable byte, it retains the packed byte backing and original whole-object operation rather than changing aliasing or byte-comparison semantics.
