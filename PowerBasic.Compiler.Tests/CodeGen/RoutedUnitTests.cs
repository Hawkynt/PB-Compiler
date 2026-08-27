using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// A <c>$COMPILE UNIT</c> compiled through the x86-16 back end, linked, and run.
///
/// Units were excluded from routing outright, alongside <c>_allowExternalCalls</c>. The reason did
/// not hold for procedures: a unit exports its procedures with the STACK convention - they are
/// called from outside, so <c>OptRegParm</c> never converts them - and that is exactly the ABI this
/// back end emits. An imported callee is already handled by the routing fixpoint, which routes a
/// function only if every callee is routed, so a call to something the unit does not define excludes
/// it by construction rather than by a flag.
///
/// The thing that matters is the artefact: a <c>.PBU</c> built through the IR path has to link
/// against a main module built the ordinary way and produce the same output. Compiling is not the
/// test - the test is that the two halves still fit together.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class RoutedUnitTests {

  private const string _unit = """
    $COMPILE UNIT

    FUNCTION AddInts%(BYVAL a%, BYVAL b%)
      AddInts% = a% + b%
    END FUNCTION

    FUNCTION Poly%(BYVAL x%)
      Poly% = x% * x% + 3 * x% - 2
    END FUNCTION

    SUB Bump(x%)
      x% = x% + 7
    END SUB

    FUNCTION Greet$(name$)
      Greet$ = "HI " + name$ + "!"
    END FUNCTION
    """;

  private const string _main = """
    DECLARE FUNCTION AddInts%(BYVAL a%, BYVAL b%)
    DECLARE FUNCTION Poly%(BYVAL x%)
    DECLARE SUB Bump(x%)
    DECLARE FUNCTION Greet$(name$)

    PRINT AddInts%(2, 3)
    PRINT Poly%(5)
    n% = 35
    CALL Bump(n%)
    PRINT n%
    PRINT Greet$("UNIT")
    END
    """;

  private sealed class MemorySource(string text) : ISourceProvider {
    public bool TryReadSource(string name, string? includedFrom, out string sourceText, out string resolvedName) {
      sourceText = text;
      resolvedName = name;
      return true;
    }
  }

  private static SemanticModel Bind(string source, string file) {
    var model = Binder.Bind(Parser.Parse(Preprocessor.Expand(file, new MemorySource(source)), file));
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static PbuFile CompileUnit(bool routed, out IEnumerable<string> routedNames) {
    var generator = new CodeGenerator(Bind(_unit, "U.BAS")) { Optimize = true, UseExperimentalBackend = routed };
    var unit = generator.EmitUnit("MATHU");
    Assert.That(generator.Errors, Is.Empty, "unit codegen: " + string.Join("; ", generator.Errors));
    routedNames = generator.BackendRoutedNames.ToList();

    using var stream = new MemoryStream();
    unit.Write(stream);
    stream.Position = 0;
    return PbuFile.Read(stream);
  }

  private static string LinkAndRun(IReadOnlyList<PbuFile> units, IReadOnlyList<PblFile> libraries,
      bool routed, bool optimize, out IReadOnlyList<string> routedNames) {
    var generator = new CodeGenerator(Bind(_main, "MAIN.BAS")) {
      Optimize = optimize,
      UseExperimentalBackend = routed,
    };
    var exe = generator.EmitExecutable(units, libraries);
    Assert.That(generator.Errors, Is.Empty, "link: " + string.Join("; ", generator.Errors));
    routedNames = generator.BackendRoutedNames.ToList();
    return Cpu8086.Run(exe).Output.Trim().Replace("\r\n", "|");
  }

  private static string LinkAndRun(PbuFile unit)
    => LinkAndRun([unit], [], routed: false, optimize: true, out _);

  [Test]
  public void EmitUnit_GivenTheBackEnd_ThenItRoutesTheUnitsProcedures() {
    CompileUnit(routed: true, out var names);

    Assert.That(names, Is.Not.Empty, "a unit's procedures are ordinary stack-ABI functions - they should route");
  }

  /// <summary>The artefact contract: a routed .PBU links and behaves like an unrouted one.</summary>
  [Test]
  public void EmitUnit_GivenTheBackEnd_ThenTheLinkedProgramBehavesIdentically() {
    var plain = LinkAndRun(CompileUnit(routed: false, out _));
    var routed = LinkAndRun(CompileUnit(routed: true, out var names));

    Assert.That(names, Is.Not.Empty, "nothing routed, so this proves nothing");
    Assert.That(routed, Is.EqualTo(plain));
    // PB pads a positive number with a leading sign space and a trailing one
    Assert.That(routed, Is.EqualTo("5 | 38 | 42 |HI UNIT!"));
  }

  [TestCase(false, false)]
  [TestCase(false, true)]
  [TestCase(true, false)]
  [TestCase(true, true)]
  public void EmitExecutable_GivenLinkedDefaultAbiProcedures_WhenBackEndEnabled_ThenMainRoutesAndMatchesDirect(
      bool inLibrary, bool optimize) {
    var unit = CompileUnit(routed: true, out _);
    var units = inLibrary ? Array.Empty<PbuFile>() : [unit];
    IReadOnlyList<PblFile> libraries = inLibrary ? [new PblFile { Units = { unit } }] : [];
    var direct = LinkAndRun(units, libraries, routed: false, optimize, out _);
    var routed = LinkAndRun(units, libraries, routed: true, optimize, out var routedNames);

    Assert.Multiple(() => {
      Assert.That(routedNames, Does.Contain("main"),
        "a linked BASIC/PASCAL declaration has the same stack ABI as the routed call site");
      Assert.That(routed, Is.EqualTo(direct));
      Assert.That(routed, Is.EqualTo("5 | 38 | 42 |HI UNIT!"));
    });
  }

  /// <summary>Every procedure stays exported: routing must not change what the unit offers.</summary>
  [Test]
  public void EmitUnit_GivenTheBackEnd_ThenTheExportsAreUnchanged() {
    var plain = CompileUnit(routed: false, out _);
    var routed = CompileUnit(routed: true, out _);

    Assert.That(routed.Exports.Select(e => e.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
      Is.EqualTo(plain.Exports.Select(e => e.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)));
    foreach (var export in plain.Exports)
      Assert.That(routed.Exports.Single(e => e.Name.Equals(export.Name, StringComparison.OrdinalIgnoreCase)).SignatureHash,
        Is.EqualTo(export.SignatureHash), $"{export.Name}'s signature must not change");
  }
}
