using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>Instruction-set extensions/coprocessors the selected DOS target explicitly permits.</summary>
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
  X87 = 1UL << 22,
}

/// <summary>
/// Normalized compile-time x86 target shared by runtime specialization and inline-assembly validation.
/// <see cref="CpuLevel"/> is the minimum integer core generation (86/186/286/386/486/586/686).
/// Feature requirements may be used without naming a generation: <c>$CPU SSE2</c> means "any x86
/// CPU with SSE2", and prerequisite core capabilities are inferred transitively. Conversely
/// <c>$CPU 8086</c> plus <c>$ISA AVX512 EMULATE</c> keeps an actual 8086 hardware target and lowers
/// AVX-512 in software.
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
  public bool HasX87 => this.Has(RuntimeCpuFeatures.X87);

  public IReadOnlyList<Reg> WordGeneralPurposeRegisters => _wordGprs;
  public IReadOnlyList<Reg> DwordGeneralPurposeRegisters => this.Has32BitGeneralPurpose ? _dwordGprs : _none;
  public IReadOnlyList<Reg> VectorRegisters => this.HasAvx512 ? _zmm
    : this.HasAvx ? _ymm
    : this.HasSse ? _xmm
    : this.HasMmx ? _mmx
    : _none;

  /// <summary>
  /// Widest vector width usable by the runtime's byte-preserving bulk primitives. MMX is deliberately
  /// excluded because it aliases x87 state. SSE1 is sufficient: MOVUPS/XORPS are bit-preserving, so
  /// integer payloads do not require SSE2 just because the opcode happens to have a floating spelling.
  /// </summary>
  public int MaxRuntimeBulkVectorWidthBytes => this.HasAvx512 ? 64 : this.HasAvx ? 32 : this.HasSse ? 16 : 0;

  /// <summary>
  /// Builds a target from an optional generation token followed by feature requirements. If the first
  /// token is itself a feature (<c>$CPU SSE2</c>), there is no explicit generation floor; the lowest
  /// core capable of satisfying the requested ISA is inferred.
  /// </summary>
  public static RuntimeTarget For(string? cpu, IEnumerable<string>? featureTokens = null) {
    var tokens = new List<string>();
    if (!string.IsNullOrWhiteSpace(cpu))
      tokens.Add(cpu!);
    if (featureTokens != null)
      tokens.AddRange(featureTokens.Where(t => !string.IsNullOrWhiteSpace(t)));

    var level = 86;
    var featureStart = 0;
    if (tokens.Count > 0 && TryParseCpuLevel(tokens[0], out var parsedLevel)) {
      level = parsedLevel;
      featureStart = 1;
    }

    var features = FeaturesForCpuLevel(level);
    for (var i = featureStart; i < tokens.Count; ++i)
      features |= ParseFeature(tokens[i]);

    features = Normalize(features);
    level = Math.Max(level, MinimumCpuLevel(features));
    features = Normalize(features | FeaturesForCpuLevel(level));
    return new(level, features);
  }

  public string DescribeMissing(RuntimeCpuFeatures required) {
    var missing = required & ~this.Features;
    if (missing == RuntimeCpuFeatures.None)
      return string.Empty;
    return string.Join(", ", Enum.GetValues<RuntimeCpuFeatures>()
      .Where(v => v != RuntimeCpuFeatures.None && (missing & v) != 0)
      .Select(DisplayName));
  }

  private static RuntimeCpuFeatures FeaturesForCpuLevel(int level) {
    var features = RuntimeCpuFeatures.None;
    if (level >= 386)
      features |= RuntimeCpuFeatures.GeneralPurpose32;
    if (level >= 486)
      features |= RuntimeCpuFeatures.I486;
    if (level >= 586)
      features |= RuntimeCpuFeatures.X87; // Pentium and later integrate the x87 FPU.
    if (level >= 686)
      features |= RuntimeCpuFeatures.P6;
    return features;
  }

  private static int MinimumCpuLevel(RuntimeCpuFeatures features) {
    if ((features & (RuntimeCpuFeatures.Sse | RuntimeCpuFeatures.Sse2 | RuntimeCpuFeatures.Sse3 |
        RuntimeCpuFeatures.Ssse3 | RuntimeCpuFeatures.Sse41 | RuntimeCpuFeatures.Sse42 |
        RuntimeCpuFeatures.Popcnt | RuntimeCpuFeatures.Aes | RuntimeCpuFeatures.Pclmulqdq |
        RuntimeCpuFeatures.Avx | RuntimeCpuFeatures.Avx2 | RuntimeCpuFeatures.Fma |
        RuntimeCpuFeatures.Bmi1 | RuntimeCpuFeatures.Bmi2 | RuntimeCpuFeatures.Avx512F |
        RuntimeCpuFeatures.Avx512Dq | RuntimeCpuFeatures.Avx512Bw | RuntimeCpuFeatures.Avx512Vl |
        RuntimeCpuFeatures.P6)) != 0)
      return 686;
    if ((features & RuntimeCpuFeatures.Mmx) != 0)
      return 586;
    if ((features & RuntimeCpuFeatures.I486) != 0)
      return 486;
    if ((features & RuntimeCpuFeatures.GeneralPurpose32) != 0)
      return 386;
    // X87 by itself may mean an 8087 attached to an 8086, so it imposes no integer-core floor.
    return 86;
  }

  private static RuntimeCpuFeatures Normalize(RuntimeCpuFeatures value) {
    if ((value & RuntimeCpuFeatures.Avx512F) != 0)
      value |= RuntimeCpuFeatures.Avx2 | RuntimeCpuFeatures.Avx;
    if ((value & RuntimeCpuFeatures.Avx2) != 0)
      value |= RuntimeCpuFeatures.Avx;
    if ((value & RuntimeCpuFeatures.Fma) != 0)
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
      "X87" or "8087" or "NPX" or "FPU" => RuntimeCpuFeatures.X87,
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

  private static bool TryParseCpuLevel(string? cpu, out int level) {
    var text = cpu?.Trim().ToUpperInvariant() ?? string.Empty;
    if (text is "PENTIUM" or "P5") { level = 586; return true; }
    if (text is "P6" or "PENTIUMPRO") { level = 686; return true; }
    if (!int.TryParse(text, out var numeric)) { level = 86; return false; }
    level = numeric switch {
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
    return true;
  }

  private static string DisplayName(RuntimeCpuFeatures feature) => feature switch {
    RuntimeCpuFeatures.GeneralPurpose32 => "80386",
    RuntimeCpuFeatures.I486 => "80486",
    RuntimeCpuFeatures.P6 => "P6/686",
    RuntimeCpuFeatures.X87 => "x87/NPX",
    RuntimeCpuFeatures.Sse41 => "SSE4.1",
    RuntimeCpuFeatures.Sse42 => "SSE4.2",
    RuntimeCpuFeatures.Avx512F => "AVX-512F",
    RuntimeCpuFeatures.Avx512Dq => "AVX-512DQ",
    RuntimeCpuFeatures.Avx512Bw => "AVX-512BW",
    RuntimeCpuFeatures.Avx512Vl => "AVX-512VL",
    _ => feature.ToString().ToUpperInvariant(),
  };
}
