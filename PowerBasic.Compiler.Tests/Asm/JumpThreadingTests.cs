using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

/// <summary>
/// Assembler-level jump threading: a JMP/Jcc whose target label sits on an unconditional JMP is
/// retargeted to that jump's final destination (chains followed, cycles guarded) - the cascade an
/// ITERATE creates (jump to the loop end, which jumps back to the loop head) collapses to one hop.
/// Pure fixup rewrite: byte-length-preserving, gated like the other post-emit passes.
/// </summary>
[TestFixture]
public sealed class JumpThreadingTests {

  [Test]
  public void Assemble_GivenJmpToJmp_WhenThreaded_ThenFirstJumpTargetsFinalDestination() {
    var asm = new Assembler { EnableJumpThreading = true };
    var hop = asm.DefineLabel();
    var final = asm.DefineLabel();
    asm.Jmp(hop);            // 0: E9 xx xx  -> must retarget to 'final'
    asm.Nop();
    asm.MarkLabel(hop);
    asm.Jmp(final);          // 4: E9 xx xx
    asm.Nop();
    asm.MarkLabel(final);    // 8:
    asm.Nop();
    var image = asm.ToArray();
    // E9 rel16 at 0: threaded displacement = final(8) - 3 = 5; unthreaded would be hop(4) - 3 = 1
    Assert.That(image[1] | image[2] << 8, Is.EqualTo(5), "the first JMP goes straight to the final destination");
  }

  [Test]
  public void Assemble_GivenConditionalToJmpChain_WhenThreaded_ThenJccTargetsFinalDestination() {
    var asm = new Assembler { EnableJumpThreading = true };
    var hop1 = asm.DefineLabel();
    var hop2 = asm.DefineLabel();
    var final = asm.DefineLabel();
    asm.Jz(hop1);            // 0: the 8086 pair, 75 03 E9 xx xx -> threads hop1 -> hop2 -> final
    asm.Nop();               // 5
    asm.MarkLabel(hop1);
    asm.Jmp(hop2);           // 6: E9 xx xx
    asm.MarkLabel(hop2);
    asm.Jmp(final);          // 9: E9 xx xx
    asm.Nop();               // 12
    asm.MarkLabel(final);    // 13:
    asm.Nop();
    var image = asm.ToArray();
    // the pair's JMP carries the rel16 at 3, and it threads exactly as the near form did:
    // displacement = final(13) - 5 = 8
    Assert.That(image[3] | image[4] << 8, Is.EqualTo(8), "the conditional jump follows the whole chain");
  }

  [Test]
  public void Assemble_GivenJumpCycle_WhenThreaded_ThenTerminates() {
    var asm = new Assembler { EnableJumpThreading = true };
    var a = asm.DefineLabel();
    var b = asm.DefineLabel();
    asm.Jmp(a);
    asm.MarkLabel(a);
    asm.Jmp(b);
    asm.MarkLabel(b);
    asm.Jmp(a);              // a -> b -> a cycle
    Assert.DoesNotThrow(() => asm.ToArray(), "a GOTO cycle must not hang the threader");
  }

  [Test]
  public void Assemble_GivenThreadingDisabled_WhenAssembled_ThenJumpsUntouched() {
    var asm = new Assembler();
    var hop = asm.DefineLabel();
    var final = asm.DefineLabel();
    asm.Jmp(hop);
    asm.Nop();
    asm.MarkLabel(hop);
    asm.Jmp(final);
    asm.Nop();
    asm.MarkLabel(final);
    asm.Nop();
    var image = asm.ToArray();
    Assert.That(image[1] | image[2] << 8, Is.EqualTo(1), "without the gate the faithful stream is kept");
  }

  [Test]
  public void Assemble_GivenCallToJmp_WhenThreaded_ThenCallUntouched() {
    // a CALL must keep its target (the return address bookkeeping is the callee's identity)
    var asm = new Assembler { EnableJumpThreading = true };
    var hop = asm.DefineLabel();
    var final = asm.DefineLabel();
    asm.Call(hop);           // 0: E8 xx xx stays -> hop(4)
    asm.Nop();
    asm.MarkLabel(hop);
    asm.Jmp(final);
    asm.Nop();
    asm.MarkLabel(final);
    asm.Ret();
    var image = asm.ToArray();
    Assert.That(image[1] | image[2] << 8, Is.EqualTo(1), "CALL is not a jump - never threaded");
  }
}
