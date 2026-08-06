using System.Text;
using System.Text.RegularExpressions;
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

  private static readonly string _repoRoot =
    Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  /// <summary>Every statement node the parser can build, nested bodies included.</summary>
  private static IEnumerable<Type> NodesProducedByTheSurface() {
    foreach (var form in StatementSurface.All) {
      CompilationUnit unit;
      try {
        unit = Parser.Parse(Lexer.Tokenize(StatementSurface.Program(form), "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
      } catch (Exception) {
        continue;                       // a form the pb36 parser rejects is the other census's business
      }
      foreach (var type in Walk(unit.Statements))
        yield return type;
    }
  }

  /// <summary>
  /// Every statement in <paramref name="body"/>, procedure bodies included.
  ///
  /// <see cref="OptReachability.DescendantNodes"/> walks a statement's own tree by reflection and is
  /// complete for it, but it deliberately stops at a nested SUB or FUNCTION - those are separate
  /// procedures, reached on their own when the optimizer needs them. A census that stopped there too
  /// would report EXIT FAR and REQUIRE as having no form while forms for both sat in the table,
  /// because neither statement is legal anywhere but inside a procedure.
  /// </summary>
  private static IEnumerable<Type> Walk(IEnumerable<Statement> body) {
    foreach (var statement in body) {
      yield return statement.GetType();
      foreach (var node in OptReachability.DescendantNodes(statement))
        if (node is Statement nested)
          yield return nested.GetType();

      var nestedBody = statement switch {
        SubDecl sub => sub.Body,
        FunctionDecl function => function.Body,
        _ => null,
      };
      if (nestedBody is not null)
        foreach (var type in Walk(nestedBody))
          yield return type;
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
  /// Statement kinds the PARSER constructs, read from its own source.
  ///
  /// A node the parser never builds cannot have a surface form: there is no text that produces it.
  /// TRY/CATCH becoming four Handler* statements is the binder's doing, and asking for a form that
  /// spells one directly is asking for something BASIC has no syntax for. Splitting the two is what
  /// turns the pinned list below from a tally into a work queue.
  /// </summary>
  private static HashSet<string> ConstructedByTheParser() {
    var dir = Path.Combine(_repoRoot, "PowerBasic.Compiler", "Syntax");
    Assume.That(Directory.Exists(dir), "no parser sources to read");
    var built = new HashSet<string>(StringComparer.Ordinal);
    foreach (var file in Directory.EnumerateFiles(dir, "Parser*.cs"))
      foreach (Match m in Regex.Matches(File.ReadAllText(file), @"new\s+([A-Za-z]+(?:Stmt|Decl|Group))\s*\("))
        built.Add(m.Groups[1].Value);
    return built;
  }

  /// <summary>
  /// The kinds with no form that the parser really can build - the ones a program could contain and
  /// nothing in the surface exercises. This is the actionable half of the pinned list.
  /// </summary>
  [Test]
  public void Surface_GivenTheKindsWithNoForm_ThenTheOnesTheParserBuildsAreNamedSeparately() {
    var built = ConstructedByTheParser();
    var reachable = _noSurfaceForm.Where(built.Contains).OrderBy(n => n, StringComparer.Ordinal).ToList();
    var unreachable = _noSurfaceForm.Where(n => !built.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();

    var report = new StringBuilder()
      .AppendLine($"kinds with no surface form: {_noSurfaceForm.Length}")
      .AppendLine($"  the parser builds these - a program can contain one and nothing tests it:")
      .AppendLine("    " + string.Join(", ", reachable))
      .AppendLine($"  the parser never builds these - the binder's lowering makes them, so no form can:")
      .AppendLine("    " + string.Join(", ", unreachable));
    TestContext.Out.Write(report.ToString());

    Assert.That(reachable, Is.EquivalentTo(_parserBuiltWithNoForm),
      "the set of parser-reachable statement kinds with no form has changed:\n" + report);
  }

  /// <summary>
  /// Statement kinds a program can contain that no surface form exercises.
  ///
  /// Three are left, and each is a different reason rather than an oversight. ResourceStmt bakes a
  /// named file into the image, so a form needs a payload sitting beside the test. StatementGroup is
  /// what an IS pattern binding wraps its statement in, which no statement spells on its own.
  /// DeferStmt the parser really does build - and then ParseBody rewrites it into TRY ... FINALLY
  /// before anyone sees the tree, so a form for it can exist and still never produce one. That last
  /// is the limit of reading `new XxxStmt(` out of the parser's source: it finds what is constructed,
  /// not what survives. DeferredSourceStmt deliberately has no ordinary surface form: only the
  /// BASICA/GW compatibility recovery path creates it from otherwise unparseable stored source.
  /// </summary>
  private static readonly string[] _parserBuiltWithNoForm = [
    "DeferStmt",
    "DeferredSourceStmt",
    "ResourceStmt",
    "StatementGroup",
  ];

  /// <summary>
  /// Statement kinds no surface form produces: 29 when this census was first run, 23 once forms were
  /// written for MID$ assignment, EQUATE, ARRAY SORT, BIT SET, FOR EACH and ITERATE, and 13 once ten
  /// more followed - ASC assignment, CHAIN, REPLACE, ARRAY SCAN, EXIT FAR, inline assembly,
  /// destructuring, $ASSERT, a metastatement and REQUIRE, then 8 with the code-pointer trio, the
  /// single-line type alias and a coroutine YIELD. MID$ assignment is as common a statement as BASIC
  /// has and none of the twenty-one had a form.
  ///
  /// The list is not all holes, and the companion test splits it by reading the parser's own source:
  /// five of the nine - GroupStmt and the four Handler* kinds TRY/CATCH lowers to - are built
  /// by the binder alone, and no surface form can exist for a node no source text produces. The other
  /// four include three grammars a program can really contain plus DeferredSourceStmt's dialect-only
  /// recovery path, and that is what is left of the queue.
  ///
  /// Write a form and strike the name. The test insists either way.
  /// </summary>
  private static readonly string[] _noSurfaceForm = [
    "DeferStmt",
    "DeferredSourceStmt",
    "GroupStmt",
    "HandlerArmStmt",
    "HandlerReraiseStmt",
    "HandlerRestoreStmt",
    "HandlerSaveStmt",
    "ResourceStmt",
    "StatementGroup",
  ];
}
