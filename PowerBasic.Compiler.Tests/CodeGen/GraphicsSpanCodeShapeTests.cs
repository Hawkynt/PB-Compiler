using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class GraphicsSpanCodeShapeTests {
  [TestCase("graphics", 86, 2)]
  [TestCase("graphics", 386, 2)]
  [TestCase("paint", 86, 1)]
  [TestCase("paint", 386, 1)]
  public void Emit_GivenOptimizedPackedSpans_ThenUsesOnlyTheSelectedCpuRepeatWidth(
      string section, int cpu, int spans) {
    var optimized = EmitSection(section, cpu, true);
    var plain = EmitSection(section, cpu, false);
    byte[] words = [0xF3, 0xAB];
    byte[] dwords = [0xF3, 0x66, 0xAB];
    Assert.Multiple(() => {
      Assert.That(Count(optimized, cpu == 386 ? dwords : words), Is.EqualTo(spans));
      Assert.That(Count(optimized, cpu == 386 ? words : dwords), Is.Zero);
      Assert.That(Count(plain, words), Is.Zero);
      Assert.That(Count(plain, dwords), Is.Zero);
    });
  }

  private static byte[] EmitSection(string section, int cpu, bool optimize) {
    var assembler = new Assembler();
    var runtime = new DosRuntime {
      EnableTargetOptimizations = optimize,
      Target = new RuntimeTarget(cpu, cpu == 386 ? RuntimeCpuFeatures.GeneralPurpose32 : RuntimeCpuFeatures.None),
    };
    var main = assembler.DefineLabel("test_main");
    runtime.EmitEntry(assembler, main);
    assembler.MarkLabel(main);
    assembler.Ret();
    var start = 0;
    var length = 0;
    runtime.EmitProcedures(assembler, onSection: (name, first, last) => {
      if (name != section)
        return;
      start = first;
      length = last - first;
    });
    runtime.EmitConstants(assembler);
    runtime.EmitData(assembler);
    assembler.MarkLabel("rt_stackmin");
    assembler.Dw(0);
    assembler.MarkLabel("rt_memend");
    assembler.MarkLabel("rt_dataptr");
    assembler.Dw(assembler.Lbl("rt_datapool"));
    assembler.MarkLabel("rt_datapool");
    assembler.MarkLabel("rt_dataend");
    return assembler.ToArray().AsSpan(start, length).ToArray();
  }

  private static int Count(byte[] bytes, byte[] pattern) {
    var count = 0;
    for (var index = 0; index <= bytes.Length - pattern.Length; ++index)
      if (bytes.AsSpan(index, pattern.Length).SequenceEqual(pattern))
        ++count;
    return count;
  }
}
