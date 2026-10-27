using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
[NonParallelizable]
public sealed class ExecAllocationStateTests {
  private const string _CHAIN_EXPECTED =
    "first pass\r\nsecond pass\r\n 1  123456  2.5 \r\nhello chain\r\n";

  // AX=5800h (22528) reads DOS's allocation strategy; the child ends before
  // this probe so the batch file independently observes its actual exit code.
  private const string _SHELL_SOURCE = """
    IF LEN(COMMAND$) > 0 THEN END 37
    DIM before%, after%
    ! mov ax, 22528
    ! int 33
    ! mov before%, ax
    SHELL "CHECK.BAT"
    ! mov ax, 22528
    ! int 33
    ! mov after%, ax
    OPEN "POLICY.TXT" FOR OUTPUT AS #1
    PRINT #1, before%; after%
    CLOSE #1
    END
    """;

  private static string ReadChainSource() => File.ReadAllText(Path.GetFullPath(Path.Combine(
    TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "tests", "diff", "DIFF21.BAS")));

  private static byte[] Compile(string source, bool optimize, bool com, bool auto = false) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36),
      Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var compiler = new CodeGenerator(model) {
      Optimize = optimize,
      Container = auto ? DosContainer.Auto : DosContainer.Exe,
    };
    var image = com ? compiler.EmitCom() : compiler.EmitExecutable();
    Assert.That(compiler.Errors, Is.Empty, string.Join("; ", compiler.Errors));
    Assert.That(image.AsSpan(0, 2).SequenceEqual("MZ"u8), Is.EqualTo(!com && !auto));
    return image;
  }

  [TestCase(false, false)]
  [TestCase(false, true)]
  [TestCase(true, false)]
  [TestCase(true, true)]
  public void Chain_GivenCommonSelfHandoff_ThenBothPassesCompleteUnderDos(bool optimize, bool com) {
    var result = DosBoxRunner.RunWithFiles(Compile(ReadChainSource(), optimize, com), ["RESULT.TXT", "PBCHAIN.$$$"]);
    Assert.That(result.Files.GetValueOrDefault("RESULT.TXT"), Is.EqualTo(_CHAIN_EXPECTED));
    Assert.That(result.Files.ContainsKey("PBCHAIN.$$$"), Is.False, "the child must consume the handoff");
  }

  [Test]
  public void Chain_GivenAutomaticOptimizedContainer_ThenCommonHandoffCompletesUnderDos() {
    var result = DosBoxRunner.RunWithFiles(Compile(ReadChainSource(), true, false, true), ["RESULT.TXT"]);
    Assert.That(result.Files.GetValueOrDefault("RESULT.TXT"), Is.EqualTo(_CHAIN_EXPECTED));
  }

  [TestCase(false, false, -1)]
  [TestCase(false, true, -1)]
  [TestCase(true, false, -1)]
  [TestCase(true, true, -1)]
  [TestCase(true, false, 0)]
  [TestCase(true, true, 0)]
  [TestCase(true, false, 1)]
  [TestCase(true, true, 1)]
  public void Shell_GivenChildExitCode_ThenPreservesItAndTheParentAllocationPolicy(
      bool optimize, bool com, int allocationStrategy) {
    var batch = "T.EXE CHILD\r\nIF ERRORLEVEL 38 GOTO BAD\r\nIF ERRORLEVEL 37 GOTO GOOD\r\n"
      + ":BAD\r\nECHO FAIL>CODE.TXT\r\nGOTO FIN\r\n"
      + ":GOOD\r\nECHO PASS>CODE.TXT\r\n:FIN\r\n";
    var source = allocationStrategy < 0 ? _SHELL_SOURCE : _SHELL_SOURCE.Replace("DIM before%, after%",
      $"DIM before%, after%\n! mov bx, {allocationStrategy}\n! mov ax, 22529\n! int 33",
      StringComparison.Ordinal);
    var result = DosBoxRunner.RunWithFiles(Compile(source, optimize, com), ["CODE.TXT", "POLICY.TXT"],
      extraFiles: new Dictionary<string, string> { ["CHECK.BAT"] = batch });
    Assert.That(result.Files.GetValueOrDefault("CODE.TXT"), Is.EqualTo("PASS\r\n"));
    var policies = result.Files["POLICY.TXT"].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    Assert.That(policies, Has.Length.EqualTo(2));
    Assert.That(policies[1], Is.EqualTo(policies[0]), "SHELL must restore the parent's allocation preference");
  }
}
