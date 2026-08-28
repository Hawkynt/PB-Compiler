from pathlib import Path
import re


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text()
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{path}: expected one replacement, found {count}")
    p.write_text(text.replace(old, new))


Path("PowerBasic.Compiler/Runtime/RuntimeTarget.cs").write_text(r'''using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>The instruction-set and register surface available to emitted DOS runtime routines.</summary>
[Flags]
public enum RuntimeCpuFeatures {
  None = 0,
  GeneralPurpose32 = 1 << 0,
  Mmx = 1 << 1,
  Sse2 = 1 << 2,
  Avx2 = 1 << 3,
  Avx512 = 1 << 4,
  Aes = 1 << 5,
}

/// <summary>
/// Compile-time x86 target for the embedded DOS runtime. Feature implications are normalized here so
/// every runtime section sees one coherent answer instead of re-parsing <c>$CPU</c> independently.
/// </summary>
public readonly record struct RuntimeTarget(RuntimeCpuFeatures Features) {
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

  public static RuntimeTarget Baseline => new(RuntimeCpuFeatures.None);

  public bool Has32BitGeneralPurpose => this.Features.HasFlag(RuntimeCpuFeatures.GeneralPurpose32);
  public bool HasMmx => this.Features.HasFlag(RuntimeCpuFeatures.Mmx);
  public bool HasSse2 => this.Features.HasFlag(RuntimeCpuFeatures.Sse2);
  public bool HasAvx2 => this.Features.HasFlag(RuntimeCpuFeatures.Avx2);
  public bool HasAvx512 => this.Features.HasFlag(RuntimeCpuFeatures.Avx512);
  public bool HasAes => this.Features.HasFlag(RuntimeCpuFeatures.Aes);

  /// <summary>The full 16-bit GP register pool except SP, which is never allocator scratch.</summary>
  public IReadOnlyList<Reg> WordGeneralPurposeRegisters => _wordGprs;

  /// <summary>The 386+ aliases of the GP register pool; empty on an older target.</summary>
  public IReadOnlyList<Reg> DwordGeneralPurposeRegisters => this.Has32BitGeneralPurpose ? _dwordGprs : _none;

  /// <summary>The widest declared vector register class.</summary>
  public IReadOnlyList<Reg> VectorRegisters => this.HasAvx512 ? _zmm
    : this.HasAvx2 ? _ymm
    : this.HasSse2 ? _xmm
    : this.HasMmx ? _mmx
    : _none;

  public int MaxVectorWidthBytes => this.HasAvx512 ? 64 : this.HasAvx2 ? 32 : this.HasSse2 ? 16 : this.HasMmx ? 8 : 0;
  public Reg? PreferredVectorRegister => this.VectorRegisters.Count == 0 ? null : this.VectorRegisters[0];

  /// <summary>Builds the normalized runtime target from one <c>$CPU</c> level and its optional feature tokens.</summary>
  public static RuntimeTarget For(string? cpu, IEnumerable<string>? features = null) {
    var level = CpuLevel(cpu);
    var flags = level >= 386 ? RuntimeCpuFeatures.GeneralPurpose32 : RuntimeCpuFeatures.None;
    if (level < 586)
      return new(flags);

    foreach (var raw in features ?? []) {
      var feature = raw.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
      flags |= feature switch {
        "MMX" => RuntimeCpuFeatures.Mmx,
        "SSE" or "SSE2" => RuntimeCpuFeatures.Sse2,
        "AVX" or "AVX2" => RuntimeCpuFeatures.Sse2 | RuntimeCpuFeatures.Avx2,
        "AVX512" => RuntimeCpuFeatures.Sse2 | RuntimeCpuFeatures.Avx2 | RuntimeCpuFeatures.Avx512,
        "AES" => RuntimeCpuFeatures.Sse2 | RuntimeCpuFeatures.Aes,
        _ => RuntimeCpuFeatures.None,
      };
    }
    return new(flags);
  }

  private static int CpuLevel(string? cpu) {
    var text = cpu?.Trim().ToUpperInvariant() ?? string.Empty;
    if (text == "PENTIUM")
      return 586;
    if (text == "P6")
      return 686;
    if (int.TryParse(text, out var numeric)) {
      if (numeric >= 8000)
        numeric %= 1000;
      return numeric;
    }
    return 0;
  }
}
''')

Path("PowerBasic.Compiler/Runtime/DosRuntime.Targeting.cs").write_text(r'''using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class DosRuntime {
  private RuntimeTarget _target = RuntimeTarget.Baseline;

  /// <summary>
  /// Target visible to every runtime emitter. Assigning it also keeps the legacy <see cref="Cpu386"/>
  /// gate synchronized for runtime sections that only need the 32-bit-GP distinction.
  /// </summary>
  public RuntimeTarget Target {
    get => this._target;
    set {
      this._target = value;
      this.Cpu386 = value.Has32BitGeneralPurpose;
    }
  }

  /// <summary>
  /// Emits a vector prefix for a forward copy and leaves CX as the scalar remainder. The borrowed
  /// vector register is spilled to a private runtime cell and restored, so this does not change the
  /// runtime ABI. MMX is deliberately excluded because it aliases the x87 stack and arbitrary runtime
  /// calls may have live floating-point values.
  /// </summary>
  private void EmitVectorCopyPrefix(Assembler asm) {
    var width = this.Target.MaxVectorWidthBytes;
    if (width < 16 || this.Target.PreferredVectorRegister is not { } vector)
      return;

    var scalarTail = asm.DefineLabel();
    var loop = asm.DefineLabel();
    var spill = Mem.At(asm.Lbl("rt_vecscratch"));
    asm.Cmp(Reg.CX, width * 2);                    // amortize save/restore over at least two vectors
    asm.Jb(scalarTail);
    EmitVectorStore(asm, spill, vector);

    asm.MarkLabel(loop);
    EmitVectorLoad(asm, vector, Mem.At(Reg.SI));
    EmitVectorStore(asm, Mem.At(Reg.DI).Es(), vector);
    asm.Add(Reg.SI, width);
    asm.Add(Reg.DI, width);
    asm.Sub(Reg.CX, width);
    asm.Cmp(Reg.CX, width);
    asm.Jae(loop);

    EmitVectorLoad(asm, vector, spill);
    asm.MarkLabel(scalarTail);
  }

  /// <summary>
  /// Emits the vector prefix of a zero fill. CX counts units of <paramref name="unitBytes"/> bytes and
  /// is left holding the scalar tail; DI advances by the bytes already filled.
  /// </summary>
  private void EmitVectorZeroPrefix(Assembler asm, int unitBytes) {
    var width = this.Target.MaxVectorWidthBytes;
    if (width < 16 || this.Target.PreferredVectorRegister is not { } vector)
      return;

    var unitsPerVector = width / unitBytes;
    var scalarTail = asm.DefineLabel();
    var loop = asm.DefineLabel();
    var spill = Mem.At(asm.Lbl("rt_vecscratch"));
    asm.Cmp(Reg.CX, unitsPerVector * 2);
    asm.Jb(scalarTail);
    EmitVectorStore(asm, spill, vector);
    EmitVectorZero(asm, vector);

    asm.MarkLabel(loop);
    EmitVectorStore(asm, Mem.At(Reg.DI).Es(), vector);
    asm.Add(Reg.DI, width);
    asm.Sub(Reg.CX, unitsPerVector);
    asm.Cmp(Reg.CX, unitsPerVector);
    asm.Jae(loop);

    EmitVectorLoad(asm, vector, spill);
    asm.MarkLabel(scalarTail);
  }

  /// <summary>Zero-fills CX bytes at ES:DI, selecting vector, DWORD, or byte stores for the target.</summary>
  private void EmitRepStosbZeroWidened(Assembler asm) {
    this.EmitVectorZeroPrefix(asm, unitBytes: 1);
    if (!this.Cpu386) {
      asm.Xor(Reg.AL, Reg.AL);
      asm.Rep();
      asm.Stosb();
      return;
    }
    asm.Xor(Reg.EAX, Reg.EAX);
    asm.Push(Reg.CX);
    asm.Shr(Reg.CX, 2);
    asm.Rep();
    asm.Stosd();
    asm.Pop(Reg.CX);
    asm.And(Reg.CX, (Imm)3);
    asm.Rep();
    asm.Stosb();
  }

  private static void EmitVectorLoad(Assembler asm, Reg vector, Mem source) {
    if (vector.IsZmm())
      asm.Vmovdqu512(vector, source);
    else if (vector.IsYmm())
      asm.Vmovdqu(vector, source);
    else
      asm.Movdqu(vector, source);
  }

  private static void EmitVectorStore(Assembler asm, Mem destination, Reg vector) {
    if (vector.IsZmm())
      asm.Vmovdqu512Store(destination, vector);
    else if (vector.IsYmm())
      asm.VmovdquStore(destination, vector);
    else
      asm.MovdquStore(destination, vector);
  }

  private static void EmitVectorZero(Assembler asm, Reg vector) {
    if (vector.IsZmm())
      asm.EvexPacked(0xEF, vector, vector, vector);   // VPXOR zmm,zmm,zmm
    else if (vector.IsYmm())
      asm.VexPacked(0xEF, vector, vector, vector);    // VPXOR ymm,ymm,ymm
    else
      asm.PxorX(vector, vector);                      // PXOR xmm,xmm
  }
}
''')

Path("PowerBasic.Compiler/CodeGen/CodeGenerator.RuntimeTarget.cs").write_text(r'''using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  /// <summary>One normalized target object for every emitted DOS runtime section.</summary>
  private RuntimeTarget RuntimeTargetForRuntime() {
    var cpu = model.MetaStatements.FirstOrDefault(m =>
      m.Command.Equals("CPU", StringComparison.OrdinalIgnoreCase));
    if (cpu?.Arguments is not [{ } level, ..])
      return RuntimeTarget.Baseline;
    return RuntimeTarget.For(level.Text, cpu.Arguments.Skip(1).Select(a => a.Text));
  }
}
''')

replace_once(
    "PowerBasic.Compiler/CodeGen/CodeGenerator.cs",
    "    this._rt.Cpu386 = this.Optimize && this.Cpu386;\n    this._rt.EmitEntry(asm, userMain);",
    "    this._rt.Target = this.Optimize ? this.RuntimeTargetForRuntime() : RuntimeTarget.Baseline;\n    this._rt.EmitEntry(asm, userMain);")

# CPU floors are cumulative. Pentium targets may use 486 and 386 instructions/registers too.
replace_once(
    "PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs",
    '    && level.Text is "80386" or "80486" or "386" or "486");',
    '    && level.Text is "80386" or "80486" or "80586" or "386" or "486" or "586" or "PENTIUM");')
replace_once(
    "PowerBasic.Compiler/CodeGen/CodeGenerator.Optimize.cs",
    '    && level.Text is "80486" or "486");',
    '    && level.Text is "80486" or "80586" or "486" or "586" or "PENTIUM");')

# SIMD memory instructions need segment overrides before the mandatory/VEX/EVEX prefix. The runtime
# immediately depends on ES:DI stores, but this also fixes explicit SIMD assembly using non-DS memory.
simd = "PowerBasic.Compiler/Asm/Assembler.Simd.cs"
replace_once(simd,
'''  private void MmxRegMem(byte op, Reg dest, Mem src) {
    this.EmitByte(0x0F);''',
'''  private void MmxRegMem(byte op, Reg dest, Mem src) {
    this.EmitSegmentPrefix(src);
    this.EmitByte(0x0F);''')
replace_once(simd,
'''  private void Sse2RegMem(byte op, Reg dest, Mem src) {
    this.EmitByte(0x66);''',
'''  private void Sse2RegMem(byte op, Reg dest, Mem src) {
    this.EmitSegmentPrefix(src);
    this.EmitByte(0x66);''')
replace_once(simd,
'''  public void Movdqu(Reg dst, Mem src) {
    this.EmitByte(0xF3);''',
'''  public void Movdqu(Reg dst, Mem src) {
    this.EmitSegmentPrefix(src);
    this.EmitByte(0xF3);''')
replace_once(simd,
'''  public void MovdquStore(Mem dst, Reg src) {
    this.EmitByte(0xF3);''',
'''  public void MovdquStore(Mem dst, Reg src) {
    this.EmitSegmentPrefix(dst);
    this.EmitByte(0xF3);''')
replace_once(simd,
'''  public void VexPacked(byte op, Reg dest, Reg src1, Mem src2) {
    this.VexPrefix(src1, dest.IsYmm());''',
'''  public void VexPacked(byte op, Reg dest, Reg src1, Mem src2) {
    this.EmitSegmentPrefix(src2);
    this.VexPrefix(src1, dest.IsYmm());''')
replace_once(simd,
'''  public void Vmovdqa(Reg dest, Mem src) {
    this.VexPrefix(Reg.AL, dest.IsYmm());''',
'''  public void Vmovdqa(Reg dest, Mem src) {
    this.EmitSegmentPrefix(src);
    this.VexPrefix(Reg.AL, dest.IsYmm());''')
replace_once(simd,
'''  public void VmovdqaStore(Mem dest, Reg src) {
    this.VexPrefix(Reg.AL, src.IsYmm());''',
'''  public void VmovdqaStore(Mem dest, Reg src) {
    this.EmitSegmentPrefix(dest);
    this.VexPrefix(Reg.AL, src.IsYmm());''')
replace_once(simd,
'''  public void Vmovdqu(Reg dest, Mem src) {
    this.VexPrefix(Reg.AL, dest.IsYmm(), pp: 0b10);''',
'''  public void Vmovdqu(Reg dest, Mem src) {
    this.EmitSegmentPrefix(src);
    this.VexPrefix(Reg.AL, dest.IsYmm(), pp: 0b10);''')
replace_once(simd,
'''  public void VmovdquStore(Mem dest, Reg src) {
    this.VexPrefix(Reg.AL, src.IsYmm(), pp: 0b10);''',
'''  public void VmovdquStore(Mem dest, Reg src) {
    this.EmitSegmentPrefix(dest);
    this.VexPrefix(Reg.AL, src.IsYmm(), pp: 0b10);''')
replace_once(simd,
'''  public void EvexPacked(byte op, Reg dest, Reg src1, Mem src2) {
    this.EvexPrefix(src1);''',
'''  public void EvexPacked(byte op, Reg dest, Reg src1, Mem src2) {
    this.EmitSegmentPrefix(src2);
    this.EvexPrefix(src1);''')
replace_once(simd,
'''  public void Vmovdqa512(Reg dest, Mem src) {
    this.EvexPrefix(Reg.AL);''',
'''  public void Vmovdqa512(Reg dest, Mem src) {
    this.EmitSegmentPrefix(src);
    this.EvexPrefix(Reg.AL);''')
replace_once(simd,
'''  public void Vmovdqa512Store(Mem dest, Reg src) {
    this.EvexPrefix(Reg.AL);''',
'''  public void Vmovdqa512Store(Mem dest, Reg src) {
    this.EmitSegmentPrefix(dest);
    this.EvexPrefix(Reg.AL);''')
replace_once(simd,
'''  public void Vmovdqu512(Reg dest, Mem src) {
    this.EvexPrefix(Reg.AL, pp: 0b10);''',
'''  public void Vmovdqu512(Reg dest, Mem src) {
    this.EmitSegmentPrefix(src);
    this.EvexPrefix(Reg.AL, pp: 0b10);''')
replace_once(simd,
'''  public void Vmovdqu512Store(Mem dest, Reg src) {
    this.EvexPrefix(Reg.AL, pp: 0b10);''',
'''  public void Vmovdqu512Store(Mem dest, Reg src) {
    this.EmitSegmentPrefix(dest);
    this.EvexPrefix(Reg.AL, pp: 0b10);''')

p = Path("PowerBasic.Compiler/Runtime/DosRuntime.cs")
text = p.read_text()
copy_pattern = re.compile(
    r'  /// <summary>\n  /// pb36 R3: a forward byte copy of CX bytes.*?\n  private void EmitRepMovsbWidened\(Assembler asm\) \{.*?\n  \}\n',
    re.S)
copy_replacement = '''  /// <summary>\n  /// Target-aware forward byte copy of CX bytes (DS:SI -> ES:DI, DF clear). SSE2/AVX2/AVX-512\n  /// targets use the widest declared vector register for long runs, a 386+ tail uses MOVSD, and the\n  /// baseline remains REP MOVSB. Borrowed vector state is preserved by <see cref="EmitVectorCopyPrefix"/>.\n  /// CX ends at 0 and SI/DI advance by the full count on every path.\n  /// </summary>\n  private void EmitRepMovsbWidened(Assembler asm) {\n    this.EmitVectorCopyPrefix(asm);\n    if (!this.Cpu386) {\n      asm.Rep();\n      asm.Movsb();\n      return;\n    }\n    asm.Push(Reg.CX);\n    asm.Shr(Reg.CX, 2);\n    asm.Rep();\n    asm.Movsd();\n    asm.Pop(Reg.CX);\n    asm.And(Reg.CX, (Imm)3);\n    asm.Rep();\n    asm.Movsb();\n  }\n'''
text, count = copy_pattern.subn(copy_replacement, text, count=1)
if count != 1:
    raise SystemExit(f"DosRuntime.cs: expected one copy-helper replacement, found {count}")
needle = "  private void EmitRepStoswZeroWidened(Assembler asm) {\n"
if text.count(needle) != 1:
    raise SystemExit("DosRuntime.cs: zero-word helper anchor drifted")
text = text.replace(needle, needle + "    this.EmitVectorZeroPrefix(asm, unitBytes: 2);\n")
replace_data = '    this._scratch = this.ZeroBlob(asm, "rt_scratch", 16);'
if text.count(replace_data) != 1:
    raise SystemExit("DosRuntime.cs: core-data anchor drifted")
text = text.replace(replace_data, replace_data + '\n    if (this.Target.MaxVectorWidthBytes >= 16)\n      this.ZeroBlob(asm, "rt_vecscratch", this.Target.MaxVectorWidthBytes);')
p.write_text(text)

# Centralize plain forward REP MOVSB sites. If the most recent explicit direction change is STD,
# leave the site alone because a backwards copy needs a different widened tail layout.
pair = re.compile(r'(?m)^(?P<i>[ \t]*)asm\.Rep\(\);\n(?P=i)asm\.Movsb\(\);(?P<tail>[^\n]*)$')
changed_pairs = 0
for path in sorted(Path("PowerBasic.Compiler/Runtime").glob("DosRuntime*.cs")):
    if path.name == "DosRuntime.cs":
        continue
    source = path.read_text()
    out: list[str] = []
    pos = 0
    local = 0
    for match in pair.finditer(source):
        before = source[:match.start()]
        if before.rfind("asm.Std();") > before.rfind("asm.Cld();"):
            continue
        out.append(source[pos:match.start()])
        out.append(f"{match.group('i')}this.EmitRepMovsbWidened(asm);{match.group('tail')}")
        pos = match.end()
        local += 1
    if local:
        out.append(source[pos:])
        path.write_text("".join(out))
        changed_pairs += local
        print(f"centralized {local} REP MOVSB site(s) in {path}")
if changed_pairs < 10:
    raise SystemExit(f"expected at least 10 runtime copy sites, found {changed_pairs}")

replace_once(
    "PowerBasic.Compiler/Runtime/DosRuntime.Arrays.cs",
    "    asm.Xor(Reg.AL, Reg.AL);\n    asm.Rep();\n    asm.Stosb();",
    "    this.EmitRepStosbZeroWidened(asm);")

Path("PowerBasic.Compiler.Tests/Runtime").mkdir(parents=True, exist_ok=True)
Path("PowerBasic.Compiler.Tests/Runtime/RuntimeTargetTests.cs").write_text(r'''using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Runtime;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Runtime;

[TestFixture]
public sealed class RuntimeTargetTests {
  [Test]
  public void Target_GivenAvx512_ThenItExposesTheImpliedFeaturesAndRegisterSet() {
    var target = RuntimeTarget.For("80586", ["AVX512", "AES"]);

    Assert.Multiple(() => {
      Assert.That(target.Has32BitGeneralPurpose, Is.True);
      Assert.That(target.HasSse2, Is.True);
      Assert.That(target.HasAvx2, Is.True);
      Assert.That(target.HasAvx512, Is.True);
      Assert.That(target.HasAes, Is.True);
      Assert.That(target.MaxVectorWidthBytes, Is.EqualTo(64));
      Assert.That(target.DwordGeneralPurposeRegisters, Does.Contain(Reg.EDI));
      Assert.That(target.VectorRegisters, Does.Contain(Reg.ZMM7));
      Assert.That(target.PreferredVectorRegister, Is.EqualTo(Reg.ZMM0));
    });
  }

  [Test]
  public void Target_GivenAn8086_ThenItDoesNotInventNewRegisters() {
    var target = RuntimeTarget.For("8086", ["AVX512"]);

    Assert.Multiple(() => {
      Assert.That(target.Has32BitGeneralPurpose, Is.False);
      Assert.That(target.DwordGeneralPurposeRegisters, Is.Empty);
      Assert.That(target.VectorRegisters, Is.Empty);
      Assert.That(target.MaxVectorWidthBytes, Is.Zero);
    });
  }

  [TestCase("80586", new byte[] { 0x66, 0xA5 }, TestName = "Runtime_Pentium_Uses386DwordTail")]
  [TestCase("80586 SSE", new byte[] { 0xF3, 0x0F, 0x6F }, TestName = "Runtime_Sse2_UsesXmmCopy")]
  [TestCase("80586 AVX", new byte[] { 0xC5, 0x86, 0x6F }, TestName = "Runtime_Avx2_UsesYmmCopy")]
  [TestCase("80586 AVX512", new byte[] { 0x62, 0xF1, 0x06, 0x40, 0x6F }, TestName = "Runtime_Avx512_UsesZmmCopy")]
  public void Runtime_GivenATarget_ThenEmbeddedStringCopiesUseItsBestLegalWidth(string cpu, byte[] opcode) {
    var image = Compile(cpu, optimize: true);
    Assert.That(Contains(image, opcode), Is.True,
      $"$CPU {cpu} runtime did not contain the expected target-specific copy instruction");
  }

  [Test]
  public void Runtime_GivenOptimizationOff_ThenTargetSpecializationStaysDisabled() {
    var image = Compile("80586 AVX512", optimize: false);
    Assert.That(Contains(image, [0x62, 0xF1, 0x06, 0x40, 0x6F]), Is.False,
      "--no-optimize must retain the faithful scalar runtime");
  }

  private static byte[] Compile(string cpu, bool optimize) {
    var source = $$"""
      $CPU {{cpu}}
      DIM a AS STRING, b AS STRING
      a = SPACE$(160)
      b = a + a
      PRINT LEN(b)
      END
      """;
    var unit = Parser.Parse(Lexer.Tokenize(source, "RT.BAS", Dialect.Pb36), "RT.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return image;
  }

  private static bool Contains(byte[] image, byte[] pattern) {
    for (var i = 0; i + pattern.Length <= image.Length; ++i) {
      var match = true;
      for (var j = 0; j < pattern.Length && match; ++j)
        match = image[i + j] == pattern[j];
      if (match)
        return true;
    }
    return false;
  }
}
''')

Path("PowerBasic.Compiler.Tests/Asm/AssemblerSimdSegmentTests.cs").write_text(r'''using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

[TestFixture]
public sealed class AssemblerSimdSegmentTests {
  [TestCaseSource(nameof(Cases))]
  public void SimdMemory_GivenSegmentOverride_ThenPrefixPrecedesTheInstruction(Action<Assembler> emit, byte[] expected) {
    var asm = new Assembler();
    emit(asm);
    Assert.That(asm.ToArray(), Is.EqualTo(expected));
  }

  private static IEnumerable<TestCaseData> Cases() {
    yield return new TestCaseData(
      (Action<Assembler>)(asm => asm.MovdquStore(Mem.At(Reg.DI).Es(), Reg.XMM0)),
      new byte[] { 0x26, 0xF3, 0x0F, 0x7F, 0x05 }).SetName("SSE2_store_preserves_ES_override");
    yield return new TestCaseData(
      (Action<Assembler>)(asm => asm.VmovdquStore(Mem.At(Reg.DI).Es(), Reg.YMM0)),
      new byte[] { 0x26, 0xC5, 0x86, 0x7F, 0x05 }).SetName("AVX_store_preserves_ES_override");
    yield return new TestCaseData(
      (Action<Assembler>)(asm => asm.Vmovdqu512Store(Mem.At(Reg.DI).Es(), Reg.ZMM0)),
      new byte[] { 0x26, 0x62, 0xF1, 0x06, 0x40, 0x7F, 0x05 }).SetName("AVX512_store_preserves_ES_override");
  }
}
''')

Path("docs/optimizations/R0005-runtime-target-specialization.md").write_text(r'''# R0005 — target-specialized runtime primitives

| | |
|---|---|
| **Status** | ✅ implemented |
| **Gate** | `--optimize` plus `$CPU` target/features |
| **Scope** | DOS embedded runtime |

## Requirements

- **Must** propagate the declared CPU floor and feature flags to every runtime section through one normalized target object.
- **Must** treat newer CPU floors cumulatively: a Pentium target also has the 386/486 instruction and register set.
- **Must** preserve the runtime ABI while using wider registers; any XMM/YMM/ZMM scratch register borrowed by a helper is saved and restored.
- **Must** preserve far-memory semantics: SIMD loads/stores honor DS/ES/SS/FS/GS overrides before mandatory, VEX, or EVEX prefixes.
- **Must** leave `--no-optimize` on the faithful scalar runtime and preserve observable program behaviour.
- **Should** route forward runtime block copies and zero fills through shared target-aware primitives instead of maintaining local `REP MOVSB`/`STOSB` copies.
- **Could** add target-specific implementations for scanning, formatting, or arithmetic routines when a newer ISA has a semantically exact win.
- **Won't (here)** use MMX as generic runtime scratch: MMX aliases the x87 stack, so doing that across arbitrary runtime calls would corrupt live floating-point state. AES is exposed in the target but has no applicable general runtime primitive today.

## Selection

Long forward copies and zero fills select the widest declared, safe register width:

| target | bulk width | tail |
|---|---:|---|
| 8086/286 | 1 byte | `REP MOVSB` / scalar store |
| 386/486/Pentium | 4 bytes | `REP MOVSD` + byte tail |
| SSE2 | 16 bytes | XMM bulk + 386 tail |
| AVX2 | 32 bytes | YMM bulk + 386 tail |
| AVX-512 | 64 bytes | ZMM bulk + 386 tail |

Vectorization starts only at two complete vectors, so tiny strings do not pay the save/restore overhead. The scratch vector is saved in a target-sized private runtime cell, used for the copy/fill, then restored before the scalar tail. The cell is emitted only for vector targets, so faithful and non-vector images do not acquire dead data.

The feature hierarchy is normalized once: AVX2 implies SSE2, AVX-512 implies AVX2+SSE2, and AES implies the XMM/SSE2 register state. `$CPU 80586` without an extension still enables the inherited 386 DWORD path.

## Verification

`RuntimeTargetTests` pins both halves of the contract: the target object exposes the expected GP/vector register sets, and compiled string-runtime images contain the expected `MOVSD`, `MOVDQU`, VEX `VMOVDQU`, or EVEX `VMOVDQU32` encoding. `AssemblerSimdSegmentTests` pins the otherwise easy-to-miss ES override used by runtime copies. A companion runtime test verifies that `--no-optimize` suppresses the target-specialized runtime. Existing differential and execution batteries cover scalar/386 behavioural equivalence; DOSBox does not execute SSE/AVX, so those paths are encoding-verified like R0004.
''')

index = Path("docs/optimizations/README.md")
text = index.read_text()
if "R0005-runtime-target-specialization.md" not in text:
    pattern = re.compile(r'(?m)^(\| ✅ \| \[R0004\][^\n]*\|)$')
    text, count = pattern.subn(
        r'\1\n| ✅ | [R0005](R0005-runtime-target-specialization.md) | Target-specialized runtime primitives |',
        text,
        count=1)
    if count != 1:
        raise SystemExit("optimization index: R0004 row not found")
    index.write_text(text)
