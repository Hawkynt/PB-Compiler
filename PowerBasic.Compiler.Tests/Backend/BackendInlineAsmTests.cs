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

    // Allocation is a separate, still-open constraint: the block clobbers every register, so anything
    // live across it has to spill, and the allocator cannot yet place these. That is why the
    // behaviour tests below still exercise the FALLBACK rather than the routed path.
    Assert.That(LinearScanAllocator.Allocate(m), Is.Null,
      "when this starts succeeding, the tests below become routing tests and this line should go");
  }

  /// <summary>
  /// The asm writes a BASIC local and BASIC reads what it wrote. NOTE: this currently compares the
  /// direct emitter with itself, because the asm-containing function selects but does not yet
  /// allocate and so falls back - see the assertion above. It is kept because it is a real regression
  /// test either way, and it becomes a routing test for free once allocation lands.
  /// </summary>
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
}
