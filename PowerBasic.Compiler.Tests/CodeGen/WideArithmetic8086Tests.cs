using System.Globalization;
using System.Numerics;
using System.Text;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// 64-, 128-, 256- and 512-bit integer arithmetic on the 8086, which has none of it: <c>QUAD</c> through
/// the x87 and the runtime, the pb36 wide integers as word-by-word carry chains. Each program runs on
/// <see cref="Exec.Cpu8086"/> with bit-exact x87 arithmetic, with the optimizer on and off, and every
/// answer is held to <see cref="BigInteger"/> - a wide value is read back byte by byte, so a carry lost
/// into a high word shows. The operands come from DATA, so nothing folds at compile time.
/// </summary>
[TestFixture]
public sealed class WideArithmetic8086Tests {

  private static string Run(string source, bool optimize) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    var cpu = Exec.Cpu8086.Run(exe, maxSteps: 80_000_000, exactFloatingPoint: true);
    return cpu.Output.Replace("\r\n", "\n").TrimEnd('\n');
  }

  // every answer printed stays below 10^15, where a QUAD prints as digits rather than as a DOUBLE
  private static readonly long[] QuadOperands = [9_000_000_000_123, -77_777_777_777, 3, -1, 450_359_962_737_049];

  [TestCase(true)]
  [TestCase(false)]
  public void Quad_GivenArithmetic_ThenEveryAnswerIsBigIntegers(bool optimize) {
    var source = new StringBuilder("FOR i% = 1 TO 4\n  READ a&&, b&&\n  PRINT a&& + b&&; a&& - b&&; a&& \\ b&&; a&& MOD b&&; a&& > b&&\nNEXT\n");
    source.Append("READ m&&, n&&\nPRINT m&& * n&&\n");
    long[][] pairs = [[QuadOperands[0], QuadOperands[1]], [QuadOperands[1], QuadOperands[2]], [QuadOperands[4], QuadOperands[3]], [QuadOperands[3], QuadOperands[0]]];
    source.Append("DATA ").Append(string.Join(", ", pairs.SelectMany(pair => pair))).Append(", 30370004, -30370004\n");

    var expected = new StringBuilder();
    foreach (var pair in pairs) {
      var (a, b) = (pair[0], pair[1]);
      expected.Append(Quad(a + b)).Append(Quad(a - b)).Append(Quad(a / b)).Append(Quad(a % b))
        .Append(a > b ? "-1 " : " 0 ").Append('\n');
    }
    expected.Append(Quad(30370004L * -30370004L));

    Assert.That(Run(source.ToString(), optimize).TrimEnd(' ').Replace(" \n", "\n"), Is.EqualTo(expected.ToString().TrimEnd(' ').Replace(" \n", "\n")));
  }

  private static string Quad(long value) => (value < 0 ? "" : " ") + value.ToString(CultureInfo.InvariantCulture) + " ";

  [TestCase("INT128", 128, true)]
  [TestCase("INT256", 256, true)]
  [TestCase("INT512", 512, true)]
  [TestCase("INT128", 128, false)]
  [TestCase("INT512", 512, false)]
  public void Wide_GivenCarriesAcrossEveryWord_ThenTheBytesAreBigIntegers(string type, int bits, bool optimize) {
    var bytes = bits / 8;
    // a wide value is read through its address, byte by byte, high first
    string Show(string name, int width)
      => $"p& = VARPTR({name}): FOR k% = {width - 1} TO 0 STEP -1: PRINT RIGHT$(\"0\" + HEX$(PEEK(p& + k%)), 2);: NEXT: PRINT\n";
    var source = $"DIM a AS {type}, b AS {type}, c AS {type}, d AS {type}\n"
      + "READ q&&, l&\n"
      + "a = q&&\nb = l&\nc = a + a\nc = c + c\nc = c - b\nd = b - a\nd = d - c\n"
      + Show("a", bytes) + Show("c", bytes) + Show("d", bytes)
      // a QUAD of 10^15 or more prints in DOUBLE form, as the DOS runtime prints it: read its bytes
      + "r&& = d\n" + Show("r&&", 8)
      + "DATA 9223372036854775807, -5\n";

    var modulus = BigInteger.One << bits;
    BigInteger Wrap(BigInteger v) => ((v % modulus) + modulus) % modulus;
    string Hex(BigInteger v) => Wrap(v).ToString("X", CultureInfo.InvariantCulture).TrimStart('0').PadLeft(bytes * 2, '0');
    BigInteger a = long.MaxValue, b = -5;
    var c = a + a + a + a - b;
    var d = b - a - c;
    var low = (Wrap(d) & ulong.MaxValue).ToString("X16", CultureInfo.InvariantCulture)[^16..];

    Assert.That(Run(source, optimize).Split('\n'), Is.EqualTo(new[] { Hex(a), Hex(c), Hex(d), low })
      .Using<string, string>((actual, wanted) => actual.TrimEnd() == wanted.TrimEnd()));
  }
}
