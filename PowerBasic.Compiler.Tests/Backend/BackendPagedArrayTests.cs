using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The memory-model array classes through the IR and the x86-16 back end: <c>DIM HUGE</c>, which takes
/// a DOS block and steps the segment per element, and the EMS-paged <c>VIRTUAL</c> / <c>EMS</c> /
/// <c>XMS</c> family.
///
/// <para>
/// Both models execute here. <see cref="Cpu8086"/> answers INT 21h/48h for HUGE blocks and models the
/// LIM EMS 41h-45h services for VIRTUAL storage. Tests therefore observe segment stepping past 64 KiB,
/// page-frame remapping past each 16 KiB page, write-back, allocation counts and release—not only the
/// presence of the relevant calls.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendPagedArrayTests {

  /// <summary>
  /// Elements on both sides of the 64 KiB boundary and a loop that walks across it. 16384 LONGs is
  /// exactly 65536 bytes, so <c>h(16384)</c> is the first element of the second segment - a subscript
  /// that answers whatever <c>h(0)</c> holds if the address arithmetic stayed 16-bit.
  /// </summary>
  private const string _hugeProgram = """
    DIM HUGE h(0 TO 20000) AS LONG
    h(0) = 11
    h(16383) = 22
    h(16384) = 33
    h(20000) = 999
    PRINT h(0); h(16383); h(16384); h(20000)
    t& = 0
    FOR i& = 16382 TO 16386
      h(i&) = i& * 2
      t& = t& + h(i&)
    NEXT i&
    PRINT t&
    PRINT LBOUND(h); UBOUND(h)
    ERASE h
    DIM HUGE w(1 TO 40000) AS INTEGER
    w(1) = 7
    w(32768) = 8
    w(40000) = 9
    PRINT w(1); w(32768); w(40000); LBOUND(w); UBOUND(w)
    """;

  private const string _virtualProgram = """
    before& = FRE(-11)
    DIM VIRTUAL v(1 TO 10000) AS LONG
    during& = FRE(-11)
    v(1) = 11
    v(4096) = 22
    v(4097) = 33
    v(8192) = 44
    v(8193) = 55
    v(10000) = 66
    PRINT v(1); v(4096); v(4097); v(8192); v(8193); v(10000)
    PRINT before& - during&
    ERASE v
    PRINT FRE(-11) - before&
    """;

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenAHugeArray_WhenRouted_ThenElementsAcrossSegmentsKeepTheirOwnValues(bool optimize) {
    var routed = new CodeGenerator(Bind(_hugeProgram)) { Optimize = optimize, UseExperimentalBackend = true };

    var cpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("main"),
      "the module body must not fall back to the direct emitter - then this would test that instead");
    Assert.That(cpu.Output.Replace("\r\n", "|"), Is.EqualTo(
      " 11  22  33  999 | 163840 | 0  20000 | 7  8  9  1  40000 |"));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenAHugeArray_WhenRouted_ThenItAgreesWithTheDirectEmitter(bool optimize) {
    var direct = new CodeGenerator(Bind(_hugeProgram)) { Optimize = optimize, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(_hugeProgram)) { Optimize = optimize, UseExperimentalBackend = true };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
  }

  [Test]
  public void Execute_GivenAVirtualArray_WhenRouted_ThenMappedPagesAndFreeCountMatchTheDirectEmitter() {
    var direct = new CodeGenerator(Bind(_virtualProgram)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(_virtualProgram)) { Optimize = true, UseExperimentalBackend = true };
    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("main"));

    var directCpu = Cpu8086.Run(directImage);
    var routedCpu = Cpu8086.Run(routedImage);
    Assert.Multiple(() => {
      Assert.That(directCpu.Output.Replace("\r\n", "|"),
        Is.EqualTo(" 11  22  33  44  55  66 | 65536 | 0 |"));
      Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
    });
  }

  /// <summary>
  /// The structural half of VIRTUAL coverage: the whole module body has to reach the back end and the
  /// window mapping has to be present, independently of the executable behavior test above.
  /// </summary>
  [Test]
  public void Route_GivenAVirtualArray_ThenTheModuleBodyRoutesThroughTheEmsPageMapper() {
    const string source = """
      DIM VIRTUAL v(1 TO 50000) AS LONG
      v(1) = 42
      v(4097) = 4097
      PRINT v(1); v(4097); FRE(-11) > 0
      """;
    var routed = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };

    var image = routed.EmitExecutable();

    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("main"));
    Assert.That(image, Is.Not.Empty);
  }

  /// <summary>
  /// The boundary of what lowers, pinned so widening it stays a deliberate act. Each of these is a
  /// shape the DIRECT emitter refuses too, or one whose descriptor the two paths could not share -
  /// and a decline costs coverage where a guess would cost correctness.
  /// </summary>
  [TestCase("DIM HUGE h(0 TO 3, 0 TO 3) AS LONG\nh(0, 0) = 1",
    "rank above one: the direct emitter reports it unsupported")]
  [TestCase("DIM HUGE h(0 TO 9) AS STRING\nh(0) = \"x\"",
    "a string element is a heap handle, not storage")]
  [TestCase("DIM HUGE h(0 TO 9) AS LONG\nREDIM PRESERVE h(0 TO 19)",
    "PRESERVE would have to copy between two segment-stepped blocks")]
  [TestCase("DIM EMS e(0 TO 9) AS LONG\ne(0) = 1\nERASE e",
    "ERASE of an EMS array reclaims it as a heap block on the direct path")]
  [TestCase("DIM HUGE h(0 TO 9) AS LONG\nCALL Bump(h(0))\nSUB Bump(BYREF n AS LONG)\n n = n + 1\nEND SUB",
    "a far element address passed BYREF would arrive as a near one")]
  [TestCase("DIM HUGE h(0 TO 9) AS LONG\nCALL Fill\nSUB Fill\n SHARED h()\n h(0) = 1\nEND SUB",
    "the descriptor is a frame slot, so a procedure cannot share it")]
  public void Lower_GivenAPagedArrayOutsideTheSubset_ThenDeclinesRatherThanGuessing(string source, string why) {
    var module = IrLowering.TryLowerModule(Bind(source), out var reason);

    Assert.That(module, Is.Null, $"{why}: expected a decline, got a lowered module");
    Assert.That(reason, Is.Not.Null.And.Not.Empty);
  }

  /// <summary>
  /// <c>FRE(-11)</c> is the free EMS byte count and lowers; every other FRE answers an advisory
  /// constant after CONSUMING its argument, which is an ownership rule the IR does not model.
  /// </summary>
  [Test]
  public void Lower_GivenFreOtherThanTheEmsForm_ThenDeclines() {
    Assert.That(IrLowering.TryLowerModule(Bind("PRINT FRE(-11)"), out _), Is.Not.Null);
    Assert.That(IrLowering.TryLowerModule(Bind("a$ = \"x\"\nPRINT FRE(a$)"), out var reason), Is.Null);
    Assert.That(reason, Does.Contain("FRE"));
  }
}
