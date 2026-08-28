using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  /// <summary>The module's normalized $CPU target. Feature tokens are deliberately independent.</summary>
  private RuntimeTarget RuntimeTargetForRuntime() {
    var cpu = model.MetaStatements.FirstOrDefault(m =>
      m.Command.Equals("CPU", StringComparison.OrdinalIgnoreCase));
    if (cpu?.Arguments is not [{ } level, ..])
      return RuntimeTarget.Baseline;
    return RuntimeTarget.For(level.Text, cpu.Arguments.Skip(1).Select(a => a.Text));
  }
}
