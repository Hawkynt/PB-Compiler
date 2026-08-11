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
  /// The counter is a BASIC VARIABLE rather than CX, and that is not incidental: a register set by
  /// one <c>!</c> statement does NOT survive an intervening BASIC statement on the routed path,
  /// because the allocator is free to put a temporary in it and has no way to know the asm cared.
  /// That is a separate defect, it has nothing to do with labels (a block with no label in it loses
  /// CX the same way), and it is pinned by
  /// <see cref="InlineAsm_GivenARegisterHeldAcrossABasicStatement_ThenTheTwoPathsDisagree"/> rather
  /// than smuggled into this reading of the branch.
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
  /// The open defect the test above steps around, written down so it fails the day it is fixed
  /// rather than being discovered by a program: a register an <c>!</c> statement loads is destroyed
  /// by the next BASIC statement on the routed path. The direct emitter computes through AX and so
  /// leaves CX alone by luck rather than by contract; the back end's allocator picks CX for a
  /// temporary and the asm's value is gone.
  ///
  /// <para>
  /// No label is involved - this is what binding one made REACHABLE for LOWLEVEL.BAS, not what it
  /// introduced. LOWLEVEL still declines before it can be bitten (a 32-bit LShr), so the corpus
  /// differential does not yet see this; whoever removes that decline must fix this first.
  /// </para>
  /// </summary>
  [Test]
  public void InlineAsm_GivenARegisterHeldAcrossABasicStatement_ThenTheTwoPathsDisagree() {
    const string source = """
      DIM n AS INTEGER
      DIM r AS INTEGER
      n = 0
      ! MOV CX, 5
      n = n + 1
      ! MOV r, CX
      PRINT n; r
      """;

    Assert.That(Run(source, routed: false), Is.EqualTo("1  5"), "the direct emitter happens to leave CX alone");
    Assert.That(Run(source, routed: true), Is.EqualTo("1  1"),
      "KNOWN DEFECT: the allocator put n+1 in CX. When this starts failing, the defect is fixed - "
      + "make it assert agreement instead of pinning the disagreement");
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
