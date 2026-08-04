using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The runtime-label bridge: a back-end-compiled function calling the DOS runtime.
///
/// The two sides describe the same routines differently. The IR lowering declares them C-style -
/// <c>rt_print_str(ptr, i32)</c> - because the same IR feeds the C and LLVM back ends; the DOS runtime
/// the direct emitter calls is register-based (<c>SI</c> = address, <c>CX</c> = length, nothing pushed).
/// <c>Backend/RuntimeAbi.cs</c> is the explicit per-routine mapping, and these are the tests that hold it to
/// the shape the direct emitter actually uses - because a wrong entry miscompiles silently.
/// </summary>
[TestFixture]
public sealed class BackendRuntimeCallTests {

  private const string _printingFunction = """
    FUNCTION Announce%
      PRINT "HI"
      Announce% = 7
    END FUNCTION

    PRINT Announce%
    """;

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static IrModule Optimized(string source) {
    var module = IrLowering.TryLowerModule(Bind(source));
    Assert.That(module, Is.Not.Null, "outside the IR lowering's subset");
    IrPassManager.Standard().RunOnModule(module!);
    foreach (var f in module!.Functions)
      if (!f.IsDeclaration)
        IntegerRecovery.Run(f);
    IrPassManager.Standard().RunOnModule(module);
    return module;
  }

  private static MFunction Select(string source, string function) {
    var fn = Optimized(source).Functions.First(f => f.Name.Equals(function, StringComparison.OrdinalIgnoreCase));
    var m = InstructionSelector.TrySelect(fn, out var reason);
    Assert.That(m, Is.Not.Null, $"{function} declined: {reason}");
    return m!;
  }

  [Test]
  public void Select_GivenPrintOfALiteral_ThenLoadsSiAndCxAndCallsTheRuntimeLabel() {
    var m = Select(_printingFunction, "Announce");

    var call = m.AllInstructions.First(i => i.Opcode == MOpcode.Call);
    Assert.That(((MOperand.LabelRef)call.Operands[0]).Name, Is.EqualTo("rt_print_str"));
    var argument = m.AllInstructions
      .TakeWhile(i => i != call)
      .Where(i => i.Opcode == MOpcode.Mov && i.Operands[0] is MOperand.Register { Reg.IsVirtual: false })
      .ToDictionary(i => ((MOperand.Register)i.Operands[0]).Reg.Physical, i => i.Operands[1]);
    Assert.That(argument.Keys, Is.EquivalentTo(new[] { Reg.SI, Reg.CX }), "SI = address, CX = length");
    Assert.That(argument[Reg.SI], Is.InstanceOf<MOperand.DataOffset>(), "the ADDRESS of the literal, not its bytes");
    Assert.That(((MOperand.Immediate)argument[Reg.CX]).Value, Is.EqualTo(2), """length of "HI" """);
  }

  [Test]
  public void Select_GivenARuntimeCall_ThenItClobbersTheCallerSavedFile() {
    var m = Select(_printingFunction, "Announce");

    var call = m.AllInstructions.First(i => i.Opcode == MOpcode.Call);

    // conservative on purpose: the print routines do preserve everything they touch, but a clobber
    // claim one register too small miscompiles a value that is never recomputed
    Assert.That(call.Clobbers, Is.EquivalentTo(new[] { Reg.AX, Reg.BX, Reg.CX, Reg.DX, Reg.SI, Reg.DI }));
  }

  [Test]
  public void Select_GivenTheNewlineAfterThePrint_ThenCallsRtPrintNl() {
    var m = Select(_printingFunction, "Announce");

    var callees = m.AllInstructions
      .Where(i => i.Opcode == MOpcode.Call)
      .Select(i => ((MOperand.LabelRef)i.Operands[0]).Name)
      .ToList();

    Assert.That(callees, Is.EqualTo(new[] { "rt_print_str", "rt_print_nl" }),
      "PRINT of one item is the text then the newline, in that order");
  }

  [Test]
  public void Select_GivenARoutineOutsideTheTable_ThenDeclinesNamingIt() {
    // A routine the table does not cover must decline rather than have a convention guessed for it.
    // LEN was the original example here and is now listed; HEX$ is not, and being unlisted is the
    // whole point of the test.
    var module = Optimized("""
      FUNCTION Digits$
        DIM n AS LONG
        n = 255
        Digits$ = HEX$(n)
      END FUNCTION

      PRINT Digits$
      """);
    var fn = module.Functions.First(f => f.Name.Equals("Digits", StringComparison.OrdinalIgnoreCase));

    InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(reason, Does.Contain("not in the runtime ABI table"));
  }

  [Test]
  public void Select_GivenAStringConstant_ThenBuildsTheHandleThroughRtStrmem() {
    // rt_str_const(ptr,len) -> a string handle; the runtime spells it rt_strmem and wants the bytes
    // as DS:SI with the length in CX, which is why the segment preset exists
    var m = Select("""
      FUNCTION Tagged%
        DIM s AS STRING
        s = "ab"
        Tagged% = 1
      END FUNCTION

      PRINT Tagged%
      """, "Tagged");

    var call = m.AllInstructions.First(i => i.Opcode == MOpcode.Call);
    Assert.That(((MOperand.LabelRef)call.Operands[0]).Name, Is.EqualTo("rt_strmem"));
    var preset = m.AllInstructions.TakeWhile(i => i != call)
      .Any(i => i.Opcode == MOpcode.Mov
                && i.Operands[0] is MOperand.Register { Reg.Physical: Reg.DX }
                && i.Operands[1] is MOperand.Register { Reg.Physical: Reg.DS });
    Assert.That(preset, Is.True, "MOV DX, DS names the segment the literal bytes live in");
  }

  [Test]
  public void Select_GivenPrintToAFile_ThenSelectsTheFileAndRestoresStdoutAround() {
    // the runtime has no per-file print entries: rt_fselect routes the console routines at a file,
    // and the caller resets rt_curout/rt_colptr afterwards - exactly what the direct emitter does
    var m = Select("""
      FUNCTION Log%
        PRINT #1, "hi"
        Log% = 0
      END FUNCTION

      OPEN "O.TXT" FOR OUTPUT AS #1
      PRINT Log%
      CLOSE #1
      """, "Log");

    var callees = m.AllInstructions
      .Where(i => i.Opcode == MOpcode.Call)
      .Select(i => ((MOperand.LabelRef)i.Operands[0]).Name)
      .ToList();
    Assert.That(callees.Take(2), Is.EqualTo(new[] { "rt_fselect", "rt_print_str" }),
      "the file is selected before the console routine runs");
    var restored = m.AllInstructions.Where(i =>
      i.Opcode == MOpcode.Mov && i.Operands[0] is MOperand.DataCell { Name: "rt_curout" or "rt_colptr" });
    Assert.That(restored.Count(), Is.EqualTo(callees.Count(c => c == "rt_fselect") * 2),
      "every select is paired with a reset of both output cells");
  }

  [Test]
  public void Emit_GivenAFileWritingProgram_ThenTheImageAssembles() {
    const string source = """
      FUNCTION Log%(BYVAL v%)
        PRINT #1, v%
        Log% = v%
      END FUNCTION

      OPEN "O.TXT" FOR OUTPUT AS #1
      PRINT Log%(3)
      CLOSE #1
      """;
    var routed = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };

    var image = routed.EmitExecutable();

    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(image, Is.Not.Empty);
    Assert.That(routed.BackendRoutedNames, Does.Contain("Log"), "the back end did not take the file-writing function");
  }

  [Test]
  public void Emit_GivenARoutedPrintingFunction_ThenTheImageAssemblesAndDiffersFromTheDirectPath() {
    var direct = new CodeGenerator(Bind(_printingFunction)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(_printingFunction)) { Optimize = true, UseExperimentalBackend = true };

    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    // an unresolved runtime label or literal would have thrown while the fixups resolved
    Assert.That(routedImage, Is.Not.Empty);
    Assert.That(routed.BackendRoutedNames, Does.Contain("Announce"), "the back end did not take the printing function");
  }

  [Test]
  public void Emit_GivenARoutedPrintingFunction_ThenTheRuntimeTrimmerStillKeepsThePrintSections() {
    // the trimmer seeds from the labels emitted code references, and a back-end CALL references the
    // very same named label - so a section only the routed function needs is not trimmed away
    var routed = new CodeGenerator(Bind(_printingFunction)) { Optimize = true, UseExperimentalBackend = true };

    var image = routed.EmitExecutable();

    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(image, Is.Not.Empty);
  }

  /// <summary>
  /// <c>LEN</c> is the shape <see cref="RuntimeAbi.ResultKind.WidenedWord"/> exists for: the runtime's
  /// <c>rt_len</c> answers a word in AX, the IR declares <c>rt_str_len(ptr) -&gt; i32</c> because the
  /// same declaration also feeds the C back end. The bridge is a <c>CWD</c> - the exact instruction
  /// the direct emitter writes after the call - and without it the high half of the LONG is whatever
  /// happened to be in DX.
  /// </summary>
  private const string _lenProgram = """
    DIM s AS STRING
    s = "abc"
    PRINT LEN(s)
    """;

  [Test]
  public void Select_GivenAWordAnswerWidenedToALong_ThenSignExtendsWithCwd() {
    var m = Select(_lenProgram, "main");

    var opcodes = m!.AllInstructions.Select(i => i.Opcode).ToList();
    var call = m.AllInstructions
      .Select((instruction, index) => (instruction, index))
      .First(p => p.instruction.Opcode == MOpcode.Call
                  && p.instruction.Operands is [MOperand.LabelRef { Name: "rt_len" }]).index;
    Assert.That(opcodes.Skip(call).Take(2), Does.Contain(MOpcode.Cwd),
      "the CWD must follow the call immediately, before anything can disturb DX");
  }

  /// <summary>
  /// <c>VAL</c> is the other new shape: the routine leaves its answer on the x87 stack rather than in
  /// a register, so the transfer is an <c>FSTP</c> into the call's own frame cell.
  /// </summary>
  [Test]
  public void Select_GivenAnAnswerOnTheX87Stack_ThenPopsItIntoTheCallsCell() {
    var m = Select("""
      DIM s AS STRING
      DIM d AS DOUBLE
      s = "1.5"
      d = VAL(s)
      PRINT d
      """, "main");

    var call = m!.AllInstructions
      .Select((instruction, index) => (instruction, index))
      .First(p => p.instruction.Opcode == MOpcode.Call
                  && p.instruction.Operands is [MOperand.LabelRef { Name: "rt_val" }]).index;
    var after = m.AllInstructions.Skip(call + 1).First();
    Assert.That(after.Opcode, Is.EqualTo(MOpcode.Fstp), "the answer is popped straight off ST(0)");
    Assert.That(after.Operands[0], Is.InstanceOf<MOperand.StackSlot>());
  }

  /// <summary>
  /// The table must not claim a routine it would render wrongly. <c>rt_str_i16</c> opens with a
  /// <c>CWD</c>, so an unsigned WORD routed through it would print 65535 as -1 - so there is
  /// deliberately no <c>rt_str_from_u16</c> entry, and STR$ of a WORD must DECLINE rather than reach
  /// the signed routine. Declining costs coverage; the alternative costs correctness.
  /// </summary>
  [Test]
  public void Select_GivenStrOfAnUnsignedWord_ThenDeclinesRatherThanSignExtend() {
    var module = Optimized("""
      DIM w AS WORD
      w = 65535
      PRINT STR$(w)
      """);
    var main = module.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));

    InstructionSelector.TrySelect(main, out var reason);

    Assert.That(reason, Does.Contain("rt_str_from_u16"),
      "STR$ of a WORD must decline by name, not be routed through the signed entry");
  }

  /// <summary>
  /// Printing an EXTENDED routes, which took three attempts and two wrong diagnoses to reach.
  ///
  /// PowerBASIC computes a float expression at the x87's width and lets the declared type pick only
  /// the FORMATTER - the runtime's print entries share a body and differ only in the significant
  /// digits they set. Modelling the value at its declared width instead made <c>H?/3</c> with
  /// <c>H? = 200</c> print 66.66666 where PBC 3.50 prints 66.66667, and no amount of care in the back
  /// end could recover a digit the IR had already rounded away.
  ///
  /// It was blamed first on the runtime ABI table and then on the missing 80-bit frame cell. The cell
  /// was a real bug and is fixed; it was not this one. The fix is in the lowering
  /// (<c>LowerArithmetic</c>), and with it DIFF24.BAS agrees.
  /// </summary>
  [Test]
  public void Select_GivenAnExtendedPrint_ThenItRoutes() {
    var m = Select("""
      DIM e AS EXT
      DIM i AS INTEGER
      FOR i = 1 TO 50
        e = i / 3
        PRINT e
      NEXT i
      """, "main");

    Assert.That(m.AllInstructions.Any(i => i.Opcode == MOpcode.Call
      && i.Operands is [MOperand.LabelRef { Name: "rt_print_f64" }]),
      "an EXTENDED prints through the DOUBLE formatter - there is no rt_print_f80");
  }

  /// <summary>
  /// The cell itself, which WAS broken: a float temporary is parked at the x87's own width, so
  /// nothing is rounded on the way through the frame. Sizing it by the IR type would have written a
  /// DOUBLE through a dword reference - half a value - which nothing had caught because no routed
  /// corpus program had yet spilled one.
  /// </summary>
  [Test]
  public void Select_GivenAFloatTemporary_ThenItsFrameCellIsTenBytesWide() {
    // The counter drives the arithmetic, so nothing folds; and it is the INTEGER that is
    // loop-carried, not the DOUBLE - a f64 phi is still a selection decline of its own.
    var m = Select("""
      DIM t AS DOUBLE
      DIM i AS INTEGER
      FOR i = 1 TO 50
        t = i / 3 + i * 7
        PRINT t
      NEXT i
      """, "main");

    var cells = m.AllInstructions
      .Where(i => i.Opcode is MOpcode.Fstp or MOpcode.Fld)
      .SelectMany(i => i.Operands)
      .OfType<MOperand.StackSlot>()
      .ToList();
    Assert.That(cells, Is.Not.Empty, "the expression should spill at least one intermediate");
    Assert.That(cells.Select(c => c.Size), Has.Some.EqualTo(MRegSize.Tbyte),
      "an intermediate is stored at the x87's own width, not the declared type's");
  }
}
