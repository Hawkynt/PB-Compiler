namespace PowerBasic.Compiler.Ir;

/// <summary>
/// A construct one of the IR back ends (<see cref="CEmitter"/>, <see cref="LlvmEmitter"/>) has no
/// rendering for. It is the emitters' half of the same answer <see cref="IrLoweringException"/>
/// gives for the lowering: caught by <c>TryEmit</c> and turned into a named refusal, never a stack
/// trace out of the compiler.
///
/// <para>
/// The hosted C and LLVM targets are explicit alternate renderers, not DOS fallbacks. A decline from
/// any production target therefore buys only a diagnostic that names the construct; it never grants
/// permission to emit the body through another path.
/// </para>
///
/// <para>
/// So it is held apart from <see cref="Backend.BackendInvariantException"/>, which spells the other
/// failure: "this back end has no answer for that yet" and "this back end has a bug" are different
/// reports, and before these two types both were <c>NotSupportedException</c> and
/// <c>InvalidOperationException</c> in turn. The message names the CONSTRUCT, because the reader's
/// next question is always which line of their program to change.
/// </para>
/// </summary>
public sealed class EmitDeclinedException(string construct) : Exception(construct) {

  /// <summary>
  /// The refusal for an instruction a back end has no case for, phrased as the construct the reader
  /// WROTE rather than the IR class name they never see: "an inline assembly block" is something to
  /// go and look for in a program, "IrInlineAsm" is not.
  /// </summary>
  public static EmitDeclinedException For(string backEnd, IrInstruction instruction) =>
    new($"{backEnd}: {Describe(instruction)}");

  private static string Describe(IrInstruction instruction) => instruction switch {
    IrInlineAsm => "an inline assembly block (a `!` statement), which is x86-16 machine code by definition",
    IrFarPtr => "a far (segment:offset) pointer - a segmented PEEK/POKE, or an array DIMmed AT an "
      + "address - which has no portable pointer form",
    IrIndirectBr => "a computed branch (GOTO DWORD / GOSUB DWORD, through a CODEPTR32 address)",
    _ => $"the IR instruction {instruction.GetType().Name}, which this back end has no case for",
  };
}
