# Graphics runtime optimization: measurement protocol

The first runtime-aware graphics optimizations are reviewed as separate slices (#317, #321). To prevent a change from claiming performance merely because a helper looks shorter, `GraphicsSpanDispatchTests` executes **the same BASIC source** with optimization off and on, on each declared CPU target.

The test CPU now reports `ExecutedInstructions`, the number of emulator instruction-dispatch iterations until halt. That is deterministic for a given program/image. It is **not a cycle count**: a `REP STOSW` on a real 8086 executes 160 individual writes, whereas the interpreter performs one instruction dispatch. Consequently a dispatch reduction is necessary evidence of removed runtime calls and scalar address calculations, but **not proof of a hardware speedup**.

## Advisory batteries

| Shape | Source range | Targets | Observation |
|---|---|---|---|
| Filled rectangle | 320 x 20 pixels (`LINE ... BF`) | $CPU 8086, 80386 | Pixel readback, total instruction-dispatch count |
| Solid horizontal line | 320 x 1 pixels (`LINE`) | $CPU 8086, 80386 | Pixel readback, total instruction-dispatch count |

Both compile optimized and faithful variants; expected output must match. The advisory `Performance` category logs both dispatch counts and requires an improvement; the existing CI marks performance failures advisory. Ordinary semantic tests remain required.

## Future PE stages

1. Record emitted binary size, optimizer time and runtime-section reference count for each variant.
2. Add DOSBox-X fixed-cycle profiling and measured hardware/period-correct emulator timing for 8088, 286 and 386+, with execution environment pinned.
3. Compare instruction layout, register pressure and segment-setup repetition. For short spans, compare fallback-versus-fast threshold; refuse fast expansion if fixed setup dominates.
4. Expand batteries to adjacent-color PSET/POKE groups, optional POKE observer barriers, PAINT spans, constant DRAW geometry and GET/PUT raster kernels.
5. Run the same oracle capture and pixel-equivalence tests for every optimization level and selected CPU generation.

References: Intel STOS semantics (Intel SDM Vol. 2), LLVM volatile memory language reference, project `docs/optimizations/O0177-cycle-estimate-battery.md`. No third-party code reused.
