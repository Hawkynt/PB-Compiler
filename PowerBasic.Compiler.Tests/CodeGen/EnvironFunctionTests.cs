using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// <c>ENVIRON$</c>, which has shipped since it was written and had no test.
///
/// It could not have had one: the intrinsic walks the environment block whose segment DOS leaves in
/// the PSP word at 2Ch, and the in-repo interpreter left the PSP blank, so every program running
/// under it read an environment segment of zero. The interpreter now installs a small environment
/// (PATH, COMSPEC, PROMPT) laid out the way DOS does - NAME=VALUE strings each NUL-terminated, the
/// block closed by a second NUL - and the intrinsic can be executed rather than merely compiled.
///
/// The lookups worth having are the ones that go wrong quietly: a name that is a PREFIX of a real
/// entry must not match it, and a name that is a whole entry's prefix up to the '=' must. The walk
/// compares up to the name's length and then checks for '=', so PROMPT and PROM differ only in that
/// last check.
/// </summary>
[TestFixture]
public sealed class EnvironFunctionTests {

  private static string Run(string body) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(body + "\nEND\n", "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  [TestCase("PATH", "C:\\DOS")]
  [TestCase("COMSPEC", "C:\\COMMAND.COM")]
  [TestCase("PROMPT", "$P$G")]
  public void Environ_GivenAName_ThenItReturnsThatEntrysValue(string name, string expected) =>
    Assert.That(Run($"PRINT ENVIRON$(\"{name}\")"), Is.EqualTo(expected));

  /// <summary>An entry that is not there is the empty string, not the next entry along.</summary>
  [Test]
  public void Environ_GivenAnAbsentName_ThenItReturnsEmpty() =>
    Assert.That(Run("PRINT \"[\"; ENVIRON$(\"NOSUCHVAR\"); \"]\""), Is.EqualTo("[]"));

  /// <summary>
  /// A prefix of a real name must not match it. PROM against PROMPT is the case the walk gets wrong
  /// if it compares the name's bytes and forgets to insist on the '=' that follows.
  /// </summary>
  [Test]
  public void Environ_GivenAPrefixOfARealName_ThenItDoesNotMatch() =>
    Assert.That(Run("PRINT \"[\"; ENVIRON$(\"PROM\"); \"]\""), Is.EqualTo("[]"));

  /// <summary>The name is matched case-insensitively - the runtime upper-cases it first.</summary>
  [Test]
  public void Environ_GivenALowercaseName_ThenItStillMatches() =>
    Assert.That(Run("PRINT ENVIRON$(\"path\")"), Is.EqualTo("C:\\DOS"));

  /// <summary>An empty name matches nothing rather than the first entry.</summary>
  [Test]
  public void Environ_GivenAnEmptyName_ThenItReturnsEmpty() =>
    Assert.That(Run("PRINT \"[\"; ENVIRON$(\"\"); \"]\""), Is.EqualTo("[]"));
}
