using System.Text;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.Syntax;

/// <summary>
/// Whether the statement surface covers every KIND of statement, not merely every keyword.
///
/// <see cref="StatementSurfaceCoverageTests"/> asks that each word in <see cref="Parser.StatementKeywords"/>
/// opens some form, and that turns out not to be enough: one keyword can front several unrelated
/// grammars. GET and PUT are the clear case - <c>GET #1, 1</c> and <c>GET (0,0)-(3,3), a%(0)</c> are
/// a file statement and a graphics statement sharing a word - and the surface had six forms for the
/// file one and none for the graphics one, so <c>GetPutGraphicsStmt</c> reached no code generator
/// for as long as it existed and no census counted it as a gap. VIEW PRINT's row range was the same
/// story in a different shape.
///
/// The parser already knows the answer here too. Every distinct grammar ends in a distinct AST node,
/// so the check is: parse every form, collect the node types they produce, and compare against every
/// non-abstract <see cref="Statement"/> the assembly defines. A node type no form produces is a
/// grammar nothing tests.
/// </summary>
[TestFixture]
public sealed class StatementNodeCoverageTests {

  /// <summary>Every statement node the parser can build, nested bodies included.</summary>
  private static IEnumerable<Type> NodesProducedByTheSurface() {
    foreach (var form in StatementSurface.All) {
      CompilationUnit unit;
      try {
        unit = Parser.Parse(Lexer.Tokenize(StatementSurface.Program(form), "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
      } catch (Exception) {
        continue;                       // a form the pb36 parser rejects is the other census's business
      }
      // OptReachability walks the tree by reflection, so it is complete for anything the AST holds
      foreach (var statement in unit.Statements) {
        yield return statement.GetType();
        foreach (var node in OptReachability.DescendantNodes(statement))
          if (node is Statement nested)
            yield return nested.GetType();
      }
    }
  }

  [Test]
  public void Surface_GivenEveryStatementNodeTheParserBuilds_ThenSomeFormProducesIt() {
    var defined = typeof(Statement).Assembly.GetTypes()
      .Where(t => !t.IsAbstract && typeof(Statement).IsAssignableFrom(t))
      .OrderBy(t => t.Name, StringComparer.Ordinal)
      .ToList();
    var produced = NodesProducedByTheSurface().ToHashSet();

    var missing = defined.Where(t => !produced.Contains(t)).Select(t => t.Name).ToList();
    var report = new StringBuilder()
      .AppendLine($"statement node types: {defined.Count}, produced by a surface form: {defined.Count - missing.Count}");
    foreach (var name in missing)
      report.AppendLine($"  NO FORM  {name}");
    TestContext.Out.Write(report.ToString());

    Assert.That(missing, Is.EquivalentTo(_noSurfaceForm),
      "the set of statement kinds no surface form produces has changed:\n" + report);
  }

  /// <summary>
  /// Statement kinds no surface form produces - 29 when this census was first run, and 23 once forms
  /// were written for MID$ assignment, EQUATE, ARRAY SORT, BIT SET, FOR EACH and ITERATE, none of
  /// which had one. MID$ assignment is as common a statement as BASIC has.
  ///
  /// The list is not all holes. Some of these are built by the BINDER's lowering rather than by the
  /// parser - the four Handler* kinds are what TRY/CATCH becomes, and StatementGroup is a container -
  /// and no surface form can exist for a node no source text produces. Separating the two is the
  /// next refinement; until then the names are pinned so the set cannot grow while nobody is looking,
  /// which is the failure this whole fixture exists to stop.
  ///
  /// Write a form and strike the name. The test insists either way.
  /// </summary>
  private static readonly string[] _noSurfaceForm = [
    "ArrayScanStmt",
    "AscAssignStmt",
    "CallPtrStmt",
    "ChainStmt",
    "DeferStmt",
    "DestructureStmt",
    "ExitFarStmt",
    "GosubPtrStmt",
    "GotoPtrStmt",
    "GroupStmt",
    "HandlerArmStmt",
    "HandlerReraiseStmt",
    "HandlerRestoreStmt",
    "HandlerSaveStmt",
    "InlineAsmStmt",
    "MetaStmt",
    "ReplaceStmt",
    "RequireStmt",
    "ResourceStmt",
    "StatementGroup",
    "StaticAssertStmt",
    "TypeAliasDecl",
    "YieldStmt",
  ];
}
