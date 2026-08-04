using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// <c>MKDIR</c> / <c>RMDIR</c> / <c>CHDIR</c>: three DOS calls that had no code generator, so a
/// program using any of them failed to compile at all.
///
/// Each is the same shape - the path arrives as a string handle, the runtime turns it into the ASCIIZ
/// buffer INT 21h wants, and the function number is the only difference. None of them reports a
/// result: a failed CHDIR is not an error in PowerBASIC, and inventing one here would be a behaviour
/// the genuine compiler does not have.
/// </summary>
[TestFixture]
public sealed class DirectoryCommandTests {

  private static Cpu8086 Run(string body) {
    var source = body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image);
  }

  [Test]
  public void MkDir_GivenAPath_ThenTheDirectoryExistsAfterwards() {
    var cpu = Run("MKDIR \"WORK\"");

    Assert.That(cpu.DirectoryExists("WORK"), "MKDIR has to reach INT 21h AH=39h");
  }

  [Test]
  public void RmDir_GivenADirectoryItMade_ThenItIsGoneAgain() {
    var cpu = Run("""
      MKDIR "WORK"
      RMDIR "WORK"
      """);

    Assert.That(cpu.DirectoryExists("WORK"), Is.False);
  }

  /// <summary>
  /// PowerBASIC does not raise on a directory call that fails, so removing something that was never
  /// there has to run straight through rather than end the program.
  /// </summary>
  [Test]
  public void RmDir_GivenAPathThatIsNotThere_ThenTheProgramCarriesOn() {
    var cpu = Run("""
      RMDIR "NOSUCH"
      PRINT "after"
      """);

    Assert.That(cpu.Output.Trim(), Is.EqualTo("after"));
  }

  [Test]
  public void ChDir_GivenADirectoryItMade_ThenTheProgramCarriesOn() {
    var cpu = Run("""
      MKDIR "WORK"
      CHDIR "WORK"
      PRINT "after"
      """);

    Assert.That(cpu.Output.Trim(), Is.EqualTo("after"));
  }

  /// <summary>The path is an expression, not just a literal - a built name has to work too.</summary>
  [Test]
  public void MkDir_GivenAComputedPath_ThenItUsesTheComputedName() {
    var cpu = Run("""
      a$ = "WO"
      b$ = "RK"
      MKDIR a$ + b$
      """);

    Assert.That(cpu.DirectoryExists("WORK"));
  }
}
