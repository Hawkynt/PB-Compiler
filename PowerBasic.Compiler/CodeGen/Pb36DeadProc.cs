using PowerBasic.Compiler.Semantics;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// pb36 O22 - dead procedure elimination. A SUB/FUNCTION that nothing references is
/// unreachable machine code: omitting it shrinks the image with no observable effect.
/// The set of <em>live</em> procedures is every one the binder recorded as a call target
/// (<see cref="SemanticModel.CallBindings"/> - which also holds CODEPTR/CALL DWORD address
/// references) plus every lifted lambda (invoked through a procedure pointer rather than by
/// name). Any defined procedure outside that set has no reachable reference and is dropped.
///
/// Conservative on purpose: a procedure called only from another (itself dead) procedure
/// still counts as referenced and is kept - removing it would need the full call graph, and
/// keeping it only costs a little size, never correctness. Applied to whole programs only
/// (a $COMPILE UNIT must still export every procedure for separate compilation).
/// </summary>
public static class Pb36DeadProc {

  public static HashSet<ProcedureSymbol> Live(SemanticModel model) {
    var live = new HashSet<ProcedureSymbol>(ReferenceEqualityComparer.Instance);
    foreach (var target in model.CallBindings.Values)
      live.Add(target);                       // every direct call site and CODEPTR-family reference
    foreach (var lifted in model.LambdaProcs.Values)
      live.Add(lifted);                       // lambdas reach the emitter via a pointer, not a named call
    return live;
  }
}
