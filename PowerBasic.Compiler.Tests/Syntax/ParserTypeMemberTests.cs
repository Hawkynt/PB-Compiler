using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;
using static PowerBasic.Compiler.Tests.Syntax.ParserTestHelper;

namespace PowerBasic.Compiler.Tests.Syntax;

/// <summary>
/// Front-end of PB 3.6 TYPE members: a TYPE block parses SUB / FUNCTION /
/// PROPERTY GET / PROPERTY SET members alongside its fields, gated to pb36.
/// </summary>
[TestFixture]
public sealed class ParserTypeMemberTests {

  private static TypeDecl ParseType(string source) {
    var unit = Parse(source, Dialect.Pb36);
    var decl = unit.Statements.OfType<TypeDecl>().Single();
    return decl;
  }

  private const string _stack =
    "TYPE Stack\n" +
    "  Count AS INTEGER\n" +
    "  Items(1 TO 100) AS LONG\n" +
    "  SUB Push(BYVAL v AS LONG)\n" +
    "    INCR THIS.Count\n" +
    "    THIS.Items(THIS.Count) = v\n" +
    "  END SUB\n" +
    "  FUNCTION Pop() AS LONG\n" +
    "    Pop = THIS.Items(THIS.Count)\n" +
    "    DECR THIS.Count\n" +
    "  END FUNCTION\n" +
    "  PROPERTY GET Size() AS INTEGER\n" +
    "    Size = THIS.Count\n" +
    "  END PROPERTY\n" +
    "  PROPERTY SET Size(BYVAL n AS INTEGER)\n" +
    "    THIS.Count = n\n" +
    "  END PROPERTY\n" +
    "END TYPE";

  [Test]
  public void Parse_GivenTypeWithMembers_WhenPb36_ThenFieldsAndMembersBothCaptured() {
    var decl = ParseType(_stack);
    Assert.Multiple(() => {
      Assert.That(decl.Fields.Select(f => f.Name), Is.EqualTo(new[] { "Count", "Items" }));
      Assert.That(decl.Members, Has.Count.EqualTo(4));
    });
  }

  [Test]
  public void Parse_GivenSubMember_WhenPb36_ThenKindAndParametersCaptured() {
    var push = ParseType(_stack).Members.Single(m => m.Name == "Push");
    Assert.Multiple(() => {
      Assert.That(push.Kind, Is.EqualTo(TypeMemberKind.Sub));
      Assert.That(push.Parameters, Has.Count.EqualTo(1));
      Assert.That(push.Parameters[0].ByVal, Is.True);
      Assert.That(push.ReturnType, Is.Null);
      Assert.That(push.Body, Has.Count.EqualTo(2));
    });
  }

  [Test]
  public void Parse_GivenFunctionMember_WhenPb36_ThenReturnTypeCaptured() {
    var pop = ParseType(_stack).Members.Single(m => m.Name == "Pop");
    Assert.Multiple(() => {
      Assert.That(pop.Kind, Is.EqualTo(TypeMemberKind.Function));
      Assert.That(pop.ReturnType, Is.Not.Null);
      Assert.That(pop.Parameters, Is.Empty);
    });
  }

  [Test]
  public void Parse_GivenPropertyGetAndSet_WhenPb36_ThenBothKindsCaptured() {
    var members = ParseType(_stack).Members.Where(m => m.Name == "Size").ToList();
    Assert.Multiple(() => {
      Assert.That(members.Select(m => m.Kind),
        Is.EquivalentTo(new[] { TypeMemberKind.PropertyGet, TypeMemberKind.PropertySet }));
      Assert.That(members.Single(m => m.Kind == TypeMemberKind.PropertyGet).ReturnType, Is.Not.Null);
      Assert.That(members.Single(m => m.Kind == TypeMemberKind.PropertySet).Parameters, Has.Count.EqualTo(1));
    });
  }

  [Test]
  public void Parse_GivenTypeMember_WhenPb35_ThenRejectedWithRequirementMessage() {
    var ex = Assert.Throws<ParserException>(() => Parse(_stack, Dialect.Pb35));
    Assert.That(ex!.Message, Does.Contain("TYPE methods"));
  }

  [Test]
  public void Parse_GivenPlainTypeWithoutMembers_WhenPb35_ThenStillParses() {
    var decl = (TypeDecl)Parse("TYPE Point\n  X AS INTEGER\n  Y AS INTEGER\nEND TYPE", Dialect.Pb35)
      .Statements.Single(s => s is TypeDecl);
    Assert.Multiple(() => {
      Assert.That(decl.Fields, Has.Count.EqualTo(2));
      Assert.That(decl.Members, Is.Empty);
    });
  }
}
