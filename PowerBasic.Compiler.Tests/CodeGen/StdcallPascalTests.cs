using System.Text;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Emit.Omf;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// STDCALL / PASCAL external calling conventions (docs/LINKER.md): unlike CDECL,
/// the callee cleans the argument bytes (its <c>RET n</c>), so the caller must NOT
/// emit <c>add sp, n</c> after the call. These tests prove both the stack
/// discipline (a leak would corrupt later output / desync the frame) and, via a
/// hermetic hand-built STDCALL OMF object, that a foreign STDCALL routine runs and
/// returns correctly under DOSBox.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class StdcallPascalTests {

  private static byte[] Record(byte type, params byte[][] parts) {
    var body = parts.SelectMany(p => p).ToArray();
    return [type, (byte)(body.Length + 1), (byte)((body.Length + 1) >> 8), .. body, 0];
  }
  private static byte[] Str(string s) { var b = Encoding.ASCII.GetBytes(s); return [(byte)b.Length, .. b]; }
  private static byte[] U16(int v) => [(byte)v, (byte)(v >> 8)];

  // leaf STDCALL FUNCTION addone(BYVAL x AS LONG) AS LONG -> x + 1 in DX:AX,
  // cleaning its own 4 argument bytes with RET 4 (C2 04 00) - the only difference
  // from the CDECL leaf, which ends in a plain RET (C3).
  private static readonly byte[] _addOneStd =
    [0x55, 0x8B, 0xEC, 0x8B, 0x46, 0x04, 0x8B, 0x56, 0x06, 0x05, 0x01, 0x00, 0x83, 0xD2, 0x00, 0x5D, 0xC2, 0x04, 0x00];

  private static PbuFile AddOneStdUnit() {
    byte[] obj = [
      .. Record(0x80, Str("ADDONES")),
      .. Record(0x96, Str("_TEXT"), Str("CODE")),
      .. Record(0x98, [0x28], U16(_addOneStd.Length), [1], [2], [0]),
      .. Record(0x90, [0], [1], Str("_addone@4"), U16(0), [0]),
      .. Record(0xA0, [1], U16(0), _addOneStd),
      .. Record(0x8A, [0]),
    ];
    return OmfToPbu.Convert(OmfReader.ReadObject(obj));
  }

  private static (SemanticModel Model, CodeGenerator Gen) Compile(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var model = Binder.Bind(unit, Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return (model, new CodeGenerator(model));
  }

  // Given a STDCALL external object whose RET n cleans its own arguments,
  // When the BASIC program calls it twice and prints again afterwards,
  // Then the foreign code runs and the stack stays balanced (a caller-side
  // over-clean or a missing callee clean would corrupt the trailing output).
  [Test]
  public void Execute_GivenStdcallObjectLinked_WhenCalledTwice_ThenForeignCodeRunsAndStackBalanced() {
    const string source = """
      DECLARE FUNCTION addone STDCALL ALIAS "_addone@4" (BYVAL x AS LONG) AS LONG
      PRINT addone(41)
      PRINT addone(99)
      PRINT 7
      END
      """;
    var (_, generator) = Compile(source);
    var exe = generator.EmitExecutable([AddOneStdUnit()], []);
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    Assert.That(DosBoxRunner.Normalize(DosBoxRunner.Run(exe)), Is.EqualTo(" 42\n 100\n 7\n"));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Route_GivenStdcallObjectLinked_WhenCalledTwice_ThenMainRoutesAndStackStaysBalanced(
      bool optimize) {
    const string source = """
      DECLARE FUNCTION addone STDCALL ALIAS "_addone@4" (BYVAL x AS LONG) AS LONG
      PRINT addone(41)
      PRINT addone(99)
      PRINT 7
      END
      """;
    var (model, _) = Compile(source);
    var generator = new CodeGenerator(model) {
      Optimize = optimize,
      UseExperimentalBackend = true,
    };

    var exe = generator.EmitExecutable([AddOneStdUnit()], []);
    var output = Cpu8086.Run(exe).Output.Trim().Replace("\r\n", "|");

    Assert.Multiple(() => {
      Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
      Assert.That(generator.BackendRoutedNames, Does.Contain("main"));
      Assert.That(output, Is.EqualTo("42 | 100 | 7"));
    });
  }

  // Given DECLAREs that differ only by calling convention,
  // When each call is emitted,
  // Then CDECL emits a caller-side stack cleanup ("add sp, 4" = 83 C4 04) after the
  // CALL while STDCALL and PASCAL do not (the callee's RET n cleans instead) - so the
  // CDECL image carries exactly one more "add sp, 4" occurrence than each of the others.
  [Test]
  public void Emit_GivenConventions_WhenCalled_ThenOnlyCdeclCallerCleansStack() {
    static byte[] Image(string convention) {
      var (_, generator) = Compile($"""
        DECLARE FUNCTION f {convention} ALIAS "_f" (BYVAL x AS LONG) AS LONG
        PRINT f(1)
        END
        """);
      // a fake unit just satisfies the linker's import of "_f"; the call never runs.
      var foreign = new PbuFile { Name = "F", Code = new byte[8], Foreign = true };
      foreign.Exports.Add(new PbuExport("_f", PbuExportKind.Function, 0, 0));
      var exe = generator.EmitExecutable([foreign], []);
      Assert.That(generator.Errors, Is.Empty, $"{convention} codegen: " + string.Join("; ", generator.Errors));
      return exe;
    }

    // count "add sp, 4" (83 C4 04) opcode occurrences in the image
    static int AddSp4Count(byte[] image) {
      var count = 0;
      for (var i = 0; i + 2 < image.Length; ++i)
        if (image[i] == 0x83 && image[i + 1] == 0xC4 && image[i + 2] == 0x04)
          ++count;
      return count;
    }

    var cdecl = Image("CDECL");
    var stdcall = Image("STDCALL");
    var pascal = Image("PASCAL");

    Assert.Multiple(() => {
      // CDECL has exactly one extra caller-side cleanup over each callee-cleans convention
      Assert.That(AddSp4Count(cdecl) - AddSp4Count(stdcall), Is.EqualTo(1), "CDECL must caller-clean (one extra add sp,4) vs STDCALL");
      Assert.That(AddSp4Count(cdecl) - AddSp4Count(pascal), Is.EqualTo(1), "CDECL must caller-clean (one extra add sp,4) vs PASCAL");
    });
  }
}
