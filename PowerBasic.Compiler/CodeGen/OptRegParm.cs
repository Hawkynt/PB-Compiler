using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// pb36 $OPTIMIZE SPEED - private calling-convention specialization for procedures whose complete
/// direct-call surface is owned by this compilation. The BASIC stack convention is replaced by the
/// Watcom register convention when doing so can remove call traffic without changing a source-visible
/// ABI: leading one-word arguments travel in AX,DX,BX,CX and overflow remains on the stack.
///
/// <para>
/// O0021 introduced the original word-sized BYVAL case. O0282 supplies the policy around it: the
/// decision is made per procedure, an address escape fences only the escaped target rather than every
/// procedure in the module, and one-word BYREF parameters participate because their ABI value is the
/// near pointer itself. The same <see cref="ProcedureSymbol"/> still drives both <c>EmitCall</c> and
/// the define-side <c>BeginFrame</c> spill, so caller and callee change convention atomically.
/// </para>
///
/// <para>
/// Opaque typed procedure-pointer calls remain a module-wide fence for now. <see cref="SemanticModel"/>
/// records their signatures but not a complete target set, so proving that a candidate cannot be
/// reached indirectly would be speculation. Separately compiled/linkable programs are fenced by the
/// caller of <see cref="Apply"/> for the same reason: outside code may still call the public BASIC ABI.
/// Wider register pairs, floating arguments and routed x86 definitions remain O0282 follow-up work.
/// </para>
/// </summary>
public static class OptRegParm {

  public static void Apply(SemanticModel model, Func<ProcedureSymbol, bool>? skip = null) {
    // A typed procedure-pointer dispatch has no complete target set in the semantic model yet. Until
    // O0279 can prove that set, changing any potential target's ABI would make an indirect call unsafe.
    if (model.ProcPtrCalls.Count > 0 || model.ProcPtrStatementCalls.Count > 0)
      return;

    var (addressTaken, addressReferences) = AddressTakenProcedures(model);

    var procs = new HashSet<ProcedureSymbol>(ReferenceEqualityComparer.Instance);
    foreach (var p in model.Procedures.Values)
      procs.Add(p);
    foreach (var overloads in model.Overloads.Values)
      foreach (var p in overloads)
        procs.Add(p);

    foreach (var proc in procs)
      if (IsEligible(proc)
          && !addressTaken.Contains(proc)
          && HasDirectCallSite(model, proc, addressReferences)
          && skip?.Invoke(proc) != true)
        proc.CallConv = CallConvention.Watcall;   // AX,DX,BX,CX then stack overflow; reuses the WATCALL lowering
  }

  /// <summary>
  /// Finds explicit CODEPTR/CODESEG references to procedures. The binder records the referenced
  /// <see cref="NameExpr"/> itself in <see cref="SemanticModel.CallBindings"/>, so the exact escaped
  /// target is known; a label reference has no call binding and therefore does not fence procedures.
  /// The returned node set also lets <see cref="HasDirectCallSite"/> distinguish those address
  /// references from genuine zero-argument function calls, which are represented by the same node type.
  /// </summary>
  private static (HashSet<ProcedureSymbol> Procedures, HashSet<object> References) AddressTakenProcedures(SemanticModel model) {
    var procedures = new HashSet<ProcedureSymbol>(ReferenceEqualityComparer.Instance);
    var references = new HashSet<object>(ReferenceEqualityComparer.Instance);

    foreach (var (site, intrinsic) in model.IntrinsicBindings) {
      if (intrinsic.Name is not ("CODEPTR" or "CODESEG" or "CODEPTR32"))
        continue;
      if (site is not CallOrIndexExpr { Arguments: [NameExpr reference] })
        continue;
      if (!model.CallBindings.TryGetValue(reference, out var target))
        continue; // CODEPTR(label), not a procedure escape

      procedures.Add(target);
      references.Add(reference);
    }

    return (procedures, references);
  }

  /// <summary>
  /// SPEED pays for a private ABI only when it removes traffic from at least one actual call. A
  /// CODEPTR-family reference is deliberately excluded; every other binding to this procedure is a
  /// direct user call in the current semantic model (including a bare zero-argument FUNCTION name).
  /// </summary>
  private static bool HasDirectCallSite(SemanticModel model, ProcedureSymbol proc, HashSet<object> addressReferences)
    => model.CallBindings.Any(binding
      => ReferenceEquals(binding.Value, proc) && !addressReferences.Contains(binding.Key));

  private static bool IsEligible(ProcedureSymbol proc)
    => !proc.IsExternal                          // we compile the body
    && proc.CallConv == CallConvention.Basic     // never override an explicitly declared convention
    && proc.Captures.Count == 0                  // a capturing closure receives its env pointer in BX:CX
    && proc.Parameters.Count > 0                 // nothing to lift into registers otherwise
    && proc.Parameters.All(IsWordArgument);

  /// <summary>
  /// Shapes already modelled exactly by the direct WATCALL path. A small BYVAL scalar is the O0021
  /// case; every ordinary BYREF is itself a one-word near pointer, irrespective of the pointee size.
  /// SEG-qualified references are excluded because they need a far-pointer pair rather than one slot.
  /// </summary>
  private static bool IsWordArgument(VariableSymbol parameter)
    => parameter.ByVal
      ? parameter.Type is ScalarType { IsFloat: false, ByteSize: <= 2 }
      : !parameter.Seg;
}
