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

  private static string Run(string source, bool routed) => Run(source, routed, out _);

  private static string Run(string source, bool routed, out bool ownsMain) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true, UseExperimentalBackend = routed };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    ownsMain = cg.BackendRoutedNames.Contains("main", StringComparer.OrdinalIgnoreCase);
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
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
      DIM total AS SHARED INTEGER
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
  /// The counter is a BASIC VARIABLE rather than CX, which keeps this test about the branch: whether
  /// a register survives the intervening BASIC statement is a separate promise, made by
  /// <see cref="InlineAsm_GivenARegisterHeldAcrossABasicStatement_ThenTheRoutedPathKeepsIt"/>.
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
  /// A register one <c>!</c> statement loads and a later one reads, with a BASIC statement in
  /// between - the promise an asm block could not make until it could say which registers it defines
  /// and for how long.
  ///
  /// <para>
  /// The two paths agree, and what matters is WHY. They used to agree because the routed side declined
  /// the whole function, so both numbers came from the same compiler; now the module body really is
  /// the back end's - asserted here, or this would go on passing the day something quietly took the
  /// routing away - and it keeps <c>CX</c> because the allocator was told the text is holding it,
  /// rather than because the direct emitter happens to compute through AX.
  /// </para>
  /// </summary>
  [Test]
  public void InlineAsm_GivenARegisterHeldAcrossABasicStatement_ThenTheRoutedPathKeepsIt() {
    const string source = """
      DIM n AS INTEGER
      DIM r AS INTEGER
      n = 0
      ! MOV CX, 5
      n = n + 1
      ! MOV r, CX
      PRINT n; r
      """;

    var routed = Run(source, routed: true, out var ownsMain);

    Assert.That(ownsMain, Is.True, "the back end compiled the module body, so the answer below is its own");
    Assert.That(routed, Is.EqualTo("1  5"), "the 5 the asm put in CX survived n = n + 1");
    Assert.That(Run(source, routed: false), Is.EqualTo(routed));
  }

  /// <summary>
  /// ...and what still declines: a register carried across something that DESTROYS it. A runtime call
  /// owns the whole caller-saved file, so no allocation can keep the 5 in <c>CX</c> over the
  /// <c>PRINT</c> - there is nothing to choose, and the function goes back to the direct emitter whole
  /// rather than being compiled to a guess.
  /// </summary>
  [Test]
  public void InlineAsm_GivenARegisterHeldAcrossACall_ThenAllocationDeclines() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize("""
      DIM n AS INTEGER
      DIM r AS INTEGER
      n = 7
      ! MOV CX, 5
      PRINT n
      ! MOV r, CX
      PRINT r
      """, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    IrPassManager.Standard().RunOnModule(module!);

    var main = module!.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    var m = InstructionSelector.TrySelect(main, out var selectionReason);
    Assert.That(m, Is.Not.Null, $"selection declined: {selectionReason}");

    MachineScheduler.Schedule(m!);
    Assert.That(LinearScanAllocator.Allocate(m!, out var reason), Is.Null);
    Assert.That(reason, Does.Contain("CX").And.Contain("destroys it"));
  }

  /// <summary>
  /// The flags are the same kind of promise as a register and are carried the same way - which is why
  /// the adjacent <c>! DEC c</c> / <c>! JNZ</c> pair above works. Put a comparison between them and
  /// the promise cannot be kept: nothing can be ALLOCATED to the flags, so there is no reservation to
  /// make and the function declines.
  ///
  /// <para>
  /// It takes a comparison, and that is worth knowing rather than incidental: a plain <c>n = n + 1</c>
  /// between the two is x87 loads and stores here, which leave the integer flags alone, so that
  /// program keeps its promise and routes.
  /// </para>
  /// </summary>
  [Test]
  public void InlineAsm_GivenFlagsHeldAcrossAComparison_ThenAllocationDeclines() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize("""
      DIM n AS INTEGER
      DIM c AS INTEGER
      n = 0
      c = 5
      AddLoop:
      ! DEC c
      IF n = 0 THEN n = 1
      ! JNZ AddLoop
      PRINT n
      """, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");

    var main = module!.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    var m = InstructionSelector.TrySelect(main, out var selectionReason);
    Assert.That(m, Is.Not.Null, $"selection declined: {selectionReason}");

    MachineScheduler.Schedule(m!);
    Assert.That(LinearScanAllocator.Allocate(m!, out var reason), Is.Null);
    Assert.That(reason, Does.Contain("flags"));
  }

  /// <summary>
  /// A block that writes <c>BP</c> declines at selection. <c>BP</c> is not a value in the register
  /// file, it is the frame every local, spill slot and parameter of a routed function is addressed
  /// through, so no allocation could honour such a block.
  /// </summary>
  [Test]
  public void InlineAsm_GivenAWriteToTheFramePointer_ThenSelectionDeclines() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize("""
      DIM n AS INTEGER
      n = 1
      ! MOV BP, AX
      PRINT n
      """, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");

    var main = module!.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    Assert.That(InstructionSelector.TrySelect(main, out var reason), Is.Null);
    Assert.That(reason, Does.Contain("BP or SP"));
  }

  /// <summary>
  /// The corpus program the whole promise was written for, compiled and run end to end on both paths.
  /// LOWLEVEL.BAS counts <c>CX</c> down across <c>n = n + 1</c> and prints the iteration count, so its
  /// second line reads 5 only if the countdown survived the BASIC statement - the routed path printed
  /// 1 for it, which is what a register the allocator felt free to reuse looks like from the outside.
  /// </summary>
  [Test]
  public void InlineAsm_GivenLowLevelBas_ThenTheBackEndOwnsItAndTheLoopStillRunsFiveTimes() {
    var root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
    var file = Path.Combine(root, "tests", "LOWLEVEL.BAS");
    Assume.That(File.Exists(file), $"no corpus program at {file}");
    var source = File.ReadAllText(file);

    var routed = Run(source, routed: true, out var ownsMain);

    Assert.That(ownsMain, Is.True, "the module body routes rather than falling back");
    Assert.That(routed.Split('|')[1].Trim(), Is.EqualTo("5"), "the asm countdown drove five BASIC iterations");
    Assert.That(routed, Is.EqualTo(Run(source, routed: false)));
    Assert.That(routed.Replace("|", "\n").Replace(" ", ""),
      Is.EqualTo(File.ReadAllText(Path.Combine(root, "tests", "LOWLEVEL.expected"))
        .Trim().Replace("\r\n", "\n").Replace(" ", "")),
      "...and the whole program still matches its golden output");
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

    // This program keeps its countdown in CX ACROSS `n = n + 1`, which used to decline the whole
    // function; the allocator now knows the text is holding CX there, so it selects and routes like
    // any other - and the jump target is still a code label rather than a frame cell.
    var m = InstructionSelector.TrySelect(main, out var reason);
    Assert.That(m, Is.Not.Null, $"selection declined: {reason}");
    var block = m!.AllInstructions.Single(i => i.Opcode == MOpcode.InlineAsm
      && ((MOperand.InlineAsmText)i.Operands[0]).Names.Contains("AddLoop"));
    Assert.That(block.Operands[1], Is.InstanceOf<MOperand.BlockOffset>(),
      "a jump target is a code label, not a frame cell");

    MachineScheduler.Schedule(m);
    Assert.That(LinearScanAllocator.Allocate(m, out var noRegisters), Is.Not.Null,
      $"and it allocates, so the function routes: {noRegisters}");
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
