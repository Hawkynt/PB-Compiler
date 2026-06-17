using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Source-to-DOSBox golden tests: every tests/NAME.BAS is compiled through the
/// full pipeline and its DOSBox stdout compared with tests/NAME.expected.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class GoldenTests {

  private static string TestsDirectory
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "tests");

  internal static byte[] Compile(string sourcePath) {
    var provider = new SearchPathSourceProvider([Path.GetDirectoryName(Path.GetFullPath(sourcePath))!]);
    var tokens = Preprocessor.Expand(sourcePath, provider);
    var unit = Parser.Parse(tokens, sourcePath);
    var model = Binder.Bind(unit);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return exe;
  }

  [TestCase("HELLO")]
  [TestCase("ARITH")]
  [TestCase("CTRL")]
  [TestCase("STRINGS")]
  [TestCase("STRBOUND")]
  [TestCase("STRHEAP")]
  [TestCase("SUBFN")]
  [TestCase("ARRAY")]
  [TestCase("FILEIO1")]
  [TestCase("LOWLEVEL")]
  [TestCase("INTREG")]
  [TestCase("DATAREAD")]
  [TestCase("ONERR")]
  [TestCase("ONERRNXT")]
  [TestCase("RANDFILE")]
  [TestCase("PRTUSING")]
  [TestCase("INPUTS")]
  public void Golden_GivenSource_WhenRunUnderDosBox_ThenOutputMatchesExpected(string name) {
    var source = Path.Combine(TestsDirectory, name + ".BAS");
    var expectedFile = Path.Combine(TestsDirectory, name + ".expected");
    var stdinFile = Path.Combine(TestsDirectory, name + ".IN");
    Assume.That(File.Exists(source), $"{source} missing");
    Assert.That(File.Exists(expectedFile), $"{expectedFile} missing");

    var exe = Compile(source);
    var stdin = File.Exists(stdinFile) ? File.ReadAllText(stdinFile) : null;
    var (rawOutput, _) = DosBoxRunner.RunWithFiles(exe, [], stdinText: stdin);
    var output = DosBoxRunner.Normalize(rawOutput);
    var expected = DosBoxRunner.Normalize(File.ReadAllText(expectedFile)).TrimEnd('\n') + "\n";
    Assert.That(output, Is.EqualTo(expected));
  }
}
