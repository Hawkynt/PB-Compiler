using System.Text;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// QuickBASIC-compatible <c>FILES [filespec$]</c>: current-directory header, four 18-column entries
/// per row, directory markers, and the DOS free-byte count.
/// </summary>
[TestFixture]
public sealed class FilesStatementTests {

  private static readonly IReadOnlyDictionary<string, byte[]> _disk = new Dictionary<string, byte[]> {
    ["ALPHA.TXT"] = Encoding.ASCII.GetBytes("abc"),
    ["BETA.BAS"] = Encoding.ASCII.GetBytes("123456789"),
  };

  private static Cpu8086 Run(string body, bool emitCom, out Cpu8086Exception? fault) {
    var source = body + "\nEND\n";
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = true };
    var image = emitCom ? generator.EmitCom() : generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return Cpu8086.Run(image, _disk, out fault);
  }

  [TestCase(false), TestCase(true)]
  public void Execute_GivenBareFiles_WhenDiskHasFilesAndADirectory_ThenPrintsTheOracleLayout(bool emitCom) {
    var cpu = Run("MKDIR \"SUBDIR\"\nFILES", emitCom, out var fault);

    Assert.That(fault, Is.Null);
    Assert.That(cpu.Output, Is.EqualTo(
      "C:\\PBC\r\n"
      + "        .   <DIR>         ..  <DIR> SUBDIR      <DIR> ALPHA   .TXT\r\n"
      + "BETA    .BAS\r\n"
      + " 262144000 Bytes free\r\n"));
  }

  [TestCase(false), TestCase(true)]
  public void Execute_GivenAWildcard_WhenOnlyOneFileMatches_ThenPrintsOnlyThatEntry(bool emitCom) {
    var cpu = Run("FILES \"*.BAS\"", emitCom, out var fault);

    Assert.That(fault, Is.Null);
    Assert.That(cpu.Output, Is.EqualTo(
      "C:\\PBC\r\n"
      + "BETA    .BAS\r\n"
      + " 262144000 Bytes free\r\n"));
  }

  [TestCase(false), TestCase(true)]
  public void Execute_GivenNoMatchingFile_WhenErrorIsHandled_ThenRaisesBasicError53(bool emitCom) {
    var cpu = Run("ON ERROR RESUME NEXT\nFILES \"*.ZZZ\"\nPRINT ERR", emitCom, out var fault);

    Assert.That(fault, Is.Null);
    Assert.That(cpu.Output, Is.EqualTo("C:\\PBC\r\n 53 \r\n"));
  }
}
