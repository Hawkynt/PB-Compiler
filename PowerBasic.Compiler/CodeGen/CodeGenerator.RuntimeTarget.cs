using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  private RuntimeIsaPolicy? _runtimeIsaPolicy;

  /// <summary>The module's normalized $CPU hardware target. ISA fallback policy is deliberately separate.</summary>
  private RuntimeTarget RuntimeTargetForRuntime() {
    var cpu = model.MetaStatements.FirstOrDefault(m =>
      m.Command.Equals("CPU", StringComparison.OrdinalIgnoreCase));
    if (cpu?.Arguments is not [{ } level, ..])
      return RuntimeTarget.Baseline;
    return RuntimeTarget.For(level.Text, cpu.Arguments.Skip(1).Select(a => a.Text));
  }

  /// <summary>
  /// Parses PB36 target-behaviour directives once. $CPU says what hardware the image targets;
  /// $ISA says what to do when source asks for a particular ISA:
  ///   $ISA DEFAULT EMULATE
  ///   $ISA AVX512 EMULATE
  ///   $ISA VPADDW NATIVE
  ///   $ISA SSE2 ERROR
  /// $FPU/$X87 MODE are aliases for $ISA X87 MODE.
  /// Later rules replace earlier rules of the same key, matching normal metastatement override style.
  /// </summary>
  private RuntimeIsaPolicy RuntimeIsaPolicyForRuntime() {
    if (this._runtimeIsaPolicy is { } cached)
      return cached;

    var result = new RuntimeIsaPolicy();
    foreach (var meta in model.MetaStatements) {
      string? key = null;
      string? modeText = null;
      if (meta.Command.Equals("ISA", StringComparison.OrdinalIgnoreCase)) {
        var args = meta.Arguments.Select(a => a.Text)
          .Where(a => a is not "," and not "=")
          .ToArray();
        if (args.Length >= 2) {
          key = args[0];
          modeText = args[^1];
        } else {
          this.Errors.Add(new(meta.Position, "$ISA expects an ISA/mnemonic and NATIVE, EMULATE, ERROR or AUTO"));
          continue;
        }
      } else if (meta.Command.Equals("FPU", StringComparison.OrdinalIgnoreCase)
                 || meta.Command.Equals("X87", StringComparison.OrdinalIgnoreCase)) {
        if (meta.Arguments.Count >= 1) {
          key = "X87";
          modeText = meta.Arguments[^1].Text;
        } else {
          this.Errors.Add(new(meta.Position, $"${meta.Command} expects NATIVE, EMULATE, ERROR or AUTO"));
          continue;
        }
      } else {
        continue;
      }

      if (!RuntimeIsaPolicy.TryParseMode(modeText, out var mode)) {
        this.Errors.Add(new(meta.Position, $"unknown ISA fallback mode '{modeText}' (expected NATIVE, EMULATE, ERROR or AUTO)"));
        continue;
      }
      result.Set(key, mode);
    }

    return this._runtimeIsaPolicy = result;
  }
}
