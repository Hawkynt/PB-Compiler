using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>Instruction-set extensions the selected DOS target explicitly permits.</summary>
[Flags]
public enum RuntimeCpuFeatures : ulong {
  None = 0,
  GeneralPurpose32 = 1UL << 0,
  I486 = 1UL << 1,
  P6 = 1UL << 2,
  Mmx = 1UL << 3,
  Sse = 1UL << 4,
  Sse2 = 1UL << 5,
  Sse3 = 1UL << 6,
  Ssse3 = 1UL << 7,
  Sse41 = 1UL << 8,
  Sse42 = 1UL << 9,
  Popcnt = 1UL << 10,
  Aes = 1UL << 11,
  Pclmulqdq = 1UL << 12,
  Avx = 1UL << 13,
  Avx2 = 1UL << 14,
  Fma = 1UL << 15,
  Bmi1 = 1UL << 16,
  Bmi2 = 1UL << 17,
  Avx512F = 1UL << 18,
  Avx512Dq = 1UL << 19,
  Avx512Bw = 1UL << 20,
  Avx512Vl = 1UL << 21,
}

/// <summary>
/// Normalized compile-time x86 target shared by runtime specialization and inline-assembly validation.
/// <see cref="CpuLevel"/> uses the conventional generation number (86/186/286/386/486/586/686),
/// while extension tokens opt into later ISA groups independently.
/// </summary>
public readonly record struct RuntimeTarget(int CpuLevel, RuntimeCpuFeatures Features) {
  private static readonly IReadOnlyList<Reg> _wordGprs = Array.AsReadOnly([
    Reg.AX, Reg.CX, Reg.DX, Reg.BX, Reg.BP, Reg.SI, Reg.DI,
  ]);
  private static readonly IReadOnlyList<Reg> _dwordGprs = Array.AsReadOnly([
    Reg.EAX, Reg.ECX, Reg.EDX, Reg.EBX, Reg.EBP, Reg.ESI, Reg.EDI,
  ]);
  private static readonly IReadOnlyList<Reg> _mmx = Array.AsReadOnly([
    Reg.MM0, Reg.MM1, Reg.MM2, Reg.MM3, Reg.MM4, Reg.MM5, Reg.MM6, Reg.MM7,
  ]);
  private static readonly IReadOnlyList<Reg> _xmm = Array.AsReadOnly([
    Reg.XMM0, Reg.XMM1, Reg.XMM2, Reg.XMM3, Reg.XMM4, Reg.XMM5, Reg.XMM6, Reg.XMM7,
  ]);
  private static readonly IReadOnlyList<Reg> _ymm = Array.AsReadOnly([
    Reg.YMM0, Reg.YMM1, Reg.YMM2, Reg.YMM3, Reg.YMM4, Reg.YMM5, Reg.YMM6, Reg.YMM7,
  ]);
  private static readonly IReadOnlyList<Reg> _zmm = Array.AsReadOnly([
    Reg.ZMM0, Reg.ZMM1, Reg.ZMM2, Reg.ZMM3, Reg.ZMM4, Reg.ZMM5, Reg.ZMM6, Reg.ZMM7,
  ]);
  private static readonly IReadOnlyList<Reg> _none = Array.Empty<Reg>();

  public static RuntimeTarget Baseline => new(86, RuntimeCpuFeatures.None);

  public bool Has(RuntimeCpuFeatures feature) => (this.Features & feature) == feature;
  public bool Has32BitGeneralPurpose => this.Has(RuntimeCpuFeatures.GeneralPurpose32);
  public bool Has486 => this.Has(RuntimeCpuFeatures.I486);
  public bool HasP6 => this.Has(RuntimeCpuFeatures.P6);
  public bool HasMmx => this.Has(RuntimeCpuFeatures.Mmx);
  public bool HasSse => this.Has(RuntimeCpuFeatures.Sse);
  public bool HasSse2 => this.Has(RuntimeCpuFeatures.Sse2);
  public bool HasSse3 => this.Has(RuntimeCpuFeatures.Sse3);
  public bool HasSsse3 => this.Has(RuntimeCpuFeatures.Ssse3);
  public bool HasSse41 => this.Has(RuntimeCpuFeatures.Sse41);
  public bool HasSse42 => this.Has(RuntimeCpuFeatures.Sse42);
  public bool HasAvx => this.Has(RuntimeCpuFeatures.Avx);
  public bool HasAvx2 => this.Has(RuntimeCpuFeatures.Avx2);
  public bool HasAvx512 => this.Has(RuntimeCpuFeatures.Avx512F);
  public bool HasAes => this.Has(RuntimeCpuFeatures.Aes);

  public IReadOnlyList<Reg> WordGeneralPurposeRegisters => _wordGprs;
  public IReadOnlyList<Reg> DwordGeneralPurposeRegisters => this.Has32BitGeneralPurpose ? _dwordGprs : _none;
  public IReadOnlyList<Reg> VectorRegisters => this.HasAvx512 ? _zmm
    : this.HasAvx ? _ymm
    : this.HasSse ? _xmm
    : this.HasMmx ? _mmx
    : _none;

  /// <summary>
  /// Widest vector width usable by the runtime's byte-preserving bulk-move primitives. MMX is not
  /// selected here because it aliases x87 state; SSE without SSE2 has XMM registers but not MOVDQU.
  /// </summary>
  public int MaxRuntimeBulkVectorWidthBytes => this.HasAvx512 ? 64 : this.HasAvx ? 32 : this.HasSse2 ? 16 : 0;

  public static RuntimeTarget For(string? cpu, IEnumerable<string>? featureTokens = null) {
    var level = ParseCpuLevel(cpu);
    var features = RuntimeCpuFeatures.None;
    if (level >= 386)
      features |= RuntimeCpuFeatures.GeneralPurpose32;
    if (level >= 486)
      features |= RuntimeCpuFeatures.I486;
    if (level >= 686)
      features |= RuntimeCpuFeatures.P6;

    // PB uses the first CPU token as the integer floor and remaining tokens as optional extensions.
    // Keep the historic 586 floor for SIMD feature tokens: "$CPU 8086 AVX" must not create registers
    // the selected architecture cannot expose.
    if (level >= 586)
      foreach (var token in featureTokens ?? [])
        features |= ParseFeature(token);

    return new(level, Normalize(features));
  }

  public string DescribeMissing(RuntimeCpuFeatures required) {
    var missing = required & ~this.Features;
    if (missing == RuntimeCpuFeatures.None)
      return string.Empty;
    return string.Join(", ", Enum.GetValues<RuntimeCpuFeatures>()
      .Where(v => v != RuntimeCpuFeatures.None && (missing & v) != 0)
      .Select(DisplayName));
  }

  private static RuntimeCpuFeatures Normalize(RuntimeCpuFeatures value) {
    if ((value & RuntimeCpuFeatures.Avx512F) != 0)
      value |= RuntimeCpuFeatures.Avx2 | RuntimeCpuFeatures.Avx;
    if ((value & RuntimeCpuFeatures.Avx2) != 0)
      value |= RuntimeCpuFeatures.Avx;
    if ((value & RuntimeCpuFeatures.Avx) != 0)
      value |= RuntimeCpuFeatures.Sse2 | RuntimeCpuFeatures.Sse;
    if ((value & RuntimeCpuFeatures.Sse42) != 0)
      value |= RuntimeCpuFeatures.Sse41;
    if ((value & RuntimeCpuFeatures.Sse41) != 0)
      value |= RuntimeCpuFeatures.Ssse3;
    if ((value & RuntimeCpuFeatures.Ssse3) != 0)
      value |= RuntimeCpuFeatures.Sse3;
    if ((value & RuntimeCpuFeatures.Sse3) != 0)
      value |= RuntimeCpuFeatures.Sse2;
    if ((value & RuntimeCpuFeatures.Sse2) != 0)
      value |= RuntimeCpuFeatures.Sse;
    if ((value & (RuntimeCpuFeatures.Aes | RuntimeCpuFeatures.Pclmulqdq)) != 0)
      value |= RuntimeCpuFeatures.Sse2 | RuntimeCpuFeatures.Sse;
    return value;
  }

  private static RuntimeCpuFeatures ParseFeature(string raw) {
    var feature = raw.Trim().Replace("-", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal).ToUpperInvariant();
    return feature switch {
      "MMX" => RuntimeCpuFeatures.Mmx,
      "SSE" => RuntimeCpuFeatures.Sse,
      "SSE2" => RuntimeCpuFeatures.Sse2,
      "SSE3" => RuntimeCpuFeatures.Sse3,
      "SSSE3" => RuntimeCpuFeatures.Ssse3,
      "SSE41" => RuntimeCpuFeatures.Sse41,
      "SSE42" => RuntimeCpuFeatures.Sse42,
      "POPCNT" => RuntimeCpuFeatures.Popcnt,
      "AES" or "AESNI" => RuntimeCpuFeatures.Aes,
      "PCLMUL" or "PCLMULQDQ" => RuntimeCpuFeatures.Pclmulqdq,
      "AVX" => RuntimeCpuFeatures.Avx,
      "AVX2" => RuntimeCpuFeatures.Avx2,
      "FMA" or "FMA3" => RuntimeCpuFeatures.Fma | RuntimeCpuFeatures.Avx,
      "BMI" or "BMI1" => RuntimeCpuFeatures.Bmi1,
      "BMI2" => RuntimeCpuFeatures.Bmi2,
      "AVX512" or "AVX512F" => RuntimeCpuFeatures.Avx512F,
      "AVX512DQ" => RuntimeCpuFeatures.Avx512F | RuntimeCpuFeatures.Avx512Dq,
      "AVX512BW" => RuntimeCpuFeatures.Avx512F | RuntimeCpuFeatures.Avx512Bw,
      "AVX512VL" => RuntimeCpuFeatures.Avx512F | RuntimeCpuFeatures.Avx512Vl,
      _ => RuntimeCpuFeatures.None,
    };
  }

  private static int ParseCpuLevel(string? cpu) {
    var text = cpu?.Trim().ToUpperInvariant() ?? string.Empty;
    if (text is "PENTIUM" or "P5")
      return 586;
    if (text is "P6" or "PENTIUMPRO")
      return 686;
    if (!int.TryParse(text, out var numeric))
      return 86;
    return numeric switch {
      86 or 8086 => 86,
      186 or 80186 => 186,
      286 or 80286 => 286,
      386 or 80386 => 386,
      486 or 80486 => 486,
      586 or 80586 => 586,
      686 or 80686 => 686,
      _ when numeric > 80686 => 686,
      _ => numeric,
    };
  }

  private static string DisplayName(RuntimeCpuFeatures feature) => feature switch {
    RuntimeCpuFeatures.GeneralPurpose32 => "80386",
    RuntimeCpuFeatures.I486 => "80486",
    RuntimeCpuFeatures.P6 => "P6/686",
    RuntimeCpuFeatures.Sse41 => "SSE4.1",
    RuntimeCpuFeatures.Sse42 => "SSE4.2",
    RuntimeCpuFeatures.Avx512F => "AVX-512F",
    RuntimeCpuFeatures.Avx512Dq => "AVX-512DQ",
    RuntimeCpuFeatures.Avx512Bw => "AVX-512BW",
    RuntimeCpuFeatures.Avx512Vl => "AVX-512VL",
    _ => feature.ToString().ToUpperInvariant(),
  };
}
