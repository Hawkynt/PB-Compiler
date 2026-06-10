namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// The TESTLIB battery: tests/MINI.BAS ($INCLUDEs tests/TESTLIB.BI) is compiled
/// through the full pipeline, run under DOSBox, and the UNITTEST.LOG it writes
/// is checked for the expected suite/pass/result lines and zero failures.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class MiniSuiteTests {

  private const int _EXPECTED_ASSERTIONS = 24;

  [Test]
  public void Mini_GivenTestlibSuite_WhenRunUnderDosBox_ThenLogShowsAllAssertionsPassing() {
    var source = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "tests", "MINI.BAS");
    Assert.That(File.Exists(source), $"{source} missing");

    var exe = GoldenTests.Compile(source);
    var (output, files) = DosBoxRunner.RunWithFiles(exe, ["UNITTEST.LOG"]);

    Assert.That(DosBoxRunner.Normalize(output), Does.Contain("MINI DONE"));
    Assert.That(files, Does.ContainKey("UNITTEST.LOG"), "the suite must create its log");

    var log = DosBoxRunner.Normalize(files["UNITTEST.LOG"]);
    TestContext.Out.WriteLine(log);
    Assert.Multiple(() => {
      Assert.That(log, Does.Contain("[SUITE] MINI"));
      Assert.That(log, Does.Not.Contain("[FAIL]"));
      Assert.That(log, Does.Contain("[PASS] StringBasics :: concat"));
      Assert.That(log, Does.Contain("[PASS] Procs :: recursion"));
      Assert.That(log, Does.Contain("[PASS] Arrays :: dynamic element"));
      Assert.That(log, Does.Contain("[RESULT] MINI assertions= 24"));
      Assert.That(log, Does.Contain("failed= 0"));
      Assert.That(log.Split("[PASS]").Length - 1, Is.EqualTo(_EXPECTED_ASSERTIONS), "every assertion must be logged as a pass");
    });
  }
}
