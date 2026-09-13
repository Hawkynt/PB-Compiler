using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// A record inside a record: <c>a.b.c</c>.
///
/// <para>
/// Member access lowered for a named UDT variable, for an array element of UDT, and for a pointer
/// deref - and not for a member whose TARGET is itself a member. A header record holding a palette
/// record is the ordinary shape in graphics code, so declining it cost 14 module bodies over the
/// SVGA corpus. The inner member's address is the outer one's base, which is the recursion the
/// field offsets already describe.
/// </para>
/// <para>
/// The fields are given DIFFERENT values and the neighbours are read back too, because what a wrong
/// recursion produces is not a crash but the wrong OFFSET: a base taken from the outer record rather
/// than the inner one writes over a sibling, and a test with one field, or with equal values, cannot
/// see it. <c>tag</c> before and <c>tail</c> after the nested record are there to catch a write that
/// lands outside it.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendNestedRecordTests {

  private const string _source = """
    TYPE Inner
      x AS INTEGER
      y AS INTEGER
    END TYPE
    TYPE Outer
      tag AS INTEGER
      pos AS Inner
      tail AS INTEGER
    END TYPE
    DIM o AS Outer
    o.tag = 7
    o.pos.x = 11
    o.pos.y = 22
    o.tail = 33
    PRINT o.tag; o.pos.x; o.pos.y; o.tail
    o.pos.y = o.pos.x + o.tag
    PRINT o.pos.y; o.tail
    """;

  private static (string Output, IEnumerable<string> Routed) Run(bool optimize) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(_source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return (Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|"), generator.BackendRoutedNames.ToList());
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Run_GivenANestedRecord_ThenEachFieldKeepsItsOwnStorage(bool optimize) {
    var (output, _) = Run(optimize);

    Assert.That(output, Is.EqualTo("7  11  22  33 | 18  33"),
      "a nested field must reach its own offset, and must not write over the fields either side of it");
  }

  /// <summary>The premise: before this, the body declined and those values were the direct emitter's.</summary>
  [Test]
  public void Route_GivenANestedRecord_ThenTheModuleBodyIsTakenByTheBackEnd() {
    var (_, routed) = Run(optimize: false);

    Assert.That(routed, Does.Contain("main"));
  }
}
