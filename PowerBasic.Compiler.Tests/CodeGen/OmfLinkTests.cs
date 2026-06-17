using System.Text;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Emit.Omf;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// End-to-end external OMF object linking (docs/LINKER.md, M1): a BASIC program
/// DECLAREs a CDECL function with an ALIAS to a C-style public, $LINK pulls in a
/// (hand-built, hermetic) OMF object that defines it, and the call runs under DOSBox.
/// Proves the OMF reader + synthetic-unit lowering + linker + cdecl call path work
/// together to call foreign object code.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class OmfLinkTests {

  private static byte[] Record(byte type, params byte[][] parts) {
    var body = parts.SelectMany(p => p).ToArray();
    return [type, (byte)(body.Length + 1), (byte)((body.Length + 1) >> 8), .. body, 0];
  }
  private static byte[] Str(string s) { var b = Encoding.ASCII.GetBytes(s); return [(byte)b.Length, .. b]; }
  private static byte[] U16(int v) => [(byte)v, (byte)(v >> 8)];

  // leaf cdecl FUNCTION addone(BYVAL x AS LONG) AS LONG -> x + 1 in DX:AX
  private static readonly byte[] _addOne =
    [0x55, 0x8B, 0xEC, 0x8B, 0x46, 0x04, 0x8B, 0x56, 0x06, 0x05, 0x01, 0x00, 0x83, 0xD2, 0x00, 0x5D, 0xC3];

  private static PbuFile AddOneUnit() {
    byte[] obj = [
      .. Record(0x80, Str("ADDONE")),
      .. Record(0x96, Str("_TEXT"), Str("CODE")),
      .. Record(0x98, [0x28], U16(_addOne.Length), [1], [2], [0]),
      .. Record(0x90, [0], [1], Str("_addone"), U16(0), [0]),
      .. Record(0xA0, [1], U16(0), _addOne),
      .. Record(0x8A, [0]),
    ];
    return OmfToPbu.Convert(OmfReader.ReadObject(obj));
  }

  [Test]
  public void Execute_GivenCdeclObjectLinked_WhenCalled_ThenForeignCodeRuns() {
    const string source = """
      DECLARE FUNCTION addone CDECL ALIAS "_addone" (BYVAL x AS LONG) AS LONG
      PRINT addone(41)
      END
      """;
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var model = Binder.Bind(unit, Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable([AddOneUnit()], []);
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    Assert.That(DosBoxRunner.Normalize(DosBoxRunner.Run(exe)), Is.EqualTo(" 42\n"));
  }
}
