namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// What code generation does only when an instruction is not natively supported by the selected
/// hardware target. Native support always wins: ERROR is a "no fallback allowed" policy, not an
/// instruction blacklist. NATIVE is the deliberate escape hatch that emits an unsupported encoding;
/// EMULATE deliberately avoids it and uses compiler/runtime lowering.
/// </summary>
public enum IsaFallbackMode : byte {
  /// <summary>Use native code when legal; otherwise use the compiler's normal exact fallback or diagnostic.</summary>
  Auto,
  /// <summary>Emit the native instruction even when the declared target does not advertise it.</summary>
  Native,
  /// <summary>Lower to an architecture-independent compiler/runtime implementation when native support is absent (or when explicitly forced for testing).</summary>
  Emulate,
  /// <summary>When native support is absent, reject instead of synthesizing a fallback.</summary>
  Error,
}

/// <summary>
/// Per-compilation ISA policy. Rules are case-insensitive and may name an exact mnemonic, an ISA
/// feature/family (GP32, 486, P6, MMX, SSE*, AVX*, ...), SIMD, X87, or DEFAULT. Resolution is most
/// specific first: mnemonic, exact ISA, broader family, DEFAULT.
/// </summary>
public sealed class RuntimeIsaPolicy {
  private readonly Dictionary<string, IsaFallbackMode> _rules = new(StringComparer.OrdinalIgnoreCase);

  public static RuntimeIsaPolicy Default { get; } = new();

  public RuntimeIsaPolicy Set(string key, IsaFallbackMode mode) {
    ArgumentException.ThrowIfNullOrWhiteSpace(key);
    this._rules[NormalizeKey(key)] = mode;
    return this;
  }

  public bool TryGet(string key, out IsaFallbackMode mode) => this._rules.TryGetValue(NormalizeKey(key), out mode);

  /// <summary>Resolves a non-x87 instruction policy.</summary>
  public IsaFallbackMode Resolve(string mnemonic, RuntimeCpuFeatures required) {
    if (this.TryGet(mnemonic, out var mode))
      return mode;

    foreach (var key in FeatureKeys(required))
      if (this.TryGet(key, out mode))
        return mode;

    if (IsSimd(required) && this.TryGet("SIMD", out mode))
      return mode;
    return this.TryGet("DEFAULT", out mode) ? mode : IsaFallbackMode.Auto;
  }

  /// <summary>Resolves x87 independently because it has PB-compatible $FLOAT policy spellings in addition to $ISA.</summary>
  public IsaFallbackMode ResolveX87(string mnemonic) {
    if (this.TryGet(mnemonic, out var mode))
      return mode;
    if (this.TryGet("X87", out mode))
      return mode;
    if (this.TryGet("FPU", out mode))
      return mode;
    return this.TryGet("DEFAULT", out mode) ? mode : IsaFallbackMode.Auto;
  }

  public static bool TryParseMode(string? text, out IsaFallbackMode mode) {
    var normalized = NormalizeKey(text ?? string.Empty);
    mode = normalized switch {
      "AUTO" or "DEFAULT" => IsaFallbackMode.Auto,
      "NATIVE" or "HARDWARE" or "HW" => IsaFallbackMode.Native,
      "EMULATE" or "EMULATED" or "SOFTWARE" or "SOFT" => IsaFallbackMode.Emulate,
      "ERROR" or "THROW" or "FAIL" => IsaFallbackMode.Error,
      _ => (IsaFallbackMode)byte.MaxValue,
    };
    return mode != (IsaFallbackMode)byte.MaxValue;
  }

  public static string NormalizeKey(string key) => key.Trim()
    .Replace("-", string.Empty, StringComparison.Ordinal)
    .Replace(".", string.Empty, StringComparison.Ordinal)
    .Replace("_", string.Empty, StringComparison.Ordinal)
    .ToUpperInvariant();

  private static IEnumerable<string> FeatureKeys(RuntimeCpuFeatures feature) {
    // Exact names first. Several aliases intentionally collapse onto one switchable family name.
    if ((feature & RuntimeCpuFeatures.Avx512Vl) != 0) yield return "AVX512VL";
    if ((feature & RuntimeCpuFeatures.Avx512Bw) != 0) yield return "AVX512BW";
    if ((feature & RuntimeCpuFeatures.Avx512Dq) != 0) yield return "AVX512DQ";
    if ((feature & RuntimeCpuFeatures.Avx512F) != 0) { yield return "AVX512F"; yield return "AVX512"; }
    if ((feature & RuntimeCpuFeatures.Bmi2) != 0) yield return "BMI2";
    if ((feature & RuntimeCpuFeatures.Bmi1) != 0) { yield return "BMI1"; yield return "BMI"; }
    if ((feature & RuntimeCpuFeatures.Fma) != 0) yield return "FMA";
    if ((feature & RuntimeCpuFeatures.Avx2) != 0) yield return "AVX2";
    if ((feature & RuntimeCpuFeatures.Avx) != 0) yield return "AVX";
    if ((feature & RuntimeCpuFeatures.Pclmulqdq) != 0) { yield return "PCLMULQDQ"; yield return "PCLMUL"; }
    if ((feature & RuntimeCpuFeatures.Aes) != 0) { yield return "AES"; yield return "AESNI"; }
    if ((feature & RuntimeCpuFeatures.Popcnt) != 0) yield return "POPCNT";
    if ((feature & RuntimeCpuFeatures.Sse42) != 0) yield return "SSE42";
    if ((feature & RuntimeCpuFeatures.Sse41) != 0) yield return "SSE41";
    if ((feature & RuntimeCpuFeatures.Ssse3) != 0) yield return "SSSE3";
    if ((feature & RuntimeCpuFeatures.Sse3) != 0) yield return "SSE3";
    if ((feature & RuntimeCpuFeatures.Sse2) != 0) yield return "SSE2";
    if ((feature & RuntimeCpuFeatures.Sse) != 0) yield return "SSE";
    if ((feature & RuntimeCpuFeatures.Mmx) != 0) yield return "MMX";
    if ((feature & RuntimeCpuFeatures.P6) != 0) { yield return "P6"; yield return "686"; }
    if ((feature & RuntimeCpuFeatures.I486) != 0) { yield return "486"; yield return "I486"; }
    if ((feature & RuntimeCpuFeatures.GeneralPurpose32) != 0) { yield return "GP32"; yield return "386"; yield return "I386"; }
  }

  private static bool IsSimd(RuntimeCpuFeatures feature) => (feature & (
      RuntimeCpuFeatures.Mmx | RuntimeCpuFeatures.Sse | RuntimeCpuFeatures.Sse2 | RuntimeCpuFeatures.Sse3 |
      RuntimeCpuFeatures.Ssse3 | RuntimeCpuFeatures.Sse41 | RuntimeCpuFeatures.Sse42 | RuntimeCpuFeatures.Avx |
      RuntimeCpuFeatures.Avx2 | RuntimeCpuFeatures.Fma | RuntimeCpuFeatures.Avx512F | RuntimeCpuFeatures.Avx512Dq |
      RuntimeCpuFeatures.Avx512Bw | RuntimeCpuFeatures.Avx512Vl)) != 0;
}
