namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Some fidelity assertions depend on real 80-bit x87 rounding (verified
/// against the genuine compilers on hardware-equivalent emulation). CI runs
/// classic DOSBox whose portable FPU computes in 64-bit doubles and sets
/// PBC_TEST_REDUCED_FPU - those assertions self-skip there.
/// </summary>
internal static class FpuAssume {
  public static void RequireExtendedPrecision()
    => Assume.That(Environment.GetEnvironmentVariable("PBC_TEST_REDUCED_FPU"), Is.Null,
      "assertion depends on 80-bit x87 rounding; the emulator only provides doubles");
}
