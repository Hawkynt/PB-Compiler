using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class MzSegmentLimitTests {

  private static CodeGenerator CreateGenerator(string source, bool optimize) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    return new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Emit_GivenAnOversizedPhysicalImage_ThenReportsTheSegmentLimit(bool optimize) {
    var source = $"PRINT \"{new string('A', 32700)}\"\nPRINT \"{new string('B', 32700)}\"\nEND";
    var generator = CreateGenerator(source, optimize);
    var image = generator.EmitExecutable();
    Assert.That(image.Length, Is.Zero, "a single-segment EXE must not contain wrapped data offsets");
    Assert.That(generator.Errors.Any(error => error.Message.Contains("65536", StringComparison.Ordinal)
      && error.Message.Contains("EXE", StringComparison.Ordinal)), Is.True, string.Join("; ", generator.Errors));
  }

  [Test]
  public void Emit_GivenAnOversizedVirtualBss_ThenReportsTheResidentSegmentLimit() {
    // The physical literals fit, but the live runtime string descriptor table also needs near BSS.
    // Baseline b67a4b76 emitted a 64607-byte MZ with rt_bss_end=67335, beyond the near segment.
    var source = $"PRINT \"{new string('A', 29000)}\"\nPRINT \"{new string('B', 29000)}\"\n"
      + "INPUT text$\nPRINT text$\nEND";
    var generator = CreateGenerator(source, true);
    var image = generator.EmitExecutable();
    Assert.That(image.Length, Is.Zero, "virtual BSS still occupies the program's near segment");
    Assert.That(generator.Errors.Any(error => error.Message.Contains("65536", StringComparison.Ordinal)
      && error.Message.Contains("EXE", StringComparison.Ordinal)), Is.True, string.Join("; ", generator.Errors));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Emit_GivenAValidResidentImage_ThenKeepsTheMzArtifactExecutable(bool optimize) {
    var generator = CreateGenerator("PRINT \"fits\"\nEND", optimize);
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(image.AsSpan(0, 2).SequenceEqual("MZ"u8), Is.True);
    Assert.That(Cpu8086.Run(image).Output.Trim(), Is.EqualTo("fits"));
  }
}
