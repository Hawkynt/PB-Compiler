using System.Diagnostics;
using PowerBasic.Compiler.Cli;
using PowerBasic.Compiler.Tests.CodeGen;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Units and libraries on every platform: a <c>$COMPILE UNIT</c> source compiled for the platform, a
/// program that <c>$LINK</c>s it - calling into it, and supplying a procedure the unit imports - and
/// the output, which must be DOS's. On DOS the unit is the 8086 PBU; on x86-32, x64 and the 6502 it
/// is the IR unit (<see cref="Ir.IrUnitFile"/>), and on the 6502 <c>--emit-obj</c>/<c>--emit-lib</c>
/// write the same thing as its object and library.
/// </summary>
[TestFixture]
public sealed class FlatTargetUnitTests {

  private const string Unit = """
    $COMPILE UNIT
    DECLARE FUNCTION Triple%(BYVAL x%)
    FUNCTION AddInts%(BYVAL a%, BYVAL b%)
      AddInts% = a% + b%
    END FUNCTION
    SUB Greet(n$)
      PRINT "hello "; n$; ERR
    END SUB
    FUNCTION SixTimes%(BYVAL x%)
      SixTimes% = Triple%(x%) * 2
    END FUNCTION
    """;

  private static string Program(string link) => $"""
    $LINK "{link}"
    DECLARE FUNCTION AddInts%(BYVAL a%, BYVAL b%)
    DECLARE SUB Greet(n$)
    DECLARE FUNCTION SixTimes%(BYVAL x%)
    FUNCTION Triple%(BYVAL x%)
      Triple% = x% * 3
    END FUNCTION
    PRINT AddInts%(2, 40); SixTimes%(7)
    Greet "unit"
    """;

  private const string Expected = " 42  42\nhello unit 0";

  private string _work = null!;

  [SetUp]
  public void CreateWorkDirectory() => _work = Directory.CreateTempSubdirectory("pbc-units-").FullName;

  [TearDown]
  public void DeleteWorkDirectory() => Directory.Delete(_work, recursive: true);

  private (int Code, string Error) Compile(string name, string source, string platform, params string[] extra) {
    var path = Path.Combine(_work, name);
    File.WriteAllText(path, source);
    var stderr = new StringWriter();
    string[] platformArgs = platform == "dos" ? [] : ["--platform", platform];
    var code = Driver.Run(["--dialect", "pb36", .. platformArgs, .. extra, path], TextWriter.Null, stderr);
    return (code, stderr.ToString());
  }

  private string RunProgram(string platform) {
    var program = Path.Combine(_work, "MAIN");
    switch (platform) {
      case "dos":
        return DosBoxRunner.Run(File.ReadAllBytes(program + ".EXE"));
      case "6502": {
        var result = Cpu6502.RunC64Program(File.ReadAllBytes(program + ".PRG"));
        Assert.That(result.Returned, Is.True, "the program returns to BASIC");
        return result.Output;
      }
      default: {
        Assume.That(OperatingSystem.IsLinux(), "the executables are Linux programs");
        using var process = Process.Start(new ProcessStartInfo(program) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
      }
    }
  }

  [TestCaseSource(typeof(FlatTargets), nameof(FlatTargets.Platforms))]
  public void Link_GivenAUnitCompiledForThePlatform_ThenTheProgramPrintsWhatDosPrints(string platform) {
    var unit = this.Compile("MATHU.BAS", Unit, platform);
    Assert.That(unit.Code, Is.Zero, unit.Error);
    var program = this.Compile("MAIN.BAS", Program("MATHU.PBU"), platform);
    Assert.That(program.Code, Is.Zero, program.Error);

    Assert.That(Vice.Normalize(this.RunProgram(platform)), Is.EqualTo(Expected));
  }

  [TestCaseSource(typeof(FlatTargets), nameof(FlatTargets.Platforms))]
  public void Link_GivenALibraryOfTheUnit_ThenTheProgramPrintsWhatDosPrints(string platform) {
    Assert.That(this.Compile("MATHU.BAS", Unit, platform).Code, Is.Zero);
    var stderr = new StringWriter();
    var built = Driver.Run(["lib", "build", Path.Combine(_work, "MATH.PBL"), Path.Combine(_work, "MATHU.PBU")], TextWriter.Null, stderr);
    Assert.That(built, Is.Zero, stderr.ToString());
    var program = this.Compile("MAIN.BAS", Program("MATH.PBL"), platform);
    Assert.That(program.Code, Is.Zero, program.Error);

    Assert.That(Vice.Normalize(this.RunProgram(platform)), Is.EqualTo(Expected));
  }

  [TestCase("--emit-obj", "MATHO.OBJ")]
  [TestCase("--emit-lib", "MATHO.LIB")]
  public void Link_GivenThe6502sObjectOrLibrary_ThenTheProgramPrintsWhatDosPrints(string option, string file) {
    var unit = this.Compile("MATHO.BAS", Unit.Replace("$COMPILE UNIT\n", "", StringComparison.Ordinal), "6502", option, "-o", Path.Combine(_work, file));
    Assert.That(unit.Code, Is.Zero, unit.Error);
    var program = this.Compile("MAIN.BAS", Program(file), "6502");
    Assert.That(program.Code, Is.Zero, program.Error);

    Assert.That(Vice.Normalize(this.RunProgram("6502")), Is.EqualTo(Expected));
  }

  [Test]
  public void Link_GivenADosUnitOnAnIrPlatform_ThenItIsRefusedWithTheReason() {
    Assert.That(this.Compile("MATHU.BAS", Unit, "dos").Code, Is.Zero);
    var program = this.Compile("MAIN.BAS", Program("MATHU.PBU"), "x64");

    Assert.Multiple(() => {
      Assert.That(program.Code, Is.Not.Zero);
      Assert.That(program.Error, Does.Contain("holds 8086 code"));
    });
  }

  [Test]
  public void Link_GivenAnIrUnitOnDos_ThenItIsRefusedWithTheReason() {
    Assert.That(this.Compile("MATHU.BAS", Unit, "x64").Code, Is.Zero);
    var program = this.Compile("MAIN.BAS", Program("MATHU.PBU"), "dos");

    Assert.Multiple(() => {
      Assert.That(program.Code, Is.Not.Zero);
      Assert.That(program.Error, Does.Contain("holds IR"));
    });
  }

  [Test]
  public void Compile_GivenAUnitWithModuleLevelCode_ThenItIsRefusedAsOnDos() {
    var unit = this.Compile("BAD.BAS", "$COMPILE UNIT\nPRINT 1\nSUB X\nEND SUB\n", "6502");

    Assert.Multiple(() => {
      Assert.That(unit.Code, Is.Not.Zero);
      Assert.That(unit.Error, Does.Contain("module-level code"));
    });
  }
}
