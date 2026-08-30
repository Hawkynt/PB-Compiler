using System.Reflection;
using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class InlineAsmIsaCoverageTests {
  private sealed record AdvertisedInstruction(string Mnemonic, string Feature, string NativeTarget, string Operands);

  [Test]
  public void AdvertisedExtendedIntegerInstructions_GivenIsaPolicies_ThenEveryInstructionHasAnExplicitResolution() {
    var advertised = DiscoverAdvertisedInstructions();
    Assert.That(advertised, Is.Not.Empty, "extended SIMD encoder discovery returned no instructions");

    var failures = new List<string>();
    foreach (var instruction in advertised) {
      AssertCompiles(instruction, "8086", "EMULATE", failures);
      AssertCompiles(instruction, instruction.NativeTarget, "AUTO", failures);
      AssertCompiles(instruction, instruction.NativeTarget, "NATIVE", failures);
      AssertErrorsWhenEmulationForbidden(instruction, failures);
    }

    Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
  }

  private static IReadOnlyList<AdvertisedInstruction> DiscoverAdvertisedInstructions() {
    var classifiers = new[] {
      (Method: Classifier("IsSsse3"), Feature: "SSSE3", Target: "SSSE3"),
      (Method: Classifier("IsSse41"), Feature: "SSE4.1", Target: "SSE4.1"),
      (Method: Classifier("IsSse42"), Feature: "SSE4.2", Target: "SSE4.2"),
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
      result.Add(new(mnemonic, classifier.Feature, classifier.Target, operands));
    }

    return result.OrderBy(item => item.Feature).ThenBy(item => item.Mnemonic).ToArray();
  }

  private static MethodInfo Classifier(string name) =>
    typeof(CodeGenerator).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException($"missing ISA classifier {name}");

  private static void AssertCompiles(AdvertisedInstruction instruction, string target, string policy, List<string> failures) {
    var generator = Compile($"$CPU {target}\n$ISA {instruction.Feature} {policy}\n! {instruction.Mnemonic} {instruction.Operands}\nEND\n");
    if (generator.Errors.Count == 0)
      return;
    failures.Add($"{instruction.Mnemonic} / {target} / {policy}: {string.Join("; ", generator.Errors)}");
  }

  private static void AssertErrorsWhenEmulationForbidden(AdvertisedInstruction instruction, List<string> failures) {
    var generator = Compile($"$CPU 8086\n$ISA {instruction.Feature} ERROR\n! {instruction.Mnemonic} {instruction.Operands}\nEND\n");
    if (generator.Errors.Any(error => error.Message.Contains("forbids emulation", StringComparison.OrdinalIgnoreCase)))
      return;
    failures.Add($"{instruction.Mnemonic} / 8086 / ERROR did not resolve to the explicit ISA-policy error path");
  }

  private static CodeGenerator Compile(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "isa-coverage.bas", Dialect.Pb36), "isa-coverage.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    if (model.Errors.Count != 0)
      throw new AssertionException("bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = false };
    _ = generator.EmitExecutable();
    return generator;
  }
}
