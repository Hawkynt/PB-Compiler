using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  private RuntimeIsaPolicy? _runtimeIsaPolicy;

  /// <summary>
  /// The module's normalized hardware requirement. $CPU may be a generation floor, a feature-only
  /// requirement ($CPU SSE2), or a floor plus features. Historic $FLOAT NPX also means the produced
  /// program requires an x87/NPX even when the integer CPU floor is only 8086/286/386.
  /// </summary>
  private RuntimeTarget RuntimeTargetForRuntime() {
    var cpu = model.MetaStatements.FirstOrDefault(m =>
      m.Command.Equals("CPU", StringComparison.OrdinalIgnoreCase));
    var target = cpu?.Arguments is [{ } first, ..]
      ? RuntimeTarget.For(first.Text, cpu.Arguments.Skip(1).Select(a => a.Text))
      : RuntimeTarget.Baseline;

    var floatMode = model.MetaStatements.LastOrDefault(m =>
      m.Command.Equals("FLOAT", StringComparison.OrdinalIgnoreCase));
    var floatToken = floatMode?.Arguments.Select(a => RuntimeIsaPolicy.NormalizeKey(a.Text))
      .FirstOrDefault(a => a is "NPX" or "EMULATE" or "PROCEDURE");
    if (floatToken == "NPX")
      target = target with { Features = target.Features | RuntimeCpuFeatures.X87 };

    return target;
  }

  /// <summary>
  /// Parses target-behaviour directives once. $CPU describes native machine capabilities; $ISA says
  /// what to do only when a requested instruction is not native:
  ///   $ISA DEFAULT EMULATE
  ///   $ISA AVX512 EMULATE
  ///   $ISA VPADDW NATIVE
  ///   $ISA SSE2 ERROR
  ///
  /// Native support always wins over ERROR. $FPU/$X87 MODE are aliases for $ISA X87 MODE.
  /// Historic PB/DOS spellings are kept too:
  ///   $FLOAT NPX       -> force native x87 (and declare x87 as required hardware)
  ///   $FLOAT PROCEDURE -> force software floating-point lowering
  ///   $FLOAT EMULATE   -> hybrid: native x87 when available, software otherwise
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
      } else if (meta.Command.Equals("FLOAT", StringComparison.OrdinalIgnoreCase)) {
        var token = meta.Arguments.Select(a => RuntimeIsaPolicy.NormalizeKey(a.Text))
          .FirstOrDefault(a => a is "NPX" or "EMULATE" or "PROCEDURE");
        if (token is null)
          continue; // other historical $FLOAT arguments stay owned by the ordinary PB front end
        key = "X87";
        modeText = token switch {
          "NPX" => "NATIVE",
          "PROCEDURE" => "EMULATE",
          _ => "AUTO", // PB's EMULATE library is hybrid: probe/use i87, software otherwise.
        };
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
