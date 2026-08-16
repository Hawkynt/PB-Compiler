namespace PowerBasic.Compiler.Backend;

/// <summary>
/// An internal-consistency violation inside the x86-16 back end: something the selector, the
/// allocator or the emitter GUARANTEES to its own later stages did not hold.
///
/// <para>
/// It exists to keep two very different failures from looking alike. A construct the back end cannot
/// compile must DECLINE - <c>InstructionSelector.Decline</c>, a null from
/// <c>LinearScanAllocator.Allocate</c>, an <c>IrLoweringException</c> - so the direct emitter takes
/// the function and the refusal lands in the coverage histogram with a reason attached. That path is
/// survivable and is measured. A broken invariant is neither: no fallback can repair it, and it must
/// be loud. Before this type both spellings were <c>NotSupportedException</c> and
/// <c>InvalidOperationException</c>, so reading a stack trace could not tell "this program uses
/// something we do not support" from "this back end has a bug".
/// </para>
///
/// <para>
/// Every message therefore names the function that found the violation and states the invariant that
/// was broken - not just what was observed. "operand 3 is not a source" says where to look; "the
/// selector emits only Register/Immediate/Memory operands into a source position" says what to look
/// for.
/// </para>
/// </summary>
public sealed class BackendInvariantException(string where, string invariant)
  : System.InvalidOperationException($"x86-16 back end invariant broken in {where}: {invariant}") {

  /// <summary>The function that detected the violation.</summary>
  public string Where { get; } = where;
}
