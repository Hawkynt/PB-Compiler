using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The metastatements that describe the COMPILATION rather than the run - <c>$DYNAMIC</c>,
/// <c>$STATIC</c>, <c>$OPTION</c>, <c>$DIM</c>, <c>$STACK</c> - over both back ends.
///
/// <para>
/// Each is consumed before or beside the emission of the statement list: <c>$DYNAMIC</c>/<c>$STATIC</c>
/// and <c>$OPTION SIGNED</c> by the BINDER, <c>$OPTION VIDEO</c> through a model flag, and
/// <c>$OPTION CNTLBREAK</c> and <c>$STACK</c> by a codegen pre-pass over <c>model.MetaStatements</c>.
/// None of them is an instruction, so a routed module body - which never walks the statement list -
/// gets exactly what a directly emitted one gets. The IR lowering nonetheless RAISED on all five,
/// which took the whole module off the routed path over a directive with nothing to emit.
/// </para>
/// <para>
/// <b>These fixtures compile through the DRIVER's front end, and that is the point of the file.</b>
/// <c>Lexer.Tokenize</c> does not run <c>Preprocessor.Expand</c>, and a metastatement written the
/// classic way - <c>'$DYNAMIC</c>, inside a comment - is then just a comment. A test built on the
/// lexer would have compiled a program with no directive in it and passed either way.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendMetaStatementTests {

  /// <param name="Label">how the case reads in the test list.</param>
  /// <param name="Source">the whole program.</param>
  /// <param name="Expected">what both builds must print.</param>
  public sealed record Program(string Label, string Source, string Expected) {
    public override string ToString() => this.Label;
  }

  private static readonly Program[] _programs = [
    new("$STATIC", """
      $STATIC
      DIM a%(1 TO 4)
      FOR i% = 1 TO 4 : a%(i%) = i% * 3 : NEXT
      PRINT a%(2); a%(4)
      END
      """, " 6  12"),
    // $DYNAMIC makes the array dynamic in the BINDER, so what routes here is a REDIMable array
    // reached through its descriptor rather than a frame slot
    new("$DYNAMIC with REDIM", """
      $DYNAMIC
      DIM a%(1 TO 4)
      REDIM a%(1 TO 6)
      FOR i% = 1 TO 6 : a%(i%) = i% * 3 : NEXT
      PRINT a%(2); a%(6); UBOUND(a%)
      END
      """, " 6  18  6"),
    new("$OPTION SIGNED", """
      $OPTION SIGNED
      DIM v%
      v% = 7
      PRINT v%; VARPTR(v%) <> 0
      END
      """, " 7 -1"),
    new("$OPTION CNTLBREAK OFF", """
      $OPTION CNTLBREAK OFF
      PRINT 1 + 1
      END
      """, " 2"),
    new("$DIM ALL", """
      $DIM ALL
      DIM v%
      v% = 5
      PRINT v%
      END
      """, " 5"),
    new("$STACK", """
      $STACK 4096
      DIM v%
      v% = 9
      PRINT v%
      END
      """, " 9"),
  ];

  private static SemanticModel Bind(string source) {
    var directory = Path.Combine(Path.GetTempPath(), "pbc-meta-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      var file = Path.Combine(directory, "T.BAS");
      File.WriteAllText(file, source);
      // Preprocessor.Expand, not Lexer.Tokenize: it is the driver's own entry point, and the only
      // one that turns a metastatement into a MetaStmt rather than into a comment.
      var model = Binder.Bind(Parser.Parse(Preprocessor.Expand(file, new FileSourceProvider(), Dialect.Pb36),
        "T.BAS", Dialect.Pb36), Dialect.Pb36);
      Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
      return model;
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  [TestCaseSource(nameof(_programs))]
  public void Compile_GivenAMetastatementProgram_WhenRoutingIsEnabled_ThenTheModuleBodyRoutesAndAgrees(Program program) {
    foreach (var optimize in new[] { true, false }) {
      var direct = new CodeGenerator(Bind(program.Source)) { Optimize = optimize, UseExperimentalBackend = false };
      var routed = new CodeGenerator(Bind(program.Source)) { Optimize = optimize, UseExperimentalBackend = true };
      var directImage = direct.EmitExecutable();
      var routedImage = routed.EmitExecutable();
      Assert.Multiple(() => {
        Assert.That(direct.Errors, Is.Empty, "direct: " + string.Join("; ", direct.Errors));
        Assert.That(routed.Errors, Is.Empty, "routed: " + string.Join("; ", routed.Errors));
        Assert.That(routed.BackendRoutedNames, Does.Contain("main"),
          $"[optimize={optimize}] the module body did not route, so the run below compares nothing: "
            + string.Join(" | ", routed.BackendDeclines.Select(d => d.Name + ": " + d.Reason)));
        var directOutput = Cpu8086.Run(directImage).Output;
        Assert.That(Cpu8086.Run(routedImage).Output, Is.EqualTo(directOutput), $"[optimize={optimize}]");
        Assert.That(directOutput.Replace("\r", "").Trim(), Is.EqualTo(program.Expected.Trim()),
          $"[optimize={optimize}] the directive changed what the program means");
      });
    }
  }
}
