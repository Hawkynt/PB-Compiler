using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// <c>DIM a(...) AT segment</c> through the IR and the x86-16 back end: an ABSOLUTE array is a VIEW of
/// memory the program does not own, so the only thing worth asserting about it is WHERE its stores
/// land.
///
/// <para>
/// That is why these tests read the bytes back rather than reading the program's output: an element
/// store lowered to an ordinary near pointer writes somewhere in the program's own data and then reads
/// back exactly what it wrote, so a round trip through the array alone proves nothing at all. The
/// checks here look at the emulated machine's memory at the named segment, and at what PEEK - which
/// reaches those bytes by an unrelated route, through <c>DEF SEG</c> and the runtime - says is there.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendAbsoluteArrayTests {

  /// <summary>
  /// Writes 0x4142 and 0x3039 into the text screen through an AT array, reads them back as bytes with
  /// PEEK, and prints both. The values are chosen so every byte is distinct and non-zero: a store that
  /// went to the wrong segment leaves zeros here, and zeros are what an unwritten cell reads as.
  /// </summary>
  private const string _videoProgram = """
    DIM DYNAMIC vid%(0 TO 7) AT &HB800
    vid%(0) = 16706
    vid%(7) = 12345
    FOR i% = 1 TO 6
      vid%(i%) = i% * 3 - 1
    NEXT i%
    DEF SEG = &HB800
    PRINT PEEK(0); PEEK(1); PEEK(14); PEEK(15)
    DEF SEG
    PRINT vid%(0); vid%(4); vid%(7)
    """;

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenAnAtArray_WhenRouted_ThenEveryStoreLandsInTheNamedSegment(bool optimize) {
    var routed = new CodeGenerator(Bind(_videoProgram)) { Optimize = optimize, UseExperimentalBackend = true };

    var cpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("main"),
      "the module body must not fall back to the direct emitter - then this would test that instead");
    // 16706 = 0x4142 and 12345 = 0x3039, little-endian, at the first and last element of eight words
    Assert.That(Bytes(cpu, 0xB800, 0, 4), Is.EqualTo(new byte[] { 0x42, 0x41, 0x02, 0x00 }));
    Assert.That(Bytes(cpu, 0xB800, 14, 2), Is.EqualTo(new byte[] { 0x39, 0x30 }));
    Assert.That(cpu.Output.Replace("\r\n", "|"),
      Is.EqualTo(" 66  65  57  48 | 16706  11  12345 |"));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenAnAtArray_WhenRouted_ThenItAgreesWithTheDirectEmitter(bool optimize) {
    var direct = new CodeGenerator(Bind(_videoProgram)) { Optimize = optimize, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(_videoProgram)) { Optimize = optimize, UseExperimentalBackend = true };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
    Assert.That(Bytes(routedCpu, 0xB800, 0, 16), Is.EqualTo(Bytes(directCpu, 0xB800, 0, 16)));
  }

  /// <summary>
  /// Two AT arrays over the same segment with different lower bounds name the same bytes by different
  /// subscripts - <c>alt%(4)</c> is at offset 0, which is <c>vid%(0)</c>. This is what pins the
  /// lower-bound bias to the OFFSET rather than to some base the array does not have.
  /// </summary>
  [Test]
  public void Execute_GivenTwoAtArraysOverOneSegment_ThenTheirLowerBoundsBiasTheSameBytes() {
    const string source = """
      DIM DYNAMIC vid%(0 TO 7) AT &HB800
      DIM DYNAMIC alt%(4 TO 7) AT &HB800
      vid%(0) = 111
      vid%(3) = 333
      PRINT alt%(4); alt%(7)
      alt%(5) = 222
      PRINT vid%(1)
      """;
    var routed = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };

    var cpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(routed.BackendRoutedNames, Does.Contain("main"));
    Assert.That(cpu.Output.Replace("\r\n", "|"), Is.EqualTo(" 111  333 | 222 |"));
  }

  /// <summary>
  /// An element past the 32 KiB mark. The offset is a 16-bit UNSIGNED displacement within the
  /// segment, so 40000 is a perfectly ordinary address and the signed <c>int</c> the machine operand
  /// carries it in must not turn it into one 25536 bytes below the segment base.
  /// </summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Execute_GivenAnElementAboveThirtyTwoKiB_ThenTheDisplacementIsUnsigned(bool constantSubscript) {
    var subscript = constantSubscript ? "20000" : "k%";
    var source = $"""
      DIM DYNAMIC a%(0 TO 20000) AT &HB000
      k% = 20000
      a%({subscript}) = 4660
      PRINT a%({subscript})
      """;
    var routed = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };

    var cpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(routed.BackendRoutedNames, Does.Contain("main"));
    Assert.That(Bytes(cpu, 0xB000, 40000, 2), Is.EqualTo(new byte[] { 0x34, 0x12 }));
    Assert.That(cpu.Output.Replace("\r\n", "|"), Is.EqualTo(" 4660 |"));
  }

  /// <summary>
  /// The boundary of what lowers, pinned so that widening it is a deliberate act. Every one of these
  /// declines - and a decline costs coverage, where lowering an element access whose segment the IR
  /// cannot name costs correctness.
  /// </summary>
  [TestCase("DIM HUGE h&(0 TO 99999)\nh&(0) = 1", "HUGE spans more segments than one AT names")]
  [TestCase("DIM VIRTUAL v&(1 TO 50000)\nv&(1) = 1", "VIRTUAL is EMS-paged, not a fixed segment")]
  [TestCase("s% = &HB800\nDIM DYNAMIC a%(0 TO 7) AT s%\na%(0) = 1", "a runtime AT segment")]
  [TestCase("DIM DYNAMIC a%(0 TO 7) AT &HB800\nERASE a%", "ERASE unmaps an AT array")]
  [TestCase("DIM DYNAMIC a%(0 TO 7) AT &HB800\nREDIM a%(0 TO 15)", "REDIM would allocate over the view")]
  public void Lower_GivenAnArrayClassOutsideTheSubset_ThenDeclinesRatherThanGuessingASegment(
      string source, string why) {
    var module = IrLowering.TryLowerModule(Bind(source), out var reason);

    Assert.That(module, Is.Null, $"{why}: expected a decline, got a lowered module");
    Assert.That(reason, Is.Not.Null.And.Not.Empty);
  }

  private static byte[] Bytes(Cpu8086 cpu, ushort segment, int offset, int count)
    => [.. Enumerable.Range(offset, count).Select(at => cpu.MemoryAt(segment, at))];
}
