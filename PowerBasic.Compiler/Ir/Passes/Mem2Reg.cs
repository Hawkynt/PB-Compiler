namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Promotes stack slots to SSA registers: an alloca whose only uses are direct,
/// storage-compatible loads and stores is replaced by values flowing through phi nodes placed at the
/// iterated dominance frontier of its stores (the classic Cytron construction). This turns the
/// lowering's trivially-correct alloca/load/store form into real SSA, which is what every downstream
/// value-based pass (SCCP, GVN, instcombine) needs.
///
/// PB zero-initializes variables, so a slot with no reaching store reads as the
/// zero constant of its type — never undef.
///
/// <para>
/// Direct pointer use alone is not sufficient under opaque pointers. Packed UDT backing is an
/// <c>alloca i8, N</c>, and its offset-zero field may be loaded/stored as <c>i16</c> or <c>i32</c>.
/// UNION fields make this especially easy to hit because all views start at offset zero. Promoting
/// such a byte backing slot as though it were an i8 scalar changes storage semantics, so every access
/// must have the allocation's storage shape before this pass owns it.
/// </para>
/// </summary>
public static class Mem2Reg {

  /// <summary>Promotes every promotable alloca in the function; returns how many were promoted.</summary>
  public static int Run(IrFunction fn) => Run(fn, preserveWriteOnlySourceVariables: false);

  /// <summary>Retains a BASIC variable that is written but never read, as faithful emission requires.</summary>
  public static int RunForFaithfulSelection(IrFunction fn)
    => Run(fn, preserveWriteOnlySourceVariables: true);

  private static int Run(IrFunction fn, bool preserveWriteOnlySourceVariables) {
    if (fn.Entry is null)
      return 0;
    var dom = IrDominators.Build(fn)!;
    var allocas = CollectPromotable(fn, preserveWriteOnlySourceVariables);
    if (allocas.Count == 0)
      return 0;

    var phis = PlacePhis(fn, dom, allocas);
    var deadMemoryOps = new List<IrInstruction>();
    Rename(fn.Entry, dom, BuildDomTreeChildren(fn, dom), allocas, phis, SeedZeros(allocas), deadMemoryOps);

    foreach (var op in deadMemoryOps)
      op.EraseFromParent();
    foreach (var alloca in allocas)
      if (alloca.HasNoUsers)
        alloca.EraseFromParent();

    return allocas.Count;
  }

  private static List<IrAlloca> CollectPromotable(IrFunction fn, bool preserveWriteOnlySourceVariables) {
    var result = new List<IrAlloca>();
    foreach (var inst in fn.AllInstructions.ToList()) {
      if (inst is not IrAlloca a || !IsPromotable(a))
        continue;
      if (preserveWriteOnlySourceVariables && a.IsSourceVariable && !a.Users.OfType<IrLoad>().Any())
        continue;
      result.Add(a);
    }
    return result;
  }

  /// <summary>
  /// An alloca is promotable when every use is a direct load/store through it and every access has
  /// the slot's storage type. Signed/unsigned integer views are storage-compatible; wider/narrower
  /// aggregate field views are not.
  /// </summary>
  private static bool IsPromotable(IrAlloca a) {
    foreach (var user in a.Users)
      switch (user) {
        case IrLoad load when ReferenceEquals(load.Pointer, a) && load.Type.SameStorage(a.Allocated):
          break;
        case IrStore store when ReferenceEquals(store.Pointer, a)
          && !ReferenceEquals(store.Value, a)
          && store.Value.Type.SameStorage(a.Allocated):
          break;
        default:
          return false;                              // gep, escape, incompatible view, or address stored elsewhere
      }
    return true;
  }

  private static Dictionary<IrBasicBlock, Dictionary<IrAlloca, IrPhi>> PlacePhis(
      IrFunction fn, IrDominators dom, List<IrAlloca> allocas) {
    var phis = new Dictionary<IrBasicBlock, Dictionary<IrAlloca, IrPhi>>(ReferenceEqualityComparer.Instance);

    foreach (var alloca in allocas) {
      var defBlocks = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
      foreach (var user in alloca.Users)
        if (user is IrStore store && store.Parent is { } b && dom.IsReachable(b))
          defBlocks.Add(b);

      foreach (var block in IteratedFrontier(dom, defBlocks)) {
        if (!phis.TryGetValue(block, out var perBlock))
          phis[block] = perBlock = new Dictionary<IrAlloca, IrPhi>(ReferenceEqualityComparer.Instance);
        if (!perBlock.ContainsKey(alloca)) {
          var phi = new IrPhi(alloca.Allocated) { Name = alloca.Name };
          block.AppendPhi(phi);
          perBlock[alloca] = phi;
        }
      }
    }
    return phis;
  }

  private static HashSet<IrBasicBlock> IteratedFrontier(IrDominators dom, HashSet<IrBasicBlock> defBlocks) {
    var idf = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
    var worklist = new Queue<IrBasicBlock>(defBlocks);
    while (worklist.Count > 0) {
      var x = worklist.Dequeue();
      foreach (var y in dom.FrontierOf(x))
        if (idf.Add(y))
          worklist.Enqueue(y);
    }
    return idf;
  }

  private static Dictionary<IrAlloca, IrValue> SeedZeros(List<IrAlloca> allocas) {
    var seed = new Dictionary<IrAlloca, IrValue>(ReferenceEqualityComparer.Instance);
    foreach (var a in allocas)
      seed[a] = a.Allocated.IsFloat ? new IrConstantFloat(a.Allocated, 0.0)
        : a.Allocated.IsPointer ? new IrNullPtr(a.Allocated)  // an uninitialized string handle reads as null (empty); the space it points into survives
        : new IrConstantInt(a.Allocated, 0);
    return seed;
  }

  private static Dictionary<IrBasicBlock, List<IrBasicBlock>> BuildDomTreeChildren(IrFunction fn, IrDominators dom) {
    var children = new Dictionary<IrBasicBlock, List<IrBasicBlock>>(ReferenceEqualityComparer.Instance);
    foreach (var block in dom.ReversePostorder) {
      var idom = dom.ImmediateDominatorOf(block);
      if (idom is null || ReferenceEquals(idom, block))
        continue;
      if (!children.TryGetValue(idom, out var list))
        children[idom] = list = [];
      list.Add(block);
    }
    return children;
  }

  private static void Rename(
      IrBasicBlock block,
      IrDominators dom,
      Dictionary<IrBasicBlock, List<IrBasicBlock>> children,
      List<IrAlloca> allocas,
      Dictionary<IrBasicBlock, Dictionary<IrAlloca, IrPhi>> phis,
      Dictionary<IrAlloca, IrValue> incoming,
      List<IrInstruction> deadMemoryOps) {

    // a private copy so siblings in the dom tree do not see each other's stores
    var reaching = new Dictionary<IrAlloca, IrValue>(incoming, ReferenceEqualityComparer.Instance);

    // phis placed in this block become the reaching definition on entry
    if (phis.TryGetValue(block, out var blockPhis))
      foreach (var (alloca, phi) in blockPhis)
        reaching[alloca] = phi;

    foreach (var inst in block.Instructions.ToList()) {
      switch (inst) {
        case IrLoad load when load.Pointer is IrAlloca a && reaching.ContainsKey(a):
          load.ReplaceAllUsesWith(reaching[a]);
          deadMemoryOps.Add(load);
          break;
        case IrStore store when store.Pointer is IrAlloca a && reaching.ContainsKey(a):
          reaching[a] = store.Value;
          deadMemoryOps.Add(store);
          break;
      }
    }

    // hand the reaching definitions to each successor's phis
    foreach (var succ in block.Successors)
      if (phis.TryGetValue(succ, out var succPhis))
        foreach (var (alloca, phi) in succPhis)
          phi.AddIncoming(reaching[alloca], block);

    if (children.TryGetValue(block, out var kids))
      foreach (var child in kids)
        Rename(child, dom, children, allocas, phis, reaching, deadMemoryOps);
  }
}
