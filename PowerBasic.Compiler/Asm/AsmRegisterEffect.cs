namespace PowerBasic.Compiler.Asm;

/// <summary>
/// What one inline-assembly statement does to the integer register file, read out of the text by the
/// assembler that also emits it (<see cref="TextAssembler.Analyze"/>).
///
/// <para>
/// It exists so that a back end laying out its own registers can honour a register an <c>!</c>
/// statement leaves for a LATER one - <c>! MOV CX, 5</c>, a BASIC statement, <c>! DEC CX</c>. A
/// clobber list says "nothing of yours survives this block", which is a different claim and does not
/// stop the allocator putting a temporary IN <c>CX</c> in the middle.
/// </para>
///
/// <para>
/// The three register sets are approximated in DIFFERENT directions, and getting one of them the
/// wrong way round is a silent miscompile rather than a missed optimization:
/// </para>
/// <list type="bullet">
///   <item><see cref="Reads"/> - <b>may</b> read. Over-claiming costs a register somebody else could
///   have had (or a decline); under-claiming loses a value the text needed.</item>
///   <item><see cref="Defines"/> - <b>may</b> write, and so may be the producer a later read is
///   taking its value from. Under-claiming leaves that read with no producer to protect.</item>
///   <item><see cref="Kills"/> - <b>must</b> write, which ends an earlier statement's claim on the
///   register. Over-claiming drops the protection an earlier producer needed, so a byte half
///   (<c>MOV AL, ...</c>) defines its word register without killing it.</item>
/// </list>
///
/// <para>
/// Everything is canonicalized to the word registers the allocator hands out: <c>AH</c> and
/// <c>EAX</c> are both <c>AX</c>, because that is the resource being contended for. Segment, x87,
/// MMX and SSE registers are not tracked at all - none of them is allocated here, and the direct
/// emitter reloads <c>ES</c> in front of a far access exactly as this back end does, so neither path
/// promises a segment register survives a BASIC statement.
/// </para>
/// </summary>
/// <param name="Reads">word registers the statement may read</param>
/// <param name="Defines">word registers the statement may write</param>
/// <param name="Kills">word registers the statement certainly overwrites whole</param>
/// <param name="ReadsFlags">whether it consumes the flags a previous statement set</param>
/// <param name="WritesFlags">whether it sets flags</param>
/// <param name="IsOpaque">
/// true when the text was not understood - an unlisted mnemonic, an <c>INT</c>, a <c>CALL</c>, or a
/// line that did not parse. The sets are then the whole file both ways, which keeps a chain of
/// producers and consumers unbroken across it, but a read that is only INFERRED this way is not
/// evidence the text wanted the register: see <c>LinearScanAllocator.AsmHeldByIndex</c>, where a
/// precise read whose value something destroys declines the function and an inferred one does not.
/// </param>
public sealed record AsmRegisterEffect(
  IReadOnlySet<Reg> Reads,
  IReadOnlySet<Reg> Defines,
  IReadOnlySet<Reg> Kills,
  bool ReadsFlags,
  bool WritesFlags,
  bool IsOpaque) {

  /// <summary>
  /// The register a <c>PUSH r</c> SAVES, null for every other statement - including a <c>PUSH</c> of a
  /// segment register, an immediate or a memory cell, none of which is a register this file tracks.
  /// </summary>
  public Reg? Saves { get; init; }

  /// <summary>The register a <c>POP r</c> RESTORES, on the same terms as <see cref="Saves"/>.</summary>
  public Reg? Restores { get; init; }

  /// <summary>
  /// What the statement moves the stack by, in words: +1 for a push, -1 for a pop, 0 for everything
  /// else, and <b>null</b> when the text was not understood and may move it by anything.
  ///
  /// <para>
  /// It exists to PAIR a save with its restore. Reading <c>PUSH DI</c> as a use of <c>DI</c> and
  /// <c>POP DI</c> as a definition of it is true of the instructions and false of the IDIOM: together
  /// they promise nothing and consume nothing, they hand the register back exactly as they found it.
  /// Depth is how the two are matched, because only depth distinguishes the pair from a <c>PUSH</c>
  /// and a <c>POP</c> that merely name the same register.
  /// </para>
  /// </summary>
  public int? StackDelta { get; init; }

  /// <summary>The allocatable integer file - <c>BP</c>/<c>SP</c> are the frame and belong to nobody's text.</summary>
  public static IReadOnlySet<Reg> GeneralRegisters { get; } =
    new HashSet<Reg> { Reg.AX, Reg.CX, Reg.DX, Reg.BX, Reg.SI, Reg.DI };

  /// <summary>
  /// The effect of a statement this pass does not understand: it may read anything, and anything in a
  /// register afterwards came from it. Both halves are needed for the chain to stay unbroken - a
  /// producer before it is protected up to it, and a consumer after it is protected from it.
  /// </summary>
  public static AsmRegisterEffect Opaque { get; } =
    new(GeneralRegisters, GeneralRegisters, GeneralRegisters, ReadsFlags: true, WritesFlags: true, IsOpaque: true) {
      StackDelta = null,                            // an INT or a CALL may leave the stack anywhere
    };
}
