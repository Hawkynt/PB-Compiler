using System.Reflection;
using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Runtime;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class InlineAsmIsaCoverageTests {
  private sealed record AdvertisedInstruction(
    string Mnemonic,
    string Feature,
    RuntimeCpuFeatures Required,
    string Operands
  );

  private static readonly string[] _targets = [
    "8086", "386", "P6", "SSE2", "SSSE3", "SSE4.1", "SSE4.2", "AVX", "AVX2", "AVX512",
  ];

  private static readonly string[] _policies = ["AUTO", "NATIVE", "EMULATE", "ERROR"];

  [Test]
  public void AdvertisedExtendedIntegerInstructions_GivenTargetPolicyMatrix_ThenEveryInstructionHasAnExplicitResolution() {
    var advertised = DiscoverAdvertisedInstructions();
    Assert.That(advertised, Is.Not.Empty, "extended SIMD encoder discovery returned no instructions");

    var failures = new List<string>();
    foreach (var instruction in advertised)
      foreach (var targetName in _targets)
        foreach (var policy in _policies)
          AssertResolution(instruction, targetName, policy, failures);

    Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
  }

  private static IReadOnlyList<AdvertisedInstruction> DiscoverAdvertisedInstructions() {
    var classifiers = new[] {
      (Method: Classifier("IsSsse3"), Feature: "SSSE3", Required: RuntimeCpuFeatures.Ssse3),
      (Method: Classifier("IsSse41"), Feature: "SSE4.1", Required: RuntimeCpuFeatures.Sse41),
      (Method: Classifier("IsSse42"), Feature: "SSE4.2", Required: RuntimeCpuFeatures.Sse42),
    };

    var methods = typeof(Assembler).GetMethods(BindingFlags.Public | BindingFlags.Instance)
      .Where(method => method.DeclaringType == typeof(Assembler))
      .GroupBy(method => method.Name, StringComparer.OrdinalIgnoreCase)
      .Select(group => group.ToArray());

    var result = new List<AdvertisedInstruction>();
    foreach (var overloads in methods) {
      var mnemonic = overloads[0].Name.ToUpperInvariant();
      var classifier = classifiers.FirstOrDefault(item => (bool)item.Method.Invoke(null, [mnemonic])!);
      if (classifier.Method is null)
        continue;

      var hasImmediate = overloads.Any(method => method.GetParameters().LastOrDefault()?.ParameterType == typeof(byte));
      var operands = mnemonic == "CRC32"
        ? "EAX, AL"
        : hasImmediate ? "XMM0, XMM1, 0" : "XMM0, XMM1";
      result.Add(new(mnemonic, classifier.Feature, classifier.Required, operands));
    }

    return result.OrderBy(item => item.Feature).ThenBy(item => item.Mnemonic).ToArray();
  }

  private static MethodInfo Classifier(string name) =>
    typeof(CodeGenerator).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException($"missing ISA classifier {name}");

  private static void AssertResolution(AdvertisedInstruction instruction, string targetName, string policy,
      List<string> failures) {
    var target = RuntimeTarget.For(targetName);
    var nativelySupported = target.Has(instruction.Required);
    var generator = Compile($"$CPU {targetName}\n$ISA {instruction.Feature} {policy}\n! {instruction.Mnemonic} {instruction.Operands}\nEND\n");

    if (policy == "ERROR" && !nativelySupported) {
      if (generator.Errors.Any(error => error.Message.Contains("forbids emulation", StringComparison.OrdinalIgnoreCase)))
        return;
      failures.Add($"{instruction.Mnemonic} / {targetName} / ERROR: expected explicit forbidden-emulation diagnostic, got {Describe(generator)}");
      return;
    }

    if (generator.Errors.Count == 0)
      return;
    failures.Add($"{instruction.Mnemonic} / {targetName} / {policy}: {Describe(generator)}");
  }

  private static string Describe(CodeGenerator generator) => generator.Errors.Count == 0
    ? "no diagnostic"
    : string.Join("; ", generator.Errors);

  private static CodeGenerator Compile(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "isa-coverage.bas", Dialect.Pb36), "isa-coverage.bas", Dialect.Pb36);
    var model = PowerBasic.Compiler.Semantics.Binder.Bind(unit, Dialect.Pb36);
    if (model.Errors.Count != 0)
      throw new AssertionException("bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = false };
    _ = generator.EmitExecutable();
    return generator;
  }
}
