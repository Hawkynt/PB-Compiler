using System.Diagnostics;
using PowerBasic.Compiler.Cli;

namespace PowerBasic.Compiler.Tests.Cli;

/// <summary>
/// <c>pbc --platform x86-32|x64</c>: native Linux ELF executables, objects and archives, emitted by
/// pbc's own x86 back end, runtime and ELF writer. Each case runs what it built, so a pass is a
/// program that printed the right thing. The executables need nothing but a Linux kernel - an i386
/// one runs on an x64 kernel without any 32-bit library. The object and the archive are linked by
/// the host's C compiler into a C <c>main</c> that calls <c>pb_main</c>: the C compiler is the
/// oracle that they are well-formed objects, never part of the build. Where the host has no C
/// library for the machine, the bare linker links them alone, entered at their <c>pb_start</c>.
/// </summary>
[TestFixture]
public sealed class PlatformTests {

  private const string Program = """
    DECLARE FUNCTION Tri%(BYVAL n%)
    PRINT "native"; Tri%(INP(&H60) + 10)
    x! = (INP(&H60) + 1) / 3
    PRINT x!; CLNG(x! * 3000)
    END
    FUNCTION Tri%(BYVAL n%)
      FOR i% = 1 TO n%
        s% = s% + i%
      NEXT
      Tri% = s%
    END FUNCTION
    """;

  // INP has no port to read on Linux; the runtime answers 0, so Tri% sums 1..10
  private const string Expected = "native 55 \n .3333333  1000 ";

  private static readonly object[] _platforms = [
    new object[] { "x86-32", "-m32", (byte)1, (ushort)3 },
    new object[] { "x64", "-m64", (byte)2, (ushort)62 },
  ];

  private string _work = null!;

  [SetUp]
  public void CreateWorkDirectory() => _work = Directory.CreateTempSubdirectory("pbc-platform-").FullName;

  [TearDown]
  public void DeleteWorkDirectory() => Directory.Delete(_work, recursive: true);

  private string Build(string platform, params string[] extra) {
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
    Assume.That(OperatingSystem.IsLinux(), "the executables are Linux programs");
    using var process = Process.Start(new ProcessStartInfo(path) { RedirectStandardOutput = true, UseShellExecute = false })!;
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    Assert.That(process.ExitCode, Is.Zero, output);
    return output.TrimEnd('\n');
  }

  /// <summary>Links <paramref name="inputs"/> into a C program whose <c>main</c> calls <c>pb_main</c>.</summary>
  private string LinkWithC(string machineFlag, params string[] inputs) {
    Assume.That(OperatingSystem.IsLinux(), "linking ELF objects requires a Linux host toolchain");

    var compiler = Environment.GetEnvironmentVariable("CC") is { Length: > 0 } cc ? cc : "cc";
    var main = Path.Combine(_work, "main.c");
    File.WriteAllText(main, "void pb_main(void);\nint main(void) { pb_main(); return 0; }\n");
    var output = Path.Combine(_work, "linked");
    ProcessStartInfo start = new(compiler, [machineFlag, "-o", output, main, .. inputs]) {
      RedirectStandardError = true,
      UseShellExecute = false,
    };
    Process? process;
    try {
      process = Process.Start(start);
    } catch (System.ComponentModel.Win32Exception) {
      process = null;
    }
    Assume.That(process, Is.Not.Null, "no C compiler to link the object with");
    var errors = process!.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode == 0)
      return output;
    // no C library for this machine: the bare linker enters the object's own pb_start instead
    return this.LinkWithLd(machineFlag, errors, inputs);
  }

  private string LinkWithLd(string machineFlag, string whyNotC, string[] inputs) {
    var output = Path.Combine(_work, "linked");
    ProcessStartInfo start = new("ld", ["-m", machineFlag == "-m32" ? "elf_i386" : "elf_x86_64",
      "-e", "pb_start", "-o", output, .. inputs]) { RedirectStandardError = true, UseShellExecute = false };
    Process? process;
    try {
      process = Process.Start(start);
    } catch (System.ComponentModel.Win32Exception) {
      process = null;
    }
    Assume.That(process, Is.Not.Null, $"neither the C compiler ({whyNotC}) nor ld can link {machineFlag}");
    var errors = process!.StandardError.ReadToEnd();
    process.WaitForExit();
    Assert.That(process.ExitCode, Is.Zero, $"ld rejected the object: {errors}");
    return output;
  }

  [TestCaseSource(nameof(_platforms))]
  public void Build_GivenAPlatform_WhenExecutable_ThenAStaticElfPrintsWhatTheProgramComputes(
      string platform, string machineFlag, byte elfClass, ushort machine) {
    var executable = Path.ChangeExtension(this.Build(platform), null);

    AssertElf(executable, elfClass, machine, fileType: 2);
    Assert.That(Execute(executable), Is.EqualTo(Expected));
  }

  [TestCaseSource(nameof(_platforms))]
  public void Build_GivenAPlatform_WhenObject_ThenARelocatableExportingPbMainThatACProgramCalls(
      string platform, string machineFlag, byte elfClass, ushort machine) {
    var obj = Path.ChangeExtension(this.Build(platform, "--emit-obj"), ".o");
    AssertElf(obj, elfClass, machine, fileType: 1);

    Assert.That(Execute(this.LinkWithC(machineFlag, obj)), Is.EqualTo(Expected));
  }

  [TestCaseSource(nameof(_platforms))]
  public void Build_GivenAPlatform_WhenLibrary_ThenAnIndexedArchiveTheLinkerSearches(
      string platform, string machineFlag, byte elfClass, ushort machine) {
    var archive = Path.ChangeExtension(this.Build(platform, "--emit-lib"), ".a");

    Assert.That(File.ReadAllBytes(archive)[..8], Is.EqualTo("!<arch>\n"u8.ToArray()));
    Assert.That(Execute(this.LinkWithC(machineFlag, archive)), Is.EqualTo(Expected),
      "pb_main is found through the archive's symbol index");
  }

  [TestCase("--emit-com")]
  public void Build_GivenALinuxPlatform_WhenADosContainerIsAskedFor_ThenItIsRefused(string option) {
    var source = Path.Combine(_work, "PROG.BAS");
    File.WriteAllText(source, Program);
    var stderr = new StringWriter();

    var code = Driver.Run(["--platform", "x64", option, source], TextWriter.Null, stderr);

    Assert.That(code, Is.Not.Zero);
    Assert.That(stderr.ToString(), Does.Contain("DOS container"));
  }
}
