namespace PowerBasic.Compiler.Ir;

/// <summary>Checks the pre-SSA middle-end contract: a MIR-stage module with valid explicit CFG and operations.</summary>
public static class MirVerifier {

  public static IReadOnlyList<string> Verify(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    if (module.RepresentationStage != IrRepresentationStage.Mir)
      return [$"MIR verification requires Mir stage, got {module.RepresentationStage}"];
    return IrVerifier.Verify(module);
  }
}
