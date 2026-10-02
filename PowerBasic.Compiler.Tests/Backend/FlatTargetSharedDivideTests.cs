using PowerBasic.Compiler.Cli;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// O0079 on x86-32 and x64: <c>n \ d</c> and <c>n MOD d</c> over the same operands share one
/// <c>IDIV</c>, so a program that asks for both carries no more divides than one that asks only for the
/// quotient (<see cref="Ir.IrDivRem"/>; the answers themselves are held to DOS by
/// <see cref="FlatTargetRuntimeTests"/>).
/// </summary>
[TestFixture]
public sealed class FlatTargetSharedDivideTests {

  private static byte[] Build(string platform, string source) {
    var work = Directory.CreateTempSubdirectory("pbc-divide-");
    try {
      var path = Path.Combine(work.FullName, "PROG.BAS");
      File.WriteAllText(path, source);
      var stderr = new StringWriter();
      Assert.That(Driver.Run(["--dialect", "pb36", "--platform", platform, path], TextWriter.Null, stderr), Is.Zero, stderr.ToString());
      return File.ReadAllBytes(Path.ChangeExtension(path, null));
    } finally {
      work.Delete(recursive: true);
    }
  }

  /// <summary>How often <c>IDIV ECX</c>/<c>IDIV RCX</c> (<c>F7 F9</c>) occurs in the image.</summary>
  private static int Divides(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0xF7 && image[i + 1] == 0xF9)
        ++count;
    return count;
  }

  [TestCase("x86-32")]
  [TestCase("x64")]
  public void Build_GivenQuotientAndRemainderOfTheSameOperands_ThenOneDivideAnswersBoth(string platform) {
    const string quotientOnly = "INPUT n&, d&\nq& = n& \\ d&\nPRINT \"q\"\nPRINT q&\n";
    const string both = "INPUT n&, d&\nq& = n& \\ d&\nPRINT \"q\"\nr& = n& MOD d&\nPRINT q&; r&\n";

    Assert.That(Divides(Build(platform, both)), Is.EqualTo(Divides(Build(platform, quotientOnly))));
  }
}
