using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The declared <c>$CPU</c> target decides how a transcendental is computed, and the x86-16 back end
/// has to decide it the same way the direct emitter does.
///
/// <para>
/// The two paths emit into the SAME image - a program is routed function by function, not whole. So a
/// disagreement here is not two compilers producing two answers, it is ONE program computing sine two
/// different ways depending on which procedure asked. The back end used to call <c>rt_sin</c>
/// unconditionally on the grounds that it declared no CPU floor, which was true of the back end and
/// beside the point for the image.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendCpuTargetTests {

  /// <summary>
  /// The 80387-only opcodes. FPTAN is deliberately NOT among them: it dates from the original 8087
  /// and is exactly what the sub-386 routine is built out of - only the 387 READING of it (keep the
  /// tangent, discard the pushed one) is newer, and that is a sequence rather than an opcode.
  /// </summary>
  private static readonly (string Name, byte[] Bytes)[] _x387 =
    [("FSIN", [0xD9, 0xFE]), ("FCOS", [0xD9, 0xFF])];

  private static byte[] Compile(string source, bool routed) => Compile(source, routed, []);

  private static byte[] Compile(string source, bool routed, params string[] requiredRoutes) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true, UseExperimentalBackend = routed };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    if (routed) {
      Assert.That(cg.BackendRoutedNames, Does.Contain("main"), "the test must exercise routed code");
      foreach (var requiredRoute in requiredRoutes)
        Assert.That(cg.BackendRoutedNames, Does.Contain(requiredRoute), $"{requiredRoute} did not route");
    }
    return image;
  }

  private static bool Contains(byte[] image, byte[] pattern) {
    for (var i = 0; i + pattern.Length <= image.Length; ++i) {
      var hit = true;
      for (var j = 0; j < pattern.Length && hit; ++j)
        hit = image[i + j] == pattern[j];
      if (hit)
        return true;
    }
    return false;
  }

  private const string _TRIG = """
    $CPU {0}
    DIM x AS DOUBLE
    x = 0.5
    PRINT SIN(x); COS(x); TAN(x)
    END
    """;

  /// <summary>
  /// Under an 8086 floor neither path may emit a 387 opcode. Genuine PBC 3.5 compiles SIN, COS and
  /// TAN with none of them, through one shared FPTAN routine, and an image that contains one would
  /// fault on the processor it says it is for.
  /// </summary>
  [Test]
  public void Trig_GivenAn8086Target_ThenNeitherPathEmitsA387Opcode() {
    foreach (var routed in new[] { false, true }) {
      var image = Compile(string.Format(_TRIG, "8086"), routed);
      foreach (var (name, bytes) in _x387)
        Assert.That(Contains(image, bytes), Is.False,
          $"{(routed ? "routed" : "direct")} image for an 8086 contains {name}");
    }
  }

  /// <summary>
  /// Under a 386 floor the single instruction is used - and by BOTH paths, which is the whole point.
  /// Before the target reached the back end, this test would have passed for the direct image and
  /// failed for the routed one.
  /// </summary>
  [Test]
  public void Trig_GivenA386Target_ThenBothPathsUseTheInstruction() {
    foreach (var routed in new[] { false, true }) {
      var image = Compile(string.Format(_TRIG, "80386"), routed);
      Assert.That(Contains(image, [0xD9, 0xFE]), Is.True,
        $"{(routed ? "routed" : "direct")} image for a 386 should contain FSIN");
      Assert.That(Contains(image, [0xD9, 0xFF]), Is.True,
        $"{(routed ? "routed" : "direct")} image for a 386 should contain FCOS");
    }
  }

  /// <summary>
  /// And whichever way it is computed, the two paths must still print the same thing. An agreement
  /// about opcodes that did not survive execution would be worth nothing.
  ///
  /// <para>
  /// Only the 8086 target is run here. Focused tests below execute the emitted 386 integer subset;
  /// the transcendental image can use additional operand-prefixed forms outside that bounded model,
  /// so its target choice remains pinned by the opcode assertions above.
  /// </para>
  /// </summary>
  [TestCase("8086")]
  public void Trig_GivenEitherTarget_ThenTheTwoPathsAgreeWhenRun(string cpu) {
    var source = string.Format(_TRIG, cpu);
    var direct = Cpu8086.Run(Compile(source, routed: false));
    var routed = Cpu8086.Run(Compile(source, routed: true));
    Assert.That(routed.Output, Is.EqualTo(direct.Output), $"$CPU {cpu}");
  }

  /// <summary>
  /// The transcendentals that are the same sequence on every target, checked so that a future change
  /// to the CPU switch cannot quietly make one of them target-dependent as well.
  /// </summary>
  [TestCase("8086")]
  public void Logarithms_GivenEitherTarget_ThenTheTwoPathsAgreeWhenRun(string cpu) {
    var source = $"""
      $CPU {cpu}
      DIM x AS DOUBLE
      x = 2.5
      PRINT LOG(x); EXP(x); ATN(x); SQR(x)
      END
      """;
    var direct = Cpu8086.Run(Compile(source, routed: false));
    var routed = Cpu8086.Run(Compile(source, routed: true));
    Assert.That(routed.Output, Is.EqualTo(direct.Output), $"$CPU {cpu}");
  }

  /// <summary>
  /// The 80186 shift-by-immediate, asserted where it can actually be seen.
  ///
  /// <para>
  /// <c>C0</c>/<c>C1</c> - a shift by a constant count above one - is an 80186 instruction, and under
  /// the default <c>$CPU 8086</c> the selector emitted it from three places: a narrow shift by a
  /// literal, subscript scaling for a 4- or 8-byte element, and the sign smear of a widening. NO
  /// ORACLE HERE CAN SEE THAT. <c>Cpu8086</c> implements <c>C0</c>/<c>C1</c> (its own case says "186
  /// shift by immediate") and DOSBox emulates a 386, so every battery, golden and differential run
  /// passes either way - which is why it went unnoticed and why a byte-pattern search of the image
  /// would be the wrong test as well: <c>C1</c> is an ordinary byte and matches a displacement, a
  /// literal or a string as readily as an opcode.
  /// </para>
  ///
  /// <para>
  /// So the assertion is made against the MACHINE IR, where a shift is a shift: after selection for an
  /// 8086 target, no <c>SHL</c>/<c>SHR</c>/<c>SAR</c> may carry an immediate count other than one.
  /// Everything larger is repeated single-bit steps or a count staged into <c>CL</c>, which is the
  /// rule <c>CodeGenerator.EmitShiftLeft</c> states for the direct emitter.
  /// </para>
  /// </summary>
  [Test]
  public void Selection_GivenAn8086Target_ThenNoShiftCarriesAn80186ImmediateCount() {
    // every shape that used to produce one: a literal narrow shift, an array of a 4-byte element
    // (SHL 2) and of an 8-byte one (SHL 3), and INTEGER-to-LONG widening (SAR 15)
    const string source = """
      DECLARE FUNCTION Opaque%(BYVAL v%)
      DIM w%, l&, d#, i%
      DIM la&(0 TO 8), da#(0 TO 8)
      w% = Opaque%(9)
      SHIFT LEFT w%, 4
      i% = Opaque%(2)
      la&(i%) = 5 : da#(i%) = 2.5
      l& = CLNG(Opaque%(-3)) * 2
      PRINT w%; l&; la&(i%); da#(i%)
      END
      FUNCTION Opaque%(BYVAL v%) NOINLINE
        Opaque% = v% + 1
      END FUNCTION
      """;
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "S.BAS", Dialect.Pb36), "S.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));

    var generator = new CodeGenerator(model) { Optimize = true, UseExperimentalBackend = true };
    generator.EmitExecutable();
    // the program has to route, or this asserts about instructions nobody selected
    Assert.That(generator.BackendRoutedNames, Does.Contain("main"), "the module body did not route");

    var module = IrLowering.TryLowerModule(model);
    Assert.That(module, Is.Not.Null);
    IrPassManager.Standard().RunOnModule(module!);
    foreach (var fn in module!.Functions)
      if (!fn.IsDeclaration)
        IntegerRecovery.Run(fn);
    IrPassManager.Standard().RunOnModule(module);

    var offenders = new List<string>();
    var shifts = 0;
    foreach (var fn in module.Functions) {
      if (fn.IsDeclaration || InstructionSelector.TrySelect(fn, new SelectionTarget(Cpu386: false, Optimize: true)) is not { } machine)
        continue;
      foreach (var instr in machine.Blocks.SelectMany(b => b.Instructions)) {
        if (instr.Opcode is not (MOpcode.Shl or MOpcode.Shr or MOpcode.Sar))
          continue;
        ++shifts;
        if (instr.Operands[1] is MOperand.Immediate { Value: not 1 } count)
          offenders.Add($"{fn.Name}: {instr.Opcode} by {count.Value}");
      }
    }

    Assert.That(shifts, Is.GreaterThan(0), "no shift was selected at all - the program measures nothing");
    Assert.That(offenders, Is.Empty,
      "an 8086 target selected the 80186 shift-by-immediate:\n  " + string.Join("\n  ", offenders));
  }

  [TestCase("LEFT", "1", 16, "65536")]
  [TestCase("LEFT", "1", 31, "-2147483648")]
  [TestCase("RIGHT", "-2147483648", 31, "1")]
  [TestCase("RIGHT", "-1", 15, "131071")]
  public void LongShift_GivenA386RoutedBackend_ThenMatchesThe8086DirectBoundaryValue(
      string direction, string value, int count, string expected) {
    const string source = """
      $CPU {0}
      $OPTIMIZE SPEED
      DECLARE FUNCTION Shifted&(BYVAL x&)
      PRINT Shifted&({1})
      PRINT Shifted&(5)
      END
      FUNCTION Shifted&(BYVAL x&) NOINLINE
        SHIFT {2} x&, {3}
        Shifted& = x&
      END FUNCTION
      """;

    var direct = Cpu8086.Run(Compile(string.Format(source, "8086", value, direction, count), routed: false));
    var routedImage = Compile(string.Format(source, "80386", value, direction, count), routed: true, "Shifted");
    Assert.That(Contains(routedImage, [0x66, 0xC1]), Is.True, "the routed function did not use a dword shift");
    var routed = Cpu8086.Run(routedImage);

    Assert.Multiple(() => {
      Assert.That(direct.Output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[0].Trim(),
        Is.EqualTo(expected), "direct boundary result");
      Assert.That(routed.Output, Is.EqualTo(direct.Output), "routed");
    });
  }

  [Test]
  public void LongLoop_GivenA386SpeedTarget_ThenRoutedEsiAndEdiResidencyMatchesTheDirectEmitter() {
    const string source = """
      $CPU 80386
      $OPTIMIZE SPEED
      s& = 0
      FOR i& = 1 TO 100
        s& = s& + i&
        PRINT s&
      NEXT i&
      PRINT s&
      END
      """;
    var direct = Cpu8086.Run(Compile(source, routed: false));
    var routedImage = Compile(source, routed: true);
    var routed = Cpu8086.Run(routedImage);

    Assert.Multiple(() => {
      Assert.That(Contains(routedImage, [0x66, 0x83, 0xC6]), Is.True,
        "the routed LONG counter should increment in ESI");
      Assert.That(Contains(routedImage, [0x66, 0x01, 0xF7]), Is.True,
        "the routed LONG accumulator should add ESI directly into EDI");
      Assert.That(routed.Output, Is.EqualTo(direct.Output));
      var lines = routed.Output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .ToList();
      Assert.That(lines, Has.Count.EqualTo(101));
      Assert.That(lines[0], Is.EqualTo("1"));
      Assert.That(lines[^1], Is.EqualTo("5050"));
    });
  }

  [Test]
  public void LongDivide_GivenA386Target_ThenDirectCdqIdivAndRoutedRuntimeAgree() {
    const string source = """
      $CPU 80386
      $OPTIMIZE SPEED
      DECLARE SUB Report(BYVAL n&)
      Report 100000007
      Report -100000007
      Report -8
      END
      SUB Report(BYVAL n&) NOINLINE
        PRINT n& \ 7; n& MOD 7; n& \ -7; n& MOD -7
      END SUB
      """;
    var directImage = Compile(source, routed: false);
    var routedImage = Compile(source, routed: true, "Report");

    Assert.Multiple(() => {
      Assert.That(Contains(directImage, [0x66, 0x99]), Is.True, "the direct 386 path should sign-extend EAX with CDQ");
      Assert.That(Contains(directImage, [0x66, 0xF7]), Is.True, "the direct 386 path should use dword IDIV");
      Assert.That(Cpu8086.Run(routedImage).Output, Is.EqualTo(Cpu8086.Run(directImage).Output));
    });
  }

  [Test]
  public void LongRotate_GivenA386Target_ThenDirectAndRoutedBoundaryPatternsAgree() {
    const string source = """
      $CPU 80386
      $OPTIMIZE SPEED
      x& = &H12345678
      ROTATE LEFT x&, 8
      PRINT x&
      ROTATE RIGHT x&, 16
      PRINT x&
      END
      """;
    var directImage = Compile(source, routed: false);
    var routedImage = Compile(source, routed: true);

    var direct = Cpu8086.Run(directImage).Output;
    var routed = Cpu8086.Run(routedImage).Output;
    Assert.Multiple(() => {
      Assert.That(direct, Is.EqualTo(" 878082066 \r\n 2014458966 \r\n"));
      Assert.That(routed, Is.EqualTo(direct));
    });
  }

  [Test]
  public void ConstantArrayFill_GivenA386Target_ThenRepStosdBroadcastsEveryWord() {
    const string source = """
      $CPU 80386
      $OPTIMIZE SPEED
      DIM a%(1 TO 5)
      FOR i% = 1 TO 5
        a%(i%) = -5
      NEXT i%
      PRINT a%(1); a%(3); a%(5)
      END
      """;
    var image = Compile(source, routed: false);

    Assert.Multiple(() => {
      Assert.That(Contains(image, [0xF3, 0x66, 0xAB]), Is.True,
        "the 386 constant-fill optimization should use REP STOSD");
      Assert.That(Cpu8086.Run(image).Output, Is.EqualTo("-5 -5 -5 \r\n"));
    });
  }
}
