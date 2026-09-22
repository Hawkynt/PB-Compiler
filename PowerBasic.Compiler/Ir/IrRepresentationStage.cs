namespace PowerBasic.Compiler.Ir;

/// <summary>
/// The semantic contract currently carried by an <see cref="IrModule"/>. The repository deliberately
/// does not manufacture separate object models for every conceptual layer, but consumers can still
/// state which boundary they require while the lowering split proceeds incrementally.
/// </summary>
public enum IrRepresentationStage {
  Lowered,
  OptimizedSsa,
  LowIr,
  MachineSsa,
  MachineIr,
}
