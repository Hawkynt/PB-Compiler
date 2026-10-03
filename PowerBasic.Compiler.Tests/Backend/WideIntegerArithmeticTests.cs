using System.Globalization;
using System.Numerics;
using System.Text;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// pb36's wide integers, <c>INT128</c> to <c>UINT512</c>, through every operator on every target: the
/// 8086 (on <see cref="Cpu8086"/>, optimized and not), x86-32, x64 and the 6502. Each answer is held
/// to <see cref="BigInteger"/> wrapped to the type, and the operands come from DATA, so nothing is
/// settled at compile time.
/// </summary>
[TestFixture]
public sealed class WideIntegerArithmeticTests {

  private static readonly (string Type, int Bits, bool Signed)[] Types = [
    ("INT128", 128, true), ("INT256", 256, true), ("INT512", 512, true), ("UINT256", 256, false),
  ];

  private static IEnumerable<TestCaseData> Cases()
    => from target in (string[])["8086", "8086-optimized", "x86-32", "x64", "6502"]
       from type in Types
       select new TestCaseData(target, type.Type).SetName($"Run_GivenWideArithmetic_ThenEveryAnswerIsBigIntegers({target}, {type.Type})");

  /// <summary>a = x * 2^s + y and b likewise, so each operand reaches as high as its type lets it.</summary>
  private static (string Source, string Expected) Program(string type, int bits, bool signed) {
    var modulus = BigInteger.One << bits;
    BigInteger Wrap(BigInteger v) {
      var u = ((v % modulus) + modulus) % modulus;
      return signed && u >= modulus >> 1 ? u - modulus : u;
    }
    var shiftA = bits - 70;
    var shiftB = bits / 2;
    long xa = -987_654_321_012L, ya = 123_456_789L, xb = 77_777_777L, yb = -5L;
    if (!signed)
      (xa, yb) = (987_654_321_012L, 5L);
    var a = Wrap(Wrap(new BigInteger(xa) << shiftA) + ya);
    var b = Wrap(Wrap(new BigInteger(xb) << shiftB) + yb);

    var source = new StringBuilder();
    source.Append($"DIM a AS {type}, b AS {type}, c AS {type}, lo&\n");
    source.Append("READ xa&&, ya&&, sa%, xb&&, yb&&, sb%\n");
    source.Append("a = xa&&: a = a << sa%: a = a + ya&&\n");
    source.Append("b = xb&&: b = b << sb%: b = b + yb&&\n");
    source.Append("PRINT a\nPRINT b\n");
    source.Append("PRINT (a + b); (a - b)\nPRINT (a * b)\n");
    source.Append("PRINT (a \\ b); (a MOD b)\n");
    source.Append("PRINT (a = b); (a < b); (a > b); (a <> a); (b >= b)\n");
    source.Append("PRINT (a >> 7); (b << 3)\n");
    source.Append("PRINT (a AND b); (a OR b); (a XOR b)\n");
    source.Append("PRINT -b; NOT a\n");
    source.Append("c = a * a: lo& = c \\ b: PRINT c; lo&\n");
    source.Append("PRINT STR$(a \\ 1000000007)\n");
    // the top bit set: >> brings the sign down for a signed value and zeros for an unsigned one
    source.Append("c = NOT b: PRINT (c >> 3); (c >>> 3)\n");
    source.Append($"DATA {xa}, {ya}, {shiftA}, {xb}, {yb}, {shiftB}\n");

    static string N(BigInteger v) => (v.Sign < 0 ? "-" : " ") + BigInteger.Abs(v).ToString(CultureInfo.InvariantCulture) + " ";
    static string T(bool v) => v ? "-1 " : " 0 ";
    BigInteger Quotient(BigInteger x, BigInteger y) => BigInteger.Divide(x, y);
    var c = Wrap(a * a);
    // a LONG takes the low 32 bits of the quotient
    var low = (long)(int)(uint)(((Quotient(c, b) % (BigInteger.One << 32)) + (BigInteger.One << 32)) % (BigInteger.One << 32));
    var expected = new StringBuilder();
    expected.Append(N(a)).Append('\n').Append(N(b)).Append('\n');
    expected.Append(N(Wrap(a + b))).Append(N(Wrap(a - b))).Append('\n').Append(N(Wrap(a * b))).Append('\n');
    expected.Append(N(Wrap(Quotient(a, b)))).Append(N(Wrap(BigInteger.Remainder(a, b)))).Append('\n');
    expected.Append(T(a == b)).Append(T(a < b)).Append(T(a > b)).Append(T(false)).Append(T(true)).Append('\n');
    expected.Append(N(signed ? a >> 7 : a >> 7)).Append(N(Wrap(b << 3))).Append('\n');
    var mask = modulus - 1;
    BigInteger Bits(BigInteger v) => v & mask;
    expected.Append(N(Wrap(Bits(a) & Bits(b)))).Append(N(Wrap(Bits(a) | Bits(b)))).Append(N(Wrap(Bits(a) ^ Bits(b)))).Append('\n');
    expected.Append(N(Wrap(-b))).Append(N(Wrap(Bits(a) ^ mask))).Append('\n');
    expected.Append(N(c)).Append(N(low)).Append('\n');
    expected.Append(N(Wrap(Quotient(a, 1_000_000_007))).TrimEnd(' ')).Append('\n');
    var inverted = Wrap(Bits(b) ^ mask);
    expected.Append(N(Wrap(inverted >> 3))).Append(N(Wrap(Bits(inverted) >> 3))).Append('\n');
    return (source.ToString(), expected.ToString());
  }

  private static string OnThe8086(string source, bool optimize) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return Cpu8086.Run(image, maxSteps: 400_000_000).Output;
  }

  private static string Lines(string text)
    => string.Join("\n", Vice.Normalize(text).Replace("\r\n", "\n").Split('\n').Select(line => line.TrimEnd(' ')).Where(line => line.Length > 0));

  [TestCaseSource(nameof(Cases))]
  public void Run_GivenWideArithmetic_ThenEveryAnswerIsBigIntegers(string target, string type) {
    var (_, bits, signed) = Types.Single(t => t.Type == type);
    var (source, expected) = Program(type, bits, signed);
    var output = target switch {
      "8086" => OnThe8086(source, optimize: false),
      "8086-optimized" => OnThe8086(source, optimize: true),
      _ => FlatTargets.Run(target, source),
    };
    Assert.That(Lines(output), Is.EqualTo(Lines(expected)));
  }

  [TestCase("x64")]
  [TestCase("6502")]
  public void Run_GivenAWideDivisionByZero_ThenItIsError11(string platform) {
    var output = Vice.Normalize(FlatTargets.Run(platform, "DIM a AS INT512, b AS INT512\nREAD n&\na = 5\nb = n&\nPRINT \"before\"\nPRINT a \\ b\nDATA 0\n"));
    Assert.That(output, Does.StartWith("before"));
    Assert.That(output, Does.Contain("11"));
  }
}
