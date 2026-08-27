using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// $COMPILE UNIT / $LINK end to end: unit emission (exports, imports, fixups,
/// diagnostics), linking against PBU/PBL, and DOSBox round trips with
/// cross-unit numeric, string and BYREF calls.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class UnitLinkTests {

  private const string _MATH_UNIT_SOURCE = """
    $COMPILE UNIT

    FUNCTION AddInts%(BYVAL a%, BYVAL b%)
      AddInts% = a% + b%
    END FUNCTION

    SUB Bump(x%)
      x% = x% + 7
    END SUB

    FUNCTION Greet$(name$)
      Greet$ = "HI " + name$ + "!"
    END FUNCTION
    """;

  private const string _LINK_DEMO_SOURCE = """
    DECLARE FUNCTION AddInts%(BYVAL a%, BYVAL b%)
    DECLARE SUB Bump(x%)
    DECLARE FUNCTION Greet$(name$)

    PRINT AddInts%(2, 3)
    n% = 35
    CALL Bump(n%)
    PRINT n%
    PRINT Greet$("UNIT")
    """;

  private const string _EXPECTED_DEMO_OUTPUT = " 5\n 42\nHI UNIT!\n";

  #region helpers

  private sealed class MemorySourceProvider(string text) : ISourceProvider {
    public bool TryReadSource(string name, string? includedFrom, out string sourceText, out string resolvedName) {
      sourceText = text;
      resolvedName = name;
      return true;
    }
  }

  private static SemanticModel Bind(string source, string fileName) {
    var tokens = Preprocessor.Expand(fileName, new MemorySourceProvider(source));
    var unit = Parser.Parse(tokens, fileName);
    var model = Binder.Bind(unit);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static PbuFile CompileUnit(string source, string name) {
    var generator = new CodeGenerator(Bind(source, name + ".BAS"));
    var unit = generator.EmitUnit(name);
    Assert.That(generator.Errors, Is.Empty, "unit codegen: " + string.Join("; ", generator.Errors));

    // always exercise the on-disk format on the way through
    using var stream = new MemoryStream();
    unit.Write(stream);
    stream.Position = 0;
    return PbuFile.Read(stream);
  }

  private static byte[] CompileMain(string source, IReadOnlyList<PbuFile> units,
      IReadOnlyList<PblFile> libraries, out List<Diagnostic> errors, bool routed = false) {
    var generator = new CodeGenerator(Bind(source, "MAIN.BAS")) { UseExperimentalBackend = routed };
    var exe = generator.EmitExecutable(units, libraries);
    errors = generator.Errors;
    return exe;
  }

  #endregion

  #region unit emission

  [Test]
  public void EmitUnit_GivenProcedures_WhenCompiled_ThenAllExportedWithSignatureHashes() {
    var unit = CompileUnit(_MATH_UNIT_SOURCE, "MATHUNIT");

    Assert.Multiple(() => {
      Assert.That(unit.Exports, Has.Count.EqualTo(3));
      var add = unit.Exports.Single(e => e.Name.Equals("AddInts", StringComparison.OrdinalIgnoreCase));
      Assert.That(add.Kind, Is.EqualTo(PbuExportKind.Function));
      Assert.That(add.SignatureHash, Is.EqualTo(PbuFile.HashSignature("ADDINTS(byval:integer,byval:integer)->integer")));
      var bump = unit.Exports.Single(e => e.Name.Equals("Bump", StringComparison.OrdinalIgnoreCase));
      Assert.That(bump.Kind, Is.EqualTo(PbuExportKind.Sub));
      Assert.That(unit.Exports.All(e => e.CodeOffset < unit.Code.Length), "entry points must lie inside the code image");
    });
  }

  [Test]
  public void EmitUnit_GivenStringHandling_WhenCompiled_ThenRuntimeSymbolsImported() {
    var unit = CompileUnit(_MATH_UNIT_SOURCE, "MATHUNIT");

    Assert.Multiple(() => {
      Assert.That(unit.Imports.Select(i => i.Name), Does.Contain("rt_strcat"), "string concatenation must import the runtime");
      Assert.That(unit.Imports.Where(i => i.Name.StartsWith("rt_", StringComparison.OrdinalIgnoreCase)).All(i => i.SignatureHash == 0), "runtime imports are unchecked (hash 0)");
      Assert.That(unit.Fixups.Any(f => f.Kind == PbuFixupKind.ImportCall), "runtime calls must be import-call fixups");
      Assert.That(unit.Data.Length, Is.GreaterThan(0), "the string literal lives in the unit's data area");
      Assert.That(unit.Fixups.Any(f => f.Kind == PbuFixupKind.DataOffset), "literal references must be data-offset fixups");
    });
  }

  [Test]
  public void EmitUnit_GivenDeclaredButUndefinedProcedure_WhenCalled_ThenImportCarriesDeclareSignature() {
    const string source = """
      $COMPILE UNIT
      DECLARE FUNCTION Triple%(BYVAL x%)

      FUNCTION SixTimes%(BYVAL x%)
        SixTimes% = Triple%(x%) + Triple%(x%)
      END FUNCTION
      """;

    var unit = CompileUnit(source, "CHAIN");

    var import = unit.Imports.Single(i => i.Name.Equals("Triple", StringComparison.OrdinalIgnoreCase));
    Assert.That(import.SignatureHash, Is.EqualTo(PbuFile.HashSignature("TRIPLE(byval:integer)->integer")));
  }

  [Test]
  public void EmitUnit_GivenModuleLevelExecutableCode_WhenCompiled_ThenDiagnostic() {
    const string source = """
      $COMPILE UNIT
      PRINT "NOT ALLOWED"

      SUB Dummy(x%)
      END SUB
      """;

    var generator = new CodeGenerator(Bind(source, "BAD.BAS"));
    generator.EmitUnit("BAD");

    Assert.That(generator.Errors.Select(e => e.Message), Has.Some.Contains("module-level code"));
  }

  [Test]
  public void SignatureOf_GivenMixedParameters_WhenRendered_ThenCanonicalFormat() {
    var proc = new ProcedureSymbol("Foo", isFunction: true) { ReturnType = PbType.Long };
    proc.Parameters.Add(new("a", PbType.Integer, VariableStorage.Parameter) { ByVal = true });
    proc.Parameters.Add(new("b", PbType.String, VariableStorage.Parameter));
    proc.Parameters.Add(new("c", PbType.Any, VariableStorage.Parameter));
    proc.Parameters.Add(new("d", new ArrayType(PbType.Double, null, 1), VariableStorage.Parameter));

    Assert.That(CodeGenerator.SignatureOf(proc), Is.EqualTo("Foo(byval:integer,byref:string,byref:any,byref:double())->long"));
  }

  [Test]
  public void SignatureOf_GivenSub_WhenRendered_ThenNoReturnSuffix() {
    var proc = new ProcedureSymbol("Bar", isFunction: false);
    Assert.That(CodeGenerator.SignatureOf(proc), Is.EqualTo("Bar()"));
  }

  #endregion

  #region link diagnostics

  [Test]
  public void EmitExecutable_GivenMismatchedDeclare_WhenLinked_ThenSignatureMismatchDiagnostic() {
    const string mismatched = """
      DECLARE FUNCTION AddInts&(BYVAL a&, BYVAL b&)
      PRINT AddInts&(2, 3)
      """;

    var unit = CompileUnit(_MATH_UNIT_SOURCE, "MATHUNIT");
    CompileMain(mismatched, [unit], [], out var errors, routed: true);

    Assert.That(errors.Select(e => e.Message), Has.Some.Contains("signature mismatch"));
  }

  [Test]
  public void EmitExecutable_GivenUnresolvedDeclare_WhenLinked_ThenUnresolvedSymbolDiagnostic() {
    const string callsMissing = """
      DECLARE SUB Missing(x%)
      n% = 1
      CALL Missing(n%)
      """;

    var unit = CompileUnit(_MATH_UNIT_SOURCE, "MATHUNIT");
    CompileMain(callsMissing, [unit], [], out var errors, routed: true);

    Assert.That(errors.Select(e => e.Message), Has.Some.Contains("unresolved symbol"));
  }

  [Test]
  public void EmitExecutable_GivenNoLinkTargets_WhenExternalCalled_ThenCompileDiagnostic() {
    const string source = """
      DECLARE SUB Missing(x%)
      n% = 1
      CALL Missing(n%)
      """;

    var generator = new CodeGenerator(Bind(source, "MAIN.BAS")) { UseExperimentalBackend = true };
    generator.EmitExecutable();

    Assert.Multiple(() => {
      Assert.That(generator.Errors.Select(e => e.Message), Has.Some.Contains("external procedure"));
      Assert.That(generator.BackendRoutedNames, Does.Not.Contain("main"));
      Assert.That(generator.BackendDeclines.Any(d => d.Name == "main" && d.Reason.Contains("no link symbol")),
        Is.True, string.Join("; ", generator.BackendDeclines));
    });
  }

  #endregion

  #region DOSBox round trips

  [Test]
  public void Link_GivenUnitAndMain_WhenRunUnderDosBox_ThenCrossUnitCallsProduceGoldenOutput() {
    var unit = CompileUnit(_MATH_UNIT_SOURCE, "MATHUNIT");
    var exe = CompileMain(_LINK_DEMO_SOURCE, [unit], [], out var errors);
    Assert.That(errors, Is.Empty, "link: " + string.Join("; ", errors));

    var output = DosBoxRunner.Normalize(DosBoxRunner.Run(exe));

    Assert.That(output, Is.EqualTo(_EXPECTED_DEMO_OUTPUT));
  }

  [Test]
  public void Link_GivenSameUnitInsideLibrary_WhenRunUnderDosBox_ThenSameGoldenOutput() {
    var library = new PblFile();
    library.Units.Add(CompileUnit(_MATH_UNIT_SOURCE, "MATHUNIT"));
    using var stream = new MemoryStream();
    library.Write(stream);
    stream.Position = 0;

    var exe = CompileMain(_LINK_DEMO_SOURCE, [], [PblFile.Read(stream)], out var errors);
    Assert.That(errors, Is.Empty, "link: " + string.Join("; ", errors));

    var output = DosBoxRunner.Normalize(DosBoxRunner.Run(exe));

    Assert.That(output, Is.EqualTo(_EXPECTED_DEMO_OUTPUT));
  }

  #endregion
}
