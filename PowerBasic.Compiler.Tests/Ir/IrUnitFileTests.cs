using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// <see cref="IrUnitFile"/> writes a module and reads back the same module: every battery and
/// differential program that lowers survives the round trip with its printed IR unchanged, and the
/// file the second module writes is byte for byte the first one.
/// </summary>
[TestFixture]
public sealed class IrUnitFileTests {

  private static readonly string _repoRoot =
    Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  private static IEnumerable<TestCaseData> Programs() {
    var tests = Path.Combine(_repoRoot, "tests");
    if (!Directory.Exists(tests))
      yield break;
    foreach (var file in Directory.GetFiles(tests, "*.BAS").Concat(Directory.GetFiles(Path.Combine(tests, "diff"), "*.BAS", SearchOption.AllDirectories)).Order())
      yield return new TestCaseData(Path.GetRelativePath(tests, file)).SetName($"RoundTrip_GivenAProgram_ThenTheModuleIsUnchanged({Path.GetRelativePath(tests, file)})");
  }

  private static Dialect DialectOf(string relative) {
    var folder = Path.GetDirectoryName(relative)?.Split(Path.DirectorySeparatorChar).LastOrDefault() ?? "";
    return folder.Length > 0 && folder != "diff" && Enum.TryParse<Dialect>(folder, ignoreCase: true, out var dialect) ? dialect : Dialect.Pb35;
  }

  [TestCaseSource(nameof(Programs))]
  public void RoundTrip_GivenAProgram_ThenTheModuleIsUnchanged(string relative) {
    var file = Path.Combine(_repoRoot, "tests", relative);
    var dialect = DialectOf(relative);
    SemanticModel model;
    try {
      model = Binder.Bind(Parser.Parse(Preprocessor.Expand(file, new FileSourceProvider(), dialect), file, dialect), dialect);
    } catch (Exception exception) {
      Assume.That(false, $"the front end does not take it: {exception.Message}");
      return;
    }
    var module = IrLowering.TryLowerModule(model, out var declined);
    Assume.That(module, Is.Not.Null, declined);

    var bytes = IrUnitFile.Write(module!);
    var read = IrUnitFile.Read(bytes);

    Assert.Multiple(() => {
      Assert.That(IrPrinter.Print(read), Is.EqualTo(IrPrinter.Print(module!)));
      Assert.That(IrUnitFile.Write(read), Is.EqualTo(bytes), "the module read back writes the same file");
    });
  }

  [Test]
  public void Read_GivenSomethingElse_ThenItSaysSoRatherThanGuessing() {
    Assert.Multiple(() => {
      Assert.That(IrUnitFile.IsIrUnit("PBU\0"u8), Is.False);
      Assert.Throws<InvalidDataException>(() => IrUnitFile.Read("PBU\0something"u8.ToArray()));
    });
  }
}
