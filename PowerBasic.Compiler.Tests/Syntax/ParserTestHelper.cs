using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.Syntax;

/// <summary>Shared shorthands for the parser test fixtures.</summary>
internal static class ParserTestHelper {

  public static CompilationUnit Parse(string source)
    => Parser.Parse(Lexer.Tokenize(source, "test.bas"), "test.bas");

  public static CompilationUnit Parse(string source, Dialect dialect)
    => Parser.Parse(Lexer.Tokenize(source, "test.bas", dialect), "test.bas", dialect);

  public static Statement ParseSingle(string source) {
    var unit = Parse(source);
    Assert.That(unit.Statements, Has.Count.EqualTo(1), $"expected exactly one statement for: {source}");
    return unit.Statements[0];
  }

  public static T ParseSingle<T>(string source) where T : Statement {
    var statement = ParseSingle(source);
    Assert.That(statement, Is.InstanceOf<T>(), $"for: {source}");
    return (T)statement;
  }

  public static Expression ParseExpression(string source)
    => ParseSingle<AssignStmt>("x = " + source).Value;

  public static T ParseExpression<T>(string source) where T : Expression {
    var expression = ParseExpression(source);
    Assert.That(expression, Is.InstanceOf<T>(), $"for: {source}");
    return (T)expression;
  }
}
