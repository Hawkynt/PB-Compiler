using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Semantics;

/// <summary>
/// pb36 natural type-name aliases: alternative spellings of the existing types so the language reads
/// more naturally. INTEGER stays 16-bit and LONG 32-bit (classic PowerBASIC widths, so the
/// differential harness is untouched), hence SHORT/INT16 = INTEGER and INT32 = LONG; the wide tiers
/// mirror QUAD/QWORD with D/Q/O (double/quad/octa) prefixes. The aliases are pb36-only
/// (<see cref="LanguageFeature.TypeAliases"/>).
/// </summary>
[TestFixture]
public sealed class TypeAliasTests {

  private static PbType BindVarType(string keyword) {
    var src = $"DIM x AS {keyword}\n";
    var unit = Parser.Parse(Lexer.Tokenize(src, "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
    return model.ModuleVariables.Values.Single(v => v.Name.Equals("x", System.StringComparison.OrdinalIgnoreCase)).Type;
  }

  [TestCase("INT16", "INTEGER")]
  [TestCase("SHORT", "INTEGER")]
  [TestCase("INT32", "LONG")]
  [TestCase("INT64", "QUAD")]
  [TestCase("UINT8", "BYTE")]
  [TestCase("UINT16", "WORD")]
  [TestCase("UINT32", "DWORD")]
  public void Bind_GivenScalarAlias_ThenResolvesToTheClassicType(string alias, string classic) {
    // the alias and its classic spelling must resolve to the same scalar type (same width, same sign)
    Assert.That(BindVarType(alias), Is.EqualTo(BindVarType(classic)));
  }

  [TestCase("DQUAD", 16, true)]
  [TestCase("QQUAD", 32, true)]
  [TestCase("OQUAD", 64, true)]
  [TestCase("DQWORD", 16, false)]
  [TestCase("QQWORD", 32, false)]
  [TestCase("OWORD", 64, false)]
  public void Bind_GivenWideAlias_ThenResolvesToWideIntTypeWithSizeAndSign(string alias, int bytes, bool signed) {
    var type = BindVarType(alias);
    Assert.That(type, Is.InstanceOf<WideIntType>());
    var wide = (WideIntType)type;
    Assert.Multiple(() => {
      Assert.That(wide.ByteSize, Is.EqualTo(bytes));
      Assert.That(wide.Signed, Is.EqualTo(signed));
    });
  }

  [TestCase("INT8", 1, true)]
  [TestCase("SBYTE", 1, true)]
  [TestCase("UINT64", 8, false)]
  [TestCase("QWORD", 8, false)]
  public void Bind_GivenNewScalarType_ThenResolvesToScalarWithSizeAndSign(string alias, int bytes, bool signed) {
    // SBYTE/INT8 (signed 8-bit) and QWORD/UINT64 (unsigned 64-bit) are genuinely new scalars
    var type = BindVarType(alias);
    Assert.That(type, Is.InstanceOf<ScalarType>());
    var s = (ScalarType)type;
    Assert.Multiple(() => {
      Assert.That(s.ByteSize, Is.EqualTo(bytes));
      Assert.That(s.Signed, Is.EqualTo(signed));
      Assert.That(s.IsFloat, Is.False);
    });
  }

  [Test]
  public void Bind_GivenAliasBelowPb36_ThenRejected() {
    Assert.Throws<ParserException>(() =>
      Parser.Parse(Lexer.Tokenize("DIM x AS SHORT\n", "t.bas", Dialect.Pb35), "t.bas", Dialect.Pb35));
  }

  [Test]
  public void Bind_GivenIntegerAndLong_ThenStayClassicWidths() {
    // the user's decision: INTEGER stays 16-bit and LONG 32-bit (so the 241-battery harness is safe)
    Assert.That(BindVarType("INT16"), Is.EqualTo(BindVarType("INTEGER")));
    Assert.That(BindVarType("INT32"), Is.EqualTo(BindVarType("LONG")));
    Assert.That(BindVarType("INTEGER"), Is.Not.EqualTo(BindVarType("INT32")), "INTEGER is not 32-bit");
  }
}
