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

  private const string _remainingStringKernelsProgram = """
    DECLARE FUNCTION StringOps%(BYVAL start%, BYVAL count%)

    PRINT StringOps%(2, 3)
    END

    FUNCTION StringOps%(BYVAL start%, BYVAL count%) NOINLINE
      DIM value AS STRING
      value = "abcdef"
      PRINT MID$(value, start%)
      IF MID$(value, start%, 1) < "z" THEN PRINT "less"
      MID$(value, start%, count%) = "XYZ"
      PRINT value
      StringOps% = 7
    END FUNCTION
    """;

  private const string _binaryRecordProgram = """
    i% = -12345
    mi$ = MKI$(i%)
    PRINT CVI(mi$)
    PRINT ASC(mi$, 1); ASC(mi$, 2)

    l& = -123456789
    ml$ = MKL$(l&)
    PRINT CVL(ml$)
    PRINT ASC(ml$, 1); ASC(ml$, 2); ASC(ml$, 3); ASC(ml$, 4)

    dw??? = 3000000000
    mdw$ = MKDWD$(dw???)
    PRINT CVDWD(mdw$)
    PRINT ASC(mdw$, 1); ASC(mdw$, 2); ASC(mdw$, 3); ASC(mdw$, 4)

    s! = 3.5
    ms$ = MKS$(s!)
    PRINT CVS(ms$)
    PRINT ASC(ms$, 1); ASC(ms$, 2); ASC(ms$, 3); ASC(ms$, 4)

    d# = 2.5
    md$ = MKD$(d#)
    PRINT CVD(md$)
    PRINT ASC(md$, 7); ASC(md$, 8)
    """;

  private const string _binaryRecordAliasProgram = """
    ok% = -1
    IF CVBYT(MKBYT$(200)) <> 200 THEN ok% = 0
    IF CVWRD(MKWRD$(50000)) <> 50000 THEN ok% = 0
    IF CVE(MKE$(2.5)) <> 2.5 THEN ok% = 0
    IF CVBYT("x" + MKBYT$(200), 2) <> 200 THEN ok% = 0
    IF CVWRD("x" + MKWRD$(50000), 2) <> 50000 THEN ok% = 0
    IF CVE("x" + MKE$(2.5), 2) <> 2.5 THEN ok% = 0
    PRINT ok%
    """;

  private const string _rndRangeProgram = """
    ok% = -1
    FOR i% = 1 TO 20
      value& = RND(-5, 10)
      IF value& < -5 OR value& > 10 THEN ok% = 0
    NEXT i%
    PRINT ok%
    """;

  private const string _udtCompareProgram = """
    TYPE Pair
      A AS INTEGER
      B AS LONG
    END TYPE
    DECLARE SUB KeepPair(value AS Pair)
    DIM leftValue AS Pair
    DIM rightValue AS Pair
    leftValue.A = 1
    leftValue.B = 70000
    rightValue.A = 1
    rightValue.B = 70000
    PRINT leftValue = rightValue
    rightValue.B = 70001
    PRINT leftValue <> rightValue
    KeepPair leftValue
    END

    SUB KeepPair(value AS Pair) NOINLINE
    END SUB
    """;

  private const string _localUdtCompareProgram = """
    TYPE Pair
      A AS INTEGER
      B AS LONG
    END TYPE
    DECLARE FUNCTION LocalMatch%
    PRINT LocalMatch%
    END

    FUNCTION LocalMatch% NOINLINE
      DIM leftValue AS Pair
      DIM rightValue AS Pair
      leftValue.A = 12
      rightValue.A = 12
      LocalMatch% = leftValue = rightValue
    END FUNCTION
    """;

  private const string _udtCopyProgram = """
    TYPE Odd7
      A AS INTEGER
      B AS LONG
      C AS BYTE
    END TYPE
    DECLARE SUB KeepOdd7(value AS Odd7)
    DIM sourceValue AS Odd7
    DIM copiedValue AS Odd7
    sourceValue.A = -123
    sourceValue.B = 987654
    sourceValue.C = 250
    copiedValue = sourceValue
    PRINT copiedValue.A; copiedValue.B; copiedValue.C
    KeepOdd7 copiedValue
    END

    SUB KeepOdd7(value AS Odd7) NOINLINE
    END SUB
    """;

  private const string _staticEraseProgram = """
    DIM values(1 TO 5) AS INTEGER
    FOR i% = 1 TO 5
      values(i%) = i% * 10
    NEXT i%
    ERASE values
    PRINT values(1); values(3); values(5)
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
  public void Select_GivenNumericPrint_ThenItsVerifiedIndexRegistersRemainLive() {
    var m = Select(_printingFunction, "main");
    var calls = m.AllInstructions
      .Where(instruction => instruction.Opcode == MOpcode.Call
        && instruction.Operands is [MOperand.LabelRef { Name: "rt_print_i16" or "rt_print_nl" }])
      .ToList();

    Assert.That(calls, Is.Not.Empty);
    Assert.That(calls, Has.All.Matches<MInstr>(instruction =>
      instruction.Clobbers.Contains(Reg.AX)
      && instruction.Clobbers.Contains(Reg.DX)
      && !instruction.Clobbers.Contains(Reg.SI)
      && !instruction.Clobbers.Contains(Reg.DI)),
      "the hand-written numeric printers save/restore SI and DI around their work");
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

  /// <summary>
  /// A routine the table does not cover must DECLINE rather than have a convention guessed for it.
  ///
  /// The callee is deliberately fictitious. This test was written three times against a real routine -
  /// LEN, then HEX$, then STRING$ - and each time the routine was listed and the test started failing
  /// for the best possible reason. What is under test is the RULE, not any routine, so the rule gets a
  /// name no runtime will ever have.
  /// </summary>
  [Test]
  public void Select_GivenARoutineOutsideTheTable_ThenDeclinesNamingIt() {
    var module = new IrModule("t");
    var unknown = module.AddFunction(new IrFunction("rt_no_such_routine", IrType.Void, [new IrArgument(IrType.I16, 0)]));
    var fn = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    entry.Append(new IrCall(IrType.Void, unknown, [new IrConstantInt(IrType.I16, 1)]));
    entry.Append(new IrRet());

    InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(reason, Does.Contain("not in the runtime ABI table"));
    Assert.That(reason, Does.Contain("rt_no_such_routine"), "and it names which one");
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

  /// <summary>
  /// Every label the bridge names has to be one the DOS runtime really defines.
  ///
  /// <para>
  /// <c>Assembler.Lbl</c> MINTS a label for any name, so a wrong or stale row here used to hand the
  /// emitter a perfectly good <c>Label</c> that nothing would ever bind, and the failure surfaced as
  /// "referenced but never bound" while the fixups resolved - after every routing decision, naming a
  /// symbol rather than the row that invented it, and taking the whole compilation with it.
  /// <c>CalleeLabel</c> now asks the runtime first and declines when the answer is no, which costs one
  /// function; this asks the same question over the whole table, where a stale row fails a test the
  /// moment it is written rather than the first time a program happens to call it.
  /// </para>
  /// </summary>
  [Test]
  public void RuntimeAbi_WhenCheckedAgainstTheRuntime_ThenEveryLabelItNamesIsDefined() {
    Assert.That(CodeGenerator.UnboundRuntimeCallees, Is.Empty,
      "RuntimeAbi names routines the DOS runtime does not define; a call to one would fail at link "
      + "time, and the routed function that made it now declines instead");
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

  [Test]
  public void Select_GivenRemainingStringKernels_ThenUsesTheirExactDosRegisterConventions() {
    var m = Select(_remainingStringKernelsProgram, "StringOps");
    var instructions = m.AllInstructions.ToList();

    var mid = instructions.FindIndex(i => i.Opcode == MOpcode.Call
      && i.Operands is [MOperand.LabelRef { Name: "rt_strmid" }]);
    Assert.That(mid, Is.GreaterThan(0));
    var midArgs = instructions.Take(mid)
      .Where(IsPhysicalMove)
      .TakeLast(3)
      .ToDictionary(Destination, i => i.Operands[1]);
    Assert.That(midArgs.Keys, Is.EquivalentTo(new[] { Reg.AX, Reg.CX, Reg.DX }));
    Assert.That(midArgs[Reg.DX], Is.EqualTo(new MOperand.Immediate(0x7FFF)),
      "MID$(s, start) uses the direct emitter's maximum-length preset");

    var compare = instructions.FindIndex(i => i.Opcode == MOpcode.Call
      && i.Operands is [MOperand.LabelRef { Name: "rt_strcmp" }]);
    Assert.That(compare, Is.GreaterThan(mid));
    var compareArgs = instructions.Take(compare)
      .Where(IsPhysicalMove)
      .TakeLast(2)
      .Select(Destination);
    Assert.That(compareArgs, Is.EquivalentTo(new[] { Reg.AX, Reg.DX }));
    Assert.That(instructions[compare + 1].Opcode, Is.EqualTo(MOpcode.Cwd),
      "the runtime's -1/0/1 word answer is sign-extended to the IR's i32");

    var midSet = instructions.FindIndex(i => i.Opcode == MOpcode.Call
      && i.Operands is [MOperand.LabelRef { Name: "rt_midset" }]);
    Assert.That(midSet, Is.GreaterThan(compare));
    var midSetArgs = instructions.Take(midSet)
      .Where(IsPhysicalMove)
      .TakeLast(4)
      .Select(Destination);
    Assert.That(midSetArgs, Is.EquivalentTo(new[] { Reg.AX, Reg.BX, Reg.CX, Reg.DX }),
      "target/start/limit/replacement map to AX/CX/BX/DX");
    Assert.That(instructions[midSet + 1].Operands,
      Does.Contain(new MOperand.Register(MReg.Physical_(Reg.AX, MRegSize.Word))),
      "MidSet returns the unchanged target handle in AX");

    static bool IsPhysicalMove(MInstr instruction) => instruction.Opcode == MOpcode.Mov
      && instruction.Operands[0] is MOperand.Register { Reg.IsVirtual: false };
    static Reg Destination(MInstr instruction) => ((MOperand.Register)instruction.Operands[0]).Reg.Physical;
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenRemainingStringKernels_ThenRoutedBehaviorMatchesTheDirectEmitter(bool optimize) {
    var direct = new CodeGenerator(Bind(_remainingStringKernelsProgram)) {
      Optimize = optimize,
      UseExperimentalBackend = false,
    };
    var routed = new CodeGenerator(Bind(_remainingStringKernelsProgram)) {
      Optimize = optimize,
      UseExperimentalBackend = true,
    };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("StringOps"));
    Assert.That(routed.BackendRoutedNames, Does.Contain("main"));
    Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
  }

  [Test]
  public void Select_GivenBinaryRecordConversions_ThenUsesWidthExactRuntimeEntries() {
    var m = Select(_binaryRecordProgram, "main");
    var callees = m.AllInstructions
      .Where(instruction => instruction.Opcode == MOpcode.Call)
      .Select(instruction => ((MOperand.LabelRef)instruction.Operands[0]).Name)
      .ToList();

    Assert.That(callees, Does.Contain("rt_mki"));
    Assert.That(callees, Does.Contain("rt_mkl"));
    Assert.That(callees, Does.Contain("rt_mkdwd"));
    Assert.That(callees, Does.Contain("rt_mks"));
    Assert.That(callees, Does.Contain("rt_mkd"));
    Assert.That(callees.Count(name => name == "rt_cv"), Is.EqualTo(5));
    Assert.That(m.AllInstructions.Count(instruction => instruction.Opcode == MOpcode.Fld),
      Is.GreaterThanOrEqualTo(4), "MKS/MKD arguments and CVS/CVD results cross the x87 at exact widths");
  }

  [Test]
  public void Select_GivenCviWithANonWordResult_ThenDeclinesTheMismatchedScratchLoad() {
    var cvi = new IrFunction("rt_str_cvi", IrType.I8, [new IrArgument(IrType.I16, 0)]);
    var fn = new IrFunction("main", IrType.Void);
    var entry = fn.CreateBlock("entry");
    entry.Append(new IrCall(IrType.I8, cvi, [new IrConstantInt(IrType.I16, 0)]));
    entry.Append(new IrRet());

    InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(reason, Does.Contain("scratch word"));
    Assert.That(reason, Does.Contain("i8"));
  }

  [Test]
  public void Select_GivenBinaryRecordAliases_ThenEveryAliasUsesTheExactWidthRuntimeEntry() {
    var m = Select(_binaryRecordAliasProgram, "main");
    var callees = m.AllInstructions
      .Where(instruction => instruction.Opcode == MOpcode.Call)
      .Select(instruction => ((MOperand.LabelRef)instruction.Operands[0]).Name)
      .ToList();

    Assert.That(callees, Does.Contain("rt_mkbyt"));
    Assert.That(callees, Does.Contain("rt_mki"));
    Assert.That(callees, Does.Contain("rt_mkd"));
    Assert.That(callees.Count(name => name == "rt_cv"), Is.EqualTo(6));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenBinaryRecordAliases_ThenRoutedBehaviorMatchesTheDirectEmitter(bool optimize) {
    var direct = new CodeGenerator(Bind(_binaryRecordAliasProgram)) {
      Optimize = optimize,
      UseExperimentalBackend = false,
    };
    var routed = new CodeGenerator(Bind(_binaryRecordAliasProgram)) {
      Optimize = optimize,
      UseExperimentalBackend = true,
    };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("main"));
    Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
    Assert.That(routedCpu.Output.Trim(), Is.EqualTo("-1"));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenBinaryRecordConversions_ThenRoutedBytesMatchTheDirectEmitter(bool optimize) {
    var direct = new CodeGenerator(Bind(_binaryRecordProgram)) {
      Optimize = optimize,
      UseExperimentalBackend = false,
    };
    var routed = new CodeGenerator(Bind(_binaryRecordProgram)) {
      Optimize = optimize,
      UseExperimentalBackend = true,
    };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("main"),
      "the test is meaningful only when every conversion went through the IR back end");
    Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
  }

  [Test]
  public void Select_GivenRuntimePairResults_ThenCopiesDxAxIntoTheVirtualPair() {
    var m = Select(_rndRangeProgram, "main");
    var instructions = m.AllInstructions.ToList();
    var call = instructions.FindIndex(instruction => instruction.Opcode == MOpcode.Call
      && instruction.Operands is [MOperand.LabelRef { Name: "rt_rndrange" }]);

    Assert.That(call, Is.GreaterThanOrEqualTo(0));
    Assert.That(instructions.Skip(call + 1).Take(2).All(instruction => instruction.Opcode == MOpcode.Mov),
      "the DX:AX answer must be copied before either physical result register can be clobbered");
    Assert.That(instructions.Skip(call + 1).Take(2)
      .Select(instruction => ((MOperand.Register)instruction.Operands[1]).Reg.Physical),
      Is.EqualTo(new[] { Reg.AX, Reg.DX }));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenRndRange_ThenTheRoutedPairResultMatchesTheDirectEmitter(bool optimize) {
    var direct = new CodeGenerator(Bind(_rndRangeProgram)) {
      Optimize = optimize,
      UseExperimentalBackend = false,
    };
    var routed = new CodeGenerator(Bind(_rndRangeProgram)) {
      Optimize = optimize,
      UseExperimentalBackend = true,
    };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("main"));
    Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
    Assert.That(routedCpu.Output.Trim(), Is.EqualTo("-1"));
  }

  [Test]
  public void Select_GivenFileLengthAndPosition_ThenTheirDxAxResultsRoute() {
    var m = Select("""
      OPEN "O.TXT" FOR OUTPUT AS #1
      PRINT LOF(1)
      PRINT LOC(1)
      CLOSE #1
      """, "main");
    var callees = m.AllInstructions
      .Where(instruction => instruction.Opcode == MOpcode.Call)
      .Select(instruction => ((MOperand.LabelRef)instruction.Operands[0]).Name);

    Assert.That(callees, Does.Contain("rt_lof"));
    Assert.That(callees, Does.Contain("rt_fpos"));
  }

  [Test]
  public void Select_GivenWholeUdtComparison_ThenPassesBothSegmentedAddressesToMemCompare() {
    var m = Select(_udtCompareProgram, "main");
    var instructions = m.AllInstructions.ToList();
    var call = instructions.FindIndex(instruction => instruction.Opcode == MOpcode.Call
      && instruction.Operands is [MOperand.LabelRef { Name: "rt_memcmp" }]);

    Assert.That(call, Is.GreaterThanOrEqualTo(0));
    var physicalDestinations = instructions.Take(call)
      .Where(instruction => instruction.Opcode == MOpcode.Mov
        && instruction.Operands[0] is MOperand.Register { Reg.IsVirtual: false })
      .Select(instruction => ((MOperand.Register)instruction.Operands[0]).Reg.Physical);
    Assert.That(physicalDestinations, Does.Contain(Reg.SI));
    Assert.That(physicalDestinations, Does.Contain(Reg.DX));
    Assert.That(physicalDestinations, Does.Contain(Reg.DI));
    Assert.That(physicalDestinations, Does.Contain(Reg.BX));
    Assert.That(physicalDestinations, Does.Contain(Reg.CX));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenWholeUdtComparison_ThenRoutedBytesMatchTheDirectEmitter(bool optimize) {
    var direct = new CodeGenerator(Bind(_udtCompareProgram)) {
      Optimize = optimize,
      UseExperimentalBackend = false,
    };
    var routed = new CodeGenerator(Bind(_udtCompareProgram)) {
      Optimize = optimize,
      UseExperimentalBackend = true,
    };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("main"));
    Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
    Assert.That(routedCpu.Output.Trim().Replace("\r\n", "|").Replace(" ", ""), Is.EqualTo("-1|-1"));
  }

  [Test]
  public void Select_GivenStackLocalUdtComparison_ThenPassesSsForBothPointerSegments() {
    var m = Select(_localUdtCompareProgram, "LocalMatch");
    var stackSegments = m.AllInstructions
      .Where(instruction => instruction.Opcode == MOpcode.Mov
        && instruction.Operands is [MOperand.Register { Reg.Physical: Reg.DX or Reg.BX },
          MOperand.Register { Reg.Physical: Reg.SS }]);

    Assert.That(stackSegments, Has.Exactly(2).Items);
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenStackLocalUdtComparison_ThenRoutedBytesMatchTheDirectEmitter(bool optimize) {
    var direct = new CodeGenerator(Bind(_localUdtCompareProgram)) {
      Optimize = optimize,
      UseExperimentalBackend = false,
    };
    var routed = new CodeGenerator(Bind(_localUdtCompareProgram)) {
      Optimize = optimize,
      UseExperimentalBackend = true,
    };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("LocalMatch"));
    Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
  }

  [Test]
  public void Select_GivenWholeUdtCopy_ThenUsesTheSegmentedMemoryCopyKernel() {
    var m = Select(_udtCopyProgram, "main");

    Assert.That(m.AllInstructions, Has.Some.Matches<MInstr>(instruction => instruction.Opcode == MOpcode.Call
      && instruction.Operands is [MOperand.LabelRef { Name: "rt_memcpy" }]));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenOddSizedUdtCopy_ThenRoutedTailByteMatchesTheDirectEmitter(bool optimize) {
    var direct = new CodeGenerator(Bind(_udtCopyProgram)) {
      Optimize = optimize,
      UseExperimentalBackend = false,
    };
    var routed = new CodeGenerator(Bind(_udtCopyProgram)) {
      Optimize = optimize,
      UseExperimentalBackend = true,
    };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("main"));
    Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
  }

  [Test]
  public void Execute_GivenOddSizedUdtCopyUnderCpu386_ThenRoutedDwordAndTailMatchTheDirectEmitter() {
    var source = "$CPU 80386\n$OPTIMIZE SPEED\n" + _udtCopyProgram;
    var direct = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };

    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();
    var directCpu = Cpu8086.Run(directImage);
    var routedCpu = Cpu8086.Run(routedImage);

    Assert.Multiple(() => {
      Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
      Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
      Assert.That(routed.BackendRoutedNames, Does.Contain("main"));
      Assert.That(Contains(routedImage, 0xF3, 0x66, 0xA5), Is.True,
        "the routed rt_memcpy should widen its seven-byte copy to one DWORD and a three-byte tail");
      Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
      Assert.That(routedCpu.Output.Trim().Replace(" ", ""), Is.EqualTo("-123987654250"));
    });
  }

  [TestCase("$OPTIMIZE SPEED\n", false, TestName = "Copy_Given8086Speed_ThenNoRepMovsd")]
  [TestCase("$CPU 80386\n$OPTIMIZE OFF\n", false, TestName = "Copy_Given386OptimizeOff_ThenNoRepMovsd")]
  [TestCase("$CPU 80386\n$OPTIMIZE SPEED\n", true, TestName = "Copy_Given386Speed_ThenRepMovsd")]
  public void Emit_GivenUdtCopy_WhenTargetChanges_ThenDwordCopyIsGated(string directives, bool expected) {
    var generator = new CodeGenerator(Bind(directives + _udtCopyProgram)) {
      UseExperimentalBackend = true,
    };

    var image = generator.EmitExecutable();

    Assert.Multiple(() => {
      Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
      Assert.That(generator.BackendRoutedNames, Does.Contain("main"));
      Assert.That(Contains(image, 0xF3, 0x66, 0xA5), Is.EqualTo(expected));
    });
  }

  private static bool Contains(byte[] image, params byte[] sequence) {
    for (var i = 0; i <= image.Length - sequence.Length; ++i)
      if (image.AsSpan(i, sequence.Length).SequenceEqual(sequence))
        return true;
    return false;
  }

  [Test]
  public void Select_GivenStaticArrayErase_ThenUsesTheSegmentedMemoryFillKernel() {
    var m = Select(_staticEraseProgram, "main");

    Assert.That(m.AllInstructions, Has.Some.Matches<MInstr>(instruction => instruction.Opcode == MOpcode.Call
      && instruction.Operands is [MOperand.LabelRef { Name: "rt_memset" }]));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Execute_GivenStaticArrayErase_ThenRoutedZeroFillMatchesTheDirectEmitter(bool optimize) {
    var direct = new CodeGenerator(Bind(_staticEraseProgram)) {
      Optimize = optimize,
      UseExperimentalBackend = false,
    };
    var routed = new CodeGenerator(Bind(_staticEraseProgram)) {
      Optimize = optimize,
      UseExperimentalBackend = true,
    };

    var directCpu = Cpu8086.Run(direct.EmitExecutable());
    var routedCpu = Cpu8086.Run(routed.EmitExecutable());

    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("main"));
    Assert.That(routedCpu.Output, Is.EqualTo(directCpu.Output));
  }

  [Test]
  public void Select_GivenNonI1MemsetVolatilityFlag_ThenDeclinesRatherThanIgnoreIt() {
    var memset = new IrFunction("llvm.memset.p0.i32", IrType.Void, [
      new IrArgument(IrType.Ptr, 0),
      new IrArgument(IrType.I8, 1),
      new IrArgument(IrType.I32, 2),
      new IrArgument(IrType.I1, 3),
    ]);
    var fn = new IrFunction("main", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var bytes = entry.Append(new IrAlloca(IrType.I8) { Count = 4 });
    entry.Append(new IrCall(IrType.Void, memset, [
      bytes,
      new IrConstantInt(IrType.I8, 0),
      new IrConstantInt(IrType.I32, 4),
      new IrConstantInt(IrType.I16, 0),
    ]));
    entry.Append(new IrRet());

    InstructionSelector.TrySelect(fn, out var reason);

    Assert.That(reason, Does.Contain("non-constant LLVM volatility flag"));
  }

  /// <summary>
  /// The table must not claim a routine it would render wrongly, and for a while that meant STR$ of a
  /// WORD had to DECLINE: <c>rt_str_i16</c> opens with a <c>CWD</c>, so an unsigned WORD routed
  /// through it prints 65535 as -1.
  ///
  /// <para>
  /// Declining was the right answer to the wrong question. The 32-bit renderer answers correctly from
  /// a ZEROED high half, which is the same <c>ArgKind.ZeroPair</c> the print side has always used for
  /// <c>rt_print_u16</c> and the same <c>XOR DX,DX</c> the direct emitter writes - so the entry
  /// exists, goes through <c>rt_str_i32</c>, and what must be pinned is which routine it reaches.
  /// </para>
  /// </summary>
  [Test]
  public void Select_GivenStrOfAnUnsignedWord_ThenItRoutesThroughTheThirtyTwoBitRenderer() {
    var m = Select("""
      DECLARE FUNCTION G%(BYVAL v%)
      DIM w AS WORD
      w = G%(30000) * 2
      PRINT STR$(w)
      PRINT G%(1)
      END

      FUNCTION G%(BYVAL v%) NOINLINE
        G% = v% + 0
      END FUNCTION
      """, "main");

    var call = m.AllInstructions.First(i => i.Opcode == MOpcode.Call
      && i.Operands[0] is MOperand.LabelRef { Name: "rt_str_i32" or "rt_str_i16" });

    Assert.That(((MOperand.LabelRef)call.Operands[0]).Name, Is.EqualTo("rt_str_i32"),
      "a WORD renders through the 32-bit routine; the 16-bit one would sign-extend it");
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

  /// <summary>
  /// The allocator's size is a BYTE COUNT in <c>DX:AX</c>, so a REDIM whose bounds are constant folds
  /// to one immediate pair and the table maps it straight across. It could not have mapped
  /// <c>(count, elementSize)</c> at all: the table places arguments in registers, and turning two of
  /// them into one takes a multiply it has no way to say.
  /// </summary>
  [Test]
  public void Select_GivenARedim_ThenTheAllocatorTakesTheByteCountInDxAx() {
    var m = Select("""
      REDIM a(1 TO 5) AS LONG
      a(1) = 7
      PRINT a(1)
      """, "main");

    var call = m.AllInstructions.First(i => i.Opcode == MOpcode.Call
      && ((MOperand.LabelRef)i.Operands[0]).Name == "rt_arr_alloc");
    var staged = m.AllInstructions
      .TakeWhile(i => i != call)
      .Where(i => i.Opcode == MOpcode.Mov && i.Operands[0] is MOperand.Register { Reg.IsVirtual: false })
      .ToDictionary(i => ((MOperand.Register)i.Operands[0]).Reg.Physical, i => i.Operands[1]);
    Assert.That(staged.Keys, Is.EquivalentTo(new[] { Reg.AX, Reg.DX }));
    Assert.That(((MOperand.Immediate)staged[Reg.AX]).Value, Is.EqualTo(20), "5 LONGs is 20 bytes, folded here");
    Assert.That(((MOperand.Immediate)staged[Reg.DX]).Value, Is.EqualTo(0));
  }

  /// <summary>
  /// A dynamic array's elements are in the far array heap, not in the program's own memory, and the
  /// only thing that says so is the address space on the pointer. Every access through one has to name
  /// the segment cell - and this is the assertion that would have caught the version of this that
  /// printed the right numbers: an element written and read back through the same DS-relative address
  /// round-trips perfectly while overwriting the program's own code with it.
  /// </summary>
  [Test]
  public void Select_GivenADynamicArrayElement_ThenEveryAccessNamesTheFarHeapSegment() {
    var m = Select("""
      REDIM a(1 TO 5) AS INTEGER
      a(2) = 7
      PRINT a(2)
      """, "main");

    var accesses = m.AllInstructions
      .Where(i => i.Opcode != MOpcode.Lea)                 // an LEA computes an address, it does not read one
      .SelectMany(i => i.Operands)
      .OfType<MOperand.Memory>()
      .ToList();
    Assert.That(accesses, Is.Not.Empty, "the element write and read should both be memory operands");
    Assert.That(accesses.Select(a => a.SegmentCell), Has.All.EqualTo("rt_arrseg"),
      "every dereference of far-heap storage carries its segment");
  }
}
