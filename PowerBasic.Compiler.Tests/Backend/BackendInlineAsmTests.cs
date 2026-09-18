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

  /// <summary>
  /// <c>! PUSH DI</c> and <c>! POP DI</c> around a block that wants <c>DI</c> is how a body borrows a
  /// register the compiler is using, and the pair promises nothing to anybody: read literally, though,
  /// the push USES <c>DI</c> and the pop DEFINES it, so the pop's "value" reaches round the loop to
  /// the next iteration's push and the runtime call in between is a destroyer.
  ///
  /// <para>
  /// The <c>MID$</c> is what makes this a test rather than a shape - a BASIC statement between the pop
  /// and the next push that really does destroy <c>DI</c>. Without it the window is empty and any
  /// model of the pair passes. This is <c>Vga_PatternFill</c> in the SVGA corpus, reduced.
  /// </para>
  /// </summary>
  [Test]
  public void InlineAsm_GivenASavedRegisterAroundALoopBody_ThenTheFunctionStillRoutes() {
    const string source = """
      DIM i AS INTEGER, v AS INTEGER, total AS INTEGER, s AS STRING
      s = "A"
      total = 0
      FOR i = 1 TO 3
        v = ASC(MID$(s, 1, 1)) + i
        ! PUSH DI
        ! MOV DI, v
        ! MOV AX, DI
        ! MOV v, AX
        ! POP DI
        total = total + v
      NEXT
      PRINT total
      """;

    var routed = Run(source, routed: true, out var ownsMain);
    Assert.That(ownsMain, Is.True, "the saved register must not decline the function");
    Assert.That(routed, Is.EqualTo(Run(source, routed: false)));
    Assert.That(routed, Is.EqualTo("201"), "66 + 67 + 68");
  }

  /// <summary>
  /// A second asm run opened with <c>! XOR DI, DI</c>, a <c>CALL</c> behind it, and the first run
  /// ending in <c>! POP DI</c>. Every <c>Vesa*_HLine</c> in the SVGA corpus is this shape, and the
  /// literal reading declined all sixteen: the <c>XOR</c> "reads" <c>DI</c>, so the value the earlier
  /// <c>POP</c> restored looked wanted, and the call in between destroys it.
  ///
  /// <para>
  /// The zeroing idiom names a register that is not an input. What makes this a test rather than a
  /// shape is the <c>CALL</c>: without one the window is empty and any model of the <c>XOR</c> passes.
  /// </para>
  /// </summary>
  [Test]
  public void InlineAsm_GivenTheZeroingIdiomAfterACall_ThenNoPromiseReachesBackAcrossIt() {
    const string source = """
      DECLARE FUNCTION Op%(BYVAL v%)
      DIM v AS INTEGER, w AS INTEGER
      v = 6
      ! PUSH DI
      ! MOV DI, v
      ! MOV w, DI
      ! POP DI
      v = Op%(w) + 1
      ! PUSH DI
      ! XOR DI, DI
      ! ADD DI, v
      ! MOV w, DI
      ! POP DI
      PRINT v; w

      FUNCTION Op%(BYVAL v%) NOINLINE
        Op% = v% * 2
      END FUNCTION
      """;

    var routed = Run(source, routed: true, out var ownsMain);
    Assert.That(ownsMain, Is.True, "the zeroing idiom consumes nothing, so nothing crosses the call");
    Assert.That(routed, Is.EqualTo(Run(source, routed: false)));
    Assert.That(routed, Is.EqualTo("13  13"), "6 doubled plus one, then zero plus that");
  }

  /// <summary>
  /// A <c>BYREF</c> parameter written AFTER an inline-asm block. The pointer arrives live at entry and
  /// is a memory base, which cannot spill, so it has to be reloaded from its own incoming cell at the
  /// use - and the spiller does exactly that. What stopped it was the reload it inserted claiming the
  /// whole register file: the scan for a pending call's argument staging collects clobber lists
  /// backwards, and an asm block declares every register, so walking past one reported all six as
  /// already filled. This is <c>Vga_GetPixel</c> in the SVGA corpus, reduced.
  /// </summary>
  [Test]
  public void InlineAsm_GivenAByRefResultWrittenAfterAnAsmBlock_ThenTheProcedureRoutes() {
    const string source = """
      DECLARE SUB GetPix(x_a AS WORD, y_a AS WORD, resultVal AS BYTE)
      DIM r AS BYTE
      CALL GetPix(10, 20, r)
      PRINT r

      SUB GetPix(x_a AS WORD, y_a AS WORD, resultVal AS BYTE)
        DIM x AS WORD, y AS WORD, PixelValue AS BYTE
        x = x_a : y = y_a
        ! MOV BX, y
        ! MOV CX, x
        ! ADD BX, CX
        ! MOV PixelValue, BL
        resultVal = PixelValue
      END SUB
      """;

    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true, UseExperimentalBackend = true };
    cg.EmitExecutable();

    Assert.That(cg.BackendRoutedNames, Does.Contain("GetPix").IgnoreCase,
      "the reload of the BYREF pointer must not claim the registers the asm block declares");
    Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)));
    Assert.That(Run(source, routed: true), Is.EqualTo("30"), "10 + 20, read back through the pointer");
  }

  /// <summary>
  /// <c>! MOV AL, 4</c> makes a promise about <c>AL</c> and about nothing else. Tracking both halves
  /// as <c>AX</c> - one resource, which is true of ALLOCATION and false of the text - turned the
  /// following <c>! MOV DX, AL</c> into a word-wide claim that reached back past the BASIC statement
  /// in the middle, and declined the function for destroying a register nothing wanted. This is
  /// <c>ModeX_GetPixel</c> in the SVGA corpus, reduced.
  /// </summary>
  [Test]
  public void InlineAsm_GivenAByteHalfSetAndRead_ThenTheOtherHalfIsNotClaimedWithIt() {
    const string source = """
      DIM v AS INTEGER, w AS INTEGER, s AS STRING
      s = "A"
      v = 40
      ! MOV AX, v
      ! SHR AX, 1
      ! MOV w, AX
      v = ASC(MID$(s, 1, 1)) + w
      ! MOV AL, 4
      ! MOV AH, 0
      ! MOV w, AX
      PRINT v; w
      """;

    var routed = Run(source, routed: true, out var ownsMain);
    Assert.That(ownsMain, Is.True, "the two halves define the word between them");
    Assert.That(routed, Is.EqualTo(Run(source, routed: false)));
    Assert.That(routed, Is.EqualTo("85  4"), "65 + 20, then the word the two halves built");
  }

  /// <summary>
  /// The same pair with the body's own control flow between its halves, which splits the run across
  /// blocks - every <c>Vesa*_HLine</c> in the corpus is written this way. Matching a save to its
  /// restore by stack depth is only sound where the span is CLOSED, and a label the body jumps to is
  /// exactly the thing that could open it.
  /// </summary>
  [Test]
  public void InlineAsm_GivenASavedRegisterSpanningALabel_ThenTheFunctionStillRoutes() {
    const string source = """
      DIM i AS INTEGER, v AS INTEGER, total AS INTEGER, s AS STRING
      s = "A"
      total = 0
      FOR i = 1 TO 3
        v = ASC(MID$(s, 1, 1)) + i
        ! PUSH DI
        ! MOV DI, v
        ! TEST DI, 1
        ! JZ RoundedUp
        ! INC DI
        RoundedUp:
        ! MOV AX, DI
        ! MOV v, AX
        ! POP DI
        total = total + v
      NEXT
      PRINT total
      """;

    var routed = Run(source, routed: true, out var ownsMain);
    Assert.That(ownsMain, Is.True, "the run spans a label, and is still one run");
    Assert.That(routed, Is.EqualTo(Run(source, routed: false)));
    Assert.That(routed, Is.EqualTo("202"), "66, then 67 rounded up to 68, then 68");
  }
  /// <summary>
  /// Whether a PROCEDURE routed, unoptimized. The fixture's other tests run the optimizer, which is
  /// what hid the defect below for as long as it did: the optimizer's own rewriting happened to move
  /// the block boundary out from between the save and its restore.
  /// </summary>
  private static (string Output, bool Routed) RunProcedure(string source, string procedure, bool routed) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = false, UseExperimentalBackend = routed };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return (Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|"),
      cg.BackendRoutedNames.Contains(procedure, StringComparer.OrdinalIgnoreCase));
  }

  /// <summary>
  /// An asm run whose own label splits it across machine BLOCKS, with a BASIC <c>CALL</c> between that
  /// run and the next one. Every <c>Vesa*_HLine</c> in the SVGA corpus is this shape - thirty-three
  /// declines, the largest single row in the mandatory-routing measurement.
  ///
  /// <para>
  /// The label ends a block, the block ends with a compiler <c>JMP</c>, and the <c>JMP</c> sat between
  /// <c>! PUSH DI</c> and <c>! POP DI</c> and ended the run - so the pair never cancelled, the
  /// <c>POP</c> read as a definition, the next <c>PUSH</c> read as a use of it, and the <c>CALL</c>
  /// between them destroyed the register. A branch moves no data and leaves <c>SP</c> where it found
  /// it; where it goes is the closed-region question, asked separately.
  /// </para>
  /// <para>
  /// The <c>CALL</c> is what makes this a test rather than a shape: without one the window between the
  /// pop and the next push is empty and any model of the pair passes.
  /// </para>
  /// </summary>
  [Test]
  public void InlineAsm_GivenARunSplitByItsOwnLabel_ThenTheSaveStillPairsAcrossTheBlock() {
    const string source = """
      DECLARE SUB Bump()
      DIM hits AS SHARED WORD
      DIM a(0 TO 7) AS SHARED BYTE
      CALL Fill(2, 3)
      PRINT a(0); a(1); a(2); a(4); a(5); a(6); hits
      END
      SUB Bump()
        hits = hits + 1
      END SUB
      SUB Fill(n AS WORD, m AS WORD)
        DIM p AS WORD, q AS WORD, lo AS WORD, hi AS WORD
        p = VARPTR(a(0))
        q = p + 4
        lo = n
        hi = m
        ! PUSH ES
        ! PUSH DI
        ! MOV AX, DS
        ! MOV ES, AX
        ! MOV DI, p
        ! MOV CX, lo
        ! MOV AL, 7
        ! CLD
        ! TEST DI, 1
        ! JZ FillAligned
        ! STOSB
        ! DEC CX
        ! JZ FillDone
        FillAligned:
        ! REP STOSB
        FillDone:
        ! POP DI
        ! POP ES
        CALL Bump
        ! PUSH ES
        ! PUSH DI
        ! MOV AX, DS
        ! MOV ES, AX
        ! MOV DI, q
        ! MOV CX, hi
        ! MOV AL, 9
        ! CLD
        ! REP STOSB
        ! POP DI
        ! POP ES
      END SUB
      """;

    var (routed, tookIt) = RunProcedure(source, "Fill", routed: true);
    Assert.That(tookIt, Is.True, "a label inside the run must not end it");
    Assert.That(routed, Is.EqualTo(RunProcedure(source, "Fill", routed: false).Output));
    Assert.That(routed, Is.EqualTo("7  7  0  9  9  9  1"));
  }
  /// <summary>
  /// <c>! REP MOVSB</c> inside a FOR loop. The prefix counts <c>CX</c> down to zero and reads no flag
  /// this pass models - the flag a string move really consumes is the DIRECTION flag, which nothing
  /// here writes and nothing here tracks.
  ///
  /// <para>
  /// Saying it read the arithmetic flags made every <c>REP MOVSB</c> the consumer of whatever last set
  /// them. Around a loop that is the loop's own increment, so the body's own <c>! ADD DI, n</c> became
  /// a promise the increment destroyed, and <c>Scroll_HardwareHorizontal</c> declined for it. Only the
  /// CONDITIONAL forms re-test <c>ZF</c>, and those still say so.
  /// </para>
  /// </summary>
  [Test]
  public void InlineAsm_GivenARepeatedMoveInALoop_ThenTheLoopIncrementIsNotADestroyer() {
    const string source = """
      DECLARE SUB Slide()
      DIM src(0 TO 7) AS SHARED BYTE
      DIM dst(0 TO 7) AS SHARED BYTE
      src(0) = 3 : src(1) = 5 : src(2) = 9
      Slide
      PRINT dst(0); dst(1); dst(2); dst(3)
      END
      SUB Slide()
        DIM i AS INTEGER, from AS WORD, into AS WORD
        FOR i = 0 TO 1
          from = VARPTR(src(0))
          into = VARPTR(dst(0))
          ! PUSH ES
          ! PUSH SI
          ! PUSH DI
          ! MOV AX, DS
          ! MOV ES, AX
          ! MOV SI, from
          ! MOV DI, into
          ! ADD DI, 1
          ! MOV CX, 3
          ! CLD
          ! REP MOVSB
          ! POP DI
          ! POP SI
          ! POP ES
        NEXT i
      END SUB
      """;

    var (routed, tookIt) = RunProcedure(source, "Slide", routed: true);
    Assert.That(tookIt, Is.True, "a repeated move in a loop must not read the increment's flags");
    Assert.That(routed, Is.EqualTo(RunProcedure(source, "Slide", routed: false).Output));
    Assert.That(routed, Is.EqualTo("0  3  5  9"));
  }
  /// <summary>
  /// <c>! PUSH BP</c> ... <c>! POP BP</c>, which is how a body that needs every register borrows the
  /// frame pointer too. The selector refused any asm writing <c>BP</c> or <c>SP</c>, and a POP of BP
  /// is a write by that reading.
  ///
  /// <para>
  /// It is not a write in the sense the check protects. The pop puts back what the push took, and
  /// nothing between them writes BP AT ALL - which is the condition, and is why `! MOV BP, v` still
  /// declines: while BP is borrowed the frame is unreachable, and every asm line naming a local is
  /// addressed through it. Here BP holds the frame at every instruction boundary. The
  /// direct emitter addresses its frame through BP as well and accepts the pair - refusing it here was
  /// stricter than the path being replaced. An unbalanced pop would destroy either emitter's frame, so
  /// such a program is broken rather than broken by this decision; what still declines is a write that
  /// is not a restore, <c>MOV BP, AX</c> and <c>ADD SP, n</c>.
  /// </para>
  /// </summary>
  [Test]
  public void InlineAsm_GivenASavedFramePointer_ThenTheFunctionStillRoutes() {
    const string source = """
      DECLARE SUB Borrow()
      DIM seen AS SHARED WORD
      Borrow
      PRINT seen
      END
      SUB Borrow()
        DIM v AS WORD
        v = 7
        ! PUSH BP
        ! PUSH ES
        ! MOV AX, DS
        ! MOV ES, AX
        ! POP ES
        ! POP BP
        v = v + 1
        seen = seen + v
      END SUB
      """;

    var (routed, tookIt) = RunProcedure(source, "Borrow", routed: true);
    Assert.That(tookIt, Is.True, "a saved and restored frame pointer must not decline the function");
    Assert.That(routed, Is.EqualTo(RunProcedure(source, "Borrow", routed: false).Output));
    Assert.That(routed, Is.EqualTo("8"), "v is still reachable through BP after the pair");
  }

  /// <summary>
  /// A BASIC statement standing between a row of <c>! POP</c>s and a statement the assembler cannot
  /// read. This is <c>Timer_InterruptHandler</c> in the SVGA corpus, and it declined with "a value is
  /// live across an instruction that clobbers every register, and cannot move to memory".
  ///
  /// <para>
  /// Nothing about the program is hard. The pops restore the caller's registers, the opaque statement
  /// afterwards is assumed to read every one of them, and backward liveness therefore carries a claim
  /// on the WHOLE allocatable file across the two moves in between - leaving the allocator no register
  /// for a load and an add. The claim is a guess: what an <c>INT</c> or a <c>CALL DWORD PTR</c> reads
  /// is exactly what the compiler does not know. The pushes cannot cancel the pops here either, because
  /// the opaque statement between them moves the stack by an unknown amount.
  /// </para>
  /// <para>
  /// A guess is worth a preference and not a refusal, so it is given up at the one point where the
  /// alternative is not a worse allocation but none - after the spiller has run out of moves. The
  /// reservations the text NAMES are unaffected, and the direct emitter holds neither kind: it loads
  /// this statement through <c>AX</c> and <c>DX</c> without asking.
  /// </para>
  /// </summary>
  [Test]
  public void InlineAsm_GivenABasicStatementBetweenRestoresAndAnOpaqueRead_ThenTheFunctionStillRoutes() {
    const string source = """
      DECLARE SUB Relay()
      DIM seed AS SHARED WORD
      DIM seen AS SHARED WORD
      seed = 40
      Relay
      PRINT seen
      END
      SUB Relay()
        DIM v AS WORD
        ! PUSH AX
        ! PUSH BX
        ! PUSH CX
        ! PUSH DX
        ! PUSH SI
        ! PUSH DI
        ! MOV AH, &H30
        ! INT &H21
        ! POP DI
        ! POP SI
        ! POP DX
        ! POP CX
        ! POP BX
        ! POP AX
        v = seed + 2
        ! MOV AH, &H30
        ! INT &H21
        seen = v
      END SUB
      """;

    var (routed, tookIt) = RunProcedure(source, "Relay", routed: true);
    Assert.That(tookIt, Is.True, "an inferred read of the whole file must not refuse the function");
    Assert.That(routed, Is.EqualTo(RunProcedure(source, "Relay", routed: false).Output));
    Assert.That(routed, Is.EqualTo("42"), "the statement between the two runs still computed");
  }
}
