using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// pb36 $OPTIMIZE SPEED - internal register parameter passing. For a procedure the
/// compiler fully controls (defined in this self-contained module, never address-taken,
/// every call site visible), the BASIC stack convention is replaced by the Watcom
/// register convention: the leading word-sized arguments travel in AX,DX,BX,CX instead
/// of being pushed and popped. Caller and callee flip together - the same
/// <see cref="ProcedureSymbol"/> drives both the call site (<c>EmitCall</c>) and the
/// prologue (<c>BeginFrame</c> spill), so behaviour is identical and only the per-call
/// stack traffic disappears.
///
/// Disabled wholesale when any procedure address is taken (CODEPTR / CALL DWORD), since
/// an indirect call could reach a converted procedure without knowing to pass in
/// registers. The caller further restricts this to self-contained programs (no
/// separately compiled unit could call a converted procedure with the stack convention).
/// Only word-sized BYVAL scalar parameters qualify - the common case that the register
/// lowering already handles exactly; anything wider keeps the stack convention.
/// </summary>
public static class Pb36RegParm {

  public static void Apply(SemanticModel model) {
    // a taken procedure address means an opaque indirect call may exist - bail entirely
    foreach (var (_, intrinsic) in model.IntrinsicBindings)
      if (intrinsic.Name is "CODEPTR" or "CODESEG" or "CODEPTR32")
        return;

    var procs = new HashSet<ProcedureSymbol>(ReferenceEqualityComparer.Instance);
    foreach (var p in model.Procedures.Values)
      procs.Add(p);
    foreach (var overloads in model.Overloads.Values)
      foreach (var p in overloads)
        procs.Add(p);

    foreach (var proc in procs)
      if (IsEligible(proc))
        proc.CallConv = CallConvention.Watcall;   // AX,DX,BX,CX then stack overflow; reuses the WATCALL lowering
  }

  private static bool IsEligible(ProcedureSymbol proc)
    => !proc.IsExternal                          // we compile the body
    && proc.CallConv == CallConvention.Basic     // never override an explicitly declared convention
    && proc.Captures.Count == 0                  // a capturing closure receives its env pointer in BX:CX
    && proc.Parameters.Count > 0                 // nothing to lift into registers otherwise
    && proc.Parameters.All(p => p.ByVal && p.Type is ScalarType { IsFloat: false, ByteSize: <= 2 });
}
