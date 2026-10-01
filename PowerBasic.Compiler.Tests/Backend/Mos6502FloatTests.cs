using PowerBasic.Compiler.Backend.Mos6502;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The 6502 soft-float routines against .NET's IEEE arithmetic, bit for bit. Each case unpacks two
/// operands, runs one routine and packs the result, on <see cref="Cpu6502"/>; .NET rounds every
/// SINGLE and DOUBLE operation to nearest-even exactly once, which is precisely the contract, so any
/// difference at all is a bug. Operands are random but seeded, spread over signs, magnitudes and
/// mantissa patterns, with the special cases - equal exponents, cancellation, carries - drawn often.
/// </summary>
[TestFixture]
public sealed class Mos6502FloatTests {

  private const int Origin = 0x1000;
  private const int X = 0x0300, Y = 0x0310, Result = 0x0320, ErrorCode = 0x0330;

  /// <summary>A program that runs <paramref name="operation"/> on X and Y in <paramref name="format"/> and packs into Result.</summary>
  private static byte[] Harness(M6502FloatFormat format, M6502Routine operation, M6502FloatFormat? resultFormat = null) {
    var asm = new Mos6502Assembler();
    var runtime = new Mos6502Runtime(asm);
    var body = asm.NewLabel("body");
    var error = asm.NewLabel("error");
    // standing in for the portable runtime's rt_error: the code lands in ErrorCode, the program ends
    runtime.ErrorFunction = (error, M6502Address.Absolute(ErrorCode));
    runtime.EmitStartup(body, M6502Address.Absolute(0x0400), 0);
    asm.Bind(error);
    asm.Jump(runtime.Routine(M6502Routine.Exit));
    asm.Bind(body);
    Pointer(asm, X);
    asm.Call(runtime.Routine(Mos6502Runtime.Unpack(format, intoB: false)));
    Pointer(asm, Y);
    asm.Call(runtime.Routine(Mos6502Runtime.Unpack(format, intoB: true)));
    asm.Call(runtime.Routine(operation));
    if (operation == M6502Routine.FloatCompare) {
      asm.Memory(M6502Op.Sta, M6502Address.Absolute(Result));
    } else {
      Pointer(asm, Result);
      asm.Call(runtime.Routine(Mos6502Runtime.Pack(resultFormat ?? format)));
    }
    asm.Emit(M6502Op.Rts);
    runtime.EmitRequested();
    var image = asm.Assemble(Origin);
    var memory = new byte[0x10000];
    image.Bytes.CopyTo(memory, Origin);
    return memory;
  }

  private static void Pointer(Mos6502Assembler asm, int address) {
    asm.Immediate(M6502Op.Lda, address & 0xFF);
    asm.Memory(M6502Op.Sta, Mos6502ZeroPage.Ptr);
    asm.Immediate(M6502Op.Lda, address >> 8);
    asm.Memory(M6502Op.Sta, Mos6502ZeroPage.Ptr.Plus(1));
  }

  private static byte[] Run(byte[] program, ReadOnlySpan<byte> x, ReadOnlySpan<byte> y) {
    var memory = (byte[])program.Clone();
    x.CopyTo(memory.AsSpan(X));
    y.CopyTo(memory.AsSpan(Y));
    var result = Cpu6502.Call(memory, Origin, maxSteps: 2_000_000);
    Assert.That(result.Returned, Is.True, "the routine did not return: " + result.Output);
    return memory[Result..(Result + 10)];
  }

  /// <summary>Doubles over many magnitudes, with shared exponents and near-equal pairs drawn often.</summary>
  private static IEnumerable<(double, double)> DoublePairs(int count) {
    var random = new Random(6502);
    double Next() {
      var mantissa = random.NextDouble() + 1;
      var exponent = random.Next(-60, 61);
      var value = Math.ScaleB(mantissa, exponent);
      return random.Next(2) == 0 ? value : -value;
    }
    for (var i = 0; i < count; ++i) {
      var a = Next();
      var b = (i % 4) switch {
        0 => Next(),
        1 => Math.ScaleB(random.NextDouble() + 1, Math.ILogB(a)) * Math.Sign(a) * (random.Next(2) == 0 ? 1 : -1),
        2 => -a * (1 + Math.ScaleB(random.Next(1, 1000), -52)),
        _ => (double)random.Next(-1000, 1000),
      };
      yield return (a, b);
    }
    yield return (1, -1);
    yield return (0.1, 0.2);
    yield return (1e300, 1e-300);
    yield return (0, 5);
  }

  private static readonly (M6502Routine Routine, Func<double, double, double> Reference)[] _operations = [
    (M6502Routine.FloatAdd, (a, b) => a + b),
    (M6502Routine.FloatSubtract, (a, b) => a - b),
    (M6502Routine.FloatMultiply, (a, b) => a * b),
    (M6502Routine.FloatDivide, (a, b) => a / b),
  ];

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(2)]
  [TestCase(3)]
  public void Double_GivenRandomOperands_ThenEveryResultIsTheCorrectlyRoundedDouble(int operation) {
    var (routine, reference) = _operations[operation];
    var program = Harness(M6502FloatFormat.Double, routine);
    var failures = new List<string>();
    foreach (var (a, b) in DoublePairs(400)) {
      if (routine == M6502Routine.FloatDivide && b == 0)
        continue;
      var expected = reference(a, b);
      if (!double.IsFinite(expected))
        continue;   // BASIC has no infinities: overflow is error 6, which Overflow_... pins
      var actual = BitConverter.ToDouble(Run(program, BitConverter.GetBytes(a), BitConverter.GetBytes(b)), 0);
      if (BitConverter.DoubleToInt64Bits(expected) != BitConverter.DoubleToInt64Bits(actual) && !(expected == 0 && actual == 0))
        failures.Add($"{a:R} {routine} {b:R}: expected {expected:R}, got {actual:R}");
    }
    Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(10)));
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(2)]
  [TestCase(3)]
  public void Single_GivenRandomOperands_ThenEveryResultIsTheCorrectlyRoundedSingle(int operation) {
    var (routine, reference) = _operations[operation];
    var program = Harness(M6502FloatFormat.Single, routine);
    var failures = new List<string>();
    foreach (var (da, db) in DoublePairs(400)) {
      var (a, b) = ((float)da, (float)db);
      if ((routine == M6502Routine.FloatDivide && b == 0) || !float.IsFinite(a) || !float.IsFinite(b))
        continue;
      var expected = (float)reference(a, b);   // exact in double, then one rounding to single
      if (!float.IsFinite(expected))
        continue;
      var actual = BitConverter.ToSingle(Run(program, BitConverter.GetBytes(a), BitConverter.GetBytes(b)), 0);
      if (BitConverter.SingleToInt32Bits(expected) != BitConverter.SingleToInt32Bits(actual) && !(expected == 0 && actual == 0))
        failures.Add($"{a:R} {routine} {b:R}: expected {expected:R}, got {actual:R}");
    }
    Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(10)));
  }

  [TestCase(M6502FloatFormat.Double, M6502Routine.FloatMultiply, 1e300, 1e300, 6)]
  [TestCase(M6502FloatFormat.Double, M6502Routine.FloatDivide, 1e300, 1e-300, 6)]
  [TestCase(M6502FloatFormat.Double, M6502Routine.FloatDivide, 1.0, 0.0, 11)]
  [TestCase(M6502FloatFormat.Single, M6502Routine.FloatMultiply, 1e30, 1e30, 6)]
  public void Overflow_GivenAResultPastTheFormat_ThenBasicsErrorIsRaised(M6502FloatFormat format, M6502Routine routine,
      double a, double b, int error) {
    var program = Harness(format, routine);
    var memory = (byte[])program.Clone();
    var (x, y) = format == M6502FloatFormat.Single
      ? (BitConverter.GetBytes((float)a), BitConverter.GetBytes((float)b))
      : (BitConverter.GetBytes(a), BitConverter.GetBytes(b));
    x.CopyTo(memory.AsSpan(X));
    y.CopyTo(memory.AsSpan(Y));

    var result = Cpu6502.Call(memory, Origin, maxSteps: 2_000_000);

    Assert.That(result.Returned, Is.True);
    Assert.That(memory[ErrorCode], Is.EqualTo(error), "no infinity: the program stops with BASIC's error");
  }

  [Test]
  public void Compare_GivenRandomOperands_ThenTheOrderingIsTheDoublesOrdering() {
    var program = Harness(M6502FloatFormat.Double, M6502Routine.FloatCompare);
    var failures = new List<string>();
    foreach (var (a, b) in DoublePairs(300).Concat([(3.0, 3.0), (0.0, -0.0), (-2.0, -3.0)])) {
      var expected = (byte)(a < b ? 0xFF : a > b ? 1 : 0);
      var actual = Run(program, BitConverter.GetBytes(a), BitConverter.GetBytes(b))[0];
      if (expected != actual)
        failures.Add($"{a:R} vs {b:R}: expected {expected}, got {actual}");
    }
    Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(10)));
  }

  [Test]
  public void Extended_GivenDoublesThroughEightyBits_ThenTheyComeBackExact() {
    // Double -> Extended is exact, Extended -> Double rounds nothing: a round trip is the identity
    var widen = Harness(M6502FloatFormat.Double, M6502Routine.FloatAdd, M6502FloatFormat.Extended);
    var failures = new List<string>();
    foreach (var (a, _) in DoublePairs(100)) {
      var extended = Run(widen, BitConverter.GetBytes(a), BitConverter.GetBytes(0.0));
      var expected = PowerBasic.Compiler.Ir.IrFloat80.FromDouble(a);
      var significand = BitConverter.ToUInt64(extended, 0);
      var signExponent = BitConverter.ToUInt16(extended, 8);
      if (significand != expected.Significand || signExponent != expected.SignExponent)
        failures.Add($"{a:R}: expected {expected.SignExponent:X4}:{expected.Significand:X16}, got {signExponent:X4}:{significand:X16}");
    }
    Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(10)));
  }
}
