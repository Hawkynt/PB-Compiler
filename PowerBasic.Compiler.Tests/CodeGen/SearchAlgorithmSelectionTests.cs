using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class SearchAlgorithmSelectionTests {

  private static byte[] Compile(string body) {
    var source = "$OPTIMIZE SPEED\n" + body;
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { UseExperimentalBackend = false };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return image;
  }

  private static bool Contains(ReadOnlySpan<byte> image, ReadOnlySpan<byte> sequence) {
    if (sequence.Length == 0)
      return true;
    for (var i = 0; i <= image.Length - sequence.Length; ++i)
      if (image.Slice(i, sequence.Length).SequenceEqual(sequence))
        return true;
    return false;
  }

  private static byte[] HorspoolTable(string needle) {
    var result = new byte[256];
    Array.Fill(result, (byte)Math.Min(needle.Length, byte.MaxValue));
    for (var i = 0; i < needle.Length - 1; ++i)
      result[(byte)needle[i]] = (byte)Math.Min(needle.Length - 1 - i, byte.MaxValue);
    return result;
  }

  [TestCase("AB")]
  [TestCase("ABCD")]
  public void Emit_GivenShortConstantInstrNeedle_WhenOptimized_ThenScansCandidatesWithStringInstructions(string needle) {
    var image = Compile($"DIM s$, p%\nLINE INPUT s$\np% = INSTR(s$, \"{needle}\")\nPRINT p%\nEND");

    Assert.Multiple(() => {
      Assert.That(Contains(image, [0xF2, 0xAE]), Is.True, "short search should use REPNE SCASB for candidate starts");
      Assert.That(Contains(image, [0xF3, 0xA6]), Is.True, "short search should use REPE CMPSB to verify a candidate");
      Assert.That(Contains(image, HorspoolTable(needle)), Is.False, "short search should not pay for a 256-byte skip table");
    });
  }

  [Test]
  public void Emit_GivenLongConstantInstrNeedle_WhenOptimized_ThenEmbedsHorspoolSkipTable() {
    const string needle = "BEGIN";
    var image = Compile($"DIM s$, p%\nLINE INPUT s$\np% = INSTR(s$, \"{needle}\")\nPRINT p%\nEND");

    Assert.Multiple(() => {
      Assert.That(Contains(image, HorspoolTable(needle)), Is.True, "long constant search should embed its precomputed skip table");
      Assert.That(image, Does.Contain((byte)0xD7), "Horspool search should index the skip table with XLAT");
      Assert.That(Contains(image, [0xF3, 0xA6]), Is.True, "a last-byte hit should verify the pattern prefix with REPE CMPSB");
    });
  }

  [Test]
  public void Emit_GivenLongConstantInstrWithStart_WhenOptimized_ThenKeepsHorspoolSelection() {
    const string needle = "BEGIN";
    var image = Compile($"DIM s$, p%, k%\nLINE INPUT s$\nk% = 4\np% = INSTR(k%, s$, \"{needle}\")\nPRINT p%\nEND");

    Assert.That(Contains(image, HorspoolTable(needle)), Is.True);
  }

  [Test]
  public void Emit_GivenRuntimeInstrNeedle_WhenOptimized_ThenDoesNotEmbedConstantSkipTable() {
    const string needle = "BEGIN";
    var image = Compile("DIM s$, n$, p%\nLINE INPUT s$\nLINE INPUT n$\np% = INSTR(s$, n$)\nPRINT p%\nEND");

    Assert.That(Contains(image, HorspoolTable(needle)), Is.False, "runtime needles must retain the generic search path");
  }
}
