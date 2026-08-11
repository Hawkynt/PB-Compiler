using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// CHAIN through the retargetable path: the COMMON values written into the handoff file, and the
/// same values read back out of it by the image that is chained TO.
///
/// The two halves are separate pieces of code emitted in different places - the statement writes,
/// the module PROLOGUE reads - so testing one proves nothing about the other. They are tested here
/// as a round trip because that is the only thing the language promises: after
/// <c>CHAIN "T.EXE"</c>, the next pass sees what this one left.
///
/// <para>
/// The interpreter has no EXEC, so the run always ends on the <c>INT 21h/4Bh</c> that hands the
/// machine over - which is fine, because everything CHAIN promises has already happened by then.
/// The handoff file is lifted off the first machine's disk and put on the second's, which is
/// precisely what DOS does between two images.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendChainTests {

  /// <summary>
  /// A program that chains to ITSELF once: the first pass fills the COMMON block and hands over, the
  /// second pass finds <c>stage%</c> already set and reports what it was given. One INTEGER, one
  /// LONG, one DOUBLE and one dynamic STRING, because they travel through the handoff differently -
  /// the first three as the raw bytes of their cells, the string as a length word and its data.
  /// </summary>
  private const string _chainToSelf = """
    COMMON stage%, n&, d#, msg$
    IF stage% = 0 THEN
      stage% = 1
      n& = 123456
      d# = 2.5
      msg$ = "hello chain"
      PRINT "first pass"
      CHAIN "T.EXE"
    END IF
    PRINT "second pass"
    PRINT stage%; n&; d#
    PRINT msg$
    END
    """;

  private const string _handoffFile = "PBCHAIN.$$$";

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static (byte[] Image, IEnumerable<string> Routed) Compile(string source, bool backend) {
    var codegen = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = backend };
    var image = codegen.EmitExecutable();
    Assert.That(codegen.Errors, Is.Empty, string.Join("; ", codegen.Errors));
    return (image, codegen.BackendRoutedNames.ToList());
  }

  /// <summary>Runs the image twice, carrying the handoff file from the first pass into the second.</summary>
  private static (string First, byte[] Handoff, string Second) ChainToSelf(byte[] image) {
    var first = Cpu8086.Run(image, new Dictionary<string, byte[]>(), out var fault);
    Assert.That(fault, Is.Not.Null, "the first pass is expected to stop on the EXEC this interpreter has no answer for");
    var handoff = first.FileBytes(_handoffFile);
    Assert.That(handoff, Is.Not.Null, "CHAIN wrote no handoff file at all");

    var second = Cpu8086.Run(image, new Dictionary<string, byte[]> { [_handoffFile] = handoff! }, out var secondFault);
    Assert.That(secondFault, Is.Null, secondFault?.Message ?? "");
    return (first.Output, handoff!, second.Output);
  }

  /// <summary>
  /// The handoff is the COMMON block in DECLARATION order and nothing else: no names, no types, no
  /// padding. Pinning the exact bytes is what makes the two halves independent - a reader that
  /// happened to agree with a wrong writer would round-trip perfectly and still be wrong.
  /// </summary>
  private static byte[] ExpectedHandoff() {
    var bytes = new List<byte>();
    bytes.AddRange(BitConverter.GetBytes((short)1));                 // stage% (INTEGER, 2 bytes)
    bytes.AddRange(BitConverter.GetBytes(123456));                   // n& (LONG, 4 bytes)
    bytes.AddRange(BitConverter.GetBytes(2.5));                      // d# (DOUBLE, 8 IEEE bytes)
    bytes.AddRange(BitConverter.GetBytes((short)"hello chain".Length));
    bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("hello chain"));
    return [.. bytes];
  }

  [Test]
  public void Run_GivenARoutedChainToItself_ThenTheSecondPassSeesTheFirstPassCommonValues() {
    var (image, routed) = Compile(_chainToSelf, backend: true);
    Assert.That(routed, Does.Contain("main"), "the back end did not take the module body under test");

    var (first, handoff, second) = ChainToSelf(image);

    Assert.That(first.Trim(), Is.EqualTo("first pass"));
    Assert.That(handoff, Is.EqualTo(ExpectedHandoff()), "the COMMON block, in declaration order");
    // the values themselves, not merely "the same as the other back end": an INTEGER, a LONG, a
    // DOUBLE and a STRING all survived the handoff
    Assert.That(Lines(second), Is.EqualTo(new[] { "second pass", "1  123456  2.5", "hello chain" }));
  }

  [Test]
  public void Run_GivenAChainToItself_ThenTheRoutedPathAgreesWithTheDirectEmitter() {
    var (routedImage, routed) = Compile(_chainToSelf, backend: true);
    var (directImage, _) = Compile(_chainToSelf, backend: false);
    Assert.That(routed, Does.Contain("main"));

    var (directFirst, directHandoff, directSecond) = ChainToSelf(directImage);
    var (routedFirst, routedHandoff, routedSecond) = ChainToSelf(routedImage);

    Assert.That(routedHandoff, Is.EqualTo(directHandoff), "the two back ends wrote different handoffs");
    Assert.That(routedFirst, Is.EqualTo(directFirst));
    Assert.That(routedSecond, Is.EqualTo(directSecond));
  }

  /// <summary>
  /// Without a handoff on the disk there is nothing to absorb, and the prologue has to leave the
  /// COMMON cells alone rather than filling them from a file that is not there. That is the ordinary
  /// case - a program started from the command line - so it is the one that must not regress.
  /// </summary>
  [Test]
  public void Run_GivenNoHandoffFile_ThenTheRoutedPrologueLeavesTheCommonBlockAlone() {
    var (image, routed) = Compile(_chainToSelf, backend: true);
    Assert.That(routed, Does.Contain("main"));

    var cpu = Cpu8086.Run(image, new Dictionary<string, byte[]>(), out var fault);

    Assert.That(cpu.Output.Trim(), Is.EqualTo("first pass"), "stage% must still be its initial zero");
    Assert.That(fault, Is.Not.Null, "and the pass ends by chaining, as it did before");
  }

  /// <summary>
  /// A COMMON variable has to live in a DATA cell, not in the frame. <c>rt_chwrite</c> takes its
  /// buffer as a bare offset with DS assumed, so a frame slot would have been streamed out of the
  /// wrong segment - and the IR names that cell <c>g.&lt;name&gt;</c>, which is how the codegen
  /// resolves it to the very cell the direct emitter uses.
  /// </summary>
  [Test]
  public void Lower_GivenCommonVariables_ThenTheyBecomeModuleGlobalsRatherThanFrameSlots() {
    var module = IrLowering.TryLowerModule(Bind(_chainToSelf));
    Assert.That(module, Is.Not.Null, "outside the IR lowering's subset");

    Assert.That(module!.Globals.Select(g => g.Name),
      Is.SupersetOf(new[] { "g.stage", "g.n", "g.d", "g.msg" }));
  }

  /// <summary>
  /// The statement's shape: open, one transfer per COMMON variable in declaration order, close
  /// KEEPING the file, then hand over. The order is the whole protocol - the file carries no names.
  /// </summary>
  [Test]
  public void Lower_GivenChain_ThenItStreamsEveryCommonVariableInDeclarationOrder() {
    var module = IrLowering.TryLowerModule(Bind(_chainToSelf));
    var main = module!.Functions.First(f => f.Name == "main");

    var calls = main.Blocks
      .SelectMany(b => b.Instructions)
      .OfType<IrCall>()
      .Select(c => (c.Callee as IrFunction)?.Name)
      .Where(n => n is not null && n.StartsWith("rt_chain_", StringComparison.Ordinal))
      .ToList();

    Assert.That(calls, Is.EqualTo(new[] {
      // the prologue: absorb whatever the previous image left
      "rt_chain_open_read", "rt_chain_read", "rt_chain_read", "rt_chain_read",
      "rt_chain_read_str", "rt_chain_close",
      // ...and the statement: write the same four back out, in the same order
      "rt_chain_open_write", "rt_chain_write", "rt_chain_write", "rt_chain_write",
      "rt_chain_write_str", "rt_chain_close", "rt_chain_exec",
    }));
  }

  /// <summary>
  /// The ABI claim itself: <c>rt_chwrite</c> wants the buffer's OFFSET in DX and the byte count in
  /// CX. The offset is an immediate rather than a computed address because the routine assumes DS -
  /// so only a module-level cell may be handed to it, which is what <c>ArgKind.Offset</c> enforces.
  /// </summary>
  [Test]
  public void Select_GivenAChainWrite_ThenTheBufferOffsetIsInDxAndTheCountInCx() {
    var module = IrLowering.TryLowerModule(Bind(_chainToSelf));
    IrPassManager.Standard().RunOnModule(module!);
    var main = module!.Functions.First(f => f.Name == "main");
    var m = InstructionSelector.TrySelect(main, out var reason);
    Assert.That(m, Is.Not.Null, $"main declined: {reason}");

    var instructions = m!.AllInstructions.ToList();
    var call = instructions.First(i => i.Opcode == MOpcode.Call
      && i.Operands[0] is MOperand.LabelRef { Name: "rt_chwrite" });
    var staged = instructions
      .TakeWhile(i => i != call)
      .Where(i => i.Opcode == MOpcode.Mov && i.Operands[0] is MOperand.Register { Reg.IsVirtual: false })
      .GroupBy(i => ((MOperand.Register)i.Operands[0]).Reg.Physical)
      .ToDictionary(g => g.Key, g => g.Last().Operands[1]);

    Assert.That(staged[Reg.DX], Is.InstanceOf<MOperand.DataOffset>(), "DX = the ADDRESS of the cell");
    Assert.That(((MOperand.DataOffset)staged[Reg.DX]).Name, Is.EqualTo("g.stage"));
    Assert.That(((MOperand.Immediate)staged[Reg.CX]).Value, Is.EqualTo(2), "an INTEGER is two bytes");
  }

  /// <summary>
  /// RUN is CHAIN without the handoff: the same transfer of control, and nothing written. The
  /// prologue's read stays, because a program may be RUN by one image and CHAINed to by another.
  /// </summary>
  [Test]
  public void Lower_GivenRun_ThenNothingIsWrittenToTheHandoff() {
    var module = IrLowering.TryLowerModule(Bind("""
      COMMON stage%
      RUN "OTHER.EXE"
      """));
    var main = module!.Functions.First(f => f.Name == "main");

    var calls = main.Blocks
      .SelectMany(b => b.Instructions)
      .OfType<IrCall>()
      .Select(c => (c.Callee as IrFunction)?.Name)
      .Where(n => n is not null && n.StartsWith("rt_chain_", StringComparison.Ordinal))
      .ToList();

    Assert.That(calls, Does.Not.Contain("rt_chain_open_write"));
    Assert.That(calls, Does.Not.Contain("rt_chain_write"));
    Assert.That(calls, Does.Contain("rt_chain_exec"));
  }

  private static string[] Lines(string text)
    => text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(line => line.Trim()).ToArray();
}
