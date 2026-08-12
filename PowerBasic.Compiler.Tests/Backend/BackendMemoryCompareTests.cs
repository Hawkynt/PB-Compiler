using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// A comparison where NEITHER side is in a register.
///
/// <para>
/// <c>CMP</c> wants a register on its left. When the left operand is a constant the selector already
/// mirrors the comparison instead - <c>5 &gt; x</c> asks the same question as <c>x &lt; 5</c> - but
/// mirroring needs something to mirror onto, and two memory cells give it nothing. That shape used to
/// decline the whole function; it now costs one MOV.
/// </para>
/// <para>
/// SHARED globals are what produce it: they live in one cell the whole program addresses, so mem2reg
/// leaves them in memory where an ordinary local would have been promoted.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendMemoryCompareTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static string Run(string source, bool routed) {
    var cg = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = routed };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output;
  }

  private static IEnumerable<string> RoutedNames(string source) {
    var cg = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };
    cg.EmitExecutable();
    return cg.BackendRoutedNames.ToList();
  }

  /// <summary>
  /// Every predicate, both orders, on two cells the optimizer cannot promote. The mirrored form has
  /// always worked; the point of covering all six is that an unmirrored comparison emitted with the
  /// operands the wrong way round answers correctly for <c>=</c> and <c>&lt;&gt;</c> and wrongly for
  /// the other four - which a test of equality alone would call a pass.
  /// </summary>
  private const string _SOURCE = """
    DIM a AS SHARED INTEGER
    DIM b AS SHARED INTEGER
    DECLARE SUB Report()
    a = 3
    b = 7
    CALL Report
    a = 7
    b = 3
    CALL Report
    a = 5
    b = 5
    CALL Report
    END
    SUB Report()
      IF a < b THEN PRINT "lt";
      IF a <= b THEN PRINT "le";
      IF a > b THEN PRINT "gt";
      IF a >= b THEN PRINT "ge";
      IF a = b THEN PRINT "eq";
      IF a <> b THEN PRINT "ne";
      PRINT
    END SUB
    """;

  [Test]
  public void Compare_GivenTwoMemoryOperands_ThenTheRoutedProgramMatchesTheDirectEmitter()
    => Assert.That(Run(_SOURCE, routed: true), Is.EqualTo(Run(_SOURCE, routed: false)));

  /// <summary>
  /// And stated outright, so a shared misreading of the predicate cannot pass: 3 against 7 is less,
  /// 7 against 3 is greater, 5 against 5 is neither.
  /// </summary>
  [Test]
  public void Compare_GivenTwoMemoryOperands_ThenEachPredicateAnswersCorrectly() {
    var lines = Run(_SOURCE, routed: true).Replace("\r\n", "\n").Trim().Split('\n');
    Assert.That(lines, Has.Length.EqualTo(3));
    Assert.That(lines[0].Trim(), Is.EqualTo("ltlene"), "3 < 7");
    Assert.That(lines[1].Trim(), Is.EqualTo("gtgene"), "7 > 3");
    Assert.That(lines[2].Trim(), Is.EqualTo("legeeq"), "5 = 5");
  }

  /// <summary>Without this the comparison above would pass on a function the selector had declined.</summary>
  [Test]
  public void Compare_GivenTwoMemoryOperands_ThenTheBackEndOwnsTheProcedure()
    => Assert.That(RoutedNames(_SOURCE), Does.Contain("Report"));
}
