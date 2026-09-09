/* ==========================================================================
 * pbc_parallel.c - opt-in hosted parallel-loop runtime for O0311.
 *
 * Build this file with OpenMP enabled and link it beside pbc_rt.c when code was
 * emitted with --parallel-loops, for example:
 *
 *   cc -std=c99 -O2 -fopenmp -I runtime -o prog prog.c \
 *      runtime/pbc_rt.c runtime/pbc_parallel.c -lm
 *
 * Without OpenMP the file still builds, but rt_parallel_should_run() always
 * selects the compiler-retained sequential version of the loop.
 * ========================================================================== */
#include <stdarg.h>
#include <stdint.h>

#ifdef _OPENMP
#include <omp.h>
#endif

#define PBC_PARALLEL_MIN_TRIPS 1024

int32_t rt_parallel_should_run(int64_t trips) {
#ifdef _OPENMP
  return trips >= PBC_PARALLEL_MIN_TRIPS && omp_get_max_threads() > 1;
#else
  (void)trips;
  return 0;
#endif
}

void rt_parallel_for(int64_t first, int64_t step, int64_t trips, ...) {
  va_list args;
  void (*body)(int64_t);
  int64_t ordinal;

  va_start(args, trips);
  body = va_arg(args, void (*)(int64_t));
  va_end(args);

#ifdef _OPENMP
#pragma omp parallel for schedule(static)
#endif
  for (ordinal = 0; ordinal < trips; ++ordinal)
    body(first + ordinal * step);
}
