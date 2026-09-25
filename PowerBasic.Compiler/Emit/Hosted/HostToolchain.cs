using System.Collections.Concurrent;
using System.Diagnostics;

namespace PowerBasic.Compiler.Emit.Hosted;

/// <summary>The machines a program can be built for through the C back end and a host toolchain.</summary>
public enum HostedPlatform {
  /// <summary>32-bit x86 (i386 System V), built with <c>-m32</c>.</summary>
  X86_32,
  /// <summary>x86-64 (System V), built with <c>-m64</c>.</summary>
  X64,
}

/// <summary>What a hosted build produces.</summary>
public enum HostedArtifact {
  /// <summary>A linked executable: the program, the portable runtime and the C library.</summary>
  Executable,
  /// <summary>The program's own relocatable object, to be linked with the runtime by the caller.</summary>
  Object,
  /// <summary>A static archive of the program's object and the runtime's.</summary>
  Library,
}

/// <summary>
/// Turns the C back end's translation unit into a native artifact with the host's C toolchain. This
/// is the retargeting path the IR was built for: everything up to the C text - lowering, the whole
/// middle end - is the same one the DOS back end runs, and a platform costs only the flags that
/// select it and an implementation of the <c>rt_*</c> ABI, which is <c>runtime/pbc_rt.c</c>.
///
/// <para>
/// The compiler is found as <c>$CC</c>, else the first of <c>cc</c>, <c>gcc</c>, <c>clang</c> on the
/// PATH; the archiver as <c>$AR</c>, else <c>ar</c>. The runtime's sources are embedded in this
/// assembly and written next to the translation unit, so a build needs no checkout.
/// </para>
/// </summary>
public static class HostToolchain {

  /// <summary>Builds <paramref name="cSource"/> into <paramref name="artifact"/> at <paramref name="outputPath"/>.</summary>
  public static bool TryBuild(string cSource, HostedPlatform platform, HostedArtifact artifact, string outputPath,
      out string? error) {
    ArgumentNullException.ThrowIfNull(cSource);
    ArgumentNullException.ThrowIfNull(outputPath);
    if (FindCompiler() is not { } compiler) {
      error = "no C compiler found (set CC, or install cc, gcc or clang)";
      return false;
    }
    if (!CanTarget(compiler, platform)) {
      error = $"the C compiler '{compiler}' cannot build for {Describe(platform)}: its C library for that machine is not installed"
        + (platform == HostedPlatform.X86_32 ? " (a multilib toolchain: gcc-multilib, lib32-glibc or glibc-devel.i686)" : "");
      return false;
    }

    var work = Directory.CreateTempSubdirectory("pbc-hosted-");
    try {
      var program = Path.Combine(work.FullName, "program.c");
      File.WriteAllText(program, cSource);
      var runtime = ExtractRuntime(work.FullName);
      var common = $"-std=c99 -O2 {MachineFlag(platform)} -I \"{work.FullName}\"";
      var output = Path.GetFullPath(outputPath);

      switch (artifact) {
        case HostedArtifact.Executable:
          return Run(compiler, $"{common} -o \"{output}\" \"{program}\" \"{runtime}\" -lm", out error);
        case HostedArtifact.Object:
          return Run(compiler, $"{common} -c -o \"{output}\" \"{program}\"", out error);
        case HostedArtifact.Library: {
          var programObject = Path.Combine(work.FullName, "program.o");
          var runtimeObject = Path.Combine(work.FullName, "pbc_rt.o");
          if (!Run(compiler, $"{common} -c -o \"{programObject}\" \"{program}\"", out error)
              || !Run(compiler, $"{common} -c -o \"{runtimeObject}\" \"{runtime}\"", out error))
            return false;
          File.Delete(output);
          return Run(Environment.GetEnvironmentVariable("AR") is { Length: > 0 } ar ? ar : "ar",
            $"rcs \"{output}\" \"{programObject}\" \"{runtimeObject}\"", out error);
        }
        default:
          throw new ArgumentOutOfRangeException(nameof(artifact), artifact, null);
      }
    } finally {
      try {
        work.Delete(recursive: true);
      } catch (IOException) {
        // a temp directory that outlives a build is litter, not a failure of it
      }
    }
  }

  /// <summary>
  /// Whether this host can build and link a C program for <paramref name="platform"/>. A 64-bit
  /// host usually has a compiler that accepts <c>-m32</c> but no 32-bit C library behind it, and
  /// the compiler's own complaint about a missing <c>gnu/stubs-32.h</c> tells a BASIC programmer
  /// nothing - so the question is asked once, of a one-line program, before any real build.
  /// </summary>
  public static bool Supports(HostedPlatform platform) => FindCompiler() is { } compiler && CanTarget(compiler, platform);

  /// <summary>The name <c>--platform</c> spells <paramref name="platform"/> with.</summary>
  public static string Describe(HostedPlatform platform) => platform switch {
    HostedPlatform.X86_32 => "x86-32",
    HostedPlatform.X64 => "x64",
    _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
  };

  private static readonly ConcurrentDictionary<(string Compiler, HostedPlatform Platform), bool> _canTarget = new();

  private static bool CanTarget(string compiler, HostedPlatform platform) => _canTarget.GetOrAdd((compiler, platform), key => {
    var work = Directory.CreateTempSubdirectory("pbc-probe-");
    try {
      var probe = Path.Combine(work.FullName, "probe.c");
      File.WriteAllText(probe, "#include <stdint.h>\n#include <stdio.h>\n#include <math.h>\nint main(void) { return (int)sqrt((double)sizeof(int32_t)) - 2; }\n");
      return Run(key.Compiler, $"{MachineFlag(key.Platform)} -o \"{Path.Combine(work.FullName, "probe")}\" \"{probe}\" -lm", out _);
    } finally {
      try {
        work.Delete(recursive: true);
      } catch (IOException) {
        // as for a build: litter, not a failure
      }
    }
  });

  private static string MachineFlag(HostedPlatform platform) => platform switch {
    HostedPlatform.X86_32 => "-m32",
    HostedPlatform.X64 => "-m64",
    _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
  };

  /// <summary>Writes the embedded runtime into <paramref name="directory"/>; the path of its implementation.</summary>
  private static string ExtractRuntime(string directory) {
    var assembly = typeof(HostToolchain).Assembly;
    foreach (var name in (string[])["pbc_rt.c", "pbc_rt.h"]) {
      using var resource = assembly.GetManifestResourceStream("runtime/" + name)
        ?? throw new InvalidOperationException($"the compiler was built without its runtime resource '{name}'");
      using var file = File.Create(Path.Combine(directory, name));
      resource.CopyTo(file);
    }
    return Path.Combine(directory, "pbc_rt.c");
  }

  private static string? FindCompiler() {
    if (Environment.GetEnvironmentVariable("CC") is { Length: > 0 } configured)
      return configured;
    var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
    foreach (var name in (string[])["cc", "gcc", "clang"])
      foreach (var directory in path)
        foreach (var candidate in (string[])[Path.Combine(directory, name), Path.Combine(directory, name + ".exe")])
          if (File.Exists(candidate))
            return candidate;
    return null;
  }

  private static bool Run(string file, string arguments, out string? error) {
    var start = new ProcessStartInfo(file, arguments) {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
    };
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var diagnostics = process.StandardError.ReadToEnd();
    process.WaitForExit();
    _ = output.Result;
    error = process.ExitCode == 0 ? null : $"{Path.GetFileName(file)} failed ({process.ExitCode}): {diagnostics.Trim()}";
    return error is null;
  }
}
