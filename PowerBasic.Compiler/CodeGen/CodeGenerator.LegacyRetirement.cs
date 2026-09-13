namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  /// <summary>
  /// Retires the legacy direct-emitter route for production callers. The historic routing properties remain
  /// temporarily source-compatible because differential/oracle fixtures still use them explicitly, but a normal
  /// <see cref="CodeGenerator"/> instance always starts with the IR/native backend enabled and mandatory.
  /// </summary>
  static CodeGenerator() {
    Environment.SetEnvironmentVariable("PBC_X_BACKEND", "1");
    Environment.SetEnvironmentVariable("PBC_X_BACKEND_STRICT", "1");
  }
}
