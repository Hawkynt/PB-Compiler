using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

/// <summary>
/// O0093's second half: once threading has bypassed an <c>A: JMP B</c> hop, the hop is dead code
/// and its bytes go.
///
/// The removal is guarded by two conditions and both are pinned here, because each looks like an
/// optimization on its own and together they are the difference between a size saving and a
/// miscompile:
///
/// NOTHING MAY TARGET IT — a hop something still jumps to is a live instruction. A named label on
/// it counts as a reference too, since another module may use the name.
///
/// CONTROL MAY NOT FALL INTO IT — a hop reached by falling off the end of the preceding instruction
/// is live however few things jump to it, and deleting it would silently redirect that fall-through
/// to whatever follows. The only instruction this assembler will claim does not fall through is
/// another unconditional JMP ending exactly at the hop's first byte. A RET does not qualify — not
/// because it falls through, but because <c>C3</c> cannot be told from a displacement byte.
///
/// Every assertion compares two builds rather than naming a byte count. The first draft named them
/// and was wrong three times over: these images are small enough that jumps take their short form,
/// and the removal iterates, so a hop going away can orphan the next one and take more than its own
/// three bytes with it. What the pass promises is a difference between two builds, so that is what
/// is measured.
/// </summary>
[TestFixture]
public sealed class OrphanedJumpHopTests {

  /// <summary>
  /// The documented shape: `JMP over` cannot fall through, so `hop` is unreachable that way, and
  /// threading leaves nothing pointing at it.
  /// </summary>
  private static byte[] BypassedHop(bool threading) {
    var asm = new Assembler { EnableJumpThreading = threading };
    var hop = asm.DefineLabel();
    var over = asm.DefineLabel();
    var final = asm.DefineLabel();
    asm.Jmp(over);           // cannot fall through, so 'hop' is unreachable that way
    asm.MarkLabel(hop);
    asm.Jmp(final);          // the hop, bypassed once the jump below threads past it
    asm.MarkLabel(over);
    asm.Jmp(hop);            // threads to 'final', leaving nothing pointing at 'hop'
    asm.Nop();
    asm.MarkLabel(final);
    asm.Nop();
    return asm.ToArray();
  }

  [Test]
  public void Assemble_GivenABypassedHopAfterAJump_ThenItsBytesAreRemoved() =>
    Assert.That(BypassedHop(threading: true).Length, Is.LessThan(BypassedHop(threading: false).Length),
      "the bypassed hop is dead and its bytes go");

  /// <summary>
  /// The condition that matters most. The hop follows a NOP, which falls straight into it, so even
  /// though nothing jumps to the hop it is reachable - removing it would send the NOP's
  /// fall-through to the wrong place. Both builds must be the same size.
  /// </summary>
  private static byte[] FallenInto(bool threading) {
    var asm = new Assembler { EnableJumpThreading = threading };
    var hop = asm.DefineLabel();
    var final = asm.DefineLabel();
    asm.Nop();               // falls through into the hop
    asm.MarkLabel(hop);
    asm.Jmp(final);          // nothing targets this, but the NOP walks into it
    asm.Nop();
    asm.MarkLabel(final);
    asm.Nop();
    return asm.ToArray();
  }

  [Test]
  public void Assemble_GivenAnUntargetedHopReachedByFallThrough_ThenItIsKept() =>
    Assert.That(FallenInto(threading: true).Length, Is.EqualTo(FallenInto(threading: false).Length),
      "a hop reachable by fall-through is live however few things jump to it");

  /// <summary>
  /// A named label on the hop keeps it: this assembler cannot see who imports that name, so the
  /// bytes stay even though nothing in this module targets them.
  /// </summary>
  private static byte[] NamedHop(bool named) {
    var asm = new Assembler { EnableJumpThreading = true };
    var hop = asm.DefineLabel();
    var over = asm.DefineLabel();
    var final = asm.DefineLabel();
    asm.Jmp(over);
    if (named)
      asm.MarkLabel("rt_exported_thunk");
    asm.MarkLabel(hop);
    asm.Jmp(final);
    asm.MarkLabel(over);
    asm.Jmp(hop);
    asm.Nop();
    asm.MarkLabel(final);
    asm.Nop();
    return asm.ToArray();
  }

  [Test]
  public void Assemble_GivenANamedLabelOnTheHop_ThenItIsKept() =>
    Assert.That(NamedHop(named: true).Length, Is.GreaterThan(NamedHop(named: false).Length),
      "an exported name may be jumped to from outside this module");

  /// <summary>
  /// Off without the flag: this rides on jump threading, which is gated for the program image, so a
  /// plain assembler emits every hop untouched.
  /// </summary>
  [Test]
  public void Assemble_WhenThreadingDisabled_ThenNothingIsRemoved() =>
    Assert.That(BypassedHop(threading: false).Length, Is.GreaterThan(BypassedHop(threading: true).Length),
      "the removal is gated with threading, not always on");

  /// <summary>
  /// A cycle of hops must still terminate and still assemble - the removal iterates, and a jump
  /// cycle is the shape that would spin a naive fixpoint loop.
  /// </summary>
  [Test]
  public void Assemble_GivenAHopCycle_ThenItStillTerminates() {
    var asm = new Assembler { EnableJumpThreading = true };
    var a = asm.DefineLabel();
    var b = asm.DefineLabel();
    asm.Jmp(a);
    asm.MarkLabel(a);
    asm.Jmp(b);
    asm.MarkLabel(b);
    asm.Jmp(a);
    Assert.DoesNotThrow(() => asm.ToArray());
  }
}
