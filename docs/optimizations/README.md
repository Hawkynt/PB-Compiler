# Optimization reference

One page per optimization pass. Each page says what the pass recognizes, which
BASIC it applies to, the code generated **with and without** the optimizer, and
the equivalent BASIC the transformed program behaves like.

- ✅ **implemented** — in the compiler today and covered by the differential
  oracle, a DOSBox execution test, or an assembler unit test.
- 🟡 **partial** — part of the pass ships (and is tested); the page's header
  table says which cases are done and which remain.
- ⬜ **planned** — a roadmap idea. The "after" code on those pages is what the
  pass *would* emit; the compiler does not do it yet.

**One entry, one optimization.** A page describes a single transformation.
Where an ID once covered a family, the family is dissected: the original entry
keeps one member and lists its siblings in a **Split into** row of its header
table. IDs are stable identifiers rather than an ordering — a pass discovered or
added later takes the next free number, so nothing is ever renumbered.

Conventions used on every page:

- Assembly is the instruction sequence the emitter produces for the sample.
  Displacements are shown symbolically (`[bp-4]`, `[i]`) rather than as resolved
  numbers, and runtime helper labels keep their `rt_` names.
- "Without" always means `--no-optimize` (which is also what every historic
  dialect emits by default); "with" means `--optimize`, plus the `$OPTIMIZE
  SPEED`, `$CPU` or `$ERROR` setting named in the page header when one is
  required.
- The safety bar for every implemented pass is the same: byte-identical
  observable output against the genuine oracle compiler for the historic
  dialects. A pass that changes what a program prints is a bug, not an
  optimization.

## O — mid-end and code generation

| | # | Optimization |
|---|---|---|
| ✅ | [O0001](O0001-constant-folding.md) | Constant folding |
| ✅ | [O0002](O0002-dead-code-elimination.md) | Dead-code / dead-store elimination |
| ✅ | [O0003](O0003-common-subexpression-elimination.md) | Common-subexpression elimination |
| ✅ | [O0004](O0004-strength-reduction.md) | Strength reduction |
| ✅ | [O0005](O0005-register-residency.md) | Register residency |
| ✅ | [O0006](O0006-inlining.md) | Procedure inlining |
| ✅ | [O0007](O0007-loop-unrolling.md) | Loop unrolling |
| ✅ | [O0008](O0008-peephole-zero-idiom.md) | Peephole / zero-idiom |
| ✅ | [O0009](O0009-string-temp-economy.md) | String-temp economy |
| ✅ | [O0010](O0010-redundant-statement-elimination.md) | Redundant-statement / `DEF SEG` coalescing |
| ✅ | [O0011](O0011-literal-overlap-pooling.md) | Literal overlap pooling |
| ✅ | [O0012](O0012-float-demotion.md) | Float demotion |
| ✅ | [O0013](O0013-promotion-lowering.md) | Promotion lowering |
| ✅ | [O0014](O0014-tail-call-optimization.md) | Tail-call optimization |
| ✅ | [O0015](O0015-udt-zero-cost.md) | UDT zero-cost copy/compare |
| ✅ | [O0016](O0016-value-fact-analysis.md) | Value-fact analysis |
| ✅ | [O0017](O0017-sccp.md) | SCCP / branch folding |
| ✅ | [O0018](O0018-interprocedural-constant-propagation.md) | Interprocedural constant propagation |
| ✅ | [O0019](O0019-zero-elision.md) | Definite-assignment zero elision |
| ✅ | [O0020](O0020-idiom-replacement.md) | Algorithmic idiom replacement |
| ✅ | [O0021](O0021-register-parameters.md) | Register parameters |
| ✅ | [O0022](O0022-dead-procedure-elimination.md) | Dead procedure elimination |
| ✅ | [O0023](O0023-dead-global-elimination.md) | Dead global / data tree-shaking |
| ✅ | [O0024](O0024-multi-concat.md) | Multi-concat single allocation |
| ✅ | [O0025](O0025-pure-function-folding.md) | Pure-function compile-time evaluation |
| ✅ | [O0026](O0026-auto-vectorization.md) | Auto-vectorization |
| ✅ | [O0027](O0027-copy-propagation.md) | Copy propagation |
| ✅ | [O0028](O0028-loop-invariant-code-motion.md) | Loop-invariant code motion |
| ✅ | [O0029](O0029-select-jump-table.md) | `SELECT CASE` → jump table |
| ✅ | [O0030](O0030-induction-variable-strength-reduction.md) | Induction-variable strength reduction |
| ✅ | [O0031](O0031-branch-fusion.md) | Branch fusion |
| ✅ | [O0032](O0032-short-circuit-conditions.md) | Short-circuit `AND`/`OR`/`NOT` |
| ✅ | [O0033](O0033-constant-store.md) | Constant store as immediate |
| ✅ | [O0034](O0034-redundant-load-elimination.md) | Redundant-load elimination |
| ✅ | [O0035](O0035-jump-relaxation.md) | Jump relaxation & threading |
| ✅ | [O0036](O0036-constant-subscript-folding.md) | Constant subscript folding |
| ✅ | [O0037](O0037-fixed-point-for-counters.md) | Fixed-point FOR counters |
| ✅ | [O0038](O0038-instruction-scheduling.md) | Instruction scheduling |
| ✅ | [O0039](O0039-inline-asm-scheduling.md) | Inline-asm scheduling |
| ✅ | [O0040](O0040-identical-code-folding.md) | Identical-code folding |
| ✅ | [O0041](O0041-branch-layout.md) | Branch layout & loop alignment |
| ✅ | [O0042](O0042-ir-mem2reg.md) | IR: mem2reg |
| ✅ | [O0043](O0043-ir-instcombine.md) | IR: instruction combining |
| ✅ | [O0044](O0044-ir-sccp.md) | IR: SCCP |
| ✅ | [O0045](O0045-ir-correlated-value-propagation.md) | IR: correlated value propagation |
| ✅ | [O0046](O0046-ir-gvn.md) | IR: global value numbering |
| ✅ | [O0047](O0047-ir-redundant-memory.md) | IR: load/store forwarding |
| ✅ | [O0048](O0048-ir-dead-store-elimination.md) | IR: dead-store elimination |
| ✅ | [O0049](O0049-ir-licm.md) | IR: loop-invariant code motion |
| ✅ | [O0050](O0050-ir-dce.md) | IR: dead-code elimination |
| ✅ | [O0051](O0051-ir-if-conversion.md) | IR: if-conversion |
| ✅ | [O0052](O0052-ir-simplify-cfg.md) | IR: CFG simplification |
| ✅ | [O0053](O0053-ir-inliner.md) | IR: function inlining |
| ✅ | [O0054](O0054-ir-global-dce.md) | IR: global DCE |
| ✅ | [O0055](O0055-ir-integer-recovery.md) | IR: integer recovery |
| 🟡 | [O0056](O0056-reciprocal-division.md) | Reciprocal-multiply division |
| ⬜ | [O0057](O0057-storage-narrowing.md) | Storage narrowing |
| ⬜ | [O0058](O0058-386-register-allocation.md) | 386/486 register allocation |
| ⬜ | [O0059](O0059-scalar-replacement.md) | Scalar replacement of aggregates |
| ⬜ | [O0060](O0060-memory-ssa.md) | Memory SSA / alias analysis |
| 🟡 | [O0061](O0061-reassociation.md) | Reassociation |
| 🟡 | [O0062](O0062-loop-restructuring.md) | Loop rotation, IV simplification, fusion |
| ⬜ | [O0063](O0063-duff-unrolling.md) | Duff's-device unrolling |
| ⬜ | [O0064](O0064-lea-fusion.md) | `LEA` multiply-add fusion |
| ⬜ | [O0065](O0065-dead-frame-store-elimination.md) | Dead frame-store elimination |
| ✅ | [O0066](O0066-unrolled-counter-propagation.md) | Unrolled-counter propagation |
| ✅ | [O0067](O0067-if-chain-jump-table.md) | `IF`-chain → jump table |
| 🟡 | [O0068](O0068-array-zero-fill-elision.md) | Array zero-fill elision |
| ⬜ | [O0069](O0069-dead-parameter-elimination.md) | Dead parameters & call-shape cloning |
| ⬜ | [O0070](O0070-leaf-frame-elision.md) | Leaf-frame elision |
| ⬜ | [O0071](O0071-segment-register-allocation.md) | Segment-register allocation |
| ⬜ | [O0072](O0072-register-reassignment.md) | Register reassignment |
| ⬜ | [O0073](O0073-algorithmic-idiom-catalog.md) | Wider idiom catalog |
| ⬜ | [O0074](O0074-wider-vectorization.md) | Wider auto-vectorization |
| ⬜ | [O0075](O0075-silent-fixed-point.md) | Silent fixed-point arithmetic |
| ✅ | [O0076](O0076-algebraic-identities.md) | Algebraic identities & annihilators |
| ✅ | [O0077](O0077-negation-idioms.md) | Negation idioms |
| 🟡 | [O0078](O0078-multiply-decomposition.md) | General multiply decomposition |
| ✅ | [O0079](O0079-shared-divide.md) | Shared divide (quotient + remainder) |
| 🟡 | [O0080](O0080-division-special-cases.md) | Division special cases |
| 🟡 | [O0081](O0081-flag-reuse.md) | Flag reuse / `TEST` for zero compare |
| ✅ | [O0082](O0082-memory-operand-folding.md) | Memory operand folding |
| ⬜ | [O0083](O0083-store-to-load-forwarding.md) | Store-to-load forwarding |
| ⬜ | [O0084](O0084-cross-statement-register-caching.md) | Cross-statement register caching |
| ⬜ | [O0085](O0085-copy-coalescing.md) | Register copy coalescing |
| ⬜ | [O0086](O0086-spill-slot-reuse.md) | Spill-slot reuse |
| ⬜ | [O0087](O0087-rematerialization.md) | Rematerialization |
| ✅ | [O0088](O0088-boolean-materialization-sbb.md) | Branchless truth values |
| ⬜ | [O0089](O0089-extension-elimination.md) | Extension elimination |
| ⬜ | [O0090](O0090-demanded-bits.md) | Demanded bits |
| ⬜ | [O0091](O0091-partial-register-hazards.md) | Partial-register hazards |
| ⬜ | [O0092](O0092-encoding-selection.md) | Encoding selection |
| ⬜ | [O0093](O0093-jump-threading.md) | Jump threading |
| ⬜ | [O0094](O0094-branch-inversion.md) | Branch inversion |
| ⬜ | [O0095](O0095-branch-tail-merging.md) | Branch-tail merging |
| ✅ | [O0096](O0096-condition-combining.md) | Nested condition combining |
| ✅ | [O0097](O0097-repeated-comparison-elimination.md) | Repeated comparison elimination |
| ⬜ | [O0098](O0098-balanced-decision-tree.md) | Balanced decision tree |
| ⬜ | [O0099](O0099-bit-test-dispatch.md) | Bit-test dispatch |
| ⬜ | [O0100](O0100-perfect-hash-dispatch.md) | Perfect-hash dispatch |
| ⬜ | [O0101](O0101-jump-table-compression.md) | Jump-table sharing & compression |
| ⬜ | [O0102](O0102-return-value-forwarding.md) | Return-value forwarding |
| ⬜ | [O0103](O0103-shared-epilogue.md) | Shared epilogue |
| ⬜ | [O0104](O0104-block-placement.md) | Block placement |
| ⬜ | [O0105](O0105-hot-cold-splitting.md) | Hot/cold splitting |
| ⬜ | [O0106](O0106-trace-formation.md) | Trace formation |
| ⬜ | [O0107](O0107-branch-folding-through-phi.md) | Branch folding through phi |
| ⬜ | [O0108](O0108-branchless-select.md) | Branchless select / min / max / abs |
| ⬜ | [O0109](O0109-macro-fusion-placement.md) | Macro-fusion placement |
| ⬜ | [O0110](O0110-general-induction-variables.md) | General induction variables |
| ⬜ | [O0111](O0111-redundant-induction-variables.md) | Redundant IV elimination |
| ✅ | [O0112](O0112-countdown-loop.md) | Countdown loops |
| 🟡 | [O0113](O0113-loop-bounds-hoisted.md) | Loop bounds in registers |
| ⬜ | [O0114](O0114-loop-unswitching.md) | Loop unswitching |
| ⬜ | [O0115](O0115-loop-peeling.md) | Loop peeling |
| ⬜ | [O0116](O0116-loop-guard-hoisting.md) | Loop guard hoisting |
| ⬜ | [O0117](O0117-bounds-check-merging.md) | Bounds-check merging & hoisting |
| ⬜ | [O0118](O0118-loop-dead-store-elimination.md) | Loop dead stores |
| ⬜ | [O0119](O0119-reduction-recognition.md) | Reduction recognition |
| ⬜ | [O0120](O0120-multiple-accumulators.md) | Multiple accumulators |
| ⬜ | [O0121](O0121-reduction-tree-balancing.md) | Reduction tree balancing |
| ⬜ | [O0122](O0122-loop-interchange.md) | Loop interchange |
| ⬜ | [O0123](O0123-loop-distribution.md) | Loop distribution / fission |
| ⬜ | [O0124](O0124-loop-tiling.md) | Loop tiling |
| ⬜ | [O0125](O0125-loop-skewing.md) | Loop skewing |
| ⬜ | [O0126](O0126-unroll-and-jam.md) | Unroll and jam |
| ⬜ | [O0127](O0127-loop-interleaving.md) | Loop interleaving |
| ⬜ | [O0128](O0128-software-pipelining.md) | Software pipelining |
| ⬜ | [O0129](O0129-unroll-factor-cost-model.md) | Unroll factor by cost model |
| ⬜ | [O0130](O0130-trip-count-versioning.md) | Trip-count versioning |
| ⬜ | [O0131](O0131-exact-trip-count.md) | Exact trip count |
| ⬜ | [O0132](O0132-compile-time-loop-evaluation.md) | Compile-time loop evaluation |
| ⬜ | [O0133](O0133-loop-prefix-evaluation.md) | Loop prefix evaluation |
| ⬜ | [O0134](O0134-recurrence-shortening.md) | Recurrence shortening & closed forms |
| ⬜ | [O0135](O0135-loop-phi-constants.md) | Loop-phi constants |
| ⬜ | [O0136](O0136-adjacent-access-merging.md) | Adjacent access merging |
| ⬜ | [O0137](O0137-load-widening.md) | Load widening across iterations |
| ⬜ | [O0138](O0138-overlapping-load-combining.md) | Overlapping loads combined |
| ⬜ | [O0139](O0139-alignment-versioning.md) | Alignment peeling & versioning |
| ⬜ | [O0140](O0140-load-store-motion.md) | Load hoisting & store sinking |
| ⬜ | [O0141](O0141-access-clustering.md) | Access clustering |
| ⬜ | [O0142](O0142-non-temporal-stores.md) | Non-temporal stores |
| ⬜ | [O0143](O0143-slp-vectorization.md) | SLP vectorization |
| ⬜ | [O0144](O0144-interleaved-access-vectorization.md) | Interleaved-access vectorization |
| ⬜ | [O0145](O0145-vector-reduction.md) | Vector reduction |
| ⬜ | [O0146](O0146-vector-tail.md) | Vector tails |
| ⬜ | [O0147](O0147-vector-width-cost-model.md) | Vector width by cost model |
| ⬜ | [O0148](O0148-packed-width-selection.md) | Packed vs widening lanes |
| ⬜ | [O0149](O0149-saturating-pack.md) | Saturating pack recognition |
| ⬜ | [O0150](O0150-vector-compare-select.md) | Vector compare & select |
| ⬜ | [O0151](O0151-gather-scatter.md) | Gather / scatter |
| ⬜ | [O0152](O0152-vector-alias-versioning.md) | Runtime dependence checks |
| ⬜ | [O0153](O0153-swar-arithmetic.md) | SWAR packed arithmetic |
| ⬜ | [O0154](O0154-swar-search.md) | SWAR search idioms |
| ⬜ | [O0155](O0155-bit-plane-transformation.md) | Bit planes / bit slicing |
| ⬜ | [O0156](O0156-path-sensitive-propagation.md) | Path-sensitive propagation |
| ⬜ | [O0157](O0157-relational-range-propagation.md) | Relational ranges |
| ⬜ | [O0158](O0158-interprocedural-range-propagation.md) | Interprocedural ranges |
| ⬜ | [O0159](O0159-return-value-propagation.md) | Return-value propagation |
| ⬜ | [O0160](O0160-call-site-cloning.md) | Call-site cloning |
| ⬜ | [O0161](O0161-function-summaries.md) | Function summaries |
| ⬜ | [O0162](O0162-interprocedural-dead-store.md) | Interprocedural dead stores |
| ⬜ | [O0163](O0163-dead-field-elimination.md) | Dead field elimination |
| ⬜ | [O0164](O0164-partial-evaluation.md) | Partial evaluation |
| ⬜ | [O0165](O0165-readonly-global-propagation.md) | Read-only global propagation |
| ⬜ | [O0166](O0166-dead-call-result-elimination.md) | Dead call results |
| ⬜ | [O0167](O0167-tail-call-fact-propagation.md) | Tail-call fact propagation |
| ⬜ | [O0168](O0168-recursive-argument-evolution.md) | Recursive argument evolution |
| ⬜ | [O0169](O0169-returned-condition-propagation.md) | Returned conditions |
| ⬜ | [O0170](O0170-leaf-register-save-elision.md) | Leaf save/restore elision |
| ⬜ | [O0171](O0171-alias-analysis.md) | Alias analysis |
| ⬜ | [O0172](O0172-loop-dependence-analysis.md) | Loop dependence analysis |
| ⬜ | [O0173](O0173-speculative-load-hoisting.md) | Speculative load hoisting |
| ⬜ | [O0174](O0174-target-cost-models.md) | Per-target cost models |
| ⬜ | [O0175](O0175-critical-path-scheduling.md) | Latency & port scheduling |
| ⬜ | [O0176](O0176-register-pressure-scheduling.md) | Pressure-aware scheduling |
| ⬜ | [O0177](O0177-cycle-estimate-battery.md) | Cycle-estimate assertions (tests) |
| ✅ | [O0178](O0178-empty-string-simplification.md) | Empty-string identities |
| ⬜ | [O0179](O0179-string-self-assignment.md) | String self-assignment |
| ✅ | [O0180](O0180-string-length-caching.md) | `LEN` caching |
| ✅ | [O0181](O0181-empty-string-comparison.md) | Empty-string comparison |
| ⬜ | [O0182](O0182-small-array-scalar-replacement.md) | Small array scalar replacement |

### O — implemented sub-passes (dissected from the entries above)

| | # | Optimization |
|---|---|---|
| ✅ | [O0183](O0183-ssa-dead-store.md) | SSA dead-store elimination |
| ✅ | [O0184](O0184-cse-branch-inheritance.md) | CSE inheritance into dominated branches |
| ✅ | [O0185](O0185-cse-past-merge.md) | CSE retention past a merge |
| ✅ | [O0186](O0186-cse-loop-preheader.md) | CSE reuse through loop preheaders |
| ✅ | [O0187](O0187-redundant-array-load.md) | Redundant array-element load caching |
| ✅ | [O0188](O0188-cse-if-condition.md) | `IF`-condition subexpression caching |
| ✅ | [O0189](O0189-multiply-shift-add-shapes.md) | Multiply by `2^a ± 2^b` |
| ✅ | [O0190](O0190-divide-power-of-two.md) | Integer divide by a power of two |
| ✅ | [O0191](O0191-modulo-power-of-two.md) | Modulo by a power of two |
| ✅ | [O0192](O0192-parity-mask.md) | Parity / zero-test modulo mask |
| ✅ | [O0193](O0193-subscript-shift-scaling.md) | Subscript scaling by shift |
| ✅ | [O0194](O0194-accumulator-residency.md) | Hot accumulator in DI |
| ✅ | [O0195](O0195-nested-counter-residency.md) | Nested FOR counter residency |
| ✅ | [O0196](O0196-do-loop-residency.md) | DO/WHILE loop accumulator residency |
| ✅ | [O0197](O0197-dual-accumulators.md) | Two resident accumulators |
| ✅ | [O0198](O0198-resident-read-modify-write.md) | Resident read-modify-write |
| ✅ | [O0199](O0199-branch-tolerant-residency.md) | Residency across a conditional |
| ✅ | [O0200](O0200-trivial-method-inlining.md) | Trivial TYPE method and property inlining |
| ✅ | [O0201](O0201-inlined-procedure-purge.md) | Fully-inlined procedure purge |
| ✅ | [O0202](O0202-int16-immediate-folding.md) | 16-bit immediate operand folding |
| ✅ | [O0203](O0203-int32-immediate-folding.md) | 32-bit immediate operand folding |
| ✅ | [O0204](O0204-inc-dec-idiom.md) | `INC`/`DEC` for ±1 |
| ✅ | [O0205](O0205-or-self-zero-test.md) | Zero test as `OR reg,reg` |
| ✅ | [O0206](O0206-memory-incr-in-place.md) | In-place memory `INCR`/`DECR` |
| ✅ | [O0207](O0207-self-concat-handle-reuse.md) | Self-concat handle reuse |
| ✅ | [O0208](O0208-inplace-literal-append.md) | In-place literal append |
| ✅ | [O0209](O0209-inplace-variable-append.md) | In-place variable append |
| ✅ | [O0210](O0210-concat-chain-temp-reuse.md) | Concat-chain dead-temp reuse |
| ✅ | [O0211](O0211-console-setter-elimination.md) | Redundant console-setter elimination |
| ✅ | [O0212](O0212-promotion-lowering-32.md) | 32-bit promotion lowering |
| ✅ | [O0213](O0213-cross-procedure-tail-call.md) | Cross-procedure tail call |
| ✅ | [O0214](O0214-udt-compare-widening.md) | Whole-UDT compare widening |
| ✅ | [O0215](O0215-udt-self-copy-elision.md) | UDT self-copy elision |
| ✅ | [O0216](O0216-udt-self-compare-fold.md) | UDT self-compare folding |
| ✅ | [O0217](O0217-bounds-check-elimination.md) | Bounds-check elimination by range |
| ✅ | [O0218](O0218-range-comparison-folding.md) | Range-invariant comparison folding |
| ✅ | [O0219](O0219-overflow-check-elimination.md) | Overflow-check elimination |
| ✅ | [O0220](O0220-divide-guard-elimination.md) | Divide-by-zero guard elimination |
| ✅ | [O0221](O0221-operation-narrowing.md) | 32-bit operation narrowing |
| ✅ | [O0222](O0222-identity-operation-removal.md) | Fact-proven identity removal |
| ✅ | [O0223](O0223-constant-result-folding.md) | Fact-proven constant result |
| ✅ | [O0224](O0224-bounded-multiply-off-fpu.md) | Bounded multiply stays off the FPU |
| ✅ | [O0225](O0225-ssa-construction.md) | SSA construction (CFG, dominators, phi placement) |
| ✅ | [O0226](O0226-proven-constant-reads.md) | Cross-block proven-constant reads |
| ✅ | [O0227](O0227-constant-fill-stosw.md) | Constant array fill → `REP STOSW` |
| ✅ | [O0228](O0228-series-folding.md) | Arithmetic-series folding |
| ✅ | [O0229](O0229-copy-loop-movsw.md) | Array copy loop → `REP MOVSW` |
| ✅ | [O0230](O0230-jump-to-next-removal.md) | Jump-to-next removal |
| ✅ | [O0231](O0231-loop-top-alignment.md) | Hot loop-top alignment |
| ✅ | [O0232](O0232-procedure-entry-alignment.md) | Procedure entry alignment |
| ✅ | [O0233](O0233-hardware-constant-divide.md) | Hardware divide for constant divisors |
| ✅ | [O0234](O0234-quad-bitwise-inline.md) | Inline 64-bit bitwise operations |
| ✅ | [O0235](O0235-shld-shrd-shifts.md) | `SHLD`/`SHRD` 64-bit shifts |
| ✅ | [O0236](O0236-long-shift-rotate-collapse.md) | 32-bit shift/rotate collapse |
| ✅ | [O0237](O0237-movzx-movsx-loads.md) | `MOVZX`/`MOVSX` byte loads |
| ✅ | [O0238](O0238-setcc-relationals.md) | `SETcc` relational results |
| ✅ | [O0239](O0239-stosd-array-zero.md) | `REP STOSD` array zero-fill |
| ✅ | [O0240](O0240-stosd-loop-fill.md) | `REP STOSD` constant loop fill |
| ✅ | [O0241](O0241-dword-string-copy.md) | DWORD-wide string copy |
| ✅ | [O0242](O0242-movsd-block-copy.md) | DWORD block copy for TYPE and `LSET` |

### O — planned sub-passes (dissected from the entries above)

| | # | Optimization |
|---|---|---|
| ⬜ | [O0243](O0243-byte-register-packing.md) | 8-bit sub-register packing |
| ⬜ | [O0244](O0244-microop-selection.md) | Micro-op count selection |
| ⬜ | [O0245](O0245-decode-width-scheduling.md) | Decode-width-aware scheduling |
| ⬜ | [O0246](O0246-move-elimination-aware.md) | Move-elimination-aware allocation |
| ⬜ | [O0247](O0247-jump-table-entry-compression.md) | Jump-table entry compression |
| ⬜ | [O0248](O0248-branchless-minmax.md) | Branchless min/max |
| ✅ | [O0249](O0249-branchless-abs.md) | Branchless absolute value |
| ⬜ | [O0250](O0250-adjacent-store-merging.md) | Adjacent store merging |
| ⬜ | [O0251](O0251-misaligned-versioning.md) | Misaligned access versioning |
| ⬜ | [O0252](O0252-safe-overread-versioning.md) | Safe over-read versioning |
| ⬜ | [O0253](O0253-store-sinking.md) | Store sinking |
| ⬜ | [O0254](O0254-masked-vector-tail.md) | Masked vector tail |
| ⬜ | [O0255](O0255-overlapping-vector-tail.md) | Overlapping final vector |
| ⬜ | [O0256](O0256-vector-blend-select.md) | Vector select / blend |
| ⬜ | [O0257](O0257-vector-minmax.md) | Packed min/max |
| ⬜ | [O0258](O0258-vector-abs.md) | Packed absolute value |
| ⬜ | [O0259](O0259-scatter-stores.md) | Scatter stores |
| ⬜ | [O0260](O0260-escape-analysis.md) | Escape analysis |
| ⬜ | [O0261](O0261-termination-analysis.md) | Termination analysis |
| ⬜ | [O0262](O0262-type-based-alias.md) | Type-based alias analysis |
| ⬜ | [O0263](O0263-allocation-site-alias.md) | Allocation-site alias analysis |
| ⬜ | [O0264](O0264-live-range-splitting.md) | Live-range splitting around calls |
| ⬜ | [O0265](O0265-vector-lane-coalescing.md) | Vector lane register coalescing |
| ✅ | [O0266](O0266-zero-length-intrinsic-folding.md) | Zero-length string intrinsic folding |
| ⬜ | [O0267](O0267-modulo-scheduling.md) | Modulo scheduling |

### O — profile-guided optimization

| | # | Optimization |
|---|---|---|
| ⬜ | [O0268](O0268-profile-collection.md) | Profile collection and representation |
| ⬜ | [O0269](O0269-profile-guided-inlining.md) | Profile-guided inlining |
| ⬜ | [O0270](O0270-value-profile-specialization.md) | Value-profile specialization |
| ⬜ | [O0271](O0271-indirect-call-promotion.md) | Indirect call promotion |
| ⬜ | [O0272](O0272-profile-guided-loop-optimization.md) | Profile-guided loop optimization |
| ⬜ | [O0273](O0273-profile-guided-register-allocation.md) | Profile-guided register allocation |
| ⬜ | [O0274](O0274-profile-guided-code-layout.md) | Profile-guided code layout |
| ⬜ | [O0275](O0275-cold-code-outlining.md) | Cold-code outlining |
| ⬜ | [O0276](O0276-post-link-optimization.md) | Post-link optimization |

### O — whole-program optimization

| | # | Optimization |
|---|---|---|
| ⬜ | [O0277](O0277-link-time-optimization.md) | Link-time optimization |
| ⬜ | [O0278](O0278-global-variable-localization.md) | Global variable localization |
| ⬜ | [O0279](O0279-whole-program-devirtualization.md) | Whole-program devirtualization |
| ⬜ | [O0280](O0280-argument-structure-reduction.md) | Argument structure reduction |
| ⬜ | [O0281](O0281-return-structure-reduction.md) | Return structure reduction |
| ⬜ | [O0282](O0282-internal-calling-convention.md) | Internal calling-convention specialization |
| ⬜ | [O0283](O0283-context-sensitive-cloning.md) | Context-sensitive cloning |
| ⬜ | [O0284](O0284-semantic-function-merging.md) | Semantic function merging |
| ⬜ | [O0285](O0285-constant-data-merging.md) | Program-wide constant data merging |

### O — allocation and ownership

| | # | Optimization |
|---|---|---|
| ⬜ | [O0286](O0286-allocation-elimination.md) | Allocation elimination |
| ⬜ | [O0287](O0287-stack-promotion.md) | Stack promotion |
| ⬜ | [O0288](O0288-allocation-sinking.md) | Allocation sinking |
| ⬜ | [O0289](O0289-allocation-coalescing.md) | Allocation coalescing |
| ⬜ | [O0290](O0290-loop-temporary-reuse.md) | Temporary reuse across loop iterations |
| ⬜ | [O0291](O0291-handle-ownership-elision.md) | Handle ownership elision |
| ⬜ | [O0292](O0292-ownership-batching.md) | Ownership operation batching |
| ⬜ | [O0293](O0293-copy-on-write-elision.md) | Copy-on-write elision |

### O — strings

| | # | Optimization |
|---|---|---|
| ⬜ | [O0294](O0294-string-builder-recognition.md) | String-builder recognition |
| ⬜ | [O0295](O0295-string-result-buffer-forwarding.md) | String result-buffer forwarding |
| ⬜ | [O0296](O0296-string-move-instead-of-copy.md) | String move instead of copy |
| ⬜ | [O0297](O0297-substring-view.md) | Substring as a view |
| ⬜ | [O0298](O0298-string-compare-length-guard.md) | String comparison length guard |
| ⬜ | [O0299](O0299-interned-literal-identity.md) | Interned literal identity comparison |
| ⬜ | [O0300](O0300-ascii-string-specialization.md) | ASCII string specialization |
| ⬜ | [O0301](O0301-encoding-conversion-elimination.md) | Encoding-conversion elimination |
| ⬜ | [O0302](O0302-search-algorithm-selection.md) | Search algorithm selection by pattern |
| ⬜ | [O0303](O0303-formatted-print-specialization.md) | Formatted-print specialization |

### O — speculative optimization

| | # | Optimization |
|---|---|---|
| ⬜ | [O0304](O0304-guarded-specialization.md) | Guarded specialization |
| ⬜ | [O0305](O0305-basic-block-versioning.md) | Basic-block versioning |
| ⬜ | [O0306](O0306-loop-versioning.md) | Loop versioning |
| ⬜ | [O0307](O0307-speculative-devirtualization.md) | Speculative devirtualization |
| ⬜ | [O0308](O0308-speculative-overflow-elimination.md) | Speculative overflow elimination |
| ⬜ | [O0309](O0309-speculative-narrowing.md) | Speculative integer narrowing |
| ⬜ | [O0310](O0310-side-exit-deoptimization.md) | Side exits and deoptimization |

### O — automatic parallelization (hosted back ends only)

| | # | Optimization |
|---|---|---|
| ⬜ | [O0311](O0311-parallel-loop-versioning.md) | Parallel loop versioning |
| ⬜ | [O0312](O0312-parallel-reduction.md) | Parallel reduction |
| ⬜ | [O0313](O0313-parallel-prefix-scan.md) | Parallel prefix scan |
| ⬜ | [O0314](O0314-task-graph-extraction.md) | Task-graph extraction |
| ⬜ | [O0315](O0315-pipeline-parallelization.md) | Pipeline parallelization |
| ⬜ | [O0316](O0316-parallel-loop-collapse.md) | Parallel loop collapse |
| ⬜ | [O0317](O0317-false-sharing-avoidance.md) | False-sharing avoidance |
| ⬜ | [O0318](O0318-numa-partitioning.md) | NUMA-aware partitioning |
| ⬜ | [O0319](O0319-gpu-offload.md) | Automatic GPU offload |

### O — data layout

| | # | Optimization |
|---|---|---|
| ⬜ | [O0320](O0320-aos-to-soa.md) | Array of structs → struct of arrays |
| ⬜ | [O0321](O0321-field-reordering.md) | Field reordering |
| ⬜ | [O0322](O0322-hot-cold-field-splitting.md) | Hot/cold field splitting |
| ⬜ | [O0323](O0323-structure-packing-by-range.md) | Structure packing by range |
| ⬜ | [O0324](O0324-pointer-compression.md) | Pointer compression |
| ⬜ | [O0325](O0325-array-padding-alignment.md) | Array padding for alignment |
| ⬜ | [O0326](O0326-cache-conflict-padding.md) | Cache-conflict padding |
| ⬜ | [O0327](O0327-data-transposition.md) | Data transposition |
| ⬜ | [O0328](O0328-temporary-array-fusion.md) | Temporary array elimination by fusion |
| ⬜ | [O0329](O0329-array-contraction.md) | Array contraction |

### O — library and algorithm substitution

| | # | Optimization |
|---|---|---|
| ⬜ | [O0330](O0330-library-call-recognition.md) | Library call recognition |
| ⬜ | [O0331](O0331-bitset-substitution.md) | Bitset substitution |
| ⬜ | [O0332](O0332-lookup-table-generation.md) | Lookup-table generation |
| ⬜ | [O0333](O0333-lookup-table-elimination.md) | Lookup-table elimination |
| ⬜ | [O0334](O0334-binary-search-recognition.md) | Binary-search recognition |
| ⬜ | [O0335](O0335-perfect-hash-data.md) | Perfect-hash generation for static key sets |
| ⬜ | [O0336](O0336-fsm-compilation.md) | Finite-state-machine compilation |
| ⬜ | [O0337](O0337-polynomial-evaluation.md) | Horner / Estrin polynomial evaluation |
| ⬜ | [O0338](O0338-reciprocal-sequence-reuse.md) | Reciprocal reuse across repeated divisions |
| ⬜ | [O0339](O0339-memory-routine-by-size.md) | Memory routine specialization by size |

### O — floating point

| | # | Optimization |
|---|---|---|
| ⬜ | [O0340](O0340-fma-contraction.md) | Fused multiply-add contraction |
| ⬜ | [O0341](O0341-reciprocal-approximation.md) | Reciprocal approximation with refinement |
| ⬜ | [O0342](O0342-rsqrt-approximation.md) | Reciprocal square-root approximation |
| ⬜ | [O0343](O0343-transcendental-specialization.md) | Transcendental function specialization |
| ⬜ | [O0344](O0344-fp-reassociation.md) | Floating-point reassociation |
| ⬜ | [O0345](O0345-common-denominator-factoring.md) | Common-denominator factoring |
| ⬜ | [O0346](O0346-fp-classification-simplification.md) | Floating-point classification simplification |
| ⬜ | [O0347](O0347-mixed-precision.md) | Mixed-precision computation |
| ⬜ | [O0348](O0348-x87-stack-scheduling.md) | x87 stack scheduling |
| ⬜ | [O0349](O0349-x87-value-retention.md) | x87 value retention across expressions |

### O — checked-operation elimination

| | # | Optimization |
|---|---|---|
| ⬜ | [O0350](O0350-overflow-check-coalescing.md) | Overflow-check coalescing |
| ⬜ | [O0351](O0351-pointer-check-elimination.md) | Pointer and handle check elimination |
| ⬜ | [O0352](O0352-conversion-range-check-elimination.md) | Conversion range-check elimination |
| ⬜ | [O0353](O0353-string-capacity-hoisting.md) | String capacity check hoisting |

### O — machine-level synthesis

| | # | Optimization |
|---|---|---|
| ⬜ | [O0354](O0354-equality-saturation.md) | Equality saturation |
| ⬜ | [O0355](O0355-superoptimized-peepholes.md) | Superoptimizer-generated peepholes |
| ⬜ | [O0356](O0356-machine-combiner.md) | Machine combiner |
| ⬜ | [O0357](O0357-post-ra-peepholes.md) | Post-register-allocation peepholes |
| ⬜ | [O0358](O0358-late-load-store-optimization.md) | Late load/store optimization |
| ⬜ | [O0359](O0359-verified-arithmetic-lowering.md) | Verified arithmetic lowering |

### O — executable layout (the BBT / LEGO class)

| | # | Optimization |
|---|---|---|
| ⬜ | [O0360](O0360-basic-block-fragments.md) | Relocatable basic-block fragments |
| ⬜ | [O0361](O0361-weighted-call-graph-clustering.md) | Weighted call-graph function clustering |
| ⬜ | [O0362](O0362-temporal-function-clustering.md) | Temporal function clustering |
| ⬜ | [O0363](O0363-interprocedural-block-placement.md) | Interprocedural basic-block placement |
| ⬜ | [O0364](O0364-hot-path-block-chaining.md) | Hot-path block chaining |
| ⬜ | [O0365](O0365-maximum-weighted-fallthrough.md) | Maximum weighted fall-through |
| ⬜ | [O0366](O0366-hot-cold-function-splitting.md) | Hot/cold function splitting |
| ⬜ | [O0367](O0367-exception-handler-outlining.md) | Exception-handler outlining |
| ⬜ | [O0368](O0368-unlikely-case-arm-outlining.md) | Unlikely `CASE` arm outlining |
| ⬜ | [O0369](O0369-cold-return-path-outlining.md) | Cold return-path outlining |
| ⬜ | [O0370](O0370-startup-code-clustering.md) | Startup code clustering |
| ⬜ | [O0371](O0371-steady-state-clustering.md) | Steady-state code clustering |
| ⬜ | [O0372](O0372-shutdown-code-isolation.md) | Shutdown code isolation |
| ⬜ | [O0373](O0373-phase-aware-layout.md) | Phase-aware layout |
| ⬜ | [O0374](O0374-hot-page-packing.md) | Hot page packing |
| ⬜ | [O0375](O0375-working-set-minimization.md) | Working-set minimization |
| ⬜ | [O0376](O0376-itlb-aware-placement.md) | Instruction-TLB-aware placement |
| ⬜ | [O0377](O0377-icache-set-aware-placement.md) | Instruction-cache-set-aware placement |
| ⬜ | [O0378](O0378-cache-line-block-placement.md) | Cache-line-aware block placement |
| ⬜ | [O0379](O0379-selective-loop-alignment.md) | Selective loop alignment |
| ⬜ | [O0380](O0380-selective-function-alignment.md) | Selective function alignment |
| ⬜ | [O0381](O0381-branch-distance-minimization.md) | Branch distance minimization |
| ⬜ | [O0382](O0382-post-layout-branch-relaxation.md) | Post-layout branch relaxation |
| ⬜ | [O0383](O0383-call-displacement-optimization.md) | Call displacement optimization |
| ⬜ | [O0384](O0384-branch-island-minimization.md) | Branch island minimization |
| ⬜ | [O0385](O0385-cross-function-fallthrough.md) | Cross-function fall-through |
| ⬜ | [O0386](O0386-caller-callee-colocation.md) | Caller/callee hot-path co-location |
| ⬜ | [O0387](O0387-return-continuation-clustering.md) | Return-continuation clustering |
| ⬜ | [O0388](O0388-tail-call-layout.md) | Tail-call layout |
| ⬜ | [O0389](O0389-hot-trace-layout.md) | Cross-function hot-trace layout |
| ⬜ | [O0390](O0390-superblock-side-entry.md) | Superblock formation by side-entry duplication |
| ⬜ | [O0391](O0391-cold-code-deduplication.md) | Cold-code deduplication |
| ⬜ | [O0392](O0392-hot-code-duplication.md) | Hot-code duplication |
| ⬜ | [O0393](O0393-jump-table-near-dispatch.md) | Jump tables near their dispatch |
| ⬜ | [O0394](O0394-literal-pool-placement.md) | Literal pool placement |
| ⬜ | [O0395](O0395-runtime-helper-clustering.md) | Runtime helper clustering |
| ⬜ | [O0396](O0396-import-thunk-placement.md) | Import thunk placement |
| ⬜ | [O0397](O0397-indirect-target-clustering.md) | Indirect target clustering |
| ⬜ | [O0398](O0398-branch-target-alignment.md) | Branch target alignment |
| ⬜ | [O0399](O0399-profile-weighted-tail-merging.md) | Profile-weighted tail merging |
| ⬜ | [O0400](O0400-page-boundary-outlining.md) | Page-boundary outlining |
| ⬜ | [O0401](O0401-layout-aware-inlining.md) | Layout-aware inlining |
| ⬜ | [O0402](O0402-layout-aware-outlining.md) | Layout-aware outlining |
| ⬜ | [O0403](O0403-scenario-weighted-layout.md) | Scenario-weighted layout |
| ⬜ | [O0404](O0404-stale-profile-matching.md) | Stale profile matching |
| ⬜ | [O0405](O0405-sample-based-reordering.md) | Sample-based binary reordering |
| ⬜ | [O0406](O0406-layout-assertion-battery.md) | Executable-layout assertion battery |

## P — lean output

| | # | Pass |
|---|---|---|
| ✅ | [P0001](P0001-runtime-trimming.md) | Runtime trimming |
| ✅ | [P0002](P0002-data-on-demand.md) | Data on demand |
| ✅ | [P0003](P0003-bss.md) | BSS instead of image bytes |
| ✅ | [P0004](P0004-right-sized-memory.md) | Right-sized memory footprint |
| ✅ | [P0005](P0005-com-output.md) | `.COM`-style output |
| ✅ | [P0006](P0006-header-squeeze.md) | Header & padding squeeze |
| ✅ | [P0007](P0007-trivial-io-lowering.md) | Trivial-I/O lowering |

## R — runtime speed

| | # | Pass |
|---|---|---|
| ✅ | [R0001](R0001-fast-text-output.md) | Fast text output |
| ✅ | [R0002](R0002-fast-graphics.md) | Fast graphics primitives |
| ✅ | [R0003](R0003-string-engine.md) | String engine |
| ✅ | [R0004](R0004-asm-intrinsics.md) | Inline-asm intrinsics |

## C — target-CPU code generation

| | # | Pass |
|---|---|---|
| ✅ | [C0001](C0001-386-codegen.md) | `$CPU 80386` codegen |
| ✅ | [C0002](C0002-486-codegen.md) | `$CPU 80486` gate |
| ✅ | [C0003](C0003-x87-scheduling.md) | x87 scheduling |

## Where the passes run

The principal entries, by stage (the dissected sub-passes run with their
parents):

```
Binder → OptPruner (O0002, O0010) → OptIpcp (O0018) → OptPureFold (O0025)
       → OptReachability / OptDeadGlobals (O0022, O0023)
       → per body: SSA build → SCCP (O0017) → dead store (O0002)
                   CSE / LICM (O0003, O0028) → copy prop (O0027)
       → Emitter (O0001, O0004–O0008, O0011–O0016, O0019–O0021, O0024, O0026,
                  O0029–O0033, O0036, O0037, O0041)
       → Assembler (O0034, O0035, O0038–O0040, C0002, C0003)
       → RuntimeTrimmer (P0001, P0002) → MZ/COM writer (P0003–P0007)
```

The `Ir/Passes/` pipeline (O0042–O0055) is the separate SSA IR mid-end that runs
for `--emit-c` and `--emit-llvm`; see [../IR.md](../IR.md) and
[../BACKENDS.md](../BACKENDS.md).

## Ideas that are already covered

Frequently proposed optimizations that this list does **not** carry as separate
entries, because an implemented pass already does them:

| Idea | Covered by |
|---|---|
| literal string concatenation folded | [O0009](O0009-string-temp-economy.md) |
| array clear as `REP STOSW`, array copy as `REP MOVSW` | [O0020](O0020-idiom-replacement.md) |
| empty pure loop eliminated | [O0020](O0020-idiom-replacement.md) |
| `MOD` by a power of two is a mask | [O0004](O0004-strength-reduction.md) |
| constant condition eliminated, dead arm dropped | [O0017](O0017-sccp.md) |
| sparse conditional constant propagation | [O0017](O0017-sccp.md) (AST), [O0044](O0044-ir-sccp.md) (IR) |
| known-bits and congruence propagation | [O0016](O0016-value-fact-analysis.md) |
| bounds checks removed in a counted loop | [O0016](O0016-value-fact-analysis.md) |
| memory SSA | [O0060](O0060-memory-ssa.md) |
| loop fusion, loop rotation | [O0062](O0062-loop-restructuring.md) |
| leaf function omits its frame | [O0070](O0070-leaf-frame-elision.md) |
| tail call to another function is a jump | [O0014](O0014-tail-call-optimization.md) |
| unused parameter eliminated, constant-argument specialization | [O0069](O0069-dead-parameter-elimination.md) |
| scalar replacement of a non-escaping aggregate | [O0059](O0059-scalar-replacement.md) |
| identical basic blocks folded | [O0040](O0040-identical-code-folding.md) |
| if-conversion to `SETcc` | [C0001](C0001-386-codegen.md), [O0051](O0051-ir-if-conversion.md) |
| loop vectorization | [O0026](O0026-auto-vectorization.md), [O0074](O0074-wider-vectorization.md) |

## One warning about measurement

Instruction-count and image-size assertions lie on an 8086. `IMUL`, `IDIV`,
memory traffic, taken branches and prefetch-queue disruption have radically
different costs, so a three-instruction shift/add sequence can be substantially
better while being *larger*, and a gratuitous taken jump can be worse despite
costing two bytes.

Several planned entries here are therefore only meaningful with a per-target
cost model ([O0174](O0174-target-cost-models.md)) and a battery that can assert
`estimated-cycles-better-than-unoptimized` rather than merely
`smaller-than-unoptimized` ([O0177](O0177-cycle-estimate-battery.md)). What is
*not* negotiable in either case is the correctness bar: byte-identical
observable output against the genuine oracle compiler.
