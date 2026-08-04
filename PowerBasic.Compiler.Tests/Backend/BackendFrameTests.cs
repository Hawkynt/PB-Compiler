using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The routed frame: where a local lives and what is in it before the body writes anything.
///
/// Both properties tested here were wrong at once, and neither failed loudly. A procedure with a
/// local array printed a plausible number instead of the right one, which was blamed on the frame
/// layout and paid for by keeping every array-declaring procedure off the back end. The real causes
/// were smaller and more specific:
///
/// <list type="number">
/// <item>Stack slots are laid out DOWNWARD from BP while a GEP walks UPWARD from its base, so a
/// multi-slot alloca that points at slot 0 puts element 0 at the top of its block and sends the rest
/// climbing over the saved BP, the return address and the caller's arguments.</item>
/// <item>PB gives every local a zero start; the routed prologue never zeroed the frame, so an element
/// that was never assigned read whatever the previous call left behind.</item>
/// </list>
///
/// A scalar local hides both: one slot is its own base, and it is written before it is read.
/// </summary>
[TestFixture]
public sealed class BackendFrameTests {

  private static string Run(string source, bool routed, out IEnumerable<string> routedNames) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true, UseExperimentalBackend = routed };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    routedNames = cg.BackendRoutedNames.ToList();
    return Cpu8086.Run(image).Output.Trim();
  }

  /// <summary>
  /// The shape that found the layout bug: fifty elements summed, one of them written. Reading the
  /// array upward from the wrong end walks straight into the parameter the caller pushed, so the sum
  /// comes back as a number that looks like data rather than like a fault.
  /// </summary>
  [Test]
  public void Route_GivenALocalArraySummedInFull_ThenItAgreesWithTheDirectEmitter() {
    const string source = """
      CALL Acc(3)
      END
      SUB Acc(BYVAL n%) NOINLINE
        DIM a%(0 TO 49)
        a%(7) = n%
        s% = 0
        FOR i% = 0 TO 49
          s% = s% + a%(i%)
        NEXT i%
        PRINT "acc"; s%
      END SUB
      """;

    var routed = Run(source, routed: true, out var names);

    Assert.That(names, Does.Contain("Acc"), "the point of the test is that this procedure IS routed");
    Assert.That(routed, Is.EqualTo(Run(source, routed: false, out _)));
    Assert.That(routed, Is.EqualTo("acc 3"));
  }

  /// <summary>
  /// The other end of the array: writing the LAST element must stay inside the frame. If the base is
  /// off by the block's length this writes over the return address and the procedure never comes back.
  /// </summary>
  [Test]
  public void Route_GivenAWriteToTheLastElement_ThenTheReturnAddressSurvives() {
    const string source = """
      CALL Edges(9)
      PRINT "returned"
      END
      SUB Edges(BYVAL n%) NOINLINE
        DIM a%(0 TO 19)
        a%(19) = n%
        s% = 0
        FOR i% = 0 TO 19
          s% = s% + a%(i%)
        NEXT i%
        PRINT "edges"; s%
      END SUB
      """;

    var routed = Run(source, routed: true, out var names);

    Assert.That(names, Does.Contain("Edges"));
    Assert.That(routed, Is.EqualTo(Run(source, routed: false, out _)));
    Assert.That(routed, Does.Contain("returned"), "an overwritten return address would never get here");
  }

  /// <summary>
  /// PB starts every local at zero, so a never-assigned element reads as zero - not as whatever the
  /// previous call left on the stack. The earlier call here is what puts something there to find.
  /// </summary>
  [Test]
  public void Route_GivenAnUnassignedLocal_ThenItReadsAsZeroNotAsTheLastCallsLeftovers() {
    const string source = """
      CALL Dirty(1234)
      CALL Clean
      END
      SUB Dirty(BYVAL n%) NOINLINE
        DIM a%(0 TO 19)
        FOR i% = 0 TO 19
          a%(i%) = n%
        NEXT i%
        PRINT "dirty"; a%(0)
      END SUB
      SUB Clean NOINLINE
        DIM b%(0 TO 19)
        t% = 0
        FOR i% = 0 TO 19
          t% = t% + b%(i%)
        NEXT i%
        PRINT "clean"; t%
      END SUB
      """;

    var routed = Run(source, routed: true, out var names);

    Assert.That(names, Does.Contain("Clean"));
    Assert.That(routed, Is.EqualTo(Run(source, routed: false, out _)));
    Assert.That(routed, Does.Contain("clean 0"), "an unzeroed frame would sum the previous call's 1234s");
  }

  /// <summary>
  /// The layout property directly, without going through an execution: the address a multi-slot
  /// alloca yields is the LOWEST of its block, so every element it addresses is inside the frame.
  /// </summary>
  [Test]
  public void Select_GivenAMultiSlotAlloca_ThenItsAddressIsTheBottomOfTheBlock() {
    var fn = new IrFunction("F", IrType.Void, []);
    var entry = fn.CreateBlock("entry");
    entry.Append(new IrAlloca(IrType.I16) { Count = 8 });
    entry.Append(new IrRet(null));

    var m = InstructionSelector.TrySelect(fn, out var why);

    Assert.That(m, Is.Not.Null, $"declined: {why}");
    Assert.That(m!.StackSlots, Has.Count.EqualTo(8), "eight elements need eight slots");
    var lea = m.AllInstructions.Single(i => i.Opcode == MOpcode.Lea);
    var slot = lea.Operands.OfType<MOperand.StackSlot>().Single();
    Assert.That(slot.Index, Is.EqualTo(7),
      "slots run downward from BP, so the block's base is its LAST slot - pointing at slot 0 sends "
      + "element 1 onward out of the frame");
  }

  [Test]
  public void Select_GivenASingleSlotAlloca_ThenItsAddressIsThatSlot() {
    var fn = new IrFunction("F", IrType.Void, []);
    var entry = fn.CreateBlock("entry");
    entry.Append(new IrAlloca(IrType.I16));
    entry.Append(new IrRet(null));

    var m = InstructionSelector.TrySelect(fn, out _)!;

    Assert.That(m.AllInstructions.Single(i => i.Opcode == MOpcode.Lea)
      .Operands.OfType<MOperand.StackSlot>().Single().Index, Is.EqualTo(0), "a scalar's last slot is its first");
  }

  /// <summary>The prologue zeroes what it allocated - the frame, not part of it.</summary>
  [Test]
  public void Emit_GivenAFrame_ThenThePrologueZeroesAllOfIt() {
    var fn = new IrFunction("F", IrType.Void, []);
    var entry = fn.CreateBlock("entry");
    entry.Append(new IrAlloca(IrType.I16) { Count = 8 });
    entry.Append(new IrRet(null));
    var m = InstructionSelector.TrySelect(fn, out _)!;
    MachineScheduler.Schedule(m);
    var alloc = LinearScanAllocator.Allocate(m)!;

    var asm = new PowerBasic.Compiler.Asm.Assembler();
    MachineEmitter.EmitFunction(asm, m, alloc, [], 0);

    // 8 slots of 2 bytes = 16 bytes = 8 words, and REP STOSW (F3 AB) is how the direct path spells it
    var bytes = asm.ToArray();
    var repStosw = Enumerable.Range(0, bytes.Length - 1).Any(i => bytes[i] == 0xF3 && bytes[i + 1] == 0xAB);
    Assert.That(repStosw, "the prologue has to zero the frame it just allocated");
    // and the count is the whole frame in words - MOV CX, 8 (B9 08 00)
    Assert.That(Enumerable.Range(0, bytes.Length - 2)
      .Any(i => bytes[i] == 0xB9 && bytes[i + 1] == 0x08 && bytes[i + 2] == 0x00), "all 8 words, not some of them");
  }
}
