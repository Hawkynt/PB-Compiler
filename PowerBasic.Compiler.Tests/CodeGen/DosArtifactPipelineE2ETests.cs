using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Emit.Omf;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// End-to-end artifact gate for the mandatory multi-stage DOS path. These tests start at BASIC
/// source, require the x86-16 backend to own every emitted body, serialize the requested artifact,
/// consume it again, and execute the resulting DOS program where applicable.
/// </summary>
[TestFixture]
public sealed class DosArtifactPipelineE2ETests {

  private static SemanticModel Bind(string source, string file = "T.BAS") {
    var model = Binder.Bind(
      Parser.Parse(Lexer.Tokenize(source, file, Dialect.Pb36), file, Dialect.Pb36),
      Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  [Test]
  public void Program_GivenSameIrPipeline_WhenEmittedAsExeAndCom_ThenBothExecuteIdentically() {
    const string source = """
      PRINT "IR ARTIFACT"
      END
      """;

    var exeGen = new CodeGenerator(Bind(source)) { Optimize = true };
    var exe = exeGen.EmitExecutable();
    var comGen = new CodeGenerator(Bind(source)) { Optimize = true };
    var com = comGen.EmitCom();

    Assert.Multiple(() => {
      Assert.That(exeGen.Errors, Is.Empty, string.Join("; ", exeGen.Errors));
      Assert.That(comGen.Errors, Is.Empty, string.Join("; ", comGen.Errors));
      Assert.That(exeGen.BackendDeclines, Is.Empty);
      Assert.That(comGen.BackendDeclines, Is.Empty);
      Assert.That(exeGen.BackendRoutedNames, Does.Contain("main"));
      Assert.That(comGen.BackendRoutedNames, Does.Contain("main"));
      Assert.That(exe.Take(2), Is.EqualTo(new byte[] { (byte)'M', (byte)'Z' }));
      Assert.That(com.Take(2), Is.Not.EqualTo(new byte[] { (byte)'M', (byte)'Z' }));
    });

    Assert.That(Cpu8086.Run(com).Output, Is.EqualTo(Cpu8086.Run(exe).Output));
    Assert.That(Cpu8086.Run(com).Output.Trim(), Is.EqualTo("IR ARTIFACT"));
  }

  [Test]
  public void Unit_GivenPbuObjAndLibSerializations_ThenEveryArtifactLinksBackIntoARunningProgram() {
    const string unitSource = """
      $COMPILE UNIT
      FUNCTION AddOne%(BYVAL n%)
        AddOne% = n% + 1
      END FUNCTION
      """;
    var unitGen = new CodeGenerator(Bind(unitSource, "ADDONE.BAS")) { Optimize = true };
    var compiled = unitGen.EmitUnit("ADDONE");
    Assert.Multiple(() => {
      Assert.That(unitGen.Errors, Is.Empty, string.Join("; ", unitGen.Errors));
      Assert.That(unitGen.BackendDeclines, Is.Empty);
      Assert.That(unitGen.BackendRoutedNames, Does.Contain("AddOne"));
      Assert.That(compiled.Code, Is.Not.Empty);
    });

    using var pbuStream = new MemoryStream();
    compiled.Write(pbuStream);
    var pbuBytes = pbuStream.ToArray();
    pbuStream.Position = 0;
    var pbu = PbuFile.Read(pbuStream);

    var objBytes = OmfWriter.WriteObject(compiled);
    var obj = OmfToPbu.Convert(OmfReader.ReadObject(objBytes));

    var libBytes = OmfLibraryWriter.WriteLibrary([compiled]);
    var pbl = new PblFile();
    foreach (var module in OmfReader.ReadLibrary(libBytes))
      pbl.Units.Add(OmfToPbu.Convert(module));

    Assert.Multiple(() => {
      Assert.That(pbuBytes, Is.Not.Empty);
      Assert.That(pbu.Exports.Select(e => e.Name), Does.Contain("AddOne"));
      Assert.That(objBytes, Is.Not.Empty);
      Assert.That(obj.Exports.Select(e => e.Name), Does.Contain("AddOne"));
      Assert.That(libBytes, Is.Not.Empty);
      Assert.That(pbl.Units.SelectMany(u => u.Exports).Select(e => e.Name), Does.Contain("AddOne"));
    });

    Assert.That(RunMain([pbu], []).Trim(), Is.EqualTo("42"), "serialized PBU must link and execute");
    Assert.That(RunMain([obj], []).Trim(), Is.EqualTo("42"), "OMF OBJ must round-trip, link and execute");
    Assert.That(RunMain([], [pbl]).Trim(), Is.EqualTo("42"), "OMF LIB must be searched, linked and execute");
  }

  private static string RunMain(IReadOnlyList<PbuFile> units, IReadOnlyList<PblFile> libraries) {
    const string source = """
      DECLARE FUNCTION AddOne%(BYVAL n%)
      PRINT AddOne%(41)
      END
      """;
    var generator = new CodeGenerator(Bind(source, "MAIN.BAS")) { Optimize = true };
    var exe = generator.EmitExecutable(units, libraries);
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    Assert.That(generator.BackendRoutedNames, Does.Contain("main"));
    return Cpu8086.Run(exe).Output;
  }
}
