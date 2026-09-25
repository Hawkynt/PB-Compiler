using PowerBasic.Compiler.Ir.Analysis;

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
/// <para>
/// The optimizing entry point runs <see cref="StringMove"/> first. O0296 needs the explicit string
/// slots to prove privacy and lifetime end; promotion would deliberately erase exactly that memory
/// graph. Faithful-selection promotion skips the ownership optimization.
/// </para>
/// </summary>
public static class Mem2Reg {

  /// <summary>Runs pre-promotion ownership rewrites, then promotes every promotable alloca.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    _ = StringMove.Run(fn);
    return Promote(fn, preserveWriteOnlySourceVariables: false, IrDominators.Build(fn));
  }

  /// <summary>Retains a BASIC variable that is written but never read, as faithful emission requires.</summary>
  public static int RunForFaithfulSelection(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    return Promote(fn, preserveWriteOnlySourceVariables: true, IrDominators.Build(fn));
  }

  /// <summary>
  /// Analysis-aware optimizing entry. StringMove and promotion rewrite values/memory but never CFG
  /// topology, so the shared dominator tree remains valid throughout and all CFG-only analyses survive.
  /// </summary>
  internal static IrPassResult Run(IrFunction fn, IrAnalysisManager analyses) {
    ArgumentNullException.ThrowIfNull(fn);
    ArgumentNullException.ThrowIfNull(analyses);
    var moved = StringMove.Run(fn);
    var promoted = Promote(fn, preserveWriteOnlySourceVariables: false, analyses.Get(IrAnalyses.Dominators));
    var changes = moved + promoted;
    return changes == 0
      ? IrPassResult.Unchanged
      : IrPassResult.ChangedPreservingSets(changes, IrAnalysisSets.Cfg);
  }

  /// <summary>Analysis-aware faithful-selection entry; deliberately omits StringMove.</summary>
  internal static IrPassResult RunForFaithfulSelection(IrFunction fn, IrAnalysisManager analyses) {
    ArgumentNullException.ThrowIfNull(fn);
    ArgumentNullException.ThrowIfNull(analyses);
    var promoted = Promote(fn, preserveWriteOnlySourceVariables: true, analyses.Get(IrAnalyses.Dominators));
    return promoted == 0
      ? IrPassResult.Unchanged
      : IrPassResult.ChangedPreservingSets(promoted, IrAnalysisSets.Cfg);
  }

  private static int Promote(IrFunction fn, bool preserveWriteOnlySourceVariables, IrDominators? dom) {
    if (fn.Entry is null || dom is null)
      return 0;
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
  internal static bool IsPromotable(IrAlloca a) {
    // Microsoft Binary Format is STORAGE whose bits no target computes on: BASICA/GW keep floats in
    // it, and every read converts to IEEE while every write converts back. Promoting such a cell to
    // an SSA value hands the rest of the compiler a value in a format it has no operations for - the
    // x86-16 back end reaches the conversions through the cell's ADDRESS, and a phi has none. It is
    // refused here rather than at each consumer because the cell is the thing that is foreign, not
    // any particular use of it.
    if (a.Allocated.IsMbf)
      return false;
    // A closure environment cell is initialized by the generated prologue rather than an IR store.
    // Promoting it as an ordinary load-only alloca would replace the incoming environment with zero.
    if (a.EnvRole != ClosureEnvRole.None)
      return false;
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
