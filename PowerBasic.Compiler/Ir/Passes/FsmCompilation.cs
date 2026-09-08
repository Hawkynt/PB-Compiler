namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0336 — compiles a dense byte-valued dispatch into a 256-entry class table followed by a compact
/// switch over class ids. <see cref="SwitchFormation"/> recovers the source branch chain first; this
/// pass handles the representation step the original O0336 design calls for.
///
/// <para>
/// The transform is deliberately limited to the complete eight-bit domain. Every possible input
/// pattern is evaluated through <see cref="IrSwitch.TargetFor"/>, so the generated table preserves
/// first-case-wins semantics and the default path exactly, including signed spellings such as -1 for
/// byte pattern 255. No external implementation code is used; the table is derived from the IR's own
/// public dispatch semantics.
/// </para>
///
/// <para>
/// A 256-byte table is not free, so this pass only runs for the SPEED objective. Even there it is
/// emitted only when at least sixteen explicitly named values collapse to at most sixteen destination
/// classes and the table removes at least half of the switch cases. Sparse classifiers therefore stay
/// in the target selector, where compares, masks, hashes or compact jump tables are cheaper.
/// </para>
/// </summary>
public static class FsmCompilation {

  private const int _DOMAIN_SIZE = 256;
  private const int _MIN_NAMED_VALUES = 16;
  private const int _MAX_CLASSES = 16;

  /// <summary>Compiles profitable byte classifiers in <paramref name="fn"/>; returns the number compiled.</summary>
  public static int Run(IrModule module, IrFunction fn) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(fn);
    if (!module.OptimizeForSpeed)
      return 0;

    var compiled = 0;
    foreach (var dispatch in fn.AllInstructions.OfType<IrSwitch>().ToList())
      if (TryCompile(module, fn, dispatch))
        ++compiled;
    return compiled;
  }

  private static bool TryCompile(IrModule module, IrFunction fn, IrSwitch dispatch) {
    if (dispatch.Parent is not { } block
        || !dispatch.Condition.Type.IsInteger || dispatch.Condition.Type.Bits != 8
        || IsGeneratedClassifier(dispatch))
      return false;

    var namedValues = dispatch.Cases.Select(item => dispatch.PatternOf(item.Value)).Distinct().Count();
    if (namedValues < _MIN_NAMED_VALUES)
      return false;

    var classByTarget = new Dictionary<IrBasicBlock, byte>(ReferenceEqualityComparer.Instance);
    var targets = new List<IrBasicBlock>();
    var bytes = new byte[_DOMAIN_SIZE];
    for (var input = 0; input < _DOMAIN_SIZE; ++input) {
      var target = dispatch.TargetFor(input);
      if (!classByTarget.TryGetValue(target, out var id)) {
        if (targets.Count >= _DOMAIN_SIZE)
          return false;
        id = (byte)targets.Count;
        targets.Add(target);
        classByTarget.Add(target, id);
      }
      bytes[input] = id;
    }

    if (targets.Count is < 2 or > _MAX_CLASSES || namedValues < targets.Count * 2)
      return false;

    var table = module.AddGlobal(new IrGlobalVariable(FreshTableName(module, fn.Name), IrType.U8) {
      Bytes = bytes,
      Count = _DOMAIN_SIZE,
      IsZeroInitialized = false,
    });

    var index = block.InsertBefore(new IrCast(IrCastOp.ZExt, dispatch.Condition, IrType.I16), dispatch);
    var address = block.InsertBefore(new IrGep(table, index, IrType.U8), dispatch);
    var classification = block.InsertBefore(new IrLoad(IrType.U8, address), dispatch);
    var compact = new IrSwitch(classification, targets[0]);
    for (var id = 1; id < targets.Count; ++id)
      compact.AddCase(id, targets[id]);

    dispatch.EraseFromParent();
    block.Append(compact);
    return true;
  }

  private static bool IsGeneratedClassifier(IrSwitch dispatch)
    => dispatch.Condition is IrLoad {
      Pointer: IrGep { BasePtr: IrGlobalVariable global }
    } && global.Name.StartsWith(".fsm.", StringComparison.Ordinal);

  private static string FreshTableName(IrModule module, string functionName) {
    var ordinal = 0;
    string name;
    do
      name = $".fsm.{functionName}.{ordinal++}";
    while (module.FindGlobal(name) is not null);
    return name;
  }
}
