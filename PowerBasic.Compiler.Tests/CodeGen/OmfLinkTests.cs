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

  // leaf cdecl FUNCTION sub2(BYVAL a AS INTEGER, BYVAL b AS INTEGER) AS INTEGER -> a - b
  private static readonly byte[] _subTwo =
    [0x55, 0x8B, 0xEC, 0x8B, 0x46, 0x04, 0x2B, 0x46, 0x06, 0x5D, 0xC3];

  // leaf fastcall FUNCTION mix(a,b,c,d,e,f AS INTEGER) -> a-b-c-d-e+f; AX,DX,BX then LTR stack
  private static readonly byte[] _fastcallMix =
    [0x55, 0x8B, 0xEC, 0x2B, 0xC2, 0x2B, 0xC3, 0x2B, 0x46, 0x08, 0x2B, 0x46, 0x06,
      0x03, 0x46, 0x04, 0x5D, 0xC2, 0x06, 0x00];

  // leaf watcall FUNCTION mix(a,b,c,d,e,f AS INTEGER) -> a-b-c-d-e+f; AX,DX,BX,CX then RTL stack
  private static readonly byte[] _watcallMix =
    [0x55, 0x8B, 0xEC, 0x2B, 0xC2, 0x2B, 0xC3, 0x2B, 0xC1, 0x2B, 0x46, 0x04,
      0x03, 0x46, 0x06, 0x5D, 0xC2, 0x04, 0x00];

  // identity(BYVAL value AS BYTE): the caller must widen the value into AX
  private static readonly byte[] _registerIdentity = [0xC3];

  // load(value AS INTEGER): AX is a near BYREF pointer; return the pointed-to word
  private static readonly byte[] _registerLoad = [0x8B, 0xD8, 0x8B, 0x07, 0xC3];

  private static PbuFile ObjectUnit(string moduleName, string symbol, byte[] code) {
    byte[] obj = [
      .. Record(0x80, Str(moduleName)),
      .. Record(0x96, Str("_TEXT"), Str("CODE")),
      .. Record(0x98, [0x28], U16(code.Length), [1], [2], [0]),
      .. Record(0x90, [0], [1], Str(symbol), U16(0), [0]),
      .. Record(0xA0, [1], U16(0), code),
      .. Record(0x8A, [0]),
    ];
    return OmfToPbu.Convert(OmfReader.ReadObject(obj));
  }

  private static PbuFile AddOneUnit() => ObjectUnit("ADDONE", "_addone", _addOne);

  private static PbuFile SubTwoUnit() => ObjectUnit("SUBTWO", "_sub2", _subTwo);

  private static PbuFile RegisterMixUnit(string convention) => convention == "FASTCALL"
    ? ObjectUnit("FASTMIX", "@mix", _fastcallMix)
    : ObjectUnit("WATMIX", "mix_", _watcallMix);

  private static PbuFile RegisterIdentityUnit(string convention) => convention == "FASTCALL"
    ? ObjectUnit("FA_ID", "@identity", _registerIdentity)
    : ObjectUnit("WA_ID", "identity_", _registerIdentity);

  private static PbuFile RegisterLoadUnit(string convention) => convention == "FASTCALL"
    ? ObjectUnit("FA_LOAD", "@load", _registerLoad)
    : ObjectUnit("WA_LOAD", "load_", _registerLoad);

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

  [TestCase(false)]
  [TestCase(true)]
  public void Route_GivenCdeclObjectLinked_WhenBackEndEnabled_ThenMainRoutesAndMatchesDirect(bool optimize) {
    const string source = """
      DECLARE FUNCTION sub2 CDECL ALIAS "_sub2" (BYVAL a AS INTEGER, BYVAL b AS INTEGER) AS INTEGER
      PRINT sub2(20, 7)
      PRINT sub2(100, 9)
      END
      """;
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var model = Binder.Bind(unit, Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var directGenerator = new CodeGenerator(model) { Optimize = optimize };
    var direct = directGenerator.EmitExecutable([SubTwoUnit()], []);
    Assert.That(directGenerator.Errors, Is.Empty, "direct: " + string.Join("; ", directGenerator.Errors));

    var routedGenerator = new CodeGenerator(model) {
      Optimize = optimize,
      UseExperimentalBackend = true,
    };
    var routed = routedGenerator.EmitExecutable([SubTwoUnit()], []);
    var directOutput = Exec.Cpu8086.Run(direct).Output.Trim().Replace("\r\n", "|");
    var routedOutput = Exec.Cpu8086.Run(routed).Output.Trim().Replace("\r\n", "|");

    Assert.Multiple(() => {
      Assert.That(routedGenerator.Errors, Is.Empty,
        "routed: " + string.Join("; ", routedGenerator.Errors));
      Assert.That(routedGenerator.BackendRoutedNames, Does.Contain("main"));
      Assert.That(routedOutput, Is.EqualTo(directOutput));
      Assert.That(routedOutput, Is.EqualTo("13 | 91"));
    });
  }

  [TestCase(false, "FASTCALL", "@mix")]
  [TestCase(true, "FASTCALL", "@mix")]
  [TestCase(false, "WATCALL", "mix_")]
  [TestCase(true, "WATCALL", "mix_")]
  public void Route_GivenRegisterConventionObjectLinked_WhenBackEndEnabled_ThenRegistersAndOverflowMatch(
      bool optimize, string convention, string symbol) {
    var declaration = $"DECLARE FUNCTION mix {convention} ALIAS \"{symbol}\" "
      + "(BYVAL a AS INTEGER, BYVAL b AS INTEGER, BYVAL c AS INTEGER, "
      + "BYVAL d AS INTEGER, BYVAL e AS INTEGER, BYVAL f AS INTEGER) AS INTEGER";
    var source = $"""
      {declaration}
      PRINT mix(50, 8, 4, 2, 1, 6)
      PRINT mix(100, 9, 5, 3, 2, 4)
      PRINT 7
      END
      """;
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var model = Binder.Bind(unit, Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var linkedUnit = RegisterMixUnit(convention);
    var directGenerator = new CodeGenerator(model) { Optimize = optimize };
    var direct = directGenerator.EmitExecutable([linkedUnit], []);
    Assert.That(directGenerator.Errors, Is.Empty, "direct: " + string.Join("; ", directGenerator.Errors));

    var routedGenerator = new CodeGenerator(model) {
      Optimize = optimize,
      UseExperimentalBackend = true,
    };
    var routed = routedGenerator.EmitExecutable([linkedUnit], []);
    var directOutput = Exec.Cpu8086.Run(direct).Output.Trim().Replace("\r\n", "|");
    var routedOutput = Exec.Cpu8086.Run(routed).Output.Trim().Replace("\r\n", "|");

    Assert.Multiple(() => {
      Assert.That(routedGenerator.Errors, Is.Empty,
        "routed: " + string.Join("; ", routedGenerator.Errors));
      Assert.That(routedGenerator.BackendRoutedNames, Does.Contain("main"));
      Assert.That(routedOutput, Is.EqualTo(directOutput));
      Assert.That(routedOutput, Is.EqualTo("41 | 85 | 7"));
    });
  }

  [TestCase("FASTCALL", "@identity")]
  [TestCase("WATCALL", "identity_")]
  public void Route_GivenByteRegisterArgument_WhenBackEndEnabled_ThenWordValueMatchesDirect(
      string convention, string symbol) {
    var source = $"""
      DECLARE FUNCTION identity {convention} ALIAS "{symbol}" (BYVAL value AS BYTE) AS INTEGER
      PRINT identity(200)
      END
      """;
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var model = Binder.Bind(unit, Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var linkedUnit = RegisterIdentityUnit(convention);
    var directGenerator = new CodeGenerator(model);
    var direct = directGenerator.EmitExecutable([linkedUnit], []);
    var routedGenerator = new CodeGenerator(model) { UseExperimentalBackend = true };
    var routed = routedGenerator.EmitExecutable([linkedUnit], []);
    var directOutput = Exec.Cpu8086.Run(direct).Output.Trim();
    var routedOutput = Exec.Cpu8086.Run(routed).Output.Trim();

    Assert.Multiple(() => {
      Assert.That(directGenerator.Errors, Is.Empty, "direct: " + string.Join("; ", directGenerator.Errors));
      Assert.That(routedGenerator.Errors, Is.Empty, "routed: " + string.Join("; ", routedGenerator.Errors));
      Assert.That(routedGenerator.BackendRoutedNames, Does.Contain("main"));
      Assert.That(routedOutput, Is.EqualTo(directOutput));
      Assert.That(routedOutput, Is.EqualTo("200"));
    });
  }

  [TestCase("FASTCALL", "@load")]
  [TestCase("WATCALL", "load_")]
  public void Route_GivenByRefRegisterArgument_WhenBackEndEnabled_ThenNearPointerMatchesDirect(
      string convention, string symbol) {
    var source = $"""
      DECLARE FUNCTION load {convention} ALIAS "{symbol}" (value AS INTEGER) AS INTEGER
      DIM value AS INTEGER
      value = 1234
      PRINT load(value)
      END
      """;
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var model = Binder.Bind(unit, Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var linkedUnit = RegisterLoadUnit(convention);
    var directGenerator = new CodeGenerator(model);
    var direct = directGenerator.EmitExecutable([linkedUnit], []);
    var routedGenerator = new CodeGenerator(model) { UseExperimentalBackend = true };
    var routed = routedGenerator.EmitExecutable([linkedUnit], []);
    var directOutput = Exec.Cpu8086.Run(direct).Output.Trim();
    var routedOutput = Exec.Cpu8086.Run(routed).Output.Trim();

    Assert.Multiple(() => {
      Assert.That(directGenerator.Errors, Is.Empty, "direct: " + string.Join("; ", directGenerator.Errors));
      Assert.That(routedGenerator.Errors, Is.Empty, "routed: " + string.Join("; ", routedGenerator.Errors));
      Assert.That(routedGenerator.BackendRoutedNames, Does.Contain("main"));
      Assert.That(routedOutput, Is.EqualTo(directOutput));
      Assert.That(routedOutput, Is.EqualTo("1234"));
    });
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Emit_GivenExternalRegisterConventionWithWideArgument_ThenRejectsUnsupportedAbi(bool routed) {
    const string source = """
      DECLARE FUNCTION wide WATCALL ALIAS "wide_" (BYVAL value AS LONG) AS LONG
      PRINT wide(1)
      END
      """;
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var model = Binder.Bind(unit, Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { UseExperimentalBackend = routed };

    generator.EmitExecutable([ObjectUnit("WIDE", "wide_", [0xC3])], []);

    Assert.That(generator.Errors.Select(error => error.Message), Has.Some.Contains("word-sized"),
      "a linked external declaration must not bypass the common register-ABI size guard");
  }
}
