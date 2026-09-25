using System.Diagnostics;
using PowerBasic.Compiler.Cli;
using PowerBasic.Compiler.Emit.Hosted;

namespace PowerBasic.Compiler.Tests.Cli;

/// <summary>
/// <c>pbc --platform x86-32|x64</c>: the C back end's translation unit built into a native ELF
/// executable, object or archive by the host toolchain. Each case runs what it built, so a passing
/// test is a program that printed the right thing - not merely a file with the right header. A
/// platform the host cannot target (no multilib C library for x86-32, no C compiler at all) is
/// skipped, never passed.
/// </summary>
[TestFixture]
public sealed class PlatformTests {

  private const string Program = """
    DECLARE FUNCTION Tri%(BYVAL n%)
    PRINT "hosted"; Tri%(INP(&H60) + 10)
    a$ = "x" + STR$(3)
    PRINT a$; LEN(a$)
    END
    FUNCTION Tri%(BYVAL n%)
      FOR i% = 1 TO n%
        s% = s% + i%
      NEXT
      Tri% = s%
    END FUNCTION
    """;

  // INP has no port to read on a hosted machine; the runtime answers 0, so Tri% sums 1..10
  private const string Expected = "hosted 55 \nx 3 3 ";

  private static readonly object[] _platforms = [
    new object[] { "x86-32", HostedPlatform.X86_32, (byte)1, (ushort)3 },
    new object[] { "x64", HostedPlatform.X64, (byte)2, (ushort)62 },
  ];

  private string _work = null!;

  [SetUp]
  public void CreateWorkDirectory() => _work = Directory.CreateTempSubdirectory("pbc-platform-").FullName;

  [TearDown]
  public void DeleteWorkDirectory() => Directory.Delete(_work, recursive: true);

  private string Build(string platform, HostedPlatform hosted, params string[] extra) {
    Assume.That(HostToolchain.Supports(hosted), $"this host's C toolchain cannot build for {platform}");
    var source = Path.Combine(_work, "PROG.BAS");
    File.WriteAllText(source, Program);
    var stdout = new StringWriter();
    var stderr = new StringWriter();
    var code = Driver.Run(["--dialect", "pb36", "--platform", platform, .. extra, source], stdout, stderr);
    Assert.That(code, Is.Zero, stderr.ToString());
    Assert.That(stdout.ToString(), Does.Contain($"({platform})"));
    return source;
  }

  private static void AssertElf(string path, byte elfClass, ushort machine, ushort fileType) {
    var bytes = File.ReadAllBytes(path);
    Assert.Multiple(() => {
      Assert.That(bytes[..4], Is.EqualTo("\u007fELF"u8.ToArray()), "ELF magic");
      Assert.That(bytes[4], Is.EqualTo(elfClass), "ELFCLASS32 or ELFCLASS64");
      Assert.That(BitConverter.ToUInt16(bytes, 16), Is.EqualTo(fileType), "e_type");
      Assert.That(BitConverter.ToUInt16(bytes, 18), Is.EqualTo(machine), "e_machine: EM_386 or EM_X86_64");
    });
  }

  private static string Execute(string path) {
    using var process = Process.Start(new ProcessStartInfo(path) { RedirectStandardOutput = true, UseShellExecute = false })!;
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    Assert.That(process.ExitCode, Is.Zero, output);
    return output.Replace("\r\n", "\n").TrimEnd('\n');
  }

  /// <summary>Links <paramref name="inputs"/> with the host compiler, the way a C project would consume them.</summary>
  private string Link(HostedPlatform hosted, params string[] inputs) {
    var output = Path.Combine(_work, "linked");
    var flag = hosted == HostedPlatform.X86_32 ? "-m32" : "-m64";
    var compiler = Environment.GetEnvironmentVariable("CC") is { Length: > 0 } cc ? cc : "cc";
    using var process = Process.Start(new ProcessStartInfo(compiler,
      [flag, "-o", output, .. inputs, "-lm"]) { RedirectStandardError = true, UseShellExecute = false })!;
    var errors = process.StandardError.ReadToEnd();
    process.WaitForExit();
    Assert.That(process.ExitCode, Is.Zero, errors);
    return output;
  }

  [TestCaseSource(nameof(_platforms))]
  public void Build_GivenAPlatform_WhenExecutable_ThenANativeElfPrintsWhatTheProgramComputes(
      string platform, HostedPlatform hosted, byte elfClass, ushort machine) {
    var source = Build(platform, hosted);
    var executable = Path.ChangeExtension(source, null);

    AssertElf(executable, elfClass, machine, fileType: 3);
    Assert.That(Execute(executable), Is.EqualTo(Expected));
  }

  [TestCaseSource(nameof(_platforms))]
  public void Build_GivenAPlatform_WhenObject_ThenARelocatableThatLinksAgainstTheRuntime(
      string platform, HostedPlatform hosted, byte elfClass, ushort machine) {
    var source = Build(platform, hosted, "--emit-obj");
    var obj = Path.ChangeExtension(source, ".o");
    AssertElf(obj, elfClass, machine, fileType: 1);

    var runtime = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "runtime", "pbc_rt.c");
    Assume.That(File.Exists(runtime), "the runtime's sources are not beside this checkout");
    var linked = Link(hosted, obj, Path.GetFullPath(runtime), "-I", Path.GetDirectoryName(Path.GetFullPath(runtime))!);
    Assert.That(Execute(linked), Is.EqualTo(Expected));
  }

  [TestCaseSource(nameof(_platforms))]
  public void Build_GivenAPlatform_WhenLibrary_ThenAnArchiveThatLinksOnItsOwn(
      string platform, HostedPlatform hosted, byte elfClass, ushort machine) {
    var source = Build(platform, hosted, "--emit-lib");
    var archive = Path.ChangeExtension(source, ".a");

    Assert.That(File.ReadAllBytes(archive)[..8], Is.EqualTo("!<arch>\n"u8.ToArray()));
    Assert.That(Execute(Link(hosted, archive)), Is.EqualTo(Expected), "the archive carries the program and the runtime both");
  }

  [TestCase("--emit-com")]
  public void Build_GivenAHostedPlatform_WhenADosContainerIsAskedFor_ThenItIsRefused(string option) {
    var source = Path.Combine(_work, "PROG.BAS");
    File.WriteAllText(source, Program);
    var stderr = new StringWriter();

    var code = Driver.Run(["--platform", "x64", option, source], TextWriter.Null, stderr);

    Assert.That(code, Is.Not.Zero);
    Assert.That(stderr.ToString(), Does.Contain("DOS container"));
  }
}
