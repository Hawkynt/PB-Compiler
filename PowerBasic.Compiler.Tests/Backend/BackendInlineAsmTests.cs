using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Inline assembly through the x86-16 back end.
///
/// The thing that makes this possible is that the LOWERING binds each identifier to the storage the
/// semantic model says it denotes, rather than the emitter resolving names against whatever frame is
/// current. The back end then only has to say where IT put that storage - which is the one question
/// the direct emitter's resolver could never answer for a frame it did not lay out.
/// </summary>
[TestFixture]
public sealed class BackendInlineAsmTests {

  private static string Run(string source, bool routed) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true, UseExperimentalBackend = routed };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  /// <summary>
  /// The module body really goes through the back end - selected, scheduled and allocated. Without
  /// this a behaviour test proves nothing about the routed path: a declined function falls back to
  /// the direct emitter, and then both sides of the comparison are the same compiler. It matters most
  /// for the register-pressure cases, where the honest failure mode of a reservation is "allocation
  /// gave up" rather than "the answer was wrong".
  /// </summary>
  private static void AssertRoutes(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    IrPassManager.Standard().RunOnModule(module!);

    var main = module!.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    var m = InstructionSelector.TrySelect(main, out var reason);
    Assert.That(m, Is.Not.Null, $"selection declined: {reason}");
    MachineScheduler.Schedule(m!);
    Assert.That(LinearScanAllocator.Allocate(m!, out var noRegisters), Is.Not.Null,
      $"allocation declined: {noRegisters}");
  }

  /// <summary>
  /// Without this, every test below could pass by falling back: when selection declines, the direct
  /// emitter takes the function and both sides of the comparison are the same compiler.
  /// </summary>
  [Test]
  public void InlineAsm_GivenABoundName_ThenTheFunctionActuallySelects() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize("""
      DIM n AS INTEGER
      n = 1
      ! MOV AX, 5
      ! MOV n, AX
      PRINT n
      """, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    IrPassManager.Standard().RunOnModule(module!);

    var main = module!.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    var m = InstructionSelector.TrySelect(main, out var reason);

    Assert.That(m, Is.Not.Null, $"selection declined: {reason}");
    Assert.That(m!.AllInstructions.Any(i => i.Opcode == MOpcode.InlineAsm), "the asm reached the machine IR");

    // Every name the block mentions is paired with a MEMORY cell of this frame - which is the whole
    // claim: the emitter can answer the assembler without knowing what a BASIC variable is.
    var blocks = m.AllInstructions.Where(i => i.Opcode == MOpcode.InlineAsm).ToList();
    Assert.That(blocks, Has.Count.EqualTo(2), "one per '!' statement; MOV AX, 5 names nothing");
    var writesN = blocks.Single(b => ((MOperand.InlineAsmText)b.Operands[0]).Names.Contains("n"));
    Assert.That(writesN.Operands.Skip(1), Is.All.Matches<MOperand>(
      o => o is MOperand.StackSlot or MOperand.DataCell or MOperand.ParamCell or MOperand.Memory));
    Assert.That(writesN.Operands, Has.Count.EqualTo(2), "the descriptor plus n's own cell");

    Assert.That(LinearScanAllocator.Allocate(m), Is.Not.Null, "and it allocates, so the function routes");
  }

  /// <summary>The asm writes a BASIC local, and BASIC reads what it wrote - through the routed path.</summary>
  [Test]
  public void InlineAsm_GivenItWritesALocal_ThenTheRoutedProgramBehavesLikeTheDirectOne() {
    const string source = """
      DIM n AS INTEGER
      n = 1
      ! MOV AX, 5
      ! MOV n, AX
      PRINT n
      """;

    Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)));
  }

  /// <summary>A read in the other direction: BASIC sets the variable, the asm loads from it.</summary>
  [Test]
  public void InlineAsm_GivenItReadsALocal_ThenBothPathsAgree() {
    const string source = """
      DIM a AS INTEGER
      DIM b AS INTEGER
      a = 7
      ! MOV AX, a
      ! ADD AX, AX
      ! MOV b, AX
      PRINT b
      """;

    Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)));
  }

  /// <summary>
  /// A module-level variable is storage too, and the routed path addresses the SAME data cell the
  /// direct emitter does - the back end does not lay data out, the whole-program codegen does.
  /// </summary>
  [Test]
  public void InlineAsm_GivenItTouchesAModuleVariable_ThenBothPathsAgree() {
    const string source = """
      DIM SHARED total AS INTEGER
      total = 3
      ! MOV AX, total
      ! ADD AX, 4
      ! MOV total, AX
      PRINT total
      """;

    Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)));
  }

  /// <summary>
  /// The documented string-manager ABI, called by name: push the handle, <c>CALL GetStrLoc</c>, and
  /// the routine answers DX:AX = a far pointer at the characters and CX = the length. The name is
  /// CODE, so nothing about it belongs to a frame - the emitter resolves it to the runtime's own
  /// label, and the exact first character and length are asserted rather than only agreement, since
  /// two paths that both got a null pointer would agree too.
  /// </summary>
  [Test]
  public void InlineAsm_GivenTheStringManagerAbiCalledByName_ThenTheHandleResolvesToItsCharacters() {
    const string source = """
      a$ = "XYZZY"
      r% = 0
      c% = 0
      ! push Word Ptr a$
      ! call GetStrLoc
      ! mov  ES, DX
      ! mov  BX, AX
      ! mov  AL, ES:[BX]
      ! xor  AH, AH
      ! mov  r%, AX
      ! mov  c%, CX
      PRINT r%; c%
      """;

    Assert.That(Run(source, routed: true), Is.EqualTo("88  5"), "'X' is 88, and the string is five long");
    Assert.That(Run(source, routed: false), Is.EqualTo(Run(source, routed: true)));
  }

  /// <summary>
  /// A BASIC LABEL as a jump target. The loop is written half in assembly and half in BASIC - the
  /// body is a BASIC statement and <c>JNZ</c> goes back to the BASIC label - so the only thing that
  /// can produce 5 is the branch actually being taken four times. A block that fell through instead
  /// would print 1, and one whose target was mis-resolved would not run at all.
  ///
  /// <para>
  /// The counter is a BASIC VARIABLE rather than CX, and that is not incidental: it keeps this test
  /// about the BRANCH. Whether a register survives the intervening BASIC statement is a different
  /// question, answered by
  /// <see cref="InlineAsm_GivenARegisterHeldAcrossABasicStatement_ThenBothPathsAgree"/> and the tests
  /// beside it.
  /// </para>
  /// </summary>
  [Test]
  public void InlineAsm_GivenAJumpToABasicLabel_ThenTheLoopReallyBranches() {
    const string source = """
      DIM n AS INTEGER
      DIM c AS INTEGER
      n = 0
      c = 5
      AddLoop:
      n = n + 1
      ! DEC c
      ! JNZ AddLoop
      PRINT n
      """;

    Assert.That(Run(source, routed: true), Is.EqualTo("5"), "the asm branch drove five BASIC iterations");
    Assert.That(Run(source, routed: false), Is.EqualTo(Run(source, routed: true)));
  }

  /// <summary>
  /// The contract of <see cref="InlineAsmReservation"/> at its smallest: a register one <c>!</c>
  /// statement loads is still there for the next one, with a BASIC statement in between.
  ///
  /// <para>
  /// This used to be pinned as a DISAGREEMENT - the allocator put <c>n + 1</c> in CX and the routed
  /// image printed <c>1  1</c> - and the direct emitter only got it right by computing through AX,
  /// which is luck rather than contract. Both paths are now asserted against the exact value, not
  /// merely against each other: two images that had both lost CX would agree too.
  /// </para>
  /// </summary>
  [Test]
  public void InlineAsm_GivenARegisterHeldAcrossABasicStatement_ThenBothPathsAgree() {
    const string source = """
      DIM n AS INTEGER
      DIM r AS INTEGER
      n = 0
      ! MOV CX, 5
      n = n + 1
      ! MOV r, CX
      PRINT n; r
      """;

    AssertRoutes(source);
    Assert.That(Run(source, routed: true), Is.EqualTo("1  5"), "CX is the assembly's across the statement");
    Assert.That(Run(source, routed: false), Is.EqualTo(Run(source, routed: true)));
  }

  /// <summary>
  /// The same across SEVERAL statements, including ones that call the runtime. A PRINT goes through
  /// AX in both emitters, so the register held here is one no fixed convention wants - which is
  /// exactly the line the reservation draws: the allocator's choices are constrained, the ABI's are
  /// not.
  /// </summary>
  [Test]
  public void InlineAsm_GivenARegisterHeldAcrossSeveralBasicStatements_ThenBothPathsAgree() {
    const string source = """
      DIM n AS INTEGER
      DIM m AS INTEGER
      DIM r AS INTEGER
      n = 0
      m = 0
      ! MOV CX, 7
      n = n + 1
      m = n * 3 + 2
      n = m - n
      ! MOV r, CX
      PRINT n; m; r
      """;

    AssertRoutes(source);
    Assert.That(Run(source, routed: true), Is.EqualTo("4  5  7"));
    Assert.That(Run(source, routed: false), Is.EqualTo(Run(source, routed: true)));
  }

  /// <summary>Two registers at once, so a fix that reserved "the one register" would still be caught.</summary>
  [Test]
  public void InlineAsm_GivenTwoRegistersHeldAcrossABasicStatement_ThenBothSurvive() {
    const string source = """
      DIM n AS INTEGER
      DIM r AS INTEGER
      DIM s AS INTEGER
      n = 2
      ! MOV CX, 11
      ! MOV DX, 22
      n = n + 1
      ! MOV r, CX
      ! MOV s, DX
      PRINT n; r; s
      """;

    AssertRoutes(source);
    Assert.That(Run(source, routed: true), Is.EqualTo("3  11  22"));
    Assert.That(Run(source, routed: false), Is.EqualTo(Run(source, routed: true)));
  }

  /// <summary>
  /// The pressure case: the assembly holds four of the six allocatable registers across a BASIC
  /// statement that wants more values live than the two remaining ones can carry. What this pins is
  /// that the reservation is SPILLED AROUND rather than given up on - the values that cannot get a
  /// register go to the frame, and the function still routes. Its failure mode is
  /// <see cref="AssertRoutes"/> reporting "allocation declined", not a wrong answer; the arithmetic is
  /// asserted anyway, because a spill that loses a value is the other way this could go wrong.
  /// </summary>
  [Test]
  public void InlineAsm_GivenTheAsmHoldsMostOfTheRegisterFile_ThenTheBasicCodeSpillsAroundIt() {
    const string source = """
      DIM a AS INTEGER
      DIM b AS INTEGER
      DIM c AS INTEGER
      DIM d AS INTEGER
      DIM e AS INTEGER
      DIM r AS INTEGER
      a = 3 : b = 5 : c = 7 : d = 11 : e = 13
      ! MOV AX, 100
      ! MOV BX, 200
      ! MOV CX, 300
      ! MOV DX, 400
      a = a * b + c * d - e * b + a * c
      ! MOV r, CX
      PRINT a; r
      """;

    AssertRoutes(source);
    Assert.That(Run(source, routed: true), Is.EqualTo("48  300"));
    Assert.That(Run(source, routed: false), Is.EqualTo(Run(source, routed: true)));
  }

  /// <summary>
  /// A register held around a LOOP BACK EDGE, which is why the reservation is a reachability question
  /// rather than a span of instruction indices: the <c>FOR</c>'s increment runs between one
  /// <c>DEC CX</c> and the next while sitting AFTER it in the instruction stream. A first-to-last
  /// reading of the same function leaves the increment free to take CX.
  /// </summary>
  [Test]
  public void InlineAsm_GivenARegisterHeldAroundALoopBackEdge_ThenBothPathsAgree() {
    const string source = """
      DIM n AS INTEGER
      DIM r AS INTEGER
      DIM i AS INTEGER
      n = 0
      r = 0
      ! MOV CX, 100
      FOR i = 1 TO 3
        n = n + i
        ! DEC CX
        ! MOV r, CX
      NEXT i
      PRINT n; r
      """;

    AssertRoutes(source);
    Assert.That(Run(source, routed: true), Is.EqualTo("6  97"));
    Assert.That(Run(source, routed: false), Is.EqualTo(Run(source, routed: true)));
  }

  /// <summary>
  /// LOWLEVEL.BAS's own shape end to end: the asm loop whose body is a BASIC statement and whose
  /// branch goes back to a BASIC label. Five iterations only happen if CX is the assembly's across
  /// <c>n = n + 1</c>; a routed image that lost it prints 1.
  /// </summary>
  [Test]
  public void InlineAsm_GivenTheLowlevelLoopShape_ThenTheRoutedImageCountsFiveTimes() {
    const string source = """
      DIM n AS INTEGER
      n = 0
      ! MOV CX, 5
      AddLoop:
      n = n + 1
      ! DEC CX
      ! JNZ AddLoop
      PRINT n
      """;

    AssertRoutes(source);
    Assert.That(Run(source, routed: true), Is.EqualTo("5"));
    Assert.That(Run(source, routed: false), Is.EqualTo(Run(source, routed: true)));
  }

  /// <summary>
  /// The reservation is not a tax on every function with a <c>!</c> in it. Where no register has to
  /// survive - a run of consecutive asm statements, or the code after the last one - the allocator
  /// keeps the whole file, and DIFF20.BAS (which routes today) depends on that.
  /// </summary>
  [Test]
  public void InlineAsm_GivenNothingHasToSurvive_ThenNoRegisterIsReserved() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize("""
      DIM n AS INTEGER
      DIM r AS INTEGER
      ! MOV CX, 5
      ! MOV r, CX
      n = r + 1
      PRINT n
      """, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");

    var main = module!.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    var m = InstructionSelector.TrySelect(main, out var reason);
    Assert.That(m, Is.Not.Null, $"selection declined: {reason}");

    Assert.That(InlineAsmReservation.Compute(m!), Is.Empty,
      "the two asm statements are adjacent and nothing follows them, so CX is nobody's to hold");
  }

  /// <summary>
  /// ...and where one does, the reservation names exactly the register the text does - not the whole
  /// file, which would leave the intervening statement nowhere to compute.
  /// </summary>
  [Test]
  public void InlineAsm_GivenARegisterMustSurvive_ThenOnlyThatRegisterIsReserved() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize("""
      DIM n AS INTEGER
      DIM r AS INTEGER
      n = 0
      ! MOV CX, 5
      n = n + 1
      ! MOV r, CX
      PRINT n; r
      """, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");

    var main = module!.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    var m = InstructionSelector.TrySelect(main, out var reason);
    Assert.That(m, Is.Not.Null, $"selection declined: {reason}");

    var reserved = InlineAsmReservation.Compute(m!);
    Assert.That(reserved, Is.Not.Empty, "n = n + 1 sits between two statements that name CX");
    Assert.That(reserved.Values.SelectMany(r => r).Distinct(), Is.EqualTo(new[] { Compiler.Asm.Reg.CX }));
  }

  /// <summary>
  /// ...and it really is the ROUTED path doing it: the name binds to the block's address rather than
  /// leaving the whole statement unbindable, the selector hands the emitter a block offset rather
  /// than a frame cell, and the block reports itself address-taken - which is the property
  /// <see cref="SimplifyCfg"/> and <see cref="Sccp"/> consult before merging or dropping a block, and
  /// without it the label could be optimized out from under a jump nothing in the CFG shows.
  /// </summary>
  [Test]
  public void InlineAsm_GivenAJumpToABasicLabel_ThenTheTargetBlockIsAddressTakenAndSelectsAsABlockOffset() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize("""
      DIM n AS INTEGER
      n = 0
      ! MOV CX, 5
      AddLoop:
      n = n + 1
      ! DEC CX
      ! JNZ AddLoop
      PRINT n
      """, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");

    var main = module!.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    var jump = main.Blocks.SelectMany(b => b.Instructions).OfType<IrInlineAsm>()
      .Single(a => a.Text.Contains("JNZ", StringComparison.OrdinalIgnoreCase));

    Assert.That(jump.Routable, Is.True, "a label is a bound name, not an unknown one");
    Assert.That(jump.Names, Is.EqualTo(new[] { "AddLoop" }));
    Assert.That(jump.Operands.OfType<IrBlockAddress>().Single().Block,
      Is.SameAs(main.AddressTakenBlocks().Single()), "the target block, and it is address-taken");

    var m = InstructionSelector.TrySelect(main, out var reason);
    Assert.That(m, Is.Not.Null, $"selection declined: {reason}");
    var block = m!.AllInstructions.Single(i => i.Opcode == MOpcode.InlineAsm
      && ((MOperand.InlineAsmText)i.Operands[0]).Names.Contains("AddLoop"));
    Assert.That(block.Operands[1], Is.InstanceOf<MOperand.BlockOffset>(),
      "a jump target is a code label, not a frame cell");
    Assert.That(LinearScanAllocator.Allocate(m), Is.Not.Null, "and it allocates, so the function routes");
  }

  /// <summary>
  /// A name that is neither a variable nor a label of this scope still leaves the block unroutable -
  /// the equates and everything else the direct emitter resolves and this pass does not. Binding
  /// labels must not have turned "I do not know this name" into a silent guess.
  /// </summary>
  [Test]
  public void InlineAsm_GivenAnUnknownName_ThenTheBlockIsStillNotRoutable() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize("""
      %Limit = 4
      DIM n AS INTEGER
      ! MOV AX, %Limit
      ! MOV n, AX
      PRINT n
      """, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");

    var main = module!.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    Assert.That(InstructionSelector.TrySelect(main, out var reason), Is.Null);
    Assert.That(reason, Does.Contain("not a variable this pass could bind"));
  }

  /// <summary>
  /// The call target really does route rather than fall back to the direct emitter - which is the
  /// only thing that makes the assertion above about the ROUTED path mean anything.
  /// </summary>
  [Test]
  public void InlineAsm_GivenAnExportCalledByName_ThenTheBlockIsRoutableWithNoCellForIt() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize("""
      a$ = "XYZZY"
      ! push Word Ptr a$
      ! call GetStrLoc
      """, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");

    var main = module!.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    var asm = main.Blocks.SelectMany(b => b.Instructions).OfType<IrInlineAsm>().ToList();

    Assert.That(asm.Select(a => a.Routable), Is.All.True, "an export is a bound name, not an unknown one");
    var call = asm.Single(a => a.Text.Contains("GetStrLoc", StringComparison.OrdinalIgnoreCase));
    Assert.That(call.Names, Is.Empty, "code has no cell to pair the name with");
    Assert.That(InstructionSelector.TrySelect(main, out var reason), Is.Not.Null, $"selection declined: {reason}");
  }
}
